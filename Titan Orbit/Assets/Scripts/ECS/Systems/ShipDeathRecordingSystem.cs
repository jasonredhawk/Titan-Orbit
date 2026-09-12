using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only: watches for ships whose <see cref="ShipState.IsDead"/> just became true and
    /// adds <see cref="ShipDeathState"/> with a respawn timer. Clears people and velocity once —
    /// runs before <see cref="ShipRespawnSystem"/>. WithNone&lt;ShipDeathState&gt; ensures this
    /// fires exactly once per death.
    /// <para>
    /// [TITAN-ORBIT] Death requires hull <b>and</b> gems empty. A stray IsDead with cargo
    /// remaining is undone so leftover firepower can keep expelling gems. Combat already
    /// spilled cargo as world gems; tiny leftovers below min spawn are clamped.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [UpdateAfter(typeof(GemDepositSystem))]
    [UpdateAfter(typeof(ShipWreckExpireSystem))]
    [UpdateBefore(typeof(ShipRespawnSystem))]
    public partial struct ShipDeathRecordingSystem : ISystem
    {
        /// <summary>One-shot death bookkeeping for newly dead ships.</summary>
        public void OnUpdate(ref SystemState state)
        {
            float now = (float)SystemAPI.Time.ElapsedTime;
            uint tick = 0;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                && networkTime.ServerTick.IsValid)
                tick = networkTime.ServerTick.TickIndexForValidTick;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (shipState, kinematics, orbitState, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRW<ShipKinematics>, RefRW<ShipOrbitState>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipDeathState>()
                         .WithEntityAccess())
            {
                if (!shipState.ValueRO.IsDead)
                    continue;

                // Death needs hull AND gems empty. A stray IsDead with cargo left is a wreck.
                if (shipState.ValueRO.CurrentGems >= GemEconomyConstants.MinGemSpawnValue)
                {
                    shipState.ValueRW.IsDead = false;
                    continue;
                }

                ShipMatchStatsLogic.TryCreditKillFromAttribution(
                    state.EntityManager, entity, shipState.ValueRO.Team);

                // Combat already spilled cargo. Clamp dust below min spawn.
                shipState.ValueRW.CurrentGems = 0f;

                // Cargo still aboard dies with the hull. Unload hops that already left
                // the ship keep flying in PeopleTransportSimulationSystem.
                shipState.ValueRW.CurrentPeople = 0;
                kinematics.ValueRW.Velocity = Unity.Mathematics.float3.zero;
                orbitState.ValueRW.OrbitPlanetId = 0;
                orbitState.ValueRW.InOrbitRing = false;
                orbitState.ValueRW.UsingOrbitMotor = false;
                orbitState.ValueRW.OrbitLocked = false;
                orbitState.ValueRW.IsTransferringPeople = false;

                // --- MEGA death: free the store slot now; keep the MEGA visual until respawn ---
                if (state.EntityManager.HasComponent<MegaShipState>(entity)
                    && state.EntityManager.GetComponentData<MegaShipState>(entity).IsMega)
                {
                    MegaShipStatApplyLogic.ReleaseMegaOccupancy(state.EntityManager, entity);
                }

                PackDeathVfx(state.EntityManager, entity, now, tick, ecb);

                // [TITAN-ORBIT] Schedule respawn — ShipRespawnSystem removes this component later.
                ecb.AddComponent(entity, new ShipDeathState
                {
                    RespawnAtTime = now + ShipRespawnSystem.RespawnDelaySeconds,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// Writes <see cref="ShipDeathVfxState.Packed"/> from the last hit impulse + a tick seed
        /// so every client plays the same cosmetic breakup.
        /// </summary>
        static void PackDeathVfx(
            EntityManager em,
            Entity victim,
            float now,
            uint serverTick,
            EntityCommandBuffer ecb)
        {
            uint seed = serverTick != 0 ? serverTick : (uint)math.max(1, (int)(now * 1000f));
            if (em.HasComponent<GhostOwner>(victim))
                seed ^= (uint)em.GetComponentData<GhostOwner>(victim).NetworkId * 747796405u;

            float2 impulse = float2.zero;
            float power = 0f;
            if (em.HasComponent<ShipCombatAttribution>(victim))
            {
                var attr = em.GetComponentData<ShipCombatAttribution>(victim);
                impulse = attr.LastImpulseXZ;
                power = attr.LastImpulsePower;
            }

            var vfx = new ShipDeathVfxState { Packed = ShipDeathVfxState.Pack(seed, impulse, power) };
            if (em.HasComponent<ShipDeathVfxState>(victim))
                em.SetComponentData(victim, vfx);
            else
                ecb.AddComponent(victim, vfx);
        }

    }
}
