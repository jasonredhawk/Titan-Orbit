using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>UI color grouping for ship component stats (offense, health, energy, movement, capacity).</summary>
    public enum ShipComponentStatCategory
    {
        /// <summary>Damage, fire rate, bullet speed, ramming.</summary>
        Offense = 0,
        /// <summary>Max hull and regeneration.</summary>
        Health = 1,
        /// <summary>Energy pool and regeneration.</summary>
        Energy = 2,
        /// <summary>Move speed, acceleration, turn rate.</summary>
        Movement = 3,
        /// <summary>Gems, people, tractor beam.</summary>
        Capacity = 4
    }

    /// <summary>
    /// Serializable stat block for one ship part — base value plus per-ship-level growth.
    /// Summed across all matched prefab children to produce hull totals. Per-level fields scale with
    /// <see cref="ShipComponentStoreData.GetEffectiveStatsAtShipLevel"/>.
    /// </summary>
    [Serializable]
    public struct ShipComponentAbilityStats
    {
        /// <summary>Damage per shot before weapon multipliers.</summary>
        public float firePower;
        public float firePowerPerLevel;
        /// <summary>Projectile speed in world units per second.</summary>
        public float bulletSpeed;
        public float bulletSpeedPerLevel;
        /// <summary>Shots per second baseline.</summary>
        public float fireRate;
        public float fireRatePerLevel;
        /// <summary>Ramming offense rating for hull collisions.</summary>
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
        /// <summary>Acceleration cap used by <see cref="ShipPropulsionAggregation"/>.</summary>
        public float accelerationCap;
        public float accelerationCapPerLevel;
        /// <summary>Yaw turn rate in degrees per second.</summary>
        public float turnSpeed;
        public float turnSpeedPerLevel;
        public float maxGems;
        public float maxGemsPerLevel;
        /// <summary>Wing tractor beam reach in world units.</summary>
        public float tractorBeamDistance;
        public float tractorBeamDistancePerLevel;
        public float tractorBeamPower;
        public float tractorBeamPowerPerLevel;
        public float maxPeople;
        public float maxPeoplePerLevel;

        /// <summary>Guesses part type from component id substring for editor suggestions and icons.</summary>
        public static string ResolvePartTypeForSuggestedStats(string componentId)
        {
            // --- Resolve value ---
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

        /// <summary>Adds <paramref name="other"/> into this struct in place (used during prefab stat scan).</summary>
        public void AddInPlace(ShipComponentAbilityStats other) =>
            ShipComponentAbilityStatsMath.AddInPlace(ref this, other);
    }

    /// <summary>
    /// One authored component row inside a <see cref="ShipFamilyDefinition"/> — id, display name,
    /// stat categories, ability numbers, and optional menu preview sprite.
    /// </summary>
    [Serializable]
    public class ShipFamilyComponentEntry
    {
        /// <summary>Stable id matching USC child name, e.g. AstroEagle_Engine_2.</summary>
        public string componentId;
        /// <summary>Override label for moon-dock cards; falls back to formatted <see cref="componentId"/>.</summary>
        public string displayName;
        /// <summary>Which stat categories tint the card border in orbit-station UI.</summary>
        public List<ShipComponentStatCategory> statCategories = new List<ShipComponentStatCategory>();
        /// <summary>Authoritative ability numbers for this part before level scaling.</summary>
        public ShipComponentAbilityStats stats;
        /// <summary>Optional 2D thumbnail when family atlas does not supply one.</summary>
        public Sprite menuPreviewSprite;

        /// <summary>Ensures <see cref="statCategories"/> list exists before UI reads it.</summary>
        public void EnsureStatCategories()
        {
            if (statCategories == null)
                statCategories = new List<ShipComponentStatCategory>();
        }

        /// <summary>Menu thumbnail for moon-dock component cards (team variant reserved for future use).</summary>
        public Sprite GetMenuPreviewSprite(TeamManager.Team team = TeamManager.Team.None) => menuPreviewSprite;
    }
}
