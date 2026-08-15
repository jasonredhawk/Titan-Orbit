using TitanOrbit.Data;
using Unity.Entities;
using TitanOrbit;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// B-key bullet bank cycle. Default: owned damage banks only (hull family + purchased
    /// foreign weapons). Heal mode ignores B. Debug <c>CycleAllBulletBanks</c> wraps every
    /// <see cref="BulletVfxBank"/> category including EnergySpheres.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipCycleBulletSystem : ISystem
    {
        /// <summary>Cached category count from Resources bank (0 = bank missing).</summary>
        int _categoryCount;

        /// <summary>Load bank size once — categories do not change at runtime.</summary>
        public void OnCreate(ref SystemState state)
        {
            var bank = BulletVfxBank.LoadDefault();
            _categoryCount = bank != null ? bank.CategoryCount : 0;
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// For each simulated ship with CycleBullet pressed this tick, advance RuntimeBulletIndex.
        /// Client uses <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/> so rollback/resim
        /// does not increment the index multiple times for one press.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_categoryCount < 1)
                return;

            if (!state.World.IsServer())
            {
                // WithEntityAccess + equipped-weapon lookup — skip the whole Join Team Instantiates
                // window, not only ShouldSkipShipSimulation (map Instantiates keep GhostSpawnBacklog).
                if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                    return;

                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                if (!networkTime.IsFirstTimeFullyPredictingTick)
                    return;
            }

            foreach (var (input, loadout, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRW<ShipLoadoutState>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithEntityAccess())
            {
                if (!input.ValueRO.CycleBullet.IsSet)
                    continue;

                if (TitanOrbitDebugFlags.CycleAllBulletBanks)
                {
                    int current = loadout.ValueRO.RuntimeBulletIndex;
                    int next = BulletBankProfileUtility.NextDebugCycleBankIndex(current, _categoryCount);
                    loadout.ValueRW.RuntimeBulletIndex = next;
                    loadout.ValueRW.HealingBulletsActive = BulletBankProfileUtility.IsHealBankIndex(next);
                    continue;
                }

                if (loadout.ValueRO.HealingBulletsActive)
                    continue;

                loadout.ValueRW.RuntimeBulletIndex = BulletBankOwnership.NextOwnedDamageBank(
                    state.EntityManager, entity, loadout.ValueRO.RuntimeBulletIndex);
            }
        }
    }
}
