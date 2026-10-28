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
    /// [TITAN-ORBIT] Thruster-like mounts author Accel + turn; engine-like mounts author
    /// Move Speed + Energy Cap/Regen (power plant); Tail/Fin still author turn. Weapons author
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
                // Weapons — one family profile, whatever the prefab calls the barrel
                { "Gun", ShipFamilyPartTypes.WeaponBullet },
                { "Machinegun", ShipFamilyPartTypes.WeaponBullet },
                { "Barrel", ShipFamilyPartTypes.WeaponBullet },
                { "Cannon", ShipFamilyPartTypes.WeaponBullet },
                { "Missile", ShipFamilyPartTypes.WeaponBullet },
                { "Missile_Launcher", ShipFamilyPartTypes.WeaponBullet },
                { "Rocket", ShipFamilyPartTypes.WeaponBullet },
                { "Launcher", ShipFamilyPartTypes.WeaponBullet },
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
                // Cargo holds
                { "Cargo", ShipFamilyPartTypes.Cargo },
                { "Hold", ShipFamilyPartTypes.Cargo },
                { "Storage", ShipFamilyPartTypes.Cargo },
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

        /// <summary>
        /// Every offense stat. Cockpits and weapons share this list.
        /// Part type does not drop fire power, bullet speed, range, rate, or ramming.
        /// Scan seeds stay narrower: a cockpit writes ramming only; a weapon focuses on bullet speed.
        /// </summary>
        static readonly string[] OffenseFields =
        {
            "firePower", "firePowerPerExtraLevel",
            "bulletSpeed", "bulletSpeedPerExtraLevel",
            "bulletRange", "bulletRangePerExtraLevel",
            "fireRate", "fireRatePerExtraLevel",
            "rammingPower", "rammingPowerPerExtraLevel"
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
        /// <summary>Engine-like movement — Move Speed + OVERDRIVE knobs (power plant). Accel is thrusters only.</summary>
        static readonly string[] PropulsionMovementFields =
        {
            "moveSpeed", "moveSpeedPerExtraLevel",
            "extraSpeedPercent", "extraSpeedPercentPerExtraLevel",
            "extraSpeedEnergyDrain", "extraSpeedEnergyDrainPerExtraLevel"
        };
        /// <summary>Thruster-like movement — Acceleration + turn (no OVERDRIVE knobs; engines own those).</summary>
        static readonly string[] ThrusterMovementFields =
        {
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

            // [TITAN-ORBIT] Engines = Movement + Energy (power plant, Move Speed). Thrusters =
            // Movement only (Accel + turn). Tail/Fin = Movement (turn fields only).
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

            if (string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase)
                || ShipFamilyPartTypes.IsCargoProfile(partType))
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

        /// <summary>
        /// Stat fields shown and stored for a component based on category and part id.
        /// Offense always returns <see cref="OffenseFields"/> — fire power stays on the list.
        /// Extra part types on the name mapping add that profile's fields for this category.
        /// </summary>
        public static string[] GetAuthoringStatFieldNames(ShipComponentStatCategory category, string componentId)
        {
            string[] named = FieldsForResolvedName(category, componentId);
            IReadOnlyList<string> extra = AdditionalPartTypesFor(componentId);
            if (extra == null || extra.Count == 0)
                return named;

            var seen = new HashSet<string>(named, StringComparer.Ordinal);
            var ordered = new List<string>(named);
            AppendAdditionalPartTypeFields(seen, ordered, new[] { category }, extra);
            return ordered.Count == named.Length ? named : ordered.ToArray();
        }

        /// <summary>Name-resolved fields only. Does not read extra part types.</summary>
        static string[] FieldsForResolvedName(ShipComponentStatCategory category, string componentId)
        {
            string partType = ShipFamilyPartTypes.Normalize(
                ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId), componentId);
            switch (category)
            {
                case ShipComponentStatCategory.Offense:
                    return OffenseFields;
                case ShipComponentStatCategory.Health:
                    return HealthFields;
                case ShipComponentStatCategory.Energy:
                    // Weapons: Cap only. Engines and other Energy mounts: Cap + Regen.
                    if (ShipFamilyPartTypes.IsWeapon(partType)
                        || ShipComponentAbilityStats.IsWeaponComponent(componentId))
                        return WeaponEnergyCapFields;
                    return EnergyFields;
                case ShipComponentStatCategory.Movement:
                    // [TITAN-ORBIT] Tail/Fin = turn only; Thruster = Accel + turn; Engine = Move + OVERDRIVE.
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

        /// <summary>
        /// Stat fields for one Part Profile, ignoring the part's name.
        /// Thruster stays accel + turn even when the prefab id says Engine.
        /// </summary>
        public static string[] FieldsForExplicitPartType(ShipComponentStatCategory category, string partType)
        {
            partType = ShipFamilyPartTypes.Normalize(
                string.IsNullOrEmpty(partType) ? string.Empty : partType,
                null);
            switch (category)
            {
                case ShipComponentStatCategory.Offense:
                    return OffenseFields;
                case ShipComponentStatCategory.Health:
                    return HealthFields;
                case ShipComponentStatCategory.Energy:
                    return ShipFamilyPartTypes.IsWeapon(partType) ? WeaponEnergyCapFields : EnergyFields;
                case ShipComponentStatCategory.Movement:
                    if (ShipFamilyPartTypes.IsTurn(partType))
                        return TurnMovementFields;
                    if (ShipFamilyPartTypes.IsThrusterProfile(partType))
                        return ThrusterMovementFields;
                    return PropulsionMovementFields;
                case ShipComponentStatCategory.Capacity:
                    if (string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
                        return WingCapacityFields;
                    return CapacityFields;
                default:
                    return HealthFields;
            }
        }

        /// <summary>Every field one Part Profile authors, in display order.</summary>
        public static string[] GetAuthoringStatFieldNamesForExplicitPartType(string partType)
        {
            partType = ShipFamilyPartTypes.Normalize(
                string.IsNullOrEmpty(partType) ? string.Empty : partType,
                null);
            List<ShipComponentStatCategory> categories =
                InferDefaultStatCategoriesForPartType(partType, null);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            AppendExplicitPartTypeFields(seen, ordered, categories, partType);
            return ordered.ToArray();
        }

        /// <summary>
        /// Adds <paramref name="partType"/>'s categories onto <paramref name="dest"/> when that type contributes stats.
        /// </summary>
        public static void UnionCategoriesForPartType(List<ShipComponentStatCategory> dest, string partType)
        {
            if (dest == null || !ShipFamilyPartTypes.IsContributingProfile(partType))
                return;

            string normalized = ShipFamilyPartTypes.Normalize(partType, null);
            List<ShipComponentStatCategory> extra = InferDefaultStatCategoriesForPartType(normalized, null);
            if (extra == null)
                return;
            for (int i = 0; i < extra.Count; i++)
            {
                if (!ContainsStatCategory(dest, extra[i]))
                    dest.Add(extra[i]);
            }
        }

        /// <summary>Union of stat fields for all assigned categories (stable display order).</summary>
        public static string[] GetAuthoringStatFieldNames(
            IReadOnlyList<ShipComponentStatCategory> categories,
            string componentId)
        {
            return GetAuthoringStatFieldNames(
                categories, componentId, AdditionalPartTypesFor(componentId));
        }

        static IReadOnlyList<string> AdditionalPartTypesFor(string componentId)
        {
            ShipFamilyPartCalcProfileSet set = ShipFamilyPartCalcProfileSet.LoadShared();
            if (set == null
                || !set.TryGetNameMapping(componentId, out ShipFamilyPartNameMapping mapping)
                || mapping == null)
                return null;
            return mapping.additionalPartTypes;
        }

        /// <summary>
        /// Name-based fields for <paramref name="componentId"/>, plus each extra part type's own fields.
        /// </summary>
        public static string[] GetAuthoringStatFieldNames(
            IReadOnlyList<ShipComponentStatCategory> categories,
            string componentId,
            IReadOnlyList<string> additionalPartTypes)
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

                string[] fields = FieldsForResolvedName(category, componentId);
                for (int f = 0; f < fields.Length; f++)
                {
                    if (seen.Add(fields[f]))
                        ordered.Add(fields[f]);
                }
            }

            AppendAdditionalPartTypeFields(seen, ordered, categories, additionalPartTypes);
            return ordered.ToArray();
        }

        static void AppendAdditionalPartTypeFields(
            HashSet<string> seen,
            List<string> ordered,
            IReadOnlyList<ShipComponentStatCategory> requested,
            IReadOnlyList<string> additionalPartTypes)
        {
            if (additionalPartTypes == null)
                return;
            for (int i = 0; i < additionalPartTypes.Count; i++)
            {
                string type = additionalPartTypes[i];
                if (!ShipFamilyPartTypes.IsContributingProfile(type))
                    continue;
                AppendExplicitPartTypeFields(
                    seen,
                    ordered,
                    requested,
                    ShipFamilyPartTypes.Normalize(type, null));
            }
        }

        static void AppendExplicitPartTypeFields(
            HashSet<string> seen,
            List<string> ordered,
            IReadOnlyList<ShipComponentStatCategory> requested,
            string partType)
        {
            if (seen == null || ordered == null || !ShipFamilyPartTypes.IsContributingProfile(partType))
                return;

            List<ShipComponentStatCategory> typeCategories =
                InferDefaultStatCategoriesForPartType(partType, null);
            for (int i = 0; i < CategoryDisplayOrder.Length; i++)
            {
                ShipComponentStatCategory category = CategoryDisplayOrder[i];
                if (requested != null && !ContainsStatCategory(requested, category))
                    continue;
                if (!ContainsStatCategory(typeCategories, category))
                    continue;

                string[] fields = FieldsForExplicitPartType(category, partType);
                for (int f = 0; f < fields.Length; f++)
                {
                    if (seen.Add(fields[f]))
                        ordered.Add(fields[f]);
                }
            }
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
