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
    /// [TITAN-ORBIT] Thruster-like mounts author move/accel/turn; engine-like mounts author
    /// move/accel + Energy Cap/Regen (power plant); Tail/Fin still author turn. Weapons author
    /// Offense plus Energy Cap only (extra battery — no Regen). Propulsion particle VFX and
    /// cosmetic covers are controlled on <see cref="ShipFamilyPartNameMapping"/>.
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
                // Propulsion — separate Engine (power plant) vs Thruster (maneuver jets)
                { "ThrustCover", ShipFamilyPartTypes.Thruster },
                { "Thrusters", ShipFamilyPartTypes.Thruster },
                { "Thrusters_Big", ShipFamilyPartTypes.Thruster },
                { "Tiny_Thrusters", ShipFamilyPartTypes.Thruster },
                { "Thruster_Place", ShipFamilyPartTypes.Thruster },
                { "Exhaust", ShipFamilyPartTypes.Thruster },
                { "EngineComp1", ShipFamilyPartTypes.Engine },
                { "EngineComp2", ShipFamilyPartTypes.Engine },
                { "Engine_1", ShipFamilyPartTypes.Engine },
                { "Engine_2", ShipFamilyPartTypes.Engine },
                { "Engine1", ShipFamilyPartTypes.Engine },
                { "Engine2", ShipFamilyPartTypes.Engine },
                { "Engine", ShipFamilyPartTypes.Engine },
                { "Thruster", ShipFamilyPartTypes.Thruster },
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

        static readonly string[] RammingOffenseFields = { "rammingPower", "rammingPowerPerExtraLevel" };
        static readonly string[] WeaponOffenseFields =
        {
            "firePower", "firePowerPerExtraLevel", "bulletSpeed", "bulletSpeedPerExtraLevel",
            "bulletRange", "bulletRangePerExtraLevel",
            "fireRate", "fireRatePerExtraLevel"
        };
        static readonly string[] HealthFields =
            { "healthCap", "healthCapPerExtraLevel", "healthRegen", "healthRegenPerExtraLevel" };
        static readonly string[] EnergyFields =
            { "energyCap", "energyCapPerExtraLevel", "energyRegen", "energyRegenPerExtraLevel" };
        /// <summary>
        /// [TITAN-ORBIT] Weapon Energy category — Cap only (battery / magazine). Engines own Regen.
        /// </summary>
        static readonly string[] WeaponEnergyCapFields =
            { "energyCap", "energyCapPerExtraLevel" };
        /// <summary>Engine-like movement — move + accel + OVERDRIVE knobs (power plant; OD drain = esp × esep).</summary>
        static readonly string[] PropulsionMovementFields =
        {
            "moveSpeed", "moveSpeedPerExtraLevel",
            "accelerationCap", "accelerationCapPerExtraLevel",
            "extraSpeedPercent", "extraSpeedPercentPerExtraLevel",
            "extraSpeedEnergyDrain", "extraSpeedEnergyDrainPerExtraLevel"
        };
        /// <summary>Thruster-like movement — move + accel + turn (no OVERDRIVE knobs; engines own those).</summary>
        static readonly string[] ThrusterMovementFields =
        {
            "moveSpeed", "moveSpeedPerExtraLevel",
            "accelerationCap", "accelerationCapPerExtraLevel",
            "turnSpeed", "turnSpeedPerExtraLevel"
        };
        static readonly string[] TurnMovementFields = { "turnSpeed", "turnSpeedPerExtraLevel" };
        static readonly string[] CapacityFields =
            { "maxGems", "maxGemsPerExtraLevel", "maxPeople", "maxPeoplePerExtraLevel" };
        static readonly string[] WingCapacityFields =
        {
            "maxGems", "maxGemsPerExtraLevel",
            "tractorBeamDistance", "tractorBeamDistancePerExtraLevel",
            "tractorBeamPower", "tractorBeamPowerPerExtraLevel",
            "maxPeople", "maxPeoplePerExtraLevel"
        };

        /// <summary>Strips version suffixes: Wing1 → Wing, Wing_3 → Wing, MainBody4 → MainBody.</summary>
        public static string GetBasePartKey(string componentId)
        {
            string s = ShipFamilyDefinition.NormalizeComponentId(componentId);
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            if (AliasToCanonical.TryGetValue(s, out string alias))
                return alias;

            // Family-prefixed ids: AstroEagle_Wing_1 → try Wing_1 aliases first.
            int firstUnderscore = s.IndexOf('_');
            if (firstUnderscore > 0 && firstUnderscore < s.Length - 1)
            {
                string suffix = s.Substring(firstUnderscore + 1);
                if (AliasToCanonical.TryGetValue(suffix, out alias))
                    return alias;
            }

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
            if (AliasToCanonical.TryGetValue(s, out string alias))
                return alias;

            int firstUnderscore = s.IndexOf('_');
            if (firstUnderscore > 0 && firstUnderscore < s.Length - 1)
            {
                string suffix = s.Substring(firstUnderscore + 1);
                if (AliasToCanonical.TryGetValue(suffix, out alias))
                    return alias;
            }

            return s;
        }

        /// <summary>
        /// Default stat categories from a component id (resolves part type via keywords / aliases).
        /// Prefer <see cref="InferDefaultStatCategoriesForPartType"/> when the ProfileSet mapping
        /// already resolved the part type — that avoids mis-classifying ids like "Part_1".
        /// </summary>
        public static List<ShipComponentStatCategory> InferDefaultStatCategories(string componentId)
        {
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            partType = ShipFamilyPartTypes.Normalize(partType, componentId);
            return InferDefaultStatCategoriesForPartType(partType, componentId);
        }

        /// <summary>
        /// Default stat categories for an already-resolved Part Profile group
        /// (Engine, Wing, Weapon Bullet, …). Uses <paramref name="componentId"/> only for
        /// engine-vs-thruster name heuristics when the type label is ambiguous.
        /// </summary>
        /// <param name="partType">Canonical or legacy part type from ProfileSet / Scan.</param>
        /// <param name="componentId">Optional mount id for Engine vs Thruster name checks.</param>
        public static List<ShipComponentStatCategory> InferDefaultStatCategoriesForPartType(
            string partType,
            string componentId = null)
        {
            partType = ShipFamilyPartTypes.Normalize(
                string.IsNullOrEmpty(partType) ? string.Empty : partType,
                componentId);

            if (string.Equals(partType, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase))
            {
                return new List<ShipComponentStatCategory>
                {
                    ShipComponentStatCategory.Offense,
                    ShipComponentStatCategory.Health,
                    ShipComponentStatCategory.Capacity
                };
            }

            // [TITAN-ORBIT] Engines = Movement + Energy (power plant). Thrusters = Movement only
            // (move/accel/turn fields). Tail/Fin = Movement (turn fields only).
            if (ShipFamilyPartTypes.IsEngineProfile(partType)
                || (!string.IsNullOrEmpty(componentId) && ShipFamilyPartTypes.IsEngineLikeName(componentId)
                    && !ShipFamilyPartTypes.IsThrusterProfile(partType)
                    && !ShipFamilyPartTypes.IsTurn(partType)
                    && !ShipFamilyPartTypes.IsWeapon(partType)))
            {
                return new List<ShipComponentStatCategory>
                {
                    ShipComponentStatCategory.Movement,
                    ShipComponentStatCategory.Energy
                };
            }

            if (ShipFamilyPartTypes.IsThrusterProfile(partType)
                || ShipFamilyPartTypes.IsPropulsion(partType)
                || ShipFamilyPartTypes.IsTurn(partType)
                || (!string.IsNullOrEmpty(componentId) && ShipFamilyPartTypes.IsThrusterLikeName(componentId)))
            {
                return new List<ShipComponentStatCategory> { ShipComponentStatCategory.Movement };
            }

            if (string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
            {
                return new List<ShipComponentStatCategory>
                {
                    ShipComponentStatCategory.Health,
                    ShipComponentStatCategory.Capacity
                };
            }

            // [TITAN-ORBIT] Weapons: Offense + Energy Cap (battery). Engines own Cap+Regen production.
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
                    // Weapons: Cap only. Engines / other Energy mounts: Cap + Regen.
                    if (ShipFamilyPartTypes.IsWeapon(partType)
                        || ShipComponentAbilityStats.IsWeaponComponent(componentId))
                        return WeaponEnergyCapFields;
                    return EnergyFields;
                case ShipComponentStatCategory.Movement:
                    // [TITAN-ORBIT] Tail/Fin = turn only; Thruster profile = move/accel/turn; Engine = move/accel.
                    partType = ShipFamilyPartTypes.Normalize(partType, componentId);
                    if (ShipFamilyPartTypes.IsTurn(partType))
                        return TurnMovementFields;
                    if (ShipFamilyPartTypes.IsThrusterProfile(partType)
                        || ShipFamilyPartTypes.IsThrusterLikeName(componentId))
                        return ThrusterMovementFields;
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

        /// <summary>True when <paramref name="categories"/> already lists <paramref name="category"/>.</summary>
        public static bool ContainsStatCategory(
            IReadOnlyList<ShipComponentStatCategory> categories,
            ShipComponentStatCategory category)
        {
            if (categories == null)
                return false;
            for (int i = 0; i < categories.Count; i++)
            {
                if (categories[i] == category)
                    return true;
            }

            return false;
        }
    }
}
