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
            Show(_ecsShipView, _ecsStorePlanetView);
        }

        static T GetOrCreatePlanetView<T>(string objectName) where T : Planet
        {
            // --- Reuse existing view or create DontDestroyOnLoad shell ---
            var existing = Object.FindFirstObjectByType<T>();
            if (existing != null)
                return existing;

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
                Debug.LogWarning($"[OrbitStationStore] {message}");

            if (_ecsShipView != null && _ecsStorePlanetId > 0)
                _ecsShipView.SyncFromEcs(_ecsStorePlanetId);
        }

        /// <summary>
        /// Metronome beat — bump Bank by the actual chunk so GEM DEPOSITS ticks with the SFX.
        /// Hooked from the main <see cref="OrbitStationUI"/> OnEnable/OnDisable.
        /// </summary>
        void OnLocalDepositBeatForBank(float chunkAmount)
        {
            if (chunkAmount <= 0.001f)
                return;
            contributedGems += chunkAmount;
            lastReceivedGems = contributedGems;
            RefreshSidebar();
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
