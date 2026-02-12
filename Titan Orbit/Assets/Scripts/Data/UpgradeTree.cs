using UnityEngine;
using System.Collections.Generic;
using TitanOrbit.Data;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject that defines the ship upgrade tree structure
    /// 6 levels total, 3 choices per level (with overlap), converging to 4 mega ships
    /// </summary>
    [CreateAssetMenu(fileName = "New Upgrade Tree", menuName = "Titan Orbit/Upgrade Tree")]
    public class UpgradeTree : ScriptableObject
    {
        [Header("Upgrade Tree Structure")]
        [SerializeField] private List<ShipUpgradeNode> level2Ships = new List<ShipUpgradeNode>();
        [SerializeField] private List<ShipUpgradeNode> level3Ships = new List<ShipUpgradeNode>();
        [SerializeField] private List<ShipUpgradeNode> level4Ships = new List<ShipUpgradeNode>();
        [SerializeField] private List<ShipUpgradeNode> level5Ships = new List<ShipUpgradeNode>();
        [SerializeField] private List<ShipUpgradeNode> level6Ships = new List<ShipUpgradeNode>(); // 4 mega ships

        [Header("Upgrade Requirements")]
        [SerializeField] private float[] gemCostsPerLevel = { 0f, 100f, 250f, 500f, 1000f, 2000f, 5000f }; // Level 1-6

        public List<ShipUpgradeNode> GetShipsForLevel(int level)
        {
            switch (level)
            {
                case 2: return level2Ships;
                case 3: return level3Ships;
                case 4: return level4Ships;
                case 5: return level5Ships;
                case 6: return level6Ships;
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

        public List<ShipUpgradeNode> GetAvailableUpgrades(int currentLevel, ShipFocusType currentFocus)
        {
            int nextLevel = currentLevel + 1;
            if (nextLevel > 6) return new List<ShipUpgradeNode>();

            List<ShipUpgradeNode> nextLevelShips = GetShipsForLevel(nextLevel);
            List<ShipUpgradeNode> available = new List<ShipUpgradeNode>();

            // Filter ships based on current focus type (with some flexibility)
            foreach (var ship in nextLevelShips)
            {
                // Allow switching between focus types, but with some restrictions
                if (ship.CanUpgradeFrom(currentFocus))
                {
                    available.Add(ship);
                }
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

        [Header("Upgrade Restrictions")]
        public List<ShipFocusType> canUpgradeFrom = new List<ShipFocusType>(); // Which focus types can upgrade to this

        [Header("Stats Multipliers")]
        public float movementSpeedMultiplier = 1f;
        public float fireRateMultiplier = 1f;
        public float firePowerMultiplier = 1f;
        public float healthMultiplier = 1f;
        public float gemCapacityMultiplier = 1f;
        public float peopleCapacityMultiplier = 1f;
        public float miningRateMultiplier = 1f;

        public bool CanUpgradeFrom(ShipFocusType currentFocus)
        {
            // If no restrictions, allow all
            if (canUpgradeFrom.Count == 0) return true;

            return canUpgradeFrom.Contains(currentFocus);
        }
    }
}
