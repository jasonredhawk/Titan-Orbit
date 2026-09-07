using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One discovered <b>prefab asset</b> part name (family-prefixed, e.g. AstroEagle_Cockpit)
    /// and how Scan / VFX / attribute mesh-scale treat it. Filled by Discover &amp; Classify on
    /// the shared ProfileSet.
    /// <para>
    /// <see cref="partType"/> is the <b>group</b>: shared Part Profile stats + which attribute-scale
    /// bucket the mesh grows with (e.g. Thruster Cover stays <c>Thruster</c> so it grows with jets,
    /// but <see cref="contributesAbilityStats"/> is false and VFX stays off).
    /// </para>
    /// </summary>
    [Serializable]
    public class ShipFamilyPartNameMapping
    {
        /// <summary>Full prefab part id including family, e.g. SpaceExcalibur_Thrusters_Big.</summary>
        public string discoveredName;
        /// <summary>
        /// Broad group key matching a Part Profile row (Engine, Thruster, Wing, Cockpit, Weapon, …).
        /// Also selects the attribute mesh-scale group at runtime.
        /// </summary>
        [ShipFamilyPartType]
        public string partType = "Unmapped";
        /// <summary>
        /// When false, Scan writes zero ability stats (mass still comes from hierarchy scale).
        /// Use for covers / plates / holders that share a group for visual grow only.
        /// </summary>
        public bool contributesAbilityStats = true;
        /// <summary>When true, jet particles parent under matching mounts at runtime.</summary>
        public bool enablePropulsionVfx;
        /// <summary>Relative particle scale (Big ≈ 1.5, Tiny ≈ 0.45, default 1).</summary>
        public float propulsionVfxScale = 1f;
        /// <summary>Comma-separated family ids that contained this name (Discover fills).</summary>
        public string seenInFamilies = string.Empty;
        /// <summary>Optional designer note.</summary>
        public string notes = string.Empty;
        /// <summary>When false, Scan Folder skips creating a component entry for this name.</summary>
        public bool includeInPopulate = true;

        /// <summary>True when partType is empty, Unmapped, or Ignore.</summary>
        public bool IsUnmappedOrIgnore
        {
            get
            {
                if (string.IsNullOrWhiteSpace(partType))
                    return true;
                return string.Equals(partType, "Unmapped", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partType, "Ignore", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Per-part-type base + per-version increment used when Scan / Recalculate writes component stats.
    /// Formula: <c>vN = baseAtVersion1 + (N-1) * perVersionIncrement</c> for each non-zero field.
    /// </summary>
    [Serializable]
    public class ShipFamilyPartCalcProfile
    {
        [ShipFamilyPartType(includeUnmappedAndIgnore: false)]
        public string partType = "Engine";
        public List<ShipComponentStatCategory> defaultCategories = new List<ShipComponentStatCategory>();
        public ShipComponentAbilityStats baseAtVersion1;
        public ShipComponentAbilityStats perVersionIncrement;
        /// <summary>When &gt; 0, overrides global per-level fraction for *PerLevel fields.</summary>
        public float perLevelFractionOverride;

        /// <summary>Builds stats for a version tier (1-based).</summary>
        public ShipComponentAbilityStats EvaluateAtVersion(int version)
        {
            int v = Mathf.Max(1, version);
            float steps = v - 1;
            var s = new ShipComponentAbilityStats();
            AddScaled(ref s, baseAtVersion1, 1f);
            AddScaled(ref s, perVersionIncrement, steps);

            float frac = perLevelFractionOverride > 0.0001f
                ? perLevelFractionOverride
                : (IsPropulsionPartType(partType)
                    ? ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase
                    : ShipPropulsionAggregation.PerLevelFractionOfBase);

            // Fill *PerLevel from bases when still zero so scan produces growth curves.
            FillPerLevelIfZero(ref s, frac);

            // [TITAN-ORBIT] Weapons never grow fire rate per ship level — keep authored rate flat.
            if (ShipFamilyPartTypes.IsWeapon(partType))
                s.fireRatePerExtraLevel = 0f;

            return s;
        }

        /// <summary>
        /// Effective fraction used when *PerLevel fields are still zero (override, propulsion 0.2, else 0.25).
        /// </summary>
        public float ResolvePerLevelFraction()
        {
            if (perLevelFractionOverride > 0.0001f)
                return perLevelFractionOverride;
            return IsPropulsionPartType(partType)
                ? ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase
                : ShipPropulsionAggregation.PerLevelFractionOfBase;
        }

        /// <summary>
        /// Writes filled *PerLevel values into <see cref="baseAtVersion1"/> and
        /// <see cref="perVersionIncrement"/> when they are still zero so the Inspector matches
        /// what Scan / <see cref="EvaluateAtVersion"/> already applies (and what ShipFamilyDefinition shows).
        /// </summary>
        public void EnsureAuthoredPerLevelFilled()
        {
            float frac = ResolvePerLevelFraction();
            FillPerLevelIfZero(ref baseAtVersion1, frac);
            FillPerLevelIfZero(ref perVersionIncrement, frac);

            // [TITAN-ORBIT] Weapons never grow fire rate per ship level.
            if (ShipFamilyPartTypes.IsWeapon(partType))
            {
                baseAtVersion1.fireRatePerExtraLevel = 0f;
                perVersionIncrement.fireRatePerExtraLevel = 0f;
            }
        }

        static bool IsPropulsionPartType(string type) => ShipFamilyPartTypes.IsPropulsion(type);

        static void AddScaled(ref ShipComponentAbilityStats target, ShipComponentAbilityStats source, float factor)
        {
            if (factor == 0f)
                return;
            target.firePower += source.firePower * factor;
            target.firePowerPerExtraLevel += source.firePowerPerExtraLevel * factor;
            target.bulletSpeed += source.bulletSpeed * factor;
            target.bulletSpeedPerExtraLevel += source.bulletSpeedPerExtraLevel * factor;
            target.bulletRange += source.bulletRange * factor;
            target.bulletRangePerExtraLevel += source.bulletRangePerExtraLevel * factor;
            target.fireRate += source.fireRate * factor;
            target.fireRatePerExtraLevel += source.fireRatePerExtraLevel * factor;
            target.rammingPower += source.rammingPower * factor;
            target.rammingPowerPerExtraLevel += source.rammingPowerPerExtraLevel * factor;
            target.healthCap += source.healthCap * factor;
            target.healthCapPerExtraLevel += source.healthCapPerExtraLevel * factor;
            target.healthRegen += source.healthRegen * factor;
            target.healthRegenPerExtraLevel += source.healthRegenPerExtraLevel * factor;
            target.energyCap += source.energyCap * factor;
            target.energyCapPerExtraLevel += source.energyCapPerExtraLevel * factor;
            target.energyRegen += source.energyRegen * factor;
            target.energyRegenPerExtraLevel += source.energyRegenPerExtraLevel * factor;
            target.moveSpeed += source.moveSpeed * factor;
            target.moveSpeedPerExtraLevel += source.moveSpeedPerExtraLevel * factor;
            target.accelerationCap += source.accelerationCap * factor;
            target.accelerationCapPerExtraLevel += source.accelerationCapPerExtraLevel * factor;
            // OVERDRIVE knobs: take max (same rule as AbilityStatsMath.Add).
            float esp = source.extraSpeedPercent * factor;
            if (esp > target.extraSpeedPercent) target.extraSpeedPercent = esp;
            float espPl = source.extraSpeedPercentPerExtraLevel * factor;
            if (espPl > target.extraSpeedPercentPerExtraLevel) target.extraSpeedPercentPerExtraLevel = espPl;
            float esep = source.extraSpeedEnergyDrain * factor;
            if (esep > target.extraSpeedEnergyDrain) target.extraSpeedEnergyDrain = esep;
            float esepPl = source.extraSpeedEnergyDrainPerExtraLevel * factor;
            if (esepPl > target.extraSpeedEnergyDrainPerExtraLevel) target.extraSpeedEnergyDrainPerExtraLevel = esepPl;
            target.turnSpeed += source.turnSpeed * factor;
            target.turnSpeedPerExtraLevel += source.turnSpeedPerExtraLevel * factor;
            target.maxGems += source.maxGems * factor;
            target.maxGemsPerExtraLevel += source.maxGemsPerExtraLevel * factor;
            target.tractorBeamDistance += source.tractorBeamDistance * factor;
            target.tractorBeamDistancePerExtraLevel += source.tractorBeamDistancePerExtraLevel * factor;
            target.tractorBeamPower += source.tractorBeamPower * factor;
            target.tractorBeamPowerPerExtraLevel += source.tractorBeamPowerPerExtraLevel * factor;
            target.maxPeople += source.maxPeople * factor;
            target.maxPeoplePerExtraLevel += source.maxPeoplePerExtraLevel * factor;
        }

        /// <summary>
        /// Fills each <c>*PerExtraLevel</c> from its base × <paramref name="frac"/> when still zero.
        /// Used by Scan / Inspector so Extra Level steps are authored alongside bases.
        /// </summary>
        public static void FillPerLevelIfZero(ref ShipComponentAbilityStats s, float frac)
        {
            if (s.firePowerPerExtraLevel == 0f && s.firePower != 0f) s.firePowerPerExtraLevel = s.firePower * frac;
            if (s.bulletSpeedPerExtraLevel == 0f && s.bulletSpeed != 0f) s.bulletSpeedPerExtraLevel = s.bulletSpeed * frac;
            if (s.bulletRangePerExtraLevel == 0f && s.bulletRange != 0f) s.bulletRangePerExtraLevel = s.bulletRange * frac;
            if (s.fireRatePerExtraLevel == 0f && s.fireRate != 0f) s.fireRatePerExtraLevel = s.fireRate * frac;
            if (s.rammingPowerPerExtraLevel == 0f && s.rammingPower != 0f) s.rammingPowerPerExtraLevel = s.rammingPower * frac;
            if (s.healthCapPerExtraLevel == 0f && s.healthCap != 0f) s.healthCapPerExtraLevel = s.healthCap * frac;
            if (s.healthRegenPerExtraLevel == 0f && s.healthRegen != 0f) s.healthRegenPerExtraLevel = s.healthRegen * frac;
            if (s.energyCapPerExtraLevel == 0f && s.energyCap != 0f) s.energyCapPerExtraLevel = s.energyCap * frac;
            if (s.energyRegenPerExtraLevel == 0f && s.energyRegen != 0f) s.energyRegenPerExtraLevel = s.energyRegen * frac;
            if (s.moveSpeedPerExtraLevel == 0f && s.moveSpeed != 0f) s.moveSpeedPerExtraLevel = s.moveSpeed * frac;
            if (s.accelerationCapPerExtraLevel == 0f && s.accelerationCap != 0f)
                s.accelerationCapPerExtraLevel = s.accelerationCap * frac;
            // [TITAN-ORBIT] ExtraSpeedPercent ability step stays 0 unless designers author a step.
            // ExtraSpeedEnergyDrain PerExtraLevel follows moveSpeed fraction (Move Speed HUD).
            if (s.extraSpeedEnergyDrainPerExtraLevel == 0f && s.extraSpeedEnergyDrain != 0f)
                s.extraSpeedEnergyDrainPerExtraLevel = s.extraSpeedEnergyDrain * frac;
            if (s.turnSpeedPerExtraLevel == 0f && s.turnSpeed != 0f) s.turnSpeedPerExtraLevel = s.turnSpeed * frac;
            if (s.maxGemsPerExtraLevel == 0f && s.maxGems != 0f) s.maxGemsPerExtraLevel = s.maxGems * frac;
            if (s.tractorBeamDistancePerExtraLevel == 0f && s.tractorBeamDistance != 0f)
                s.tractorBeamDistancePerExtraLevel = s.tractorBeamDistance * frac;
            if (s.tractorBeamPowerPerExtraLevel == 0f && s.tractorBeamPower != 0f)
                s.tractorBeamPowerPerExtraLevel = s.tractorBeamPower * frac;
            // [TITAN-ORBIT] No RoundToInt — small bases (e.g. maxPeople=2 × 0.25) must stay fractional
            // so attribute mesh grow and Scan curves keep the true percent-of-base step.
            if (s.maxPeoplePerExtraLevel == 0f && s.maxPeople != 0f)
                s.maxPeoplePerExtraLevel = s.maxPeople * frac;
        }
    }

    /// <summary>
    /// Project-wide shared asset: discovered component name inventory, AI-assisted classification,
    /// per-name propulsion VFX flags/scales, part-type calc profiles used by every
    /// <see cref="ShipFamilyDefinition"/> Scan / Populate button, and the global attribute
    /// mesh-grow dampener (<see cref="globalUpgradeScaleMultiplier"/>).
    /// Place under Resources as <c>ShipFamilyPartCalcProfileSet</c> for runtime VFX fallback.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipFamilyPartCalcProfileSet",
        menuName = "Titan Orbit/Ship Family Part Calc Profile Set")]
    public class ShipFamilyPartCalcProfileSet : ScriptableObject
    {
        public const string ResourcesAssetName = "ShipFamilyPartCalcProfileSet";

        /// <summary>Default global mesh-grow dampener when the field is unset / missing on old assets.</summary>
        public const float DefaultGlobalUpgradeScaleMultiplier = 0.25f;

        static readonly Regex FirstDigitRegex = new Regex(@"\d+", RegexOptions.Compiled);

        [Header("Attribute mesh grow (presentation)")]
        [Tooltip(
            "GlobalUpgradeScaleMultiplier — multiplies ALL bottom-bar upgrade mesh growth on every " +
            "component (after per-part 1/N sharing). 1 = full growth, 0.25 = 25% of that growth, 0 = no grow. " +
            "Does not affect combat stats or whole-ship tier scale.")]
        [Range(0f, 2f)]
        public float globalUpgradeScaleMultiplier = DefaultGlobalUpgradeScaleMultiplier;

        [Header("Name inventory — one row per unique family-prefixed prefab part name (Discover & Classify)")]
        [Tooltip("Inventory of full prefab part ids (AstroEagle_Wing_2, SpaceExcalibur_Thrusters_Big…). Each maps into a Part Profile group via partType. Count is large; that is expected.")]
        public List<ShipFamilyPartNameMapping> nameMappings = new List<ShipFamilyPartNameMapping>();

        [Header("Part Profiles — Cockpit, Weapons, Wing, Engine, Thruster, Tail, Hull")]
        [Tooltip("Eight shared groups. Edit stats once per group; Scan applies to every Name Mapping in that group. Version digits in the name pick the tier. Engine = power plant (move/accel + Energy + OVERDRIVE knobs). Thruster = maneuver jets (move/accel + turn).")]
        public List<ShipFamilyPartCalcProfile> partProfiles = new List<ShipFamilyPartCalcProfile>();

        [NonSerialized] Dictionary<string, ShipFamilyPartNameMapping> _nameLookup;
        [NonSerialized] Dictionary<string, ShipFamilyPartCalcProfile> _profileLookup;

        /// <summary>Loads the Resources asset, or null when missing.</summary>
        public static ShipFamilyPartCalcProfileSet LoadShared()
        {
            return Resources.Load<ShipFamilyPartCalcProfileSet>(ResourcesAssetName);
        }

        /// <summary>Invalidates dictionaries after editor edits.</summary>
        public void InvalidateLookups()
        {
            _nameLookup = null;
            _profileLookup = null;
        }

        void EnsureNameLookup()
        {
            if (_nameLookup != null)
                return;
            _nameLookup = new Dictionary<string, ShipFamilyPartNameMapping>(StringComparer.OrdinalIgnoreCase);
            if (nameMappings == null)
                return;
            for (int i = 0; i < nameMappings.Count; i++)
            {
                var m = nameMappings[i];
                if (m == null || string.IsNullOrWhiteSpace(m.discoveredName))
                    continue;
                _nameLookup[m.discoveredName.Trim()] = m;
            }
        }

        void EnsureProfileLookup()
        {
            if (_profileLookup != null)
                return;
            _profileLookup = new Dictionary<string, ShipFamilyPartCalcProfile>(StringComparer.OrdinalIgnoreCase);
            if (partProfiles == null)
                return;
            for (int i = 0; i < partProfiles.Count; i++)
            {
                var p = partProfiles[i];
                if (p == null || string.IsNullOrWhiteSpace(p.partType))
                    continue;
                _profileLookup[p.partType.Trim()] = p;
            }
        }

        /// <summary>Looks up a mapping by discovered name or alias-resolved key.</summary>
        public bool TryGetNameMapping(string componentIdOrDiscovered, out ShipFamilyPartNameMapping mapping)
        {
            EnsureNameLookup();
            mapping = null;
            if (string.IsNullOrWhiteSpace(componentIdOrDiscovered))
                return false;

            string key = ShipFamilyDefinition.NormalizeComponentId(componentIdOrDiscovered);
            if (_nameLookup.TryGetValue(key, out mapping))
                return true;

            // Family-prefixed id: retry aliases / digit-stripped keys under the same family.
            if (TrySplitFamilyPrefixedPart(key, out string familyId, out string rest))
            {
                string familyAlias = ShipFamilyComponentPartKey.ResolveAliasKey(rest);
                if (!string.IsNullOrEmpty(familyAlias)
                    && _nameLookup.TryGetValue(familyId + "_" + familyAlias, out mapping))
                    return true;

                string familyBase = ShipFamilyComponentPartKey.GetBasePartKey(rest);
                if (!string.IsNullOrEmpty(familyBase)
                    && _nameLookup.TryGetValue(familyId + "_" + familyBase, out mapping))
                    return true;
            }

            // Try alias canonical (Thrusters_Big) then base key without trailing digits.
            string alias = ShipFamilyComponentPartKey.ResolveAliasKey(key);
            if (!string.IsNullOrEmpty(alias) && _nameLookup.TryGetValue(alias, out mapping))
                return true;

            string baseKey = ShipFamilyComponentPartKey.GetBasePartKey(key);
            return !string.IsNullOrEmpty(baseKey) && _nameLookup.TryGetValue(baseKey, out mapping);
        }

        /// <summary>
        /// Splits <c>FamilyId_Part</c> on the first underscore. Used only as a lookup fallback —
        /// Discover stores the full name.
        /// </summary>
        static bool TrySplitFamilyPrefixedPart(string componentId, out string familyId, out string rest)
        {
            familyId = string.Empty;
            rest = string.Empty;
            if (string.IsNullOrEmpty(componentId))
                return false;
            int underscore = componentId.IndexOf('_');
            if (underscore <= 0 || underscore >= componentId.Length - 1)
                return false;
            familyId = componentId.Substring(0, underscore);
            rest = componentId.Substring(underscore + 1);
            return !string.IsNullOrWhiteSpace(familyId) && !string.IsNullOrWhiteSpace(rest);
        }

        /// <summary>Resolves canonical part type for stats: mapping wins, else keyword heuristic.</summary>
        public string ResolvePartType(string componentId)
        {
            if (TryGetNameMapping(componentId, out ShipFamilyPartNameMapping mapping)
                && mapping != null
                && !string.IsNullOrWhiteSpace(mapping.partType)
                && !string.Equals(mapping.partType, "Unmapped", StringComparison.OrdinalIgnoreCase))
            {
                return ShipFamilyPartTypes.Normalize(mapping.partType.Trim(), componentId);
            }

            return ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
        }

        /// <summary>True when this component id should spawn propulsion particles.</summary>
        public bool ShouldEnablePropulsionVfx(string componentId, out float scale)
        {
            scale = 1f;
            if (TryGetNameMapping(componentId, out ShipFamilyPartNameMapping mapping) && mapping != null)
            {
                scale = mapping.propulsionVfxScale > 0.0001f ? mapping.propulsionVfxScale : 1f;
                return mapping.enablePropulsionVfx;
            }

            // Fallback: Thruster-profile mounts get VFX; cosmetics stay dark.
            string type = ResolvePartType(componentId);
            if (!ShipFamilyPartTypes.IsThrusterProfile(type) || IsCosmeticPartName(componentId))
                return false;

            return true;
        }

        /// <summary>
        /// Rewrites legacy partType labels on every mapping, then sorts Name Mappings A→Z.
        /// </summary>
        public int NormalizeMappedPartTypesAndSort()
        {
            int changed = 0;
            if (nameMappings != null)
            {
                for (int i = 0; i < nameMappings.Count; i++)
                {
                    var m = nameMappings[i];
                    if (m == null || string.IsNullOrWhiteSpace(m.partType))
                        continue;
                    if (string.Equals(m.partType, ShipFamilyPartTypes.Unmapped, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m.partType, ShipFamilyPartTypes.Ignore, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string next = ShipFamilyPartTypes.Normalize(m.partType, m.discoveredName);
                    if (!string.Equals(m.partType, next, StringComparison.Ordinal))
                    {
                        m.partType = next;
                        changed++;
                    }
                }
            }

            SortNameMappingsAlphabetically();
            InvalidateLookups();
            return changed;
        }

        /// <summary>Sorts <see cref="nameMappings"/> by discoveredName (case-insensitive).</summary>
        public void SortNameMappingsAlphabetically()
        {
            if (nameMappings == null || nameMappings.Count < 2)
                return;
            nameMappings.Sort((a, b) =>
            {
                string an = a != null ? a.discoveredName : string.Empty;
                string bn = b != null ? b.discoveredName : string.Empty;
                return string.Compare(an, bn, StringComparison.OrdinalIgnoreCase);
            });
            InvalidateLookups();
        }

        /// <summary>Looks up a calc profile by part type string.</summary>
        public bool TryGetProfile(string partType, out ShipFamilyPartCalcProfile profile)
        {
            EnsureProfileLookup();
            profile = null;
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return _profileLookup.TryGetValue(partType.Trim(), out profile);
        }

        /// <summary>First digit in the part suffix (after FamilyId_), or 1.</summary>
        public static int ExtractVersion(string componentId)
        {
            if (string.IsNullOrEmpty(componentId))
                return 1;
            string source = componentId;
            if (TrySplitFamilyPrefixedPart(
                    ShipFamilyDefinition.NormalizeComponentId(componentId),
                    out _,
                    out string rest)
                && !string.IsNullOrEmpty(rest))
                source = rest;
            Match m = FirstDigitRegex.Match(source);
            if (!m.Success)
                return 1;
            return int.TryParse(m.Value, out int v) ? Mathf.Max(1, v) : 1;
        }

        /// <summary>
        /// True when this component should receive ability stats from its Part Profile.
        /// Covers / plates / holders stay in the group for mesh scale but return false here.
        /// </summary>
        public bool ContributesAbilityStats(string componentId)
        {
            if (TryGetNameMapping(componentId, out ShipFamilyPartNameMapping mapping) && mapping != null)
                return mapping.contributesAbilityStats;
            return !IsCosmeticPartName(componentId);
        }

        /// <summary>
        /// Suggests ability stats for a component using ProfileSet profiles + categories.
        /// Cosmetics (<see cref="ShipFamilyPartNameMapping.contributesAbilityStats"/> false) get zeros.
        /// </summary>
        public ShipComponentAbilityStats SuggestStatsForComponent(
            string componentId,
            IReadOnlyList<ShipComponentStatCategory> categories)
        {
            // --- Cosmetics: same group for scale/VFX flags, no ability numbers ---
            if (!ContributesAbilityStats(componentId))
                return default;

            string partType = ResolvePartType(componentId);
            int version = ExtractVersion(componentId);
            if (categories == null || categories.Count == 0)
                categories = ShipFamilyComponentPartKey.InferDefaultStatCategories(componentId);

            ShipComponentAbilityStats merged = default;
            if (TryGetProfile(partType, out ShipFamilyPartCalcProfile profile) && profile != null)
            {
                merged = profile.EvaluateAtVersion(version);
            }
            else
            {
                // Last-resort keyword seed when profiles not reset yet.
                merged = SeedStatsHeuristic(partType, version, categories);
            }

            return ShipComponentAbilityStats.KeepOnlyAuthoringFields(merged, categories, componentId);
        }

        /// <summary>
        /// Cover / Place / Plate / Holder / ThrustCover — share a gameplay group for mesh grow,
        /// but must not get jet VFX or ability stats by default.
        /// </summary>
        public static bool IsCosmeticPartName(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return false;
            string n = ShipFamilyDefinition.NormalizeComponentId(componentId);
            if (string.IsNullOrEmpty(n))
                return false;
            return n.IndexOf("Cover", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Place", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Plate", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Holder", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Merges a discovered prefab part name into <see cref="nameMappings"/>.
        /// <para>
        /// [TITAN-ORBIT] Additive only: if the name already exists, we only append
        /// <see cref="ShipFamilyPartNameMapping.seenInFamilies"/> — partType, VFX flags, notes,
        /// and includeInPopulate are never overwritten. New names start as Unmapped (plus known-name seeds).
        /// </para>
        /// </summary>
        /// <param name="discoveredName">Full prefab part id including family (FamilyId_Part).</param>
        /// <param name="familyId">Optional family folder id recorded on seenInFamilies.</param>
        /// <returns>Existing or newly created mapping, or null if the name is empty.</returns>
        public ShipFamilyPartNameMapping MergeDiscoveredName(string discoveredName, string familyId)
        {
            if (nameMappings == null)
                nameMappings = new List<ShipFamilyPartNameMapping>();

            string key = ShipFamilyDefinition.NormalizeComponentId(discoveredName);
            if (string.IsNullOrEmpty(key))
                return null;

            // --- Existing row: preserve classification; only note which family also uses it ---
            for (int i = 0; i < nameMappings.Count; i++)
            {
                var existing = nameMappings[i];
                if (existing == null)
                    continue;
                if (!string.Equals(existing.discoveredName, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                AppendSeenFamily(existing, familyId);
                InvalidateLookups();
                return existing;
            }

            // --- New row: Unmapped inventory entry (+ seed defaults for well-known names) ---
            var created = new ShipFamilyPartNameMapping
            {
                discoveredName = key,
                partType = "Unmapped",
                contributesAbilityStats = true,
                enablePropulsionVfx = false,
                propulsionVfxScale = 1f,
                includeInPopulate = true,
            };
            AppendSeenFamily(created, familyId);
            // Seed known specials from aliases / plan examples (first discover only).
            ApplySeedDefaultsForKnownName(created, familyId);
            nameMappings.Add(created);
            InvalidateLookups();
            return created;
        }

        static void AppendSeenFamily(ShipFamilyPartNameMapping mapping, string familyId)
        {
            if (mapping == null || string.IsNullOrWhiteSpace(familyId))
                return;
            string fid = familyId.Trim();
            if (string.IsNullOrEmpty(mapping.seenInFamilies))
            {
                mapping.seenInFamilies = fid;
                return;
            }

            if (mapping.seenInFamilies.IndexOf(fid, StringComparison.OrdinalIgnoreCase) >= 0)
                return;
            mapping.seenInFamilies += "," + fid;
        }

        /// <summary>
        /// Applies plan seed defaults for well-known names when first discovered.
        /// Cosmetics stay in their gameplay group (Thruster/Wing/…) with stats + VFX off.
        /// </summary>
        public static void ApplySeedDefaultsForKnownName(
            ShipFamilyPartNameMapping mapping,
            string familyId = null)
        {
            if (mapping == null || string.IsNullOrEmpty(mapping.discoveredName))
                return;

            string full = ShipFamilyDefinition.NormalizeComponentId(mapping.discoveredName);
            string suffix = ShipFamilyDefinition.GetComponentIdSuffix(familyId, full);
            if (string.IsNullOrEmpty(suffix))
                suffix = full;
            if (string.Equals(suffix, full, StringComparison.OrdinalIgnoreCase)
                && TrySplitFamilyPrefixedPart(full, out _, out string rest)
                && !string.IsNullOrEmpty(rest))
                suffix = rest;

            bool MatchesKnown(string known) =>
                string.Equals(full, known, StringComparison.OrdinalIgnoreCase)
                || string.Equals(suffix, known, StringComparison.OrdinalIgnoreCase);

            // Heuristics still see FamilyId_Part; exact seeds try both full and suffix.
            string n = full;

            // --- Known jet mounts (Thruster Part Profile + VFX) ---
            if (MatchesKnown("Thrusters_Big"))
            {
                mapping.partType = ShipFamilyPartTypes.Thruster;
                mapping.contributesAbilityStats = true;
                mapping.enablePropulsionVfx = true;
                mapping.propulsionVfxScale = 1.5f;
                return;
            }

            if (MatchesKnown("Tiny_Thrusters"))
            {
                mapping.partType = ShipFamilyPartTypes.Thruster;
                mapping.contributesAbilityStats = true;
                mapping.enablePropulsionVfx = true;
                mapping.propulsionVfxScale = 0.45f;
                return;
            }

            if (MatchesKnown("Exhaust")
                || MatchesKnown("Thrusters")
                || MatchesKnown("Thruster"))
            {
                mapping.partType = ShipFamilyPartTypes.Thruster;
                mapping.contributesAbilityStats = true;
                mapping.enablePropulsionVfx = true;
                mapping.propulsionVfxScale = 1f;
                return;
            }

            // --- Resolve broad group from name heuristic ---
            string heuristic = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(n);
            if (!string.IsNullOrEmpty(heuristic))
                mapping.partType = ShipFamilyPartTypes.Normalize(heuristic, n);

            // --- Cosmetics: same group for mesh grow, no ability stats, no jet VFX ---
            if (IsCosmeticPartName(n))
            {
                if (string.IsNullOrEmpty(mapping.partType)
                    || string.Equals(mapping.partType, ShipFamilyPartTypes.Unmapped, StringComparison.OrdinalIgnoreCase))
                {
                    if (n.IndexOf("Thruster", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Exhaust", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Thrust", StringComparison.OrdinalIgnoreCase) >= 0)
                        mapping.partType = ShipFamilyPartTypes.Thruster;
                    else if (n.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) >= 0)
                        mapping.partType = ShipFamilyPartTypes.Engine;
                    else if (n.IndexOf("Wing", StringComparison.OrdinalIgnoreCase) >= 0)
                        mapping.partType = ShipFamilyPartTypes.Wing;
                    else if (n.IndexOf("Cockpit", StringComparison.OrdinalIgnoreCase) >= 0)
                        mapping.partType = ShipFamilyPartTypes.Cockpit;
                    else
                        mapping.partType = ShipFamilyPartTypes.Hull;
                }
                else
                {
                    mapping.partType = ShipFamilyPartTypes.Normalize(mapping.partType, n);
                }

                mapping.contributesAbilityStats = false;
                mapping.enablePropulsionVfx = false;
                mapping.propulsionVfxScale = 1f;
                return;
            }

            if (!string.IsNullOrEmpty(mapping.partType)
                && !string.Equals(mapping.partType, ShipFamilyPartTypes.Unmapped, StringComparison.OrdinalIgnoreCase))
            {
                mapping.partType = ShipFamilyPartTypes.Normalize(mapping.partType, n);
                mapping.contributesAbilityStats = true;
                // Jets only on thruster profile mounts — not Engine power-plant meshes.
                mapping.enablePropulsionVfx = ShipFamilyPartTypes.IsThrusterProfile(mapping.partType);
                mapping.propulsionVfxScale = 1f;
            }
        }

        /// <summary>
        /// Ensures every mapped partType has a Part Profile row.
        /// <para>
        /// [TITAN-ORBIT] Additive for stats: existing profile rows keep their baseAtVersion1 /
        /// perVersionIncrement / categories. We only create missing core groups and rename legacy
        /// labels (<c>Engine/Thrust</c> → Engine + Thruster profiles, Fin→Tail). Use
        /// <see cref="ResetPartProfilesToCodeDefaults"/> when you intentionally want code seeds
        /// to replace edited numbers.
        /// </para>
        /// </summary>
        /// <returns>Count of newly created profile rows (0 when everything already existed).</returns>
        public int EnsureProfilesForMappedPartTypes()
        {
            if (partProfiles == null)
                partProfiles = new List<ShipFamilyPartCalcProfile>();

            // Migrate Name Mapping labels + sort A→Z (does not invent new classifications).
            NormalizeMappedPartTypesAndSort();

            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string core in ShipFamilyPartTypes.CoreProfiles)
                needed.Add(core);

            if (nameMappings != null)
            {
                for (int i = 0; i < nameMappings.Count; i++)
                {
                    var m = nameMappings[i];
                    if (m == null || m.IsUnmappedOrIgnore)
                        continue;
                    needed.Add(ShipFamilyPartTypes.Normalize(m.partType.Trim(), m.discoveredName));
                }
            }

            // --- Keep existing core profiles (stats untouched); merge legacy type labels ---
            // Prefer rows that already use the canonical label (hand-tuned Engine/Thrust) over
            // leftover legacy seeds (Thruster → Engine/Thrust) so Ensure never clobbers edits.
            var kept = new Dictionary<string, ShipFamilyPartCalcProfile>(StringComparer.OrdinalIgnoreCase);
            // [TITAN-ORBIT] Non-core part types (custom groups) must survive Ensure — previously
            // partProfiles was replaced with core-only and authored custom rows were wiped.

            // Pass 1: already-canonical core rows win.
            for (int i = 0; i < partProfiles.Count; i++)
            {
                var p = partProfiles[i];
                if (p == null || string.IsNullOrWhiteSpace(p.partType))
                    continue;

                string original = p.partType.Trim();
                string normalized = ShipFamilyPartTypes.Normalize(original, null);
                if (IndexOfCoreProfile(normalized) >= ShipFamilyPartTypes.CoreProfiles.Length)
                    continue;
                if (!string.Equals(original, normalized, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kept.ContainsKey(normalized))
                    continue;

                p.partType = normalized;
                kept[normalized] = p;
            }

            // Pass 2: legacy core labels fill gaps only (rename label; keep that row's numbers).
            for (int i = 0; i < partProfiles.Count; i++)
            {
                var p = partProfiles[i];
                if (p == null || string.IsNullOrWhiteSpace(p.partType))
                    continue;

                string normalized = ShipFamilyPartTypes.Normalize(p.partType.Trim(), null);
                if (IndexOfCoreProfile(normalized) >= ShipFamilyPartTypes.CoreProfiles.Length)
                    continue;
                if (kept.ContainsKey(normalized))
                    continue;

                p.partType = normalized;
                kept[normalized] = p;
            }

            // Pass 3: preserve every non-core profile (do not drop custom groups).
            for (int i = 0; i < partProfiles.Count; i++)
            {
                var p = partProfiles[i];
                if (p == null || string.IsNullOrWhiteSpace(p.partType))
                    continue;

                string normalized = ShipFamilyPartTypes.Normalize(p.partType.Trim(), null);
                if (IndexOfCoreProfile(normalized) < ShipFamilyPartTypes.CoreProfiles.Length)
                    continue; // core — already in kept
                if (kept.ContainsKey(normalized))
                    continue;

                p.partType = normalized;
                kept[normalized] = p;
            }

            partProfiles = new List<ShipFamilyPartCalcProfile>(kept.Values);
            InvalidateLookups();

            // --- Create defaults only for groups that have no row yet ---
            int created = 0;
            foreach (string type in needed)
            {
                if (TryGetProfile(type, out _))
                    continue;
                partProfiles.Add(CreateDefaultProfile(type));
                created++;
            }

            // Keep Part Profiles in the same designer order as CoreProfiles (non-core after).
            partProfiles.Sort((a, b) =>
            {
                int ai = IndexOfCoreProfile(a != null ? a.partType : null);
                int bi = IndexOfCoreProfile(b != null ? b.partType : null);
                if (ai != bi) return ai.CompareTo(bi);
                return string.Compare(a?.partType, b?.partType, StringComparison.OrdinalIgnoreCase);
            });

            InvalidateLookups();
            return created;
        }

        static int IndexOfCoreProfile(string partType)
        {
            if (string.IsNullOrEmpty(partType))
                return 999;
            for (int i = 0; i < ShipFamilyPartTypes.CoreProfiles.Length; i++)
            {
                if (string.Equals(partType, ShipFamilyPartTypes.CoreProfiles[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return 500;
        }

        /// <summary>Rewrites partProfiles from code suggestion seeds (keeps nameMappings).</summary>
        public void ResetPartProfilesToCodeDefaults()
        {
            partProfiles = new List<ShipFamilyPartCalcProfile>();
            for (int i = 0; i < ShipFamilyPartTypes.CoreProfiles.Length; i++)
                partProfiles.Add(CreateDefaultProfile(ShipFamilyPartTypes.CoreProfiles[i]));
            InvalidateLookups();
        }

        /// <summary>
        /// Builds a default Part Profile row from suggestion seed constants.
        /// Used by Ensure (missing groups only) and Reset Part Profiles (full rewrite).
        /// Engine = move/accel + placeholder Energy; Thruster = move/accel + Fin-scale turn;
        /// Tail = merged Fin+Tail turn; weapons = offense + Cap-only battery (engines produce Regen).
        /// </summary>
        /// <param name="partType">Core group id (or legacy label — normalized first).</param>
        public static ShipFamilyPartCalcProfile CreateDefaultProfile(string partType)
        {
            // Normalize with a name hint so legacy Engine/Thrust can still split when possible.
            string type = ShipFamilyPartTypes.Normalize(
                string.IsNullOrEmpty(partType) ? ShipFamilyPartTypes.Hull : partType,
                partType);

            var profile = new ShipFamilyPartCalcProfile
            {
                partType = type,
                defaultCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(type),
            };

            // Version-1 bases + per-version increments derived from seed helpers (v1 vs v2 delta).
            if (ShipFamilyPartTypes.IsEngineProfile(type))
            {
                // [TITAN-ORBIT] Power plant: move/accel + Energy Cap/Regen + OVERDRIVE knobs.
                // BalanceEngineEnergyForComponents overwrites Cap/Regen from hull weapon drain on Scan.
                // Cap/Regen per-version uses the gentle moveSpeed fraction (not a second full plant).
                // OD drain/sec = ExtraSpeedEnergyDrain (absolute; not × ExtraSpeedPercent).
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    moveSpeed = ShipPropulsionAggregation.GetSuggestedPropulsionMoveSpeed(1),
                    accelerationCap = ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCap(1),
                    energyCap = ShipPropulsionAggregation.EngineEnergyFallbackCapPerVersion,
                    energyRegen = ShipPropulsionAggregation.EngineEnergyFallbackRegenPerVersion,
                    extraSpeedPercent = ShipFamilyOverdriveAbility.DefaultExtraSpeedPercent,
                    extraSpeedPercentPerExtraLevel = 0f,
                    extraSpeedEnergyDrain = ShipFamilyOverdriveAbility.DefaultExtraSpeedEnergyDrain,
                    extraSpeedEnergyDrainPerExtraLevel = ShipFamilyOverdriveAbility.DefaultExtraSpeedEnergyDrain
                        * ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase,
                };
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    moveSpeed = ShipPropulsionAggregation.SuggestedPropulsionMoveSpeedPerVersion,
                    accelerationCap = ShipPropulsionAggregation.SuggestedPropulsionMoveSpeedPerVersion
                        * ShipPropulsionAggregation.SuggestedPropulsionAccelerationFractionOfMoveSpeed,
                    energyCap = ShipPropulsionAggregation.EngineEnergyCapPerVersionIncrement,
                    energyRegen = ShipPropulsionAggregation.EngineEnergyRegenPerVersionIncrement,
                    // OVERDRIVE knobs stay flat across versions unless designers author increments.
                    extraSpeedPercent = 0f,
                    extraSpeedPercentPerExtraLevel = 0f,
                    extraSpeedEnergyDrain = 0f,
                    extraSpeedEnergyDrainPerExtraLevel = 0f,
                };
                profile.perLevelFractionOverride = ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase;
            }
            else if (ShipFamilyPartTypes.IsThrusterProfile(type))
            {
                // [TITAN-ORBIT] Maneuver jets: move/accel + Fin-scale turn (no OVERDRIVE / no separate OD drain).
                float turnV1 = ShipComponentTurnSpeedSuggestions.GetSuggestedFinTurnSpeed(1);
                float turnPerVersion = ShipComponentTurnSpeedSuggestions.GetSuggestedFinTurnSpeed(2)
                    - ShipComponentTurnSpeedSuggestions.GetSuggestedFinTurnSpeed(1);
                float turnPerLevelV1 = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(turnV1);
                float turnPerLevelPerVersion = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(turnPerVersion);

                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    moveSpeed = ShipPropulsionAggregation.GetSuggestedPropulsionMoveSpeed(1),
                    accelerationCap = ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCap(1),
                    turnSpeed = turnV1,
                    turnSpeedPerExtraLevel = turnPerLevelV1,
                };
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    moveSpeed = ShipPropulsionAggregation.SuggestedPropulsionMoveSpeedPerVersion,
                    accelerationCap = ShipPropulsionAggregation.SuggestedPropulsionMoveSpeedPerVersion
                        * ShipPropulsionAggregation.SuggestedPropulsionAccelerationFractionOfMoveSpeed,
                    turnSpeed = turnPerVersion,
                    turnSpeedPerExtraLevel = turnPerLevelPerVersion,
                };
                profile.perLevelFractionOverride = ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase;
            }
            else if (ShipFamilyPartTypes.IsTurn(type))
            {
                // [TITAN-ORBIT] Fin folded into Tail — use Fin+Tail merged seeds, not Tail-only.
                float turnV1 = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeed(1);
                float turnPerVersion = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeed(2)
                    - ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeed(1);
                float turnPerLevelV1 = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(turnV1);
                float turnPerLevelPerVersion = ShipComponentTurnSpeedSuggestions.GetSuggestedTurnSpeedPerLevel(turnPerVersion);

                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    turnSpeed = turnV1,
                    turnSpeedPerExtraLevel = turnPerLevelV1,
                };
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    turnSpeed = turnPerVersion,
                    turnSpeedPerExtraLevel = turnPerLevelPerVersion,
                };
            }
            else if (string.Equals(type, ShipFamilyPartTypes.WeaponBullet, StringComparison.OrdinalIgnoreCase))
            {
                // 3 shots/sec. Offense + Cap-only battery (Cap = firePower × fireRate = 1 sec of fire).
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    firePower = ShipComponentWeaponSuggestions.FirePowerV1,
                    firePowerPerExtraLevel = ShipComponentWeaponSuggestions.GetSuggestedFirePowerPerLevel(1),
                    bulletSpeed = ShipComponentWeaponSuggestions.BulletSpeedV1,
                    bulletSpeedPerExtraLevel = ShipComponentWeaponSuggestions.GetSuggestedBulletSpeedPerLevel(1),
                    bulletRange = ShipComponentWeaponSuggestions.BulletRangeV1,
                    bulletRangePerExtraLevel = ShipComponentWeaponSuggestions.GetSuggestedBulletRangePerLevel(1),
                    fireRate = ShipComponentWeaponSuggestions.FireRate,
                    fireRatePerExtraLevel = 0f,
                };
                ShipComponentWeaponSuggestions.ApplyWeaponBatteryCap(ref profile.baseAtVersion1);

                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    firePower = ShipComponentWeaponSuggestions.FirePowerV1,
                    bulletSpeed = ShipComponentWeaponSuggestions.BulletSpeedV1,
                    bulletRange = ShipComponentWeaponSuggestions.BulletRangePerVersion,
                    fireRate = 0f,
                    fireRatePerExtraLevel = 0f,
                    // Cap scales linearly with firePower; v2 firePower = 2×v1 ⇒ Cap step = Cap(v1).
                    energyCap = profile.baseAtVersion1.energyCap,
                    energyCapPerExtraLevel = profile.baseAtVersion1.energyCapPerExtraLevel,
                };
            }
            else if (string.Equals(type, ShipFamilyPartTypes.WeaponCannon, StringComparison.OrdinalIgnoreCase))
            {
                // 1 shot/sec, ~4× bullet fire power. Offense + Cap-only (Cap = firePower × fireRate).
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    firePower = ShipComponentWeaponSuggestions.CannonFirePowerV1,
                    firePowerPerExtraLevel = ShipComponentWeaponSuggestions.GetSuggestedCannonFirePowerPerLevel(1),
                    bulletSpeed = ShipComponentWeaponSuggestions.CannonBulletSpeedV1,
                    bulletSpeedPerExtraLevel = ShipComponentWeaponSuggestions.GetSuggestedCannonBulletSpeedPerLevel(1),
                    bulletRange = ShipComponentWeaponSuggestions.CannonBulletRangeV1,
                    bulletRangePerExtraLevel = ShipComponentWeaponSuggestions.GetSuggestedCannonBulletRangePerLevel(1),
                    fireRate = ShipComponentWeaponSuggestions.CannonFireRate,
                    fireRatePerExtraLevel = 0f,
                };
                ShipComponentWeaponSuggestions.ApplyWeaponBatteryCap(ref profile.baseAtVersion1);

                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    firePower = ShipComponentWeaponSuggestions.CannonFirePowerV1,
                    bulletSpeed = ShipComponentWeaponSuggestions.CannonBulletSpeedV1 * 0.25f,
                    bulletRange = ShipComponentWeaponSuggestions.CannonBulletRangePerVersion,
                    fireRate = 0f,
                    fireRatePerExtraLevel = 0f,
                    energyCap = profile.baseAtVersion1.energyCap,
                    energyCapPerExtraLevel = profile.baseAtVersion1.energyCapPerExtraLevel,
                };
            }
            else if (string.Equals(type, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase))
            {
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    rammingPower = 1f,
                    healthCap = ShipComponentHealthSuggestions.GetSuggestedHealthCap(1),
                    healthRegen = ShipComponentHealthSuggestions.GetSuggestedHealthRegen(1),
                    maxGems = 8f,
                    maxPeople = ShipComponentPeopleCapacitySuggestions.GetSuggestedPeopleCapacity(1),
                };
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    rammingPower = 0.12f,
                    healthCap = ShipComponentHealthSuggestions.HealthCapPerVersion,
                    healthRegen = ShipComponentHealthSuggestions.HealthCapPerVersion
                        * ShipComponentHealthSuggestions.HealthRegenFractionOfCap,
                    maxGems = 8f,
                    maxPeople = ShipComponentPeopleCapacitySuggestions.PeopleCapacityV1,
                };
            }
            else if (string.Equals(type, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
            {
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    healthCap = ShipComponentHealthSuggestions.GetSuggestedHealthCap(1),
                    healthRegen = ShipComponentHealthSuggestions.GetSuggestedHealthRegen(1),
                    maxGems = 8f,
                    maxPeople = ShipComponentPeopleCapacitySuggestions.GetSuggestedPeopleCapacity(1),
                    tractorBeamDistance = ShipComponentTractorBeamSuggestions.GetSuggestedTractorDistance(1),
                    tractorBeamPower = ShipComponentTractorBeamSuggestions.GetSuggestedTractorPower(1),
                };
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    healthCap = ShipComponentHealthSuggestions.HealthCapPerVersion,
                    healthRegen = ShipComponentHealthSuggestions.HealthCapPerVersion
                        * ShipComponentHealthSuggestions.HealthRegenFractionOfCap,
                    maxGems = 8f,
                    maxPeople = ShipComponentPeopleCapacitySuggestions.PeopleCapacityV1,
                    tractorBeamDistance = ShipComponentTractorBeamSuggestions.TractorDistancePerVersion,
                    tractorBeamPower = ShipComponentTractorBeamSuggestions.TractorPowerPerVersion,
                };
            }
            else
            {
                // Hull — light health filler; mass still comes from prefab scale.
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    healthCap = ShipComponentHealthSuggestions.GetSuggestedHealthCap(1) * 0.5f,
                    healthRegen = ShipComponentHealthSuggestions.GetSuggestedHealthRegen(1) * 0.5f,
                };
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    healthCap = ShipComponentHealthSuggestions.HealthCapPerVersion * 0.5f,
                    healthRegen = ShipComponentHealthSuggestions.HealthCapPerVersion
                        * ShipComponentHealthSuggestions.HealthRegenFractionOfCap * 0.5f,
                };
            }

            // Bake *PerLevel into the row so Inspector / Scan see the same numbers.
            profile.EnsureAuthoredPerLevelFilled();
            return profile;
        }

        static ShipComponentAbilityStats SeedStatsHeuristic(
            string partType,
            int version,
            IReadOnlyList<ShipComponentStatCategory> categories)
        {
            var profile = CreateDefaultProfile(string.IsNullOrEmpty(partType) ? "Part" : partType);
            return profile.EvaluateAtVersion(version);
        }
    }
}
