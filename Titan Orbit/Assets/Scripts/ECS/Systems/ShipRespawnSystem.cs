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
    /// Server-only: respawns destroyed ships on their team's home orbit ring after
    /// <see cref="RespawnDelaySeconds"/> (10s). Spawn angle is random but outside the gem-moon dock
    /// zone so the Orbit Menu does not open immediately. Triggered when
    /// <see cref="ShipDeathState.RespawnAtTime"/> is reached. Resets vitals, cargo, velocity, and
    /// orbit state; removes ShipDeathState. Runs after <see cref="BulletSimulationSystem"/> so
    /// death is fully processed first.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    public partial struct ShipRespawnSystem : ISystem
    {
        /// <summary>Seconds between death and respawn at home planet.</summary>
        public const float RespawnDelaySeconds = 10f;

        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
            // [TITAN-ORBIT] Death timer still uses World.Time; moon exclusion needs ServerTick orbit clock.
            float now = (float)SystemAPI.Time.ElapsedTime;
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double orbitElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (shipState, deathState, kinematics, orbitState, territoryLatch, physicsVelocity, transform, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipDeathState>, RefRW<ShipKinematics>, RefRW<ShipOrbitState>, RefRW<ShipTerritoryBoostLatch>, RefRW<PhysicsVelocity>, RefRW<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!shipState.ValueRO.IsDead)
                    continue;

                // [TITAN-ORBIT] ShipDeathRecordingSystem sets RespawnAtTime = now + delay.
                if (now < deathState.ValueRO.RespawnAtTime)
                    continue;

                // [TITAN-ORBIT] Random home orbit-ring spawn — shared with rejoin / Join Team.
                // Never last death position; never inside the gem-moon dock zone (Orbit Menu).
                // Skip this tick if home is not resolved yet — do not park the hull at origin.
                if (!ShipHomeSpawnLogic.TryFindHomeSpawnPosition(
                        state.EntityManager, shipState.ValueRO.Team, orbitElapsed, out float3 spawnPos))
                    continue;

                // --- MEGA: restore L6 while still IsDead so clients do not flash the MEGA at spawn ---
                if (state.EntityManager.HasComponent<MegaShipState>(entity)
                    && state.EntityManager.GetComponentData<MegaShipState>(entity).IsMega)
                {
                    MegaShipStatApplyLogic.RestorePreviousHull(state.EntityManager, entity);
                    // Restore writes ShipState via EM — refresh the query RW so we do not clobber L6.
                    shipState.ValueRW = state.EntityManager.GetComponentData<ShipState>(entity);
                }

                RespawnShip(
                    ref shipState.ValueRW,
                    ref kinematics.ValueRW,
                    ref orbitState.ValueRW,
                    ref territoryLatch.ValueRW,
                    ref transform.ValueRW,
                    spawnPos);
                physicsVelocity.ValueRW = PhysicsVelocity.Zero;

                // --- Clear kill attribution only (match stats stay match-long) ---
                // [TITAN-ORBIT] LastDamager must not carry across lives; Kills/Gems/People do.
                if (state.EntityManager.HasComponent<ShipCombatAttribution>(entity))
                {
                    state.EntityManager.SetComponentData(entity, new ShipCombatAttribution
                    {
                        LastDamagerNetworkId = 0,
                        LastDamageServerTime = 0f,
                        LastImpulseXZ = float2.zero,
                        LastImpulsePower = 0f,
                    });
                }

                if (state.EntityManager.HasComponent<ShipDeathVfxState>(entity))
                    state.EntityManager.SetComponentData(entity, new ShipDeathVfxState { Packed = 0 });

                ecb.RemoveComponent<ShipDeathState>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>Restores ship to full vitals at spawn position with zero velocity.</summary>
        static void RespawnShip(
            ref ShipState ship,
            ref ShipKinematics kinematics,
            ref ShipOrbitState orbit,
            ref ShipTerritoryBoostLatch territoryLatch,
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
            ship.OverdriveLockout = false;
            kinematics.Velocity = float3.zero;
            orbit.OrbitPlanetId = 0;
            orbit.InOrbitRing = false;
            orbit.UsingOrbitMotor = false;
            orbit.OrbitLocked = false;
            orbit.IsTransferringPeople = false;
            // [TITAN-ORBIT] Drop sticky triangle boost so respawn at home does not keep a latched mult.
            ShipPhysicsDriveLogic.ClearTerritoryBoostLatch(ref territoryLatch);

            LogRespawn(ship.Team, spawnPos);
        }

        [Unity.Burst.BurstDiscard]
        static void LogRespawn(TeamId team, float3 position)
        {
            UnityEngine.Debug.Log($"[ShipRespawnSystem] Respawned {team} ship at {position}.");
        }
    }
}
