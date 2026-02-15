using UnityEngine;
using System.Collections.Generic;
using TitanOrbit.Data;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject that defines the ship upgrade tree structure.
    /// 6 ship levels, 2 choices per level (with overlap between branches).
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
        [SerializeField] private List<ShipUpgradeNode> level7Ships = new List<ShipUpgradeNode>(); // 4 MEGA boss ships

        [Header("Upgrade Requirements")]
        [Tooltip("Cost to upgrade TO this level. Index 2 = level 2 cost (100 so full starter ship can upgrade).")]
        [SerializeField] private float[] gemCostsPerLevel = { 0f, 100f, 100f, 250f, 500f, 1000f, 2000f, 15000f }; // Level 1-7

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

        public float GetGemCostForLevel(int level)
        {
            if (level >= 1 && level < gemCostsPerLevel.Length)
            {
                return gemCostsPerLevel[level];
            }
            return 0f;
        }

        /// <summary>Returns upgrades available from the given level and branch index. Tree: L1(1)→L2(2)→L3(4)→L4(6)→L5(8)→L6(9)→L7(4 MEGA).</summary>
        public List<ShipUpgradeNode> GetAvailableUpgrades(int currentLevel, int currentBranchIndex)
        {
            int nextLevel = currentLevel + 1;
            if (nextLevel > 7) return new List<ShipUpgradeNode>();

            List<ShipUpgradeNode> nextLevelShips = GetShipsForLevel(nextLevel);
            List<ShipUpgradeNode> available = new List<ShipUpgradeNode>();

            foreach (var node in nextLevelShips)
            {
                if (node.CanUpgradeFromBranch(currentBranchIndex))
                    available.Add(node);
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
