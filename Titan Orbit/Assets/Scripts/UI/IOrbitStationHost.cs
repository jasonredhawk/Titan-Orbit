using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>Host API for ship upgrade tree and orbit dock sidebar (ECS-backed).</summary>
    public interface IOrbitStationHost
    {
        UpgradeTree UpgradeTree { get; }
        float ContributedGems { get; }
        int StorePlanetId { get; }
        int StorePlanetLevel { get; }
        int HomePlanetLevel { get; }
        int ShipLevel { get; }
        int BranchIndex { get; }

        bool IsTreeDataAvailable();
        float GetShipTreeLayoutBasisWidthPublic();
        bool TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> edges);
        void RefreshShipUpgradeTreeNodeStates(IReadOnlyList<ShipUpgradeTreeNodeUI> nodes, float maxPower);
        void PopulateTreeNode(ShipUpgradeTreeNodeUI view, float maxPower);
        void OnUpgradeTreeNodeClicked(int nodeLevel, int targetBranchIndex);
        void OnCurrentShipDisplayNodeClicked();
        ShipFamilyPowerScoreBreakdown GetCurrentShipPowerBreakdown();
        ShipFamilyPowerScoreBreakdown GetPowerBreakdownForTreeNode(int level, int branchIndex);
    }
}
