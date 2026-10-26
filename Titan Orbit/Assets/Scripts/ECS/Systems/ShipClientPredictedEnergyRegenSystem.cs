using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client owner prediction of energy regen — mirrors server <see cref="ShipVitalsRegenSystem"/>
    /// for the local predicted ship only so OVERDRIVE can re-engage at ≥25% MaxEnergy without
    /// waiting for the next ghost snapshot (regen is otherwise server-only).
    /// <para>
    /// [NETCODE] Runs in <see cref="PredictedFixedStepSimulationSystemGroup"/> before the motor
    /// so the same tick can regen across the 25% floor and clear <see cref="ShipState.OverdriveLockout"/>.
    /// Server remains authoritative; reconciliation corrects drift. Does not predict health regen
    /// (not needed for OD). Skips under <see cref="ClientJoinSettleCache.ShouldSkipShipSimulation"/>.
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(ShipClientPredictedPhysicsDriveSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipClientPredictedEnergyRegenSystem : ISystem
    {
        /// <summary>
        /// True while this hull's cannon laser is burning. Server regen is paused
        /// for that pulse; predicting a refill here pinned the energy bar at full.
        /// </summary>
        static bool LaserBurnPausesRegen(ref SystemState state, Entity ship)
        {
            return state.EntityManager.HasComponent<MegaShipState>(ship)
                && state.EntityManager.GetComponentData<MegaShipState>(ship).IsMega
                && state.EntityManager.GetComponentData<MegaShipState>(ship).CannonLaserPulseOn;
        }

        /// <summary>Need vitals config before regenerating energy.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipVitalsConfig>();
        }

        /// <summary>
        /// Adds EnergyRegenPerSecond × dt to the local owner's CurrentEnergy (clamped to MaxEnergy).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join / TeamChoice: do not touch predicted ship vitals ---
            if (ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                return;

            // --- Local predicted owner only (GhostOwnerIsLocal + Simulate) ---
            foreach (var (ship, vitals, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipVitalsConfig>>()
                         .WithAll<ShipTag, GhostOwnerIsLocal, Simulate>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;
                if (LaserBurnPausesRegen(ref state, entity))
                    continue;

                float regen = vitals.ValueRO.EnergyRegenPerSecond;
                if (regen <= 0f)
                    continue;

                ref var s = ref ship.ValueRW;
                if (s.CurrentEnergy >= s.MaxEnergy)
                    continue;

                // [TITAN-ORBIT] Same clamp as server ShipVitalsRegenSystem — NetCode reconciles.
                s.CurrentEnergy = math.min(s.MaxEnergy, s.CurrentEnergy + regen * dt);
            }

            // --- Hybrid host fallback when GhostOwnerIsLocal lags ---
            foreach (var (ship, vitals, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipVitalsConfig>>()
                         .WithAll<ShipTag, LocalPlayerShipTag, Simulate>()
                         .WithNone<GhostOwnerIsLocal>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;
                if (LaserBurnPausesRegen(ref state, entity))
                    continue;

                float regen = vitals.ValueRO.EnergyRegenPerSecond;
                if (regen <= 0f)
                    continue;

                ref var s = ref ship.ValueRW;
                if (s.CurrentEnergy >= s.MaxEnergy)
                    continue;

                s.CurrentEnergy = math.min(s.MaxEnergy, s.CurrentEnergy + regen * dt);
            }
        }
    }
}
