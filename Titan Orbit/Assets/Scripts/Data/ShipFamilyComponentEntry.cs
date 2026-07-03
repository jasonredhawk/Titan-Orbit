using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    public enum ShipComponentStatCategory
    {
        Offense = 0,
        Health = 1,
        Energy = 2,
        Movement = 3,
        Capacity = 4
    }

    [Serializable]
    public struct ShipComponentAbilityStats
    {
        public float firePower;
        public float firePowerPerLevel;
        public float bulletSpeed;
        public float bulletSpeedPerLevel;
        public float fireRate;
        public float fireRatePerLevel;
        public float rammingPower;
        public float rammingPowerPerLevel;
        public float healthCap;
        public float healthCapPerLevel;
        public float healthRegen;
        public float healthRegenPerLevel;
        public float energyCap;
        public float energyCapPerLevel;
        public float energyRegen;
        public float energyRegenPerLevel;
        public float moveSpeed;
        public float moveSpeedPerLevel;
        public float accelerationCap;
        public float accelerationCapPerLevel;
        public float turnSpeed;
        public float turnSpeedPerLevel;
        public float maxGems;
        public float maxGemsPerLevel;
        public float tractorBeamDistance;
        public float tractorBeamDistancePerLevel;
        public float tractorBeamPower;
        public float tractorBeamPowerPerLevel;
        public float maxPeople;
        public float maxPeoplePerLevel;

        public static string ResolvePartTypeForSuggestedStats(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return string.Empty;
            string id = componentId.ToLowerInvariant();
            if (id.Contains("weapon") || id.Contains("cannon")) return "Weapon";
            if (id.Contains("engine")) return "Engine";
            if (id.Contains("thruster")) return "Thruster";
            if (id.Contains("wing")) return "Wing";
            if (id.Contains("cockpit")) return "Cockpit";
            if (id.Contains("arm")) return "Arm";
            return string.Empty;
        }

        public static bool IsWeaponComponent(string componentId) =>
            ShipComponentAbilityStatsMath.IsWeaponComponent(componentId);

        public static bool IsPropulsionComponent(string componentId) =>
            ShipComponentAbilityStatsMath.IsPropulsionComponent(componentId);

        public void AddInPlace(ShipComponentAbilityStats other) =>
            ShipComponentAbilityStatsMath.AddInPlace(ref this, other);
    }

    [Serializable]
    public class ShipFamilyComponentEntry
    {
        public string componentId;
        public string displayName;
        public List<ShipComponentStatCategory> statCategories = new List<ShipComponentStatCategory>();
        public ShipComponentAbilityStats stats;
        public Sprite menuPreviewSprite;

        public void EnsureStatCategories()
        {
            if (statCategories == null)
                statCategories = new List<ShipComponentStatCategory>();
        }

        public Sprite GetMenuPreviewSprite(TeamManager.Team team = TeamManager.Team.None) => menuPreviewSprite;
    }
}
