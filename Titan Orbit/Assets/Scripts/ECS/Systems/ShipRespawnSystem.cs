using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only: respawns destroyed ships at their team's home planet after
    /// <see cref="RespawnDelaySeconds"/>. Triggered when <see cref="ShipDeathState.RespawnAtTime"/>
    /// is reached. Resets vitals, cargo, velocity, and orbit state; removes ShipDeathState.
    /// Runs after <see cref="BulletSimulationSystem"/> so death is fully processed first.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    public partial struct ShipRespawnSystem : ISystem
    {
        /// <summary>Seconds between death and respawn at home planet.</summary>
        public const float RespawnDelaySeconds = 5f;

        public void OnUpdate(ref SystemState state)
        {
            float now = (float)SystemAPI.Time.ElapsedTime;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (shipState, deathState, kinematics, orbitState, physicsVelocity, transform, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipDeathState>, RefRW<ShipKinematics>, RefRW<ShipOrbitState>, RefRW<PhysicsVelocity>, RefRW<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!shipState.ValueRO.IsDead)
                    continue;

                // [TITAN-ORBIT] ShipDeathRecordingSystem sets RespawnAtTime = now + delay.
                if (now < deathState.ValueRO.RespawnAtTime)
                    continue;

                float3 spawnPos = FindHomeSpawnPosition(ref state, shipState.ValueRO.Team);
                RespawnShip(ref shipState.ValueRW, ref kinematics.ValueRW, ref orbitState.ValueRW, ref transform.ValueRW, spawnPos);
                physicsVelocity.ValueRW = PhysicsVelocity.Zero;
                ecb.RemoveComponent<ShipDeathState>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// Locates the home planet for the given team, offset slightly so the ship doesn't spawn
        /// inside the planet collider.
        /// </summary>
        float3 FindHomeSpawnPosition(ref SystemState state, TeamId team)
        {
            float3 homePos = float3.zero;
            bool found = false;

            // [ECS/DOTS] Prefer live planet entities tagged HomePlanetTag.
            foreach (var (planet, planetTransform) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag, HomePlanetTag>())
            {
                if (planet.ValueRO.Ownership != team)
                    continue;

                homePos = planetTransform.ValueRO.Position;
                found = true;
                break;
            }

            // [STANDARD] Fallback to baked map layout when planet entities aren't ready yet.
            if (!found && SystemAPI.TryGetSingletonBuffer<MapLayoutEntryElement>(out var layout))
            {
                for (int i = 0; i < layout.Length; i++)
                {
                    var entry = layout[i];
                    if (entry.EntityKind == 1 && entry.Team == team)
                    {
                        homePos = entry.Position;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
                return float3.zero;

            return homePos + new float3(20f, 0f, 0f);
        }

        /// <summary>Restores ship to full vitals at spawn position with zero velocity.</summary>
        static void RespawnShip(
            ref ShipState ship,
            ref ShipKinematics kinematics,
            ref ShipOrbitState orbit,
            ref LocalTransform transform,
            float3 spawnPos)
        {
            transform.Position = spawnPos;
            transform.Rotation = quaternion.identity;

            ship.Health = ship.MaxHealth;
            ship.CurrentGems = 0f;
            ship.CurrentPeople = 0;
            ship.CurrentEnergy = ship.MaxEnergy;
            ship.IsDead = false;
            kinematics.Velocity = float3.zero;
            orbit.OrbitPlanetId = 0;
            orbit.InOrbitRing = false;
            orbit.UsingOrbitMotor = false;

            LogRespawn(ship.Team, spawnPos);
        }

        [Unity.Burst.BurstDiscard]
        static void LogRespawn(TeamId team, float3 position)
        {
            UnityEngine.Debug.Log($"[ShipRespawnSystem] Respawned {team} ship at {position}.");
        }
    }
}
