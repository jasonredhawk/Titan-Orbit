using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Marks a 0-HP wreck dead only when cargo is also empty. Timeout never kills —
    /// leftover gems keep the hull hittable until firepower finishes the hold.
    /// Server + predicted client.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    public partial struct ShipWreckExpireSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            double now = SystemAPI.Time.ElapsedTime;

            foreach (var (shipState, spin) in SystemAPI
                         .Query<RefRW<ShipState>, RefRW<ShipImpactSpinState>>()
                         .WithAll<ShipTag, Simulate>())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    if (spin.ValueRO.WreckExpiresAt != 0f || math.abs(spin.ValueRO.YawRateDegPerSec) > 0.01f)
                    {
                        spin.ValueRW.WreckExpiresAt = 0f;
                        if (shipState.ValueRO.IsDead)
                            spin.ValueRW.YawRateDegPerSec = 0f;
                    }

                    continue;
                }

                bool hullDown = shipState.ValueRO.Health <= ShipDamageLogic.DeathThreshold;
                bool gemsEmpty = shipState.ValueRO.CurrentGems < ShipImpactSpinLogic.DefaultMinGemSpawnValue;

                // Hull back up (should not happen while wrecked) — drop the wreck flag.
                if (!hullDown)
                {
                    if (spin.ValueRO.WreckExpiresAt > 0.01f)
                        spin.ValueRW.WreckExpiresAt = 0f;
                    continue;
                }

                if (gemsEmpty)
                {
                    shipState.ValueRW.IsDead = true;
                    shipState.ValueRW.Health = 0f;
                    shipState.ValueRW.CurrentGems = 0f;
                    spin.ValueRW.WreckExpiresAt = 0f;
                    continue;
                }

                // Keep wreck latched while cargo remains — no timeout death.
                if (spin.ValueRO.WreckExpiresAt <= 0.01f)
                    spin.ValueRW.WreckExpiresAt = (float)now + 1_000_000f;
            }
        }
    }
}
