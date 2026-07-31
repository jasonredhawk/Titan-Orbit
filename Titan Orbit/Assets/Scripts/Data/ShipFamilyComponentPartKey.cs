using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Parses ship-family component ids into canonical part types and decides which ability-stat
    /// fields are authored for each Stat Category × part type. Used by the ShipFamilyDefinition
    /// Inspector (filter visible fields) and by Scan / Populate when writing component stats.
    /// <para>
    /// [TITAN-ORBIT] Thrusters author move/accel only — turn speed lives on Fin/Tail.
    /// Propulsion particle VFX and cosmetic covers (stats off, still in Thruster scale group) are
    /// controlled on <see cref="ShipFamilyPartNameMapping"/>.
    /// </para>
    /// </summary>
    public static class ShipFamilyComponentPartKey
    {
        static readonly Regex TrailingDigitsRegex = new Regex(@"\d+$", RegexOptions.Compiled);

        /// <summary>
        /// Related part names that share the same mapping key
        /// (e.g. ThrustCover → Thruster, WingHolder → Wing).
        /// </summary>
        static readonly Dictionary<string, string> AliasToCanonical =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Propulsion → Engine/Thrust group
                { "ThrustCover", ShipFamilyPartTypes.Engine },
                { "Thrusters", ShipFamilyPartTypes.Engine },
                { "Thrusters_Big", ShipFamilyPartTypes.Engine },
                { "Tiny_Thrusters", ShipFamilyPartTypes.Engine },
                { "Thruster_Place", ShipFamilyPartTypes.Engine },
                { "Exhaust", ShipFamilyPartTypes.Engine },
                { "EngineComp1", ShipFamilyPartTypes.Engine },
                { "EngineComp2", ShipFamilyPartTypes.Engine },
                { "Engine_1", ShipFamilyPartTypes.Engine },
                { "Engine_2", ShipFamilyPartTypes.Engine },
                { "Engine1", ShipFamilyPartTypes.Engine },
                { "Engine2", ShipFamilyPartTypes.Engine },
                // Weapons — rapid small vs heavy slow
                { "Gun", ShipFamilyPartTypes.WeaponBullet },
                { "Machinegun", ShipFamilyPartTypes.WeaponBullet },
                { "Barrel", ShipFamilyPartTypes.WeaponBullet },
                { "Ammunition", ShipFamilyPartTypes.WeaponBullet },
                { "Missile", ShipFamilyPartTypes.WeaponCannon },
                { "Missile_Launcher", ShipFamilyPartTypes.WeaponCannon },
                // Wings / cockpit
                { "WingHolder", ShipFamilyPartTypes.Wing },
                { "Small_Wing", ShipFamilyPartTypes.Wing },
                { "Tiny_Wing", ShipFamilyPartTypes.Wing },
                { "WingMain", ShipFamilyPartTypes.Wing },
                { "WingMini", ShipFamilyPartTypes.Wing },
                { "WingTip", ShipFamilyPartTypes.Wing },
                { "WingWide", ShipFamilyPartTypes.Wing },
                { "Cockpit_Base", ShipFamilyPartTypes.Cockpit },
                { "Cockpit_Base_1", ShipFamilyPartTypes.Cockpit },
                { "Cockpit_Base_2", ShipFamilyPartTypes.Cockpit },
                { "CockpitCover", ShipFamilyPartTypes.Cockpit },
                // Hull catch-all
                { "MainBody1", ShipFamilyPartTypes.Hull },
                { "MainBody2", ShipFamilyPartTypes.Hull },
                { "MainBody3", ShipFamilyPartTypes.Hull },
                { "MainBody4", ShipFamilyPartTypes.Hull },
                { "Body_01", ShipFamilyPartTypes.Hull },
                { "Body_02", ShipFamilyPartTypes.Hull },
                { "Body_03", ShipFamilyPartTypes.Hull },
                { "Body1", ShipFamilyPartTypes.Hull },
                { "Body2", ShipFamilyPartTypes.Hull },
                { "Armor_01", ShipFamilyPartTypes.Hull },
                { "Armor_02", ShipFamilyPartTypes.Hull },
                { "Part_1", ShipFamilyPartTypes.Hull },
                { "Part_2", ShipFamilyPartTypes.Hull },
                { "Acc", ShipFamilyPartTypes.Hull },
                { "Wing_01", ShipFamilyPartTypes.Wing },
                { "Wing_02", ShipFamilyPartTypes.Wing },
                { "Wing_03", ShipFamilyPartTypes.Wing },
                { "Wing_1", ShipFamilyPartTypes.Wing },
                { "Wing_2", ShipFamilyPartTypes.Wing },
                { "Wing_3", ShipFamilyPartTypes.Wing },
                { "Wing_4", ShipFamilyPartTypes.Wing },
                { "Wing_5", ShipFamilyPartTypes.Wing },
                { "Wing1", ShipFamilyPartTypes.Wing },
                { "Wing2", ShipFamilyPartTypes.Wing },
                { "Wing3", ShipFamilyPartTypes.Wing },
                { "Wing4", ShipFamilyPartTypes.Wing },
            };

        static readonly ShipComponentStatCategory[] CategoryDisplayOrder =
        {
            ShipComponentStatCategory.Offense,
            ShipComponentStatCategory.Health,
            ShipComponentStatCategory.Energy,
            ShipComponentStatCategory.Movement,
            ShipComponentStatCategory.Capacity
        };

        static readonly string[] RammingOffenseFields = { "rammingPower", "rammingPowerPerLevel" };
        static readonly string[] WeaponOffenseFields =
        {
            "firePower", "firePowerPerLevel", "bulletSpeed", "bulletSpeedPerLevel",
            "fireRate", "fireRatePerLevel"
        };
        static readonly string[] HealthFields =
            { "healthCap", "healthCapPerLevel", "healthRegen", "healthRegenPerLevel" };
        static readonly string[] EnergyFields =
            { "energyCap", "energyCapPerLevel", "energyRegen", "energyRegenPerLevel" };
        /// <summary>Engine / thruster movement — move + accel only (no turn).</summary>
        static readonly string[] PropulsionMovementFields =
            { "moveSpeed", "moveSpeedPerLevel", "accelerationCap", "accelerationCapPerLevel" };
        static readonly string[] TurnMovementFields = { "turnSpeed", "turnSpeedPerLevel" };
        static readonly string[] CapacityFields =
            { "maxGems", "maxGemsPerLevel", "maxPeople", "maxPeoplePerLevel" };
        static readonly string[] WingCapacityFields =
        {
            "maxGems", "maxGemsPerLevel",
            "tractorBeamDistance", "tractorBeamDistancePerLevel",
            "tractorBeamPower", "tractorBeamPowerPerLevel",
            "maxPeople", "maxPeoplePerLevel"
        };

        /// <summary>Strips version suffixes: Wing1 → Wing, Wing_3 → Wing, MainBody4 → MainBody.</summary>
        public static string GetBasePartKey(string componentId)
        {
            string s = ShipFamilyDefinition.NormalizeComponentId(componentId);
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            if (AliasToCanonical.TryGetValue(s, out string alias))
                return alias;

            string[] segments = s.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && TrailingDigitsRegex.IsMatch(segments[segments.Length - 1]))
            {
                var trimmed = new List<string>(segments.Length - 1);
                for (int i = 0; i < segments.Length - 1; i++)
                    trimmed.Add(segments[i]);
                string joined = string.Join("_", trimmed);
                if (!string.IsNullOrEmpty(joined))
                    return joined;
            }

            string withoutDigits = TrailingDigitsRegex.Replace(s, string.Empty);
            return string.IsNullOrEmpty(withoutDigits) ? s : withoutDigits;
        }

        /// <summary>Returns the canonical related-part key when one exists (ThrustCover → Thruster).</summary>
        public static string ResolveAliasKey(string componentId)
        {
            string s = ShipFamilyDefinition.NormalizeComponentId(componentId);
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return AliasToCanonical.TryGetValue(s, out string alias) ? alias : s;
        }

        /// <summary>Default stat categories from part keywords when scanning or migrating component entries.</summary>
        public static List<ShipComponentStatCategory> InferDefaultStatCategories(string componentId)
        {
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            partType = ShipFamilyPartTypes.Normalize(partType, componentId);

            if (string.Equals(partType, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase))
            {
                return new List<ShipComponentStatCategory>
                {
                    ShipComponentStatCategory.Offense,
                    ShipComponentStatCategory.Health,
                    ShipComponentStatCategory.Capacity
                };
            }

            if (ShipFamilyPartTypes.IsPropulsion(partType) || ShipFamilyPartTypes.IsTurn(partType))
                return new List<ShipComponentStatCategory> { ShipComponentStatCategory.Movement };

            if (string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
            {
                return new List<ShipComponentStatCategory>
                {
                    ShipComponentStatCategory.Health,
                    ShipComponentStatCategory.Capacity
                };
            }

            if (ShipFamilyPartTypes.IsWeapon(partType))
            {
                return new List<ShipComponentStatCategory>
                {
                    ShipComponentStatCategory.Offense,
                    ShipComponentStatCategory.Energy
                };
            }

            // Hull and unknown — health only (mass comes from hierarchy scale).
            return new List<ShipComponentStatCategory> { ShipComponentStatCategory.Health };
        }

        /// <summary>First default category (legacy / CSV export).</summary>
        public static ShipComponentStatCategory InferDefaultStatCategory(string componentId)
        {
            var categories = InferDefaultStatCategories(componentId);
            return categories.Count > 0 ? categories[0] : ShipComponentStatCategory.Health;
        }

        /// <summary>Stat fields shown and stored for a component based on category and part id.</summary>
        public static string[] GetAuthoringStatFieldNames(ShipComponentStatCategory category, string componentId)
        {
            string partType = ShipFamilyPartTypes.Normalize(
                ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId), componentId);
            switch (category)
            {
                case ShipComponentStatCategory.Offense:
                    return string.Equals(partType, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase)
                        ? RammingOffenseFields
                        : WeaponOffenseFields;
                case ShipComponentStatCategory.Health:
                    return HealthFields;
                case ShipComponentStatCategory.Energy:
                    return EnergyFields;
                case ShipComponentStatCategory.Movement:
                    // [TITAN-ORBIT] Engine/Thrust = move/accel; Tail (incl. Fin) = turn only.
                    partType = ShipFamilyPartTypes.Normalize(partType, componentId);
                    if (ShipFamilyPartTypes.IsTurn(partType))
                        return TurnMovementFields;
                    if (ShipFamilyPartTypes.IsPropulsion(partType))
                        return PropulsionMovementFields;
                    return PropulsionMovementFields;
                case ShipComponentStatCategory.Capacity:
                    partType = ShipFamilyPartTypes.Normalize(partType, componentId);
                    if (string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
                        return WingCapacityFields;
                    return CapacityFields;
                default:
                    return HealthFields;
            }
        }

        /// <summary>Union of stat fields for all assigned categories (stable display order).</summary>
        public static string[] GetAuthoringStatFieldNames(
            IReadOnlyList<ShipComponentStatCategory> categories,
            string componentId)
        {
            if (categories == null || categories.Count == 0)
                return GetAuthoringStatFieldNames(ShipComponentStatCategory.Health, componentId);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            for (int i = 0; i < CategoryDisplayOrder.Length; i++)
            {
                ShipComponentStatCategory category = CategoryDisplayOrder[i];
                if (!ContainsStatCategory(categories, category))
                    continue;

                string[] fields = GetAuthoringStatFieldNames(category, componentId);
                for (int f = 0; f < fields.Length; f++)
                {
                    if (seen.Add(fields[f]))
                        ordered.Add(fields[f]);
                }
            }

            return ordered.ToArray();
        }

        /// <summary>True when any assigned category is offense and the part is a weapon (not cockpit).</summary>
        public static bool ShouldShowBulletPrefabIndex(
            IReadOnlyList<ShipComponentStatCategory> categories,
            string componentId)
        {
            if (categories == null || categories.Count == 0)
                return false;

            for (int i = 0; i < categories.Count; i++)
            {
                if (ShouldShowBulletPrefabIndex(categories[i], componentId))
                    return true;
            }

            return false;
        }

        /// <summary>True when the offense category component should expose bullet prefab index (weapons only).</summary>
        public static bool ShouldShowBulletPrefabIndex(ShipComponentStatCategory category, string componentId)
        {
            if (category != ShipComponentStatCategory.Offense)
                return false;
            return !string.Equals(
                ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId),
                "Cockpit",
                StringComparison.OrdinalIgnoreCase);
        }

        static bool ContainsStatCategory(
            IReadOnlyList<ShipComponentStatCategory> categories,
            ShipComponentStatCategory category)
        {
            for (int i = 0; i < categories.Count; i++)
            {
                if (categories[i] == category)
                    return true;
            }

            return false;
        }
    }
}
