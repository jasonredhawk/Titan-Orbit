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
            public int energyCapacityLevel = 0;
            public int firePowerLevel = 0;
            public int bulletSpeedLevel = 0;
            public int maxHealthLevel = 0;
            public int healthRegenLevel = 0;
            public int rotationSpeedLevel = 0;
            public int energyRegenLevel = 0;
        }

        [Header("Upgrade Settings")]
        [SerializeField] private float gemsPerUpgrade = 5f; // Cost = gemsPerUpgrade * shipLevel per upgrade

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

            int currentLevel = ship.GetAttributeLevel(attributeType);
            int maxUpgrades = ship.ShipLevel; // Level N ship = N upgrades per attribute

            if (currentLevel >= maxUpgrades) return;

            // Cost = 5 gems × ship level per upgrade (e.g. level 3 = 15 gems per upgrade)
            float cost = gemsPerUpgrade * ship.ShipLevel;
            if (ship.CurrentGems < cost) return;

            ship.RemoveGemsServerRpc(cost);
            ship.IncrementAttributeLevel(attributeType);

            UpgradeAttributeClientRpc(shipNetworkId, attributeType, currentLevel + 1);
        }

        /// <summary>Cost per single upgrade: gemsPerUpgrade × shipLevel.</summary>
        public float GetUpgradeCost(int shipLevel)
        {
            return gemsPerUpgrade * shipLevel;
        }

        [ClientRpc]
        private void UpgradeAttributeClientRpc(ulong shipNetworkId, ShipAttributeType attributeType, int newLevel)
        {
            Debug.Log($"Attribute {attributeType} upgraded to level {newLevel}");
        }

        public enum ShipAttributeType
        {
            MovementSpeed,
            EnergyCapacity,
            FirePower,
            BulletSpeed,
            MaxHealth,
            HealthRegen,
            RotationSpeed,
            EnergyRegen
        }
    }
}
