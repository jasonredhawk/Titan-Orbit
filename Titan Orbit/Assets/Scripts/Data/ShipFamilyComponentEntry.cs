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
    /// <c>*PerExtraLevel</c> steps used by the unified Extra Level formula.
    /// Non-weapons: <c>Base + PerExtraLevel × ((shipLevel−1) + abilityLevel + (N−1))</c>.
    /// Weapons (each barrel): <c>Base + PerExtraLevel × ((shipLevel−1) + abilityLevel)</c>.
    /// Non-weapon pools use primary-per-pool aggregation; weapons fire per-mount.
    /// </summary>
    [Serializable]
    public struct ShipComponentAbilityStats
    {
        /// <summary>Damage per shot before Extra Level scaling (primary weapon; hull may divide by gun count).</summary>
        public float firePower;
        /// <summary>
        /// [TITAN-ORBIT] Fire Power Per Extra Level — scales with ship level, Fire Power ability
        /// purchases, and weapon component count (see <see cref="ShipComponentExtraLevelMath"/>).
        /// </summary>
        [UnityEngine.Serialization.FormerlySerializedAs("firePowerPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("firePowerPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("firePowerPerShipLevel")]
        public float firePowerPerExtraLevel;
        /// <summary>
        /// Projectile speed in world units per second.
        /// [TITAN-ORBIT] On weapons: <c>Base + PerExtra × abilityLevel</c> only (no ship level, no N).
        /// </summary>
        public float bulletSpeed;
        [UnityEngine.Serialization.FormerlySerializedAs("bulletSpeedPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("bulletSpeedPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("bulletSpeedPerShipLevel")]
        public float bulletSpeedPerExtraLevel;
        /// <summary>
        /// How far a bullet travels before expiring (world units). Writes <c>ShipWeaponConfig.BulletMaxDistance</c>.
        /// Scaled by Extra Level formula (no divide-by-gun-count).
        /// </summary>
        public float bulletRange;
        [UnityEngine.Serialization.FormerlySerializedAs("bulletRangePerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("bulletRangePerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("bulletRangePerShipLevel")]
        public float bulletRangePerExtraLevel;
        /// <summary>Shots per second baseline.</summary>
        public float fireRate;
        [UnityEngine.Serialization.FormerlySerializedAs("fireRatePerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("fireRatePerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("fireRatePerShipLevel")]
        public float fireRatePerExtraLevel;
        /// <summary>Ramming offense rating for hull collisions.</summary>
        public float rammingPower;
        [UnityEngine.Serialization.FormerlySerializedAs("rammingPowerPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("rammingPowerPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("rammingPowerPerShipLevel")]
        public float rammingPowerPerExtraLevel;
        public float healthCap;
        [UnityEngine.Serialization.FormerlySerializedAs("healthCapPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("healthCapPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("healthCapPerShipLevel")]
        public float healthCapPerExtraLevel;
        public float healthRegen;
        [UnityEngine.Serialization.FormerlySerializedAs("healthRegenPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("healthRegenPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("healthRegenPerShipLevel")]
        public float healthRegenPerExtraLevel;
        public float energyCap;
        [UnityEngine.Serialization.FormerlySerializedAs("energyCapPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("energyCapPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("energyCapPerShipLevel")]
        public float energyCapPerExtraLevel;
        public float energyRegen;
        [UnityEngine.Serialization.FormerlySerializedAs("energyRegenPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("energyRegenPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("energyRegenPerShipLevel")]
        public float energyRegenPerExtraLevel;
        public float moveSpeed;
        /// <summary>
        /// [TITAN-ORBIT] Move Speed Per Extra Level — scales cruise speed with ship level,
        /// Move Speed ability purchases, and propulsion component count.
        /// </summary>
        [UnityEngine.Serialization.FormerlySerializedAs("moveSpeedPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("moveSpeedPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("moveSpeedPerShipLevel")]
        public float moveSpeedPerExtraLevel;
        /// <summary>Acceleration cap used by <see cref="ShipPropulsionAggregation"/>.</summary>
        public float accelerationCap;
        [UnityEngine.Serialization.FormerlySerializedAs("accelerationCapPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("accelerationCapPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("accelerationCapPerShipLevel")]
        public float accelerationCapPerExtraLevel;
        /// <summary>
        /// [TITAN-ORBIT] OVERDRIVE extra speed/thrust fraction on this <b>engine</b> (0.5 = +50% → 1.5×).
        /// Authored on engines only — not thrusters. Hull uses the <b>max</b> across engines for speed/thrust.
        /// </summary>
        public float extraSpeedPercent;
        /// <summary>Per Extra Level step for <see cref="extraSpeedPercent"/> (default 0 — designers opt in).</summary>
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedPercentPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedPercentPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedPercentPerShipLevel")]
        public float extraSpeedPercentPerExtraLevel;
        /// <summary>
        /// [TITAN-ORBIT] Absolute OVERDRIVE energy/sec from this engine (e.g. 2 = spend 2 energy/sec).
        /// Not multiplied by <see cref="extraSpeedPercent"/>. Hull sums into
        /// <c>ShipMotorConfig.ThrustEnergyDrainPerSecond</c>.
        /// </summary>
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyPercent")]
        public float extraSpeedEnergyDrain;
        /// <summary>Per Extra Level step for OVERDRIVE energy/sec (paired with move + accel).</summary>
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyDrainPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyPercentPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyDrainPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("extraSpeedEnergyDrainPerShipLevel")]
        public float extraSpeedEnergyDrainPerExtraLevel;
        /// <summary>Yaw turn rate in degrees per second.</summary>
        public float turnSpeed;
        [UnityEngine.Serialization.FormerlySerializedAs("turnSpeedPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("turnSpeedPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("turnSpeedPerShipLevel")]
        public float turnSpeedPerExtraLevel;
        public float maxGems;
        [UnityEngine.Serialization.FormerlySerializedAs("maxGemsPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("maxGemsPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("maxGemsPerShipLevel")]
        public float maxGemsPerExtraLevel;
        /// <summary>Wing tractor beam reach in world units.</summary>
        public float tractorBeamDistance;
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamDistancePerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamDistancePerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamDistancePerShipLevel")]
        public float tractorBeamDistancePerExtraLevel;
        public float tractorBeamPower;
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamPowerPerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamPowerPerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("tractorBeamPowerPerShipLevel")]
        public float tractorBeamPowerPerExtraLevel;
        public float maxPeople;
        [UnityEngine.Serialization.FormerlySerializedAs("maxPeoplePerAbilityLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("maxPeoplePerLevel")]
        [UnityEngine.Serialization.FormerlySerializedAs("maxPeoplePerShipLevel")]
        public float maxPeoplePerExtraLevel;

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
            if (allowedSet.Contains("firePowerPerExtraLevel")) filtered.firePowerPerExtraLevel = stats.firePowerPerExtraLevel;
            if (allowedSet.Contains("bulletSpeed")) filtered.bulletSpeed = stats.bulletSpeed;
            if (allowedSet.Contains("bulletSpeedPerExtraLevel")) filtered.bulletSpeedPerExtraLevel = stats.bulletSpeedPerExtraLevel;
            if (allowedSet.Contains("bulletRange")) filtered.bulletRange = stats.bulletRange;
            if (allowedSet.Contains("bulletRangePerExtraLevel")) filtered.bulletRangePerExtraLevel = stats.bulletRangePerExtraLevel;
            if (allowedSet.Contains("fireRate")) filtered.fireRate = stats.fireRate;
            if (allowedSet.Contains("fireRatePerExtraLevel")) filtered.fireRatePerExtraLevel = stats.fireRatePerExtraLevel;
            if (allowedSet.Contains("rammingPower")) filtered.rammingPower = stats.rammingPower;
            if (allowedSet.Contains("rammingPowerPerExtraLevel")) filtered.rammingPowerPerExtraLevel = stats.rammingPowerPerExtraLevel;
            if (allowedSet.Contains("healthCap")) filtered.healthCap = stats.healthCap;
            if (allowedSet.Contains("healthCapPerExtraLevel")) filtered.healthCapPerExtraLevel = stats.healthCapPerExtraLevel;
            if (allowedSet.Contains("healthRegen")) filtered.healthRegen = stats.healthRegen;
            if (allowedSet.Contains("healthRegenPerExtraLevel")) filtered.healthRegenPerExtraLevel = stats.healthRegenPerExtraLevel;
            if (allowedSet.Contains("energyCap")) filtered.energyCap = stats.energyCap;
            if (allowedSet.Contains("energyCapPerExtraLevel")) filtered.energyCapPerExtraLevel = stats.energyCapPerExtraLevel;
            if (allowedSet.Contains("energyRegen")) filtered.energyRegen = stats.energyRegen;
            if (allowedSet.Contains("energyRegenPerExtraLevel")) filtered.energyRegenPerExtraLevel = stats.energyRegenPerExtraLevel;
            if (allowedSet.Contains("moveSpeed")) filtered.moveSpeed = stats.moveSpeed;
            if (allowedSet.Contains("moveSpeedPerExtraLevel")) filtered.moveSpeedPerExtraLevel = stats.moveSpeedPerExtraLevel;
            if (allowedSet.Contains("accelerationCap")) filtered.accelerationCap = stats.accelerationCap;
            if (allowedSet.Contains("accelerationCapPerExtraLevel"))
                filtered.accelerationCapPerExtraLevel = stats.accelerationCapPerExtraLevel;
            if (allowedSet.Contains("extraSpeedPercent")) filtered.extraSpeedPercent = stats.extraSpeedPercent;
            if (allowedSet.Contains("extraSpeedPercentPerExtraLevel"))
                filtered.extraSpeedPercentPerExtraLevel = stats.extraSpeedPercentPerExtraLevel;
            if (allowedSet.Contains("extraSpeedEnergyDrain"))
                filtered.extraSpeedEnergyDrain = stats.extraSpeedEnergyDrain;
            if (allowedSet.Contains("extraSpeedEnergyDrainPerExtraLevel"))
                filtered.extraSpeedEnergyDrainPerExtraLevel = stats.extraSpeedEnergyDrainPerExtraLevel;
            if (allowedSet.Contains("turnSpeed")) filtered.turnSpeed = stats.turnSpeed;
            if (allowedSet.Contains("turnSpeedPerExtraLevel")) filtered.turnSpeedPerExtraLevel = stats.turnSpeedPerExtraLevel;
            if (allowedSet.Contains("maxGems")) filtered.maxGems = stats.maxGems;
            if (allowedSet.Contains("maxGemsPerExtraLevel")) filtered.maxGemsPerExtraLevel = stats.maxGemsPerExtraLevel;
            if (allowedSet.Contains("tractorBeamDistance")) filtered.tractorBeamDistance = stats.tractorBeamDistance;
            if (allowedSet.Contains("tractorBeamDistancePerExtraLevel")) filtered.tractorBeamDistancePerExtraLevel = stats.tractorBeamDistancePerExtraLevel;
            if (allowedSet.Contains("tractorBeamPower")) filtered.tractorBeamPower = stats.tractorBeamPower;
            if (allowedSet.Contains("tractorBeamPowerPerExtraLevel")) filtered.tractorBeamPowerPerExtraLevel = stats.tractorBeamPowerPerExtraLevel;
            if (allowedSet.Contains("maxPeople")) filtered.maxPeople = stats.maxPeople;
            if (allowedSet.Contains("maxPeoplePerExtraLevel")) filtered.maxPeoplePerExtraLevel = stats.maxPeoplePerExtraLevel;

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
        /// <summary>Authoritative ability numbers for this part before Extra Level scaling.</summary>
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
