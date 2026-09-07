using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Host API implemented by <see cref="OrbitStationUI"/> for ship upgrade tree and orbit dock
    /// sidebar. Decouples tree node views from the 5k-line station controller. Reads replicated
    /// ECS ghost state and contributed gems via orbit context — purchases go through RPCs.
    /// </summary>
    public interface IOrbitStationHost
    {
        // --- Tree / store context (read from ECS + RPC) ---
        UpgradeTree UpgradeTree { get; }
        float ContributedGems { get; }
        int StorePlanetId { get; }
        int StorePlanetLevel { get; }
        int HomePlanetLevel { get; }
        int ShipLevel { get; }
        int BranchIndex { get; }

        // --- Tree layout and node population ---
        // maxes = regular-family (L1–L6) per-stat ceilings. MEGA nodes resolve the
        // MEGA catalog maxes themselves so the two rosters never share a denominator.
        bool IsTreeDataAvailable();
        float GetShipTreeLayoutBasisWidthPublic();
        bool TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> edges);
        void RefreshShipUpgradeTreeNodeStates(IReadOnlyList<ShipUpgradeTreeNodeUI> nodes, ShipPowerBarStatMaxes maxes);
        void PopulateTreeNode(ShipUpgradeTreeNodeUI view, ShipPowerBarStatMaxes maxes);
        void OnUpgradeTreeNodeClicked(int nodeLevel, int targetBranchIndex);
        void OnCurrentShipDisplayNodeClicked();

        // --- Power score breakdown for tooltips ---
        ShipFamilyPowerScoreBreakdown GetCurrentShipPowerBreakdown();
        ShipFamilyPowerScoreBreakdown GetPowerBreakdownForTreeNode(int level, int branchIndex);
    }
}
