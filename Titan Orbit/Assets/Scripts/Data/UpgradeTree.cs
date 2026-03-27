using UnityEngine;
using System.Collections.Generic;
using TitanOrbit.Data;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject that defines ship data per tree slot. Levels 1–6: L ships; level 7 has 3 MEGA ships with custom 6→7 edges.
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
        [SerializeField] private List<ShipUpgradeNode> level7Ships = new List<ShipUpgradeNode>(); // 3 MEGA boss ships (not 7)

        [Header("Upgrade Requirements")]
        [Tooltip("Legacy serialized costs (unused). Cost = 20 × level² gems to upgrade TO that level.")]
        [SerializeField] private float[] gemCostsPerLevel = { 0f, 100f, 100f, 250f, 500f, 1000f, 2000f, 15000f }; // Level 1-7

        /// <summary>Number of ship slots at this level (level 1 = 1 ship, … level 6 = 6, level 7 = 3 MEGA).</summary>
        public static int GetShipCountForLevel(int level)
        {
            if (level < 1 || level > 7) return 0;
            if (level == 7) return 3;
            return level;
        }

        /// <summary>
        /// Valid branch indices at <paramref name="fromLevel"/> + 1 from (<paramref name="fromLevel"/>, <paramref name="fromBranch"/>).
        /// Levels 1–5: p → p and p+1. Level 6 → 7: 6.1&amp;6.2→7.1; 6.2–6.5→7.2; 6.5&amp;6.6→7.3 (0-based: 0,1→0; 1–4→1; 4,5→2).
        /// </summary>
        public static void GetNextLevelBranchTargets(int fromLevel, int fromBranch, List<int> outBranches)
        {
            outBranches.Clear();
            if (fromLevel < 1 || fromLevel > 6) return;
            int nextLevel = fromLevel + 1;
            if (nextLevel > 7) return;

            if (fromLevel == 6 && nextLevel == 7)
            {
                switch (fromBranch)
                {
                    case 0: outBranches.Add(0); break;
                    case 1: outBranches.Add(0); outBranches.Add(1); break;
                    case 2: outBranches.Add(1); break;
                    case 3: outBranches.Add(1); break;
                    case 4: outBranches.Add(1); outBranches.Add(2); break;
                    case 5: outBranches.Add(2); break;
                }
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
            if (toLevel != fromLevel + 1) return false;
            if (toBranch < 0 || toBranch >= GetShipCountForLevel(toLevel)) return false;

            if (fromLevel == 6 && toLevel == 7)
            {
                switch (fromBranch)
                {
                    case 0: return toBranch == 0;
                    case 1: return toBranch == 0 || toBranch == 1;
                    case 2: return toBranch == 1;
                    case 3: return toBranch == 1;
                    case 4: return toBranch == 1 || toBranch == 2;
                    case 5: return toBranch == 2;
                    default: return false;
                }
            }

            if (fromBranch < 0 || fromBranch >= GetShipCountForLevel(fromLevel)) return false;
            return toBranch == fromBranch || toBranch == fromBranch + 1;
        }

        public List<ShipUpgradeNode> GetShipsForLevel(int level)
        {
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

        /// <summary>Gems to upgrade TO this level (20 × level²). Level 1 starter = 0.</summary>
        public float GetGemCostForLevel(int level)
        {
            if (level <= 1) return 0f;
            return 20f * level * level;
        }

        /// <summary>Resolve the upgrade node for slot (level, branchIndex). Branch index is 0-based within that level.</summary>
        public ShipUpgradeNode GetNodeForBranch(int level, int branchIndex)
        {
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

    [System.Serializable]
    public class ShipUpgradeNode
    {
        [Header("Ship Identity")]
        public ShipData shipData;
        public string shipName;
        public ShipFocusType focusType;

        [Header("Upgrade Restrictions (branch indices from previous level that can upgrade to this node)")]
        public List<int> canUpgradeFromBranchIndices = new List<int>();

        [Header("Stats Multipliers")]
        public float movementSpeedMultiplier = 1f;
        public float fireRateMultiplier = 1f;
        public float firePowerMultiplier = 1f;
        public float healthMultiplier = 1f;
        public float gemCapacityMultiplier = 1f;
        public float peopleCapacityMultiplier = 1f;
        public float miningRateMultiplier = 1f;

        public bool CanUpgradeFromBranch(int previousLevelBranchIndex)
        {
            if (canUpgradeFromBranchIndices == null || canUpgradeFromBranchIndices.Count == 0) return false;
            return canUpgradeFromBranchIndices.Contains(previousLevelBranchIndex);
        }
    }
}
