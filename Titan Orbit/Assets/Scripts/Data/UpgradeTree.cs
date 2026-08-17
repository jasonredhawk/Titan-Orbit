using UnityEngine;
using System.Collections.Generic;
using TitanOrbit.Data;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject defining the ship upgrade DAG (directed acyclic graph). Levels 1–6: L ships per level;
    /// level 7 has 3 MEGA boss hulls with custom 6→7 edges. Each node references legacy <see cref="ShipData"/>.
    /// Branch routing lives in static helpers so UI and server validate the same paths.
    /// </summary>
    [CreateAssetMenu(fileName = "New Upgrade Tree", menuName = "Titan Orbit/Upgrade Tree")]
    public class UpgradeTree : ScriptableObject
    {
        [Header("Upgrade Tree Structure")]
        [SerializeField] private List<ShipUpgradeNode> level2Ships = new List<ShipUpgradeNode>();
        [SerializeField] private List<ShipUpgradeNode> level3Ships = new List<ShipUpgradeNode>();
        [SerializeField] private List<ShipUpgradeNode> level4Ships = new List<ShipUpgradeNode>();
        [SerializeField] private List<ShipUpgradeNode> level5Ships = new List<ShipUpgradeNode>();
        [SerializeField] private List<ShipUpgradeNode> level6Ships = new List<ShipUpgradeNode>();
        /// <summary>Three MEGA boss ships at tier 7 (not seven nodes).</summary>
        [SerializeField] private List<ShipUpgradeNode> level7Ships = new List<ShipUpgradeNode>();

        [Header("Upgrade Requirements")]
        [Tooltip("Legacy serialized costs (unused). Runtime ship purchase cost = 2× gem cap (L1→L6 gradient) per chassis.")]
        [SerializeField] private float[] gemCostsPerLevel = { 0f, 100f, 100f, 250f, 500f, 1000f, 2000f, 15000f };

        /// <summary>Number of ship slots at this level (level 1 = 1 ship, … level 6 = 6, level 7 = 3 MEGA).</summary>
        public static int GetShipCountForLevel(int level)
        {
            // --- Compute value ---
            if (level < 1 || level > 7) return 0;
            if (level == 7) return 3;
            return level;
        }

        /// <summary>
        /// Valid branch indices at <paramref name="fromLevel"/> + 1 from (<paramref name="fromLevel"/>, <paramref name="fromBranch"/>).
        /// Levels 1–5: p → p and p+1. Level 6 → 7: clean pairs — 1&amp;2→mega 1, 3&amp;4→mega 2, 5&amp;6→mega 3.
        /// </summary>
        public static void GetNextLevelBranchTargets(int fromLevel, int fromBranch, List<int> outBranches)
        {
            // --- Compute value ---
            outBranches.Clear();
            if (fromLevel < 1 || fromLevel > 6) return;
            int nextLevel = fromLevel + 1;
            if (nextLevel > 7) return;

            if (fromLevel == 6 && nextLevel == 7)
            {
                // [TITAN-ORBIT] Players do not choose between two megas from one L6 hull.
                int mega = fromBranch / 2;
                if (mega >= 0 && mega < 3)
                    outBranches.Add(mega);
                return;
            }

            if (fromBranch < 0 || fromBranch >= GetShipCountForLevel(fromLevel)) return;
            for (int j = fromBranch; j <= fromBranch + 1; j++)
            {
                if (j >= 0 && j < GetShipCountForLevel(nextLevel))
                    outBranches.Add(j);
            }
        }

        /// <summary>True if one upgrade step from (fromLevel, fromBranch) to (toLevel, toBranch) is allowed.</summary>
        public static bool IsValidUpgradeStep(int fromLevel, int fromBranch, int toLevel, int toBranch)
        {
            // --- IsValidUpgradeStep ---
            if (toLevel != fromLevel + 1) return false;
            if (toBranch < 0 || toBranch >= GetShipCountForLevel(toLevel)) return false;

            if (fromLevel == 6 && toLevel == 7)
                return fromBranch >= 0 && fromBranch <= 5 && toBranch == fromBranch / 2;

            if (fromBranch < 0 || fromBranch >= GetShipCountForLevel(fromLevel)) return false;
            return toBranch == fromBranch || toBranch == fromBranch + 1;
        }

        /// <summary>Returns the serialized node list for levels 2–7; empty list for level 1 or out of range.</summary>
        public List<ShipUpgradeNode> GetShipsForLevel(int level)
        {
            // --- Compute value ---
            switch (level)
            {
                case 2: return level2Ships;
                case 3: return level3Ships;
                case 4: return level4Ships;
                case 5: return level5Ships;
                case 6: return level6Ships;
                case 7: return level7Ships;
                default: return new List<ShipUpgradeNode>();
            }
        }

        /// <summary>Legacy level-only cost hook. Prefer <see cref="Systems.CardShopSystem.GetPurchaseGemCostForUpgradeSlot"/>.</summary>
        public float GetGemCostForLevel(int level)
        {
            return 0f;
        }

        /// <summary>Resolve the upgrade node for slot (level, branchIndex). Branch index is 0-based within that level.</summary>
        public ShipUpgradeNode GetNodeForBranch(int level, int branchIndex)
        {
            // --- Compute value ---
            var list = GetShipsForLevel(level);
            if (list == null || branchIndex < 0 || branchIndex >= GetShipCountForLevel(level)) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                if (n == null) continue;
                if (n.shipData != null && n.shipData.branchIndex == branchIndex) return n;
            }
            // Legacy assets: list order is slot 0..L-1 even if ShipData.branchIndex was not set.
            if (branchIndex < list.Count) return list[branchIndex];
            return null;
        }

        /// <summary>Next-tier choices from (currentLevel, currentBranchIndex), including custom level 6 → 7 routing.</summary>
        public List<ShipUpgradeNode> GetAvailableUpgrades(int currentLevel, int currentBranchIndex)
        {
            // --- Compute value ---
            int nextLevel = currentLevel + 1;
            if (nextLevel > 7) return new List<ShipUpgradeNode>();

            var available = new List<ShipUpgradeNode>();
            var targets = new List<int>(4);
            GetNextLevelBranchTargets(currentLevel, currentBranchIndex, targets);
            for (int i = 0; i < targets.Count; i++)
            {
                int j = targets[i];
                ShipUpgradeNode n = GetNodeForBranch(nextLevel, j);
                if (n != null) available.Add(n);
            }
            return available;
        }
    }

    /// <summary>
    /// One node in the upgrade tree — links to <see cref="ShipData"/>, optional name override, and
    /// per-stat multipliers applied on top of family totals when this hull is purchased.
    /// </summary>
    [System.Serializable]
    public class ShipUpgradeNode
    {
        [Header("Ship Identity")]
        /// <summary>Legacy hull stats and branch index for this slot.</summary>
        public ShipData shipData;
        public string shipName;
        public ShipFocusType focusType;

        [Header("Upgrade Restrictions (branch indices from previous level that can upgrade to this node)")]
        /// <summary>Explicit allow-list; empty means routing uses <see cref="UpgradeTree.GetNextLevelBranchTargets"/> only.</summary>
        public List<int> canUpgradeFromBranchIndices = new List<int>();

        [Header("Stats Multipliers")]
        /// <summary>Applied to movement after family + card totals.</summary>
        public float movementSpeedMultiplier = 1f;
        public float fireRateMultiplier = 1f;
        public float firePowerMultiplier = 1f;
        public float healthMultiplier = 1f;
        public float gemCapacityMultiplier = 1f;
        public float peopleCapacityMultiplier = 1f;
        public float miningRateMultiplier = 1f;

        /// <summary>True when <paramref name="previousLevelBranchIndex"/> is listed in canUpgradeFromBranchIndices.</summary>
        public bool CanUpgradeFromBranch(int previousLevelBranchIndex)
        {
            if (canUpgradeFromBranchIndices == null || canUpgradeFromBranchIndices.Count == 0) return false;
            return canUpgradeFromBranchIndices.Contains(previousLevelBranchIndex);
        }
    }
}
