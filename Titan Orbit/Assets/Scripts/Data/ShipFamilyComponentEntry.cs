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
    /// Serializable stat block for one ship part — base values plus
    /// <c>*PerAbilityLevel</c> steps for bottom-HUD Ship Ability Upgrades.
    /// Ship-tier growth is <b>not</b> authored here: each family uses
    /// <see cref="ShipFamilyDefinition.shipLevelStatGrowthFraction"/> (default 10%)
    /// in <see cref="ShipComponentStoreData.GetEffectiveStatsAtShipLevel"/>.
    /// Summed across matched prefab children to produce hull totals.
    /// </summary>
    [Serializable]
    public struct ShipComponentAbilityStats
    {
        /// <summary>Damage per shot before weapon multipliers (per barrel; hull uses average across guns).</summary>
        public float firePower;
        /// <summary>Bottom-HUD Fire Power ability step (additive when that ability uses PerAbilityLevel).</summary>
        [UnityEngine.Serialization.FormerlySerializedAs("firePowerPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("firePowerPerShipLevel")]
        public float firePowerPerAbilityLevel;
        /// <summary>Projectile speed in world units per second.</summary>
        public float bulletSpeed;
        [UnityEngine.Serialization.FormerlySerializedAs("bulletSpeedPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("bulletSpeedPerShipLevel")]
        public float bulletSpeedPerAbilityLevel;
        /// <summary>
        /// How far a bullet travels before expiring (world units). Writes <c>ShipWeaponConfig.BulletMaxDistance</c>.
        /// [TITAN-ORBIT] Ship-tier growth uses family <c>shipLevelStatGrowthFraction</c> (not this field).
        /// <see cref="bulletRangePerAbilityLevel"/> is reserved for ability / Scan authoring.
        /// </summary>
        public float bulletRange;
        [UnityEngine.Serialization.FormerlySerializedAs("bulletRangePerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("bulletRangePerShipLevel")]
        public float bulletRangePerAbilityLevel;
        /// <summary>Shots per second baseline.</summary>
        public float fireRate;
        [UnityEngine.Serialization.FormerlySerializedAs("fireRatePerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("fireRatePerShipLevel")]
        public float fireRatePerAbilityLevel;
        /// <summary>Ramming offense rating for hull collisions.</summary>
        public float rammingPower;
        [UnityEngine.Serialization.FormerlySerializedAs("rammingPowerPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("rammingPowerPerShipLevel")]
        public float rammingPowerPerAbilityLevel;
        public float healthCap;
        [UnityEngine.Serialization.FormerlySerializedAs("healthCapPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("healthCapPerShipLevel")]
        public float healthCapPerAbilityLevel;
        public float healthRegen;
        [UnityEngine.Serialization.FormerlySerializedAs("healthRegenPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("healthRegenPerShipLevel")]
        public float healthRegenPerAbilityLevel;
        public float energyCap;
        [UnityEngine.Serialization.FormerlySerializedAs("energyCapPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("energyCapPerShipLevel")]
        public float energyCapPerAbilityLevel;
        public float energyRegen;
        [UnityEngine.Serialization.FormerlySerializedAs("energyRegenPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("energyRegenPerShipLevel")]
        public float energyRegenPerAbilityLevel;
        public float moveSpeed;
        /// <summary>
        /// [TITAN-ORBIT] Bottom-HUD Move Speed ability step (additive). Each purchase adds this
        /// together with <see cref="accelerationCapPerAbilityLevel"/> and
        /// <see cref="extraSpeedEnergyDrainPerAbilityLevel"/>.
        /// </summary>
        [UnityEngine.Serialization.FormerlySerializedAs("moveSpeedPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("moveSpeedPerShipLevel")]
        public float moveSpeedPerAbilityLevel;
        /// <summary>Acceleration cap used by <see cref="ShipPropulsionAggregation"/>.</summary>
        public float accelerationCap;
        [UnityEngine.Serialization.FormerlySerializedAs("accelerationCapPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("accelerationCapPerShipLevel")]
        public float accelerationCapPerAbilityLevel;
        /// <summary>
        /// [TITAN-ORBIT] OVERDRIVE extra speed/thrust fraction on this <b>engine</b> (0.5 = +50% → 1.5×).
        /// Authored on engines only — not thrusters. Hull uses the <b>max</b> across engines for speed/thrust.
        /// </summary>
        public float extraSpeedPercent;
        /// <summary>Ability / Scan step for <see cref="extraSpeedPercent"/> (default 0 — designers opt in).</summary>
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedPercentPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedPercentPerShipLevel")]
        public float extraSpeedPercentPerAbilityLevel;
        /// <summary>
        /// [TITAN-ORBIT] Absolute OVERDRIVE energy/sec from this engine (e.g. 2 = spend 2 energy/sec).
        /// Not multiplied by <see cref="extraSpeedPercent"/>. Hull sums into
        /// <c>ShipMotorConfig.ThrustEnergyDrainPerSecond</c>. Move Speed ability adds
        /// <see cref="extraSpeedEnergyDrainPerAbilityLevel"/> per purchase.
        /// </summary>
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyPercent")]
        public float extraSpeedEnergyDrain;
        /// <summary>Bottom-HUD Move Speed ability step for OVERDRIVE energy/sec (paired with move + accel).</summary>
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyPercentPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyDrainPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyDrainPerShipLevel")]
        public float extraSpeedEnergyDrainPerAbilityLevel;
        /// <summary>Yaw turn rate in degrees per second.</summary>
        public float turnSpeed;
        [UnityEngine.Serialization.FormerlySerializedAs("turnSpeedPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("turnSpeedPerShipLevel")]
        public float turnSpeedPerAbilityLevel;
        public float maxGems;
        [UnityEngine.Serialization.FormerlySerializedAs("maxGemsPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("maxGemsPerShipLevel")]
        public float maxGemsPerAbilityLevel;
        /// <summary>Wing tractor beam reach in world units.</summary>
        public float tractorBeamDistance;
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamDistancePerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamDistancePerShipLevel")]
        public float tractorBeamDistancePerAbilityLevel;
        public float tractorBeamPower;
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamPowerPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamPowerPerShipLevel")]
        public float tractorBeamPowerPerAbilityLevel;
        public float maxPeople;
        [UnityEngine.Serialization.FormerlySerializedAs("maxPeoplePerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("maxPeoplePerShipLevel")]
        public float maxPeoplePerAbilityLevel;

        /// <summary>
        /// When multiple parts share a stack pool: the primary contributes 100% of its stats;
        /// each extra contributes this fraction of <b>its own</b> stats.
        /// Default 1 = full sum (wings, cockpits, …). Engines/Thrusters use 0.1.
        /// Unset (≤0) is resolved at aggregate time by component id.
        /// </summary>
        [Tooltip(
            "Extra stack weight: primary = 100%; each additional part in the same pool " +
            "adds this fraction of ITS stats. 1 = full sum; Engines/Thrusters = 0.1.")]
        [Min(0f)]
        public float extraStackWeight;

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
            if (allowedSet.Contains("firePowerPerAbilityLevel")) filtered.firePowerPerAbilityLevel = stats.firePowerPerAbilityLevel;
            if (allowedSet.Contains("bulletSpeed")) filtered.bulletSpeed = stats.bulletSpeed;
            if (allowedSet.Contains("bulletSpeedPerAbilityLevel")) filtered.bulletSpeedPerAbilityLevel = stats.bulletSpeedPerAbilityLevel;
            if (allowedSet.Contains("bulletRange")) filtered.bulletRange = stats.bulletRange;
            if (allowedSet.Contains("bulletRangePerAbilityLevel")) filtered.bulletRangePerAbilityLevel = stats.bulletRangePerAbilityLevel;
            if (allowedSet.Contains("fireRate")) filtered.fireRate = stats.fireRate;
            if (allowedSet.Contains("fireRatePerAbilityLevel")) filtered.fireRatePerAbilityLevel = stats.fireRatePerAbilityLevel;
            if (allowedSet.Contains("rammingPower")) filtered.rammingPower = stats.rammingPower;
            if (allowedSet.Contains("rammingPowerPerAbilityLevel")) filtered.rammingPowerPerAbilityLevel = stats.rammingPowerPerAbilityLevel;
            if (allowedSet.Contains("healthCap")) filtered.healthCap = stats.healthCap;
            if (allowedSet.Contains("healthCapPerAbilityLevel")) filtered.healthCapPerAbilityLevel = stats.healthCapPerAbilityLevel;
            if (allowedSet.Contains("healthRegen")) filtered.healthRegen = stats.healthRegen;
            if (allowedSet.Contains("healthRegenPerAbilityLevel")) filtered.healthRegenPerAbilityLevel = stats.healthRegenPerAbilityLevel;
            if (allowedSet.Contains("energyCap")) filtered.energyCap = stats.energyCap;
            if (allowedSet.Contains("energyCapPerAbilityLevel")) filtered.energyCapPerAbilityLevel = stats.energyCapPerAbilityLevel;
            if (allowedSet.Contains("energyRegen")) filtered.energyRegen = stats.energyRegen;
            if (allowedSet.Contains("energyRegenPerAbilityLevel")) filtered.energyRegenPerAbilityLevel = stats.energyRegenPerAbilityLevel;
            if (allowedSet.Contains("moveSpeed")) filtered.moveSpeed = stats.moveSpeed;
            if (allowedSet.Contains("moveSpeedPerAbilityLevel")) filtered.moveSpeedPerAbilityLevel = stats.moveSpeedPerAbilityLevel;
            if (allowedSet.Contains("accelerationCap")) filtered.accelerationCap = stats.accelerationCap;
            if (allowedSet.Contains("accelerationCapPerAbilityLevel"))
                filtered.accelerationCapPerAbilityLevel = stats.accelerationCapPerAbilityLevel;
            if (allowedSet.Contains("extraSpeedPercent")) filtered.extraSpeedPercent = stats.extraSpeedPercent;
            if (allowedSet.Contains("extraSpeedPercentPerAbilityLevel"))
                filtered.extraSpeedPercentPerAbilityLevel = stats.extraSpeedPercentPerAbilityLevel;
            if (allowedSet.Contains("extraSpeedEnergyDrain"))
                filtered.extraSpeedEnergyDrain = stats.extraSpeedEnergyDrain;
            if (allowedSet.Contains("extraSpeedEnergyDrainPerAbilityLevel"))
                filtered.extraSpeedEnergyDrainPerAbilityLevel = stats.extraSpeedEnergyDrainPerAbilityLevel;
            if (allowedSet.Contains("turnSpeed")) filtered.turnSpeed = stats.turnSpeed;
            if (allowedSet.Contains("turnSpeedPerAbilityLevel")) filtered.turnSpeedPerAbilityLevel = stats.turnSpeedPerAbilityLevel;
            if (allowedSet.Contains("maxGems")) filtered.maxGems = stats.maxGems;
            if (allowedSet.Contains("maxGemsPerAbilityLevel")) filtered.maxGemsPerAbilityLevel = stats.maxGemsPerAbilityLevel;
            if (allowedSet.Contains("tractorBeamDistance")) filtered.tractorBeamDistance = stats.tractorBeamDistance;
            if (allowedSet.Contains("tractorBeamDistancePerAbilityLevel")) filtered.tractorBeamDistancePerAbilityLevel = stats.tractorBeamDistancePerAbilityLevel;
            if (allowedSet.Contains("tractorBeamPower")) filtered.tractorBeamPower = stats.tractorBeamPower;
            if (allowedSet.Contains("tractorBeamPowerPerAbilityLevel")) filtered.tractorBeamPowerPerAbilityLevel = stats.tractorBeamPowerPerAbilityLevel;
            if (allowedSet.Contains("maxPeople")) filtered.maxPeople = stats.maxPeople;
            if (allowedSet.Contains("maxPeoplePerAbilityLevel")) filtered.maxPeoplePerAbilityLevel = stats.maxPeoplePerAbilityLevel;

            // [TITAN-ORBIT] Stack weight is meta for multi-part pools — always keep (not category-gated).
            filtered.extraStackWeight = stats.extraStackWeight;

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
