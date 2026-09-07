using TitanOrbit.Data;
using Unity.Entities;
using TitanOrbit;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// B-key and bullet-type HUD selection. Production: owned damage banks only (hull family +
    /// purchased foreign weapons). Heal mode ignores B and HUD clicks. GameManager
    /// <c>CycleAllBulletBanks</c> wraps every <see cref="BulletVfxBank"/> category including
    /// EnergySpheres. HUD clicks arrive as <see cref="ShipInput.SetBulletBank"/> and jump to
    /// <see cref="ShipInput.SelectedBulletBank"/> when that index is selectable.
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
        /// For each simulated ship with CycleBullet or SetBulletBank this tick, update
        /// RuntimeBulletIndex. Client uses <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/>
        /// so rollback/resim does not apply the same press twice.
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
                bool setBank = input.ValueRO.SetBulletBank.IsSet;
                bool cycle = input.ValueRO.CycleBullet.IsSet;
                if (!setBank && !cycle)
                    continue;

                // MEGA mounts each fire a catalog bank — B-key / HUD must not retarget the volley.
                if (SystemAPI.HasComponent<MegaShipState>(entity) &&
                    SystemAPI.GetComponentRO<MegaShipState>(entity).ValueRO.IsMega)
                    continue;

                // --- HUD click: jump to a specific bank ---
                // [TITAN-ORBIT] Applied before B so a click and a B on the same tick
                // keep the tile the player tapped.
                if (setBank)
                {
                    int requested = input.ValueRO.SelectedBulletBank;
                    if (BulletBankOwnership.IsSelectableBank(state.EntityManager, entity, requested))
                    {
                        loadout.ValueRW.RuntimeBulletIndex = requested;
                        if (TitanOrbitDebugFlags.CycleAllBulletBanks)
                            loadout.ValueRW.HealingBulletsActive =
                                BulletBankProfileUtility.IsHealBankIndex(requested);
                    }

                    continue;
                }

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
