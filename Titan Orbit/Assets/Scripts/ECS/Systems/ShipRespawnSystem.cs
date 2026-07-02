using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>Respawns destroyed ships at their team's home planet after a short delay.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    public partial struct ShipRespawnSystem : ISystem
    {
        public const float RespawnDelaySeconds = 5f;

        public void OnUpdate(ref SystemState state)
        {
            float now = (float)SystemAPI.Time.ElapsedTime;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (shipState, deathState, kinematics, orbitState, transform, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipDeathState>, RefRW<ShipKinematics>, RefRW<ShipOrbitState>, RefRW<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!shipState.ValueRO.IsDead)
                    continue;

                if (now < deathState.ValueRO.RespawnAtTime)
                    continue;

                float3 spawnPos = FindHomeSpawnPosition(ref state, shipState.ValueRO.Team);
                RespawnShip(ref shipState.ValueRW, ref kinematics.ValueRW, ref orbitState.ValueRW, ref transform.ValueRW, spawnPos);
                ecb.RemoveComponent<ShipDeathState>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        float3 FindHomeSpawnPosition(ref SystemState state, TeamId team)
        {
            float3 homePos = float3.zero;
            bool found = false;

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
