using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Game;
using TitanOrbit.Systems;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Partial <see cref="OrbitStationUI"/> — implements <see cref="IOrbitStationHost"/> and wires
    /// ECS-backed planet/ship views for the orbit dock sidebar. [HYBRID] bridges legacy Planet/Starship
    /// MonoBehaviour views with NetCode ghost state via <see cref="OrbitStationEcsContext"/>.
    /// </summary>
    public partial class OrbitStationUI
    {
        // --- Singleton and ECS store context ---
        public static OrbitStationUI Instance { get; private set; }

        int _ecsStorePlanetId;
        int _ecsHomePlanetId;

        Planet _ecsStorePlanetView;
        HomePlanet _ecsHomePlanetView;
        Starship _ecsShipView;

        UpgradeTree IOrbitStationHost.UpgradeTree =>
            UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;

        float IOrbitStationHost.ContributedGems => contributedGems;

        int IOrbitStationHost.StorePlanetId => _ecsStorePlanetId;

        int IOrbitStationHost.StorePlanetLevel =>
            _ecsStorePlanetView != null ? Mathf.Max(1, _ecsStorePlanetView.PlanetLevel) : 1;

        int IOrbitStationHost.HomePlanetLevel =>
            _ecsHomePlanetView != null ? Mathf.Max(1, _ecsHomePlanetView.HomePlanetLevel) : 1;

        int IOrbitStationHost.ShipLevel =>
            currentShip != null ? currentShip.ShipLevel : 1;

        int IOrbitStationHost.BranchIndex =>
            currentShip != null ? currentShip.BranchIndex : 0;

        bool IOrbitStationHost.IsTreeDataAvailable() => IsTreeDataAvailable();

        float IOrbitStationHost.GetShipTreeLayoutBasisWidthPublic() => GetShipTreeLayoutBasisWidthPublic();

        bool IOrbitStationHost.TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> edges) =>
            TryGetPlayerUpgradePathEdges(out edges);

        void IOrbitStationHost.RefreshShipUpgradeTreeNodeStates(IReadOnlyList<ShipUpgradeTreeNodeUI> nodes, float maxPower) =>
            RefreshShipUpgradeTreeNodeStates(nodes, maxPower);

        void IOrbitStationHost.PopulateTreeNode(ShipUpgradeTreeNodeUI view, float maxPower) =>
            PopulateTreeNode(view, maxPower);

        void IOrbitStationHost.OnUpgradeTreeNodeClicked(int nodeLevel, int targetBranchIndex) =>
            OnUpgradeTreeNodeClicked(nodeLevel, targetBranchIndex);

        void IOrbitStationHost.OnCurrentShipDisplayNodeClicked() =>
            OnCurrentShipDisplayNodeClicked();

        ShipFamilyPowerScoreBreakdown IOrbitStationHost.GetCurrentShipPowerBreakdown() =>
            GetCurrentShipPowerBreakdown();

        ShipFamilyPowerScoreBreakdown IOrbitStationHost.GetPowerBreakdownForTreeNode(int level, int branchIndex) =>
            GetPowerBreakdownForTreeNode(level, branchIndex);

        public void ShowFromEcs(int storePlanetId, int homePlanetId)
        {
            // --- Cache planet ids and sync legacy views from ECS ---
            _ecsStorePlanetId = storePlanetId;
            _ecsHomePlanetId = homePlanetId;

            _ecsShipView = Starship.GetOrCreate();
            _ecsShipView.SyncFromEcs(storePlanetId);

            _ecsStorePlanetView = GetOrCreatePlanetView<Planet>("OrbitStationStorePlanetView");
            SyncPlanetView(_ecsStorePlanetView, storePlanetId, isHome: false);

            if (homePlanetId > 0)
            {
                _ecsHomePlanetView = GetOrCreatePlanetView<HomePlanet>("OrbitStationHomePlanetView");
                SyncPlanetView(_ecsHomePlanetView, homePlanetId, isHome: true);
            }
            else
            {
                _ecsHomePlanetView = null;
            }

            OrbitStationEcsContext.Set(
                storePlanetId,
                homePlanetId,
                _ecsShipView.ShipLevel,
                _ecsShipView.BranchIndex);

            Instance = this;
            MoonOrbitClientState.SetOrbitMenuVisible(true);
            // [TITAN-ORBIT] Pass store + home explicitly so Show does not rediscover a wrong HomePlanet
            // via AllHomePlanets and treat the docked captured moon as AstroEagle.
            currentHomePlanet = _ecsHomePlanetView;
            _lastHomePlanetLookupTime = Time.time;
            Show(_ecsShipView, _ecsStorePlanetView);
        }

        static T GetOrCreatePlanetView<T>(string objectName) where T : Planet
        {
            // --- Reuse existing named view or create DontDestroyOnLoad shell ---
            // [TITAN-ORBIT] Do NOT use FindFirstObjectByType<Planet>() — HomePlanet subclasses
            // Planet, so the store-planet view would steal the home adapter and the Orbit Menu
            // would always resolve AstroEagle for captured neutrals.
            var named = GameObject.Find(objectName);
            if (named != null)
            {
                var existing = named.GetComponent<T>();
                if (existing != null)
                    return existing;
            }

            var go = new GameObject(objectName);
            Object.DontDestroyOnLoad(go);
            return go.AddComponent<T>();
        }

        static void SyncPlanetView(Planet view, int planetId, bool isHome)
        {
            // --- Mirror planet ghost state into legacy Planet view ---
            if (view == null || planetId <= 0)
                return;

            view.PlanetId = planetId;
            if (EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out var state))
            {
                view.PlanetLevel = Mathf.Max(1, state.PlanetLevel);
                view.TeamOwnership = TeamManager.FromTeamId(state.Ownership);
            }

            if (isHome && view is HomePlanet home)
                home.AssignedTeam = view.TeamOwnership;
        }

        partial void OnOrbitStationEcsUpdate()
        {
            // --- Consume RPC scratch state and refresh ship view ---
            if (MoonOrbitClientState.TryConsumeContributedGems(out float gems))
                OnContributedGemsReceived(gems);

            if (MoonOrbitClientState.TryConsumeStoreMessage(out string message))
            {
                Debug.LogWarning($"[OrbitStationStore] {message}");
                // [TITAN-ORBIT] Purchase / remove results — rebuild slots immediately (don't wait for poll).
                _ecsShipView?.InvalidateLoadoutCache();
                // Refresh Bank after spend (Local Host already updated ledger; dedicated needs a pull).
                if (OrbitStationEcsContext.HomePlanetId > 0)
                    MoonOrbitRpcClient.RequestContributedGems(OrbitStationEcsContext.HomePlanetId);
                RefreshAll();
            }

            // [TITAN-ORBIT] Spin offer / take-card events — rebuild offer tiles + card slots.
            if (MoonOrbitClientState.TryConsumeSpinOfferReceived())
            {
                TitanOrbit.Systems.CardShopSystem.RaiseClientSpinOfferReceived();
                RefreshStoreLabels();
                RefreshSlots();
            }

            if (MoonOrbitClientState.TryConsumeSpinOfferConsumed())
            {
                TitanOrbit.Systems.CardShopSystem.RaiseClientSpinOfferConsumed();
                _ecsShipView?.InvalidateLoadoutCache();
                RefreshStoreLabels();
                RefreshSlots();
            }

            if (_ecsShipView != null && _ecsStorePlanetId > 0)
            {
                // [TITAN-ORBIT] Sync ship view every frame for dock state, but only rebuild
                // equipment/card lists when store RPCs fire (RefreshAll) or every ~0.25s —
                // SyncLoadoutBuffers used to scan ServerWorld buffers every Update and felt laggy.
                _ecsShipView.SyncFromEcs(_ecsStorePlanetId);
            }
        }

        /// <summary>
        /// Metronome beat — snap Ship cargo ↓ and Bank ↑ together from optimistic state already
        /// bumped in <see cref="MoonOrbitClientState.NotifyLocalDepositBeat"/> (same stack as SFX).
        /// Hooked from the main <see cref="OrbitStationUI"/> OnEnable/OnDisable.
        /// </summary>
        void OnLocalDepositBeatForBank(float chunkAmount)
        {
            if (chunkAmount <= 0.001f)
                return;

            // NotifyLocalDepositBeat already added chunkAmount to OptimisticDepositBankGems.
            if (MoonOrbitClientState.TryGetOptimisticDepositBank(out float optBank))
                contributedGems = optBank;
            else
            {
                // Fallback if optimistic Bank was cleared mid-event (deposit toggle race).
                contributedGems += chunkAmount;
                MoonOrbitClientState.RememberContributedGems(contributedGems);
                MoonOrbitClientState.EnsureOptimisticDepositBankSeed(contributedGems);
            }

            // Beat-only deposit row refresh — avoid full RefreshSidebar (store rebuild / auto-deposit
            // re-apply) which could race the numbers off the audible tick.
            RefreshDepositFlowFromMetronome();
        }

        /// <summary>
        /// Writes Ship + Bank sidebar numbers from optimistic metronome state in one paint.
        /// Planet progress may still come from ECS (cosmetic under the Bank banner).
        /// </summary>
        void RefreshDepositFlowFromMetronome()
        {
            if (!_moonDockLayoutActive || orbitDockSidebar == null)
                return;

            float shipGems = 0f;
            bool haveGhost = EcsGameBridge.TryGetLocalShipState(out var shipState);
            float ghostGems = haveGhost ? shipState.CurrentGems : 0f;

            if (MoonOrbitClientState.TryGetOptimisticDepositCargo(out float optCargo) &&
                !(optCargo <= 0.001f && ghostGems > 0.001f))
                shipGems = optCargo;
            else if (haveGhost)
                shipGems = ghostGems;

            float bankGems = contributedGems;
            if (MoonOrbitClientState.TryGetOptimisticDepositBank(out float optBank))
            {
                bankGems = optBank;
                contributedGems = optBank;
            }

            float planetGems = 0f;
            int planetLevel = 1;
            int storePlanetId = OrbitStationEcsContext.StorePlanetId;
            if (storePlanetId <= 0 && currentPlanet != null)
                storePlanetId = currentPlanet.PlanetId;
            if (storePlanetId > 0 &&
                EcsGameBridge.TryGetPlanetStateByPlanetId(storePlanetId, out var planetState))
            {
                planetGems = planetState.CurrentGems;
                planetLevel = Mathf.Max(1, planetState.PlanetLevel);
            }

            orbitDockSidebar.RefreshDepositStatus(shipGems, bankGems, planetGems, planetLevel);
        }

        partial void OnOrbitStationEcsHide()
        {
            // --- Clear orbit menu visibility and ECS context ---
            MoonOrbitClientState.SetOrbitMenuVisible(false);
            // Keep deposit intent while still moon-docked; MoonOrbitStationController clears on undock.
            OrbitStationEcsContext.Clear();
            _ecsStorePlanetId = 0;
            _ecsHomePlanetId = 0;
        }

        void OnOrbitStationEcsAwake()
        {
            if (Instance != null && Instance != this)
                return;
            Instance = this;
        }

        void OnOrbitStationEcsDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
