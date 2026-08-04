using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>UI color grouping for ship component stats (offense, health, energy, movement, capacity).</summary>
    public enum ShipComponentStatCategory
    {
        /// <summary>Damage, fire rate, bullet speed, bullet range, ramming.</summary>
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
        /// <summary>Damage per shot before weapon multipliers (per barrel; hull uses average across guns).</summary>
        public float firePower;
        public float firePowerPerLevel;
        /// <summary>Projectile speed in world units per second.</summary>
        public float bulletSpeed;
        public float bulletSpeedPerLevel;
        /// <summary>
        /// How far a bullet travels before expiring (world units). Writes <c>ShipWeaponConfig.BulletMaxDistance</c>.
        /// [TITAN-ORBIT] Grows with ship level via <see cref="bulletRangePerLevel"/> — not a bottom-bar
        /// attribute upgrade like Fire Power. Family <c>bulletRangeMul</c> can scale both fields.
        /// </summary>
        public float bulletRange;
        /// <summary>Added to <see cref="bulletRange"/> once per ship level above 1.</summary>
        public float bulletRangePerLevel;
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

        /// <summary>
        /// Guesses canonical Part Profile group from component id (see <see cref="ShipFamilyPartTypes"/>).
        /// </summary>
        public static string ResolvePartTypeForSuggestedStats(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return string.Empty;

            // Alias table hit → normalize legacy labels into the seven core groups.
            string alias = ShipFamilyComponentPartKey.ResolveAliasKey(componentId);
            if (!string.IsNullOrEmpty(alias)
                && !string.Equals(alias, componentId, StringComparison.OrdinalIgnoreCase))
                return ShipFamilyPartTypes.Normalize(alias, componentId);

            return ShipFamilyPartTypes.InferFromComponentName(componentId);
        }

        static bool ContainsIsolatedKeyword(string haystackLower, string keywordLower)
        {
            int idx = haystackLower.IndexOf(keywordLower, StringComparison.Ordinal);
            while (idx >= 0)
            {
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(haystackLower[idx - 1]);
                int end = idx + keywordLower.Length;
                bool rightOk = end >= haystackLower.Length || !char.IsLetterOrDigit(haystackLower[end]);
                if (leftOk && rightOk)
                    return true;
                idx = haystackLower.IndexOf(keywordLower, idx + 1, StringComparison.Ordinal);
            }

            return false;
        }

        public static bool IsWeaponComponent(string componentId) =>
            ShipComponentAbilityStatsMath.IsWeaponComponent(componentId);

        public static bool IsPropulsionComponent(string componentId) =>
            ShipComponentAbilityStatsMath.IsPropulsionComponent(componentId);

        /// <summary>Adds <paramref name="other"/> into this struct in place (used during prefab stat scan).</summary>
        public void AddInPlace(ShipComponentAbilityStats other) =>
            ShipComponentAbilityStatsMath.AddInPlace(ref this, other);

        /// <summary>Keeps only the stat fields allowed for this component's categories and part id.</summary>
        public static ShipComponentAbilityStats KeepOnlyAuthoringFields(
            ShipComponentAbilityStats stats,
            IReadOnlyList<ShipComponentStatCategory> categories,
            string componentId)
        {
            string[] allowed = ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(categories, componentId);
            var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
            var filtered = new ShipComponentAbilityStats();

            if (allowedSet.Contains("firePower")) filtered.firePower = stats.firePower;
            if (allowedSet.Contains("firePowerPerLevel")) filtered.firePowerPerLevel = stats.firePowerPerLevel;
            if (allowedSet.Contains("bulletSpeed")) filtered.bulletSpeed = stats.bulletSpeed;
            if (allowedSet.Contains("bulletSpeedPerLevel")) filtered.bulletSpeedPerLevel = stats.bulletSpeedPerLevel;
            if (allowedSet.Contains("bulletRange")) filtered.bulletRange = stats.bulletRange;
            if (allowedSet.Contains("bulletRangePerLevel")) filtered.bulletRangePerLevel = stats.bulletRangePerLevel;
            if (allowedSet.Contains("fireRate")) filtered.fireRate = stats.fireRate;
            if (allowedSet.Contains("fireRatePerLevel")) filtered.fireRatePerLevel = stats.fireRatePerLevel;
            if (allowedSet.Contains("rammingPower")) filtered.rammingPower = stats.rammingPower;
            if (allowedSet.Contains("rammingPowerPerLevel")) filtered.rammingPowerPerLevel = stats.rammingPowerPerLevel;
            if (allowedSet.Contains("healthCap")) filtered.healthCap = stats.healthCap;
            if (allowedSet.Contains("healthCapPerLevel")) filtered.healthCapPerLevel = stats.healthCapPerLevel;
            if (allowedSet.Contains("healthRegen")) filtered.healthRegen = stats.healthRegen;
            if (allowedSet.Contains("healthRegenPerLevel")) filtered.healthRegenPerLevel = stats.healthRegenPerLevel;
            if (allowedSet.Contains("energyCap")) filtered.energyCap = stats.energyCap;
            if (allowedSet.Contains("energyCapPerLevel")) filtered.energyCapPerLevel = stats.energyCapPerLevel;
            if (allowedSet.Contains("energyRegen")) filtered.energyRegen = stats.energyRegen;
            if (allowedSet.Contains("energyRegenPerLevel")) filtered.energyRegenPerLevel = stats.energyRegenPerLevel;
            if (allowedSet.Contains("moveSpeed")) filtered.moveSpeed = stats.moveSpeed;
            if (allowedSet.Contains("moveSpeedPerLevel")) filtered.moveSpeedPerLevel = stats.moveSpeedPerLevel;
            if (allowedSet.Contains("accelerationCap")) filtered.accelerationCap = stats.accelerationCap;
            if (allowedSet.Contains("accelerationCapPerLevel")) filtered.accelerationCapPerLevel = stats.accelerationCapPerLevel;
            if (allowedSet.Contains("turnSpeed")) filtered.turnSpeed = stats.turnSpeed;
            if (allowedSet.Contains("turnSpeedPerLevel")) filtered.turnSpeedPerLevel = stats.turnSpeedPerLevel;
            if (allowedSet.Contains("maxGems")) filtered.maxGems = stats.maxGems;
            if (allowedSet.Contains("maxGemsPerLevel")) filtered.maxGemsPerLevel = stats.maxGemsPerLevel;
            if (allowedSet.Contains("tractorBeamDistance")) filtered.tractorBeamDistance = stats.tractorBeamDistance;
            if (allowedSet.Contains("tractorBeamDistancePerLevel")) filtered.tractorBeamDistancePerLevel = stats.tractorBeamDistancePerLevel;
            if (allowedSet.Contains("tractorBeamPower")) filtered.tractorBeamPower = stats.tractorBeamPower;
            if (allowedSet.Contains("tractorBeamPowerPerLevel")) filtered.tractorBeamPowerPerLevel = stats.tractorBeamPowerPerLevel;
            if (allowedSet.Contains("maxPeople")) filtered.maxPeople = stats.maxPeople;
            if (allowedSet.Contains("maxPeoplePerLevel")) filtered.maxPeoplePerLevel = stats.maxPeoplePerLevel;

            return filtered;
        }

        /// <summary>Keeps only the stat fields allowed for a single category.</summary>
        public static ShipComponentAbilityStats KeepOnlyAuthoringFields(
            ShipComponentAbilityStats stats,
            ShipComponentStatCategory category,
            string componentId) =>
            KeepOnlyAuthoringFields(stats, new[] { category }, componentId);
    }

    /// <summary>
    /// One authored component row inside a <see cref="ShipFamilyDefinition"/> — id, display name,
    /// stat categories, ability numbers, menu previews, and baked propulsion VFX flags.
    /// </summary>
    [Serializable]
    public class ShipFamilyComponentEntry
    {
        /// <summary>Stable id matching USC child name, e.g. AstroEagle_Engine_2.</summary>
        public string componentId;
        /// <summary>Override label for moon-dock cards; falls back to formatted <see cref="componentId"/>.</summary>
        public string displayName;
        /// <summary>Which stat categories tint the card border and filter Inspector fields.</summary>
        public List<ShipComponentStatCategory> statCategories = new List<ShipComponentStatCategory>();
        /// <summary>Authoritative ability numbers for this part before level scaling.</summary>
        public ShipComponentAbilityStats stats;
        /// <summary>Optional bullet bank override for weapon parts (-1 = family default).</summary>
        public int bulletPrefabIndex = -1;
        /// <summary>Optional 2D thumbnail (top-down) when family atlas does not supply one.</summary>
        public Sprite menuPreviewSprite;
        /// <summary>Theatrical (3/4) menu thumbnail for this component.</summary>
        public Sprite theatricalMenuPreviewSprite;
        /// <summary>Team-tinted top-down thumbnails.</summary>
        public List<ShipFamilyTeamMenuPreview> teamMenuPreviewSprites = new List<ShipFamilyTeamMenuPreview>();
        /// <summary>Team-tinted theatrical thumbnails.</summary>
        public List<ShipFamilyTeamMenuPreview> teamTheatricalMenuPreviewSprites = new List<ShipFamilyTeamMenuPreview>();
        /// <summary>Baked from ProfileSet: spawn jet particles under this mount.</summary>
        public bool enablePropulsionVfx;
        /// <summary>Baked from ProfileSet: relative particle scale (Big/Tiny).</summary>
        public float propulsionVfxScale = 1f;

        /// <summary>Ensures <see cref="statCategories"/> list exists before UI reads it.</summary>
        public void EnsureStatCategories()
        {
            if (statCategories == null)
                statCategories = new List<ShipComponentStatCategory>();
        }

        /// <summary>Menu thumbnail for moon-dock component cards (team variant when present).</summary>
        public Sprite GetMenuPreviewSprite(TeamManager.Team team = TeamManager.Team.None)
        {
            if (team != TeamManager.Team.None && teamMenuPreviewSprites != null)
            {
                for (int i = 0; i < teamMenuPreviewSprites.Count; i++)
                {
                    var entry = teamMenuPreviewSprites[i];
                    if (entry != null && entry.team == team && entry.sprite != null)
                        return entry.sprite;
                }
            }

            return menuPreviewSprite;
        }

        /// <summary>Theatrical menu thumbnail (team variant when present).</summary>
        public Sprite GetTheatricalMenuPreviewSprite(TeamManager.Team team = TeamManager.Team.None)
        {
            if (team != TeamManager.Team.None && teamTheatricalMenuPreviewSprites != null)
            {
                for (int i = 0; i < teamTheatricalMenuPreviewSprites.Count; i++)
                {
                    var entry = teamTheatricalMenuPreviewSprites[i];
                    if (entry != null && entry.team == team && entry.sprite != null)
                        return entry.sprite;
                }
            }

            return theatricalMenuPreviewSprite != null ? theatricalMenuPreviewSprite : GetMenuPreviewSprite(team);
        }
    }
}
