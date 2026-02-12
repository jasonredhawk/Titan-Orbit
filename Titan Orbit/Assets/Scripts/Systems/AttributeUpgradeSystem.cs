using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Handles attribute upgrades for ships (movement speed, fire rate, etc.)
    /// Each ship level allows that many upgrades per attribute
    /// </summary>
    public class AttributeUpgradeSystem : NetworkBehaviour
    {
        public static AttributeUpgradeSystem Instance { get; private set; }

        [System.Serializable]
        public class ShipAttributeUpgrades
        {
            public int movementSpeedLevel = 0;
            public int fireRateLevel = 0;
            public int firePowerLevel = 0;
            public int bulletSpeedLevel = 0;
            public int maxHealthLevel = 0;
            public int healthRegenLevel = 0;
            public int rotationSpeedLevel = 0;
            public int gemCapacityLevel = 0;
            public int peopleCapacityLevel = 0;
        }

        [Header("Upgrade Settings")]
        [SerializeField] private float upgradeCostPerLevel = 50f;
        [SerializeField] private float attributeMultiplierPerLevel = 0.1f; // 10% increase per level

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void UpgradeAttributeServerRpc(ulong shipNetworkId, ShipAttributeType attributeType)
        {
            NetworkObject shipNetObj = GetNetworkObject(shipNetworkId);
            if (shipNetObj == null) return;

            Starship ship = shipNetObj.GetComponent<Starship>();
            if (ship == null) return;

            // Check if ship can upgrade this attribute
            int currentLevel = GetAttributeLevel(ship, attributeType);
            int maxUpgrades = ship.ShipLevel; // Level 1 ship = 1 upgrade, Level 6 ship = 6 upgrades

            if (currentLevel >= maxUpgrades) return;

            // Check if player has enough gems
            float cost = upgradeCostPerLevel * (currentLevel + 1);
            if (ship.CurrentGems < cost) return;

            // Apply upgrade
            ship.RemoveGemsServerRpc(cost);
            ApplyAttributeUpgrade(ship, attributeType, currentLevel + 1);

            UpgradeAttributeClientRpc(shipNetworkId, attributeType, currentLevel + 1);
        }

        private int GetAttributeLevel(Starship ship, ShipAttributeType attributeType)
        {
            // This would need to be stored in a NetworkVariable or component
            // For now, return 0 as placeholder
            return 0;
        }

        private void ApplyAttributeUpgrade(Starship ship, ShipAttributeType attributeType, int newLevel)
        {
            float multiplier = 1f + (attributeMultiplierPerLevel * newLevel);

            // Apply multiplier to ship attribute
            // This would need to be implemented in Starship class
            switch (attributeType)
            {
                case ShipAttributeType.MovementSpeed:
                    // ship.movementSpeed *= multiplier;
                    break;
                case ShipAttributeType.FireRate:
                    // ship.fireRate *= multiplier;
                    break;
                // ... etc
            }
        }

        [ClientRpc]
        private void UpgradeAttributeClientRpc(ulong shipNetworkId, ShipAttributeType attributeType, int newLevel)
        {
            Debug.Log($"Attribute {attributeType} upgraded to level {newLevel}");
        }

        public enum ShipAttributeType
        {
            MovementSpeed,
            FireRate,
            FirePower,
            BulletSpeed,
            MaxHealth,
            HealthRegen,
            RotationSpeed,
            GemCapacity,
            PeopleCapacity
        }
    }
}
