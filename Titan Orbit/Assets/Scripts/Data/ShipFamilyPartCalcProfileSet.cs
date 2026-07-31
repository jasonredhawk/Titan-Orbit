using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One discovered <b>prefab asset</b> part name (after family prefix strip) and how Scan / VFX /
    /// attribute mesh-scale treat it. Filled by Discover &amp; Classify on the shared ProfileSet.
    /// <para>
    /// <see cref="partType"/> is the <b>group</b>: shared Part Profile stats + which attribute-scale
    /// bucket the mesh grows with (e.g. Thruster Cover stays <c>Thruster</c> so it grows with jets,
    /// but <see cref="contributesAbilityStats"/> is false and VFX stays off).
    /// </para>
    /// </summary>
    [Serializable]
    public class ShipFamilyPartNameMapping
    {
        /// <summary>Normalized prefab-suffix token, e.g. Exhaust, Thrusters_Big, Cockpit_Base.</summary>
        public string discoveredName;
        /// <summary>
        /// Broad group key matching a Part Profile row (Engine, Thruster, Wing, Cockpit, Weapon, …).
        /// Also selects the attribute mesh-scale group at runtime.
        /// </summary>
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
                s.fireRatePerLevel = 0f;

            return s;
        }

        static bool IsPropulsionPartType(string type) => ShipFamilyPartTypes.IsPropulsion(type);

        static void AddScaled(ref ShipComponentAbilityStats target, ShipComponentAbilityStats source, float factor)
        {
            if (factor == 0f)
                return;
            target.firePower += source.firePower * factor;
            target.firePowerPerLevel += source.firePowerPerLevel * factor;
            target.bulletSpeed += source.bulletSpeed * factor;
            target.bulletSpeedPerLevel += source.bulletSpeedPerLevel * factor;
            target.fireRate += source.fireRate * factor;
            target.fireRatePerLevel += source.fireRatePerLevel * factor;
            target.rammingPower += source.rammingPower * factor;
            target.rammingPowerPerLevel += source.rammingPowerPerLevel * factor;
            target.healthCap += source.healthCap * factor;
            target.healthCapPerLevel += source.healthCapPerLevel * factor;
            target.healthRegen += source.healthRegen * factor;
            target.healthRegenPerLevel += source.healthRegenPerLevel * factor;
            target.energyCap += source.energyCap * factor;
            target.energyCapPerLevel += source.energyCapPerLevel * factor;
            target.energyRegen += source.energyRegen * factor;
            target.energyRegenPerLevel += source.energyRegenPerLevel * factor;
            target.moveSpeed += source.moveSpeed * factor;
            target.moveSpeedPerLevel += source.moveSpeedPerLevel * factor;
            target.accelerationCap += source.accelerationCap * factor;
            target.accelerationCapPerLevel += source.accelerationCapPerLevel * factor;
            target.turnSpeed += source.turnSpeed * factor;
            target.turnSpeedPerLevel += source.turnSpeedPerLevel * factor;
            target.maxGems += source.maxGems * factor;
            target.maxGemsPerLevel += source.maxGemsPerLevel * factor;
            target.tractorBeamDistance += source.tractorBeamDistance * factor;
            target.tractorBeamDistancePerLevel += source.tractorBeamDistancePerLevel * factor;
            target.tractorBeamPower += source.tractorBeamPower * factor;
            target.tractorBeamPowerPerLevel += source.tractorBeamPowerPerLevel * factor;
            target.maxPeople += source.maxPeople * factor;
            target.maxPeoplePerLevel += source.maxPeoplePerLevel * factor;
        }

        static void FillPerLevelIfZero(ref ShipComponentAbilityStats s, float frac)
        {
            if (s.firePowerPerLevel == 0f && s.firePower != 0f) s.firePowerPerLevel = s.firePower * frac;
            if (s.bulletSpeedPerLevel == 0f && s.bulletSpeed != 0f) s.bulletSpeedPerLevel = s.bulletSpeed * frac;
            if (s.fireRatePerLevel == 0f && s.fireRate != 0f) s.fireRatePerLevel = s.fireRate * frac;
            if (s.rammingPowerPerLevel == 0f && s.rammingPower != 0f) s.rammingPowerPerLevel = s.rammingPower * frac;
            if (s.healthCapPerLevel == 0f && s.healthCap != 0f) s.healthCapPerLevel = s.healthCap * frac;
            if (s.healthRegenPerLevel == 0f && s.healthRegen != 0f) s.healthRegenPerLevel = s.healthRegen * frac;
            if (s.energyCapPerLevel == 0f && s.energyCap != 0f) s.energyCapPerLevel = s.energyCap * frac;
            if (s.energyRegenPerLevel == 0f && s.energyRegen != 0f) s.energyRegenPerLevel = s.energyRegen * frac;
            if (s.moveSpeedPerLevel == 0f && s.moveSpeed != 0f) s.moveSpeedPerLevel = s.moveSpeed * frac;
            if (s.accelerationCapPerLevel == 0f && s.accelerationCap != 0f) s.accelerationCapPerLevel = s.accelerationCap * frac;
            if (s.turnSpeedPerLevel == 0f && s.turnSpeed != 0f) s.turnSpeedPerLevel = s.turnSpeed * frac;
            if (s.maxGemsPerLevel == 0f && s.maxGems != 0f) s.maxGemsPerLevel = s.maxGems * frac;
            if (s.tractorBeamDistancePerLevel == 0f && s.tractorBeamDistance != 0f)
                s.tractorBeamDistancePerLevel = s.tractorBeamDistance * frac;
            if (s.tractorBeamPowerPerLevel == 0f && s.tractorBeamPower != 0f)
                s.tractorBeamPowerPerLevel = s.tractorBeamPower * frac;
            if (s.maxPeoplePerLevel == 0f && s.maxPeople != 0f)
                s.maxPeoplePerLevel = Mathf.Max(0f, Mathf.RoundToInt(s.maxPeople * frac));
        }
    }

    /// <summary>
    /// Project-wide shared asset: discovered component name inventory, AI-assisted classification,
    /// per-name propulsion VFX flags/scales, and part-type calc profiles used by every
    /// <see cref="ShipFamilyDefinition"/> Scan / Populate button.
    /// Place under Resources as <c>ShipFamilyPartCalcProfileSet</c> for runtime VFX fallback.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipFamilyPartCalcProfileSet",
        menuName = "Titan Orbit/Ship Family Part Calc Profile Set")]
    public class ShipFamilyPartCalcProfileSet : ScriptableObject
    {
        public const string ResourcesAssetName = "ShipFamilyPartCalcProfileSet";

        static readonly Regex FirstDigitRegex = new Regex(@"\d+", RegexOptions.Compiled);

        [Header("Name inventory — one row per unique prefab part name (Discover & Classify)")]
        [Tooltip("Inventory of prefab suffixes (Wing_2, Thrusters_Big…). Each maps into a Part Profile group via partType. Count is large; that is expected.")]
        public List<ShipFamilyPartNameMapping> nameMappings = new List<ShipFamilyPartNameMapping>();

        [Header("Part Profiles — Cockpit, Weapon Bullet/Cannon, Wing, Engine/Thrust, Tail, Hull")]
        [Tooltip("Seven shared groups. Edit stats once per group; Scan applies to every Name Mapping in that group. Version digits in the name pick the tier. Engine/Thrust covers engines + thrusters (VFX only on thruster mounts).")]
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

            // Try alias canonical (Thrusters_Big) then base key without trailing digits.
            string alias = ShipFamilyComponentPartKey.ResolveAliasKey(key);
            if (!string.IsNullOrEmpty(alias) && _nameLookup.TryGetValue(alias, out mapping))
                return true;

            string baseKey = ShipFamilyComponentPartKey.GetBasePartKey(key);
            return !string.IsNullOrEmpty(baseKey) && _nameLookup.TryGetValue(baseKey, out mapping);
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

            // Fallback: Engine/Thrust mounts that look like thrusters get VFX; cosmetics stay dark.
            string type = ResolvePartType(componentId);
            if (!ShipFamilyPartTypes.IsPropulsion(type) || IsCosmeticPartName(componentId))
                return false;

            string n = ShipFamilyDefinition.NormalizeComponentId(componentId);
            return n.IndexOf("Thruster", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Exhaust", StringComparison.OrdinalIgnoreCase) >= 0;
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

        /// <summary>First digit in component suffix, or 1.</summary>
        public static int ExtractVersion(string componentId)
        {
            if (string.IsNullOrEmpty(componentId))
                return 1;
            Match m = FirstDigitRegex.Match(componentId);
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

        /// <summary>Merges a discovered name into nameMappings without wiping existing assignments.</summary>
        public ShipFamilyPartNameMapping MergeDiscoveredName(string discoveredName, string familyId)
        {
            if (nameMappings == null)
                nameMappings = new List<ShipFamilyPartNameMapping>();

            string key = ShipFamilyDefinition.NormalizeComponentId(discoveredName);
            if (string.IsNullOrEmpty(key))
                return null;

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
            // Seed known specials from aliases / plan examples.
            ApplySeedDefaultsForKnownName(created);
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
        public static void ApplySeedDefaultsForKnownName(ShipFamilyPartNameMapping mapping)
        {
            if (mapping == null || string.IsNullOrEmpty(mapping.discoveredName))
                return;

            string n = mapping.discoveredName;

            // --- Known jet mounts (Engine/Thrust stats + VFX) ---
            if (string.Equals(n, "Thrusters_Big", StringComparison.OrdinalIgnoreCase))
            {
                mapping.partType = ShipFamilyPartTypes.Engine;
                mapping.contributesAbilityStats = true;
                mapping.enablePropulsionVfx = true;
                mapping.propulsionVfxScale = 1.5f;
                return;
            }

            if (string.Equals(n, "Tiny_Thrusters", StringComparison.OrdinalIgnoreCase))
            {
                mapping.partType = ShipFamilyPartTypes.Engine;
                mapping.contributesAbilityStats = true;
                mapping.enablePropulsionVfx = true;
                mapping.propulsionVfxScale = 0.45f;
                return;
            }

            if (string.Equals(n, "Exhaust", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "Thrusters", StringComparison.OrdinalIgnoreCase))
            {
                mapping.partType = ShipFamilyPartTypes.Engine;
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
                    if (n.IndexOf("Thrust", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Exhaust", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) >= 0)
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
                // Jets only on thruster/exhaust-like mounts — not every Engine mesh.
                bool thrusterLike = n.IndexOf("Thruster", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Exhaust", StringComparison.OrdinalIgnoreCase) >= 0;
                mapping.enablePropulsionVfx = ShipFamilyPartTypes.IsPropulsion(mapping.partType) && thrusterLike;
                mapping.propulsionVfxScale = 1f;
            }
        }

        /// <summary>Ensures every mapped partType has a profile row (creates defaults when missing).</summary>
        public int EnsureProfilesForMappedPartTypes()
        {
            if (partProfiles == null)
                partProfiles = new List<ShipFamilyPartCalcProfile>();

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

            // Drop / merge obsolete legacy profile rows (Thruster+Engine → Engine/Thrust, Fin → Tail, …).
            var kept = new Dictionary<string, ShipFamilyPartCalcProfile>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < partProfiles.Count; i++)
            {
                var p = partProfiles[i];
                if (p == null || string.IsNullOrWhiteSpace(p.partType))
                    continue;

                string normalized = ShipFamilyPartTypes.Normalize(p.partType, null);
                bool isCore = IndexOfCoreProfile(normalized) < ShipFamilyPartTypes.CoreProfiles.Length;
                if (!isCore)
                    continue;

                p.partType = normalized;
                if (!kept.ContainsKey(normalized))
                    kept[normalized] = p;
            }

            partProfiles = new List<ShipFamilyPartCalcProfile>(kept.Values);
            InvalidateLookups();

            int created = 0;
            foreach (string type in needed)
            {
                if (TryGetProfile(type, out _))
                    continue;
                partProfiles.Add(CreateDefaultProfile(type));
                created++;
            }

            // Keep Part Profiles in the same designer order as CoreProfiles.
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

        /// <summary>Builds a default profile row for a part type from suggestion seed constants.</summary>
        public static ShipFamilyPartCalcProfile CreateDefaultProfile(string partType)
        {
            string type = ShipFamilyPartTypes.Normalize(
                string.IsNullOrEmpty(partType) ? ShipFamilyPartTypes.Hull : partType, null);

            var profile = new ShipFamilyPartCalcProfile
            {
                partType = type,
                defaultCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(type),
            };

            // Version-1 bases + per-version increments derived from seed helpers (v1 vs v2 delta).
            if (ShipFamilyPartTypes.IsPropulsion(type))
            {
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    moveSpeed = ShipPropulsionAggregation.GetSuggestedPropulsionMoveSpeed(1),
                    accelerationCap = ShipPropulsionAggregation.GetSuggestedPropulsionAccelerationCap(1),
                };
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    moveSpeed = ShipPropulsionAggregation.SuggestedPropulsionMoveSpeedPerVersion,
                    accelerationCap = ShipPropulsionAggregation.SuggestedPropulsionMoveSpeedPerVersion
                        * ShipPropulsionAggregation.SuggestedPropulsionAccelerationFractionOfMoveSpeed,
                };
                profile.perLevelFractionOverride = ShipPropulsionAggregation.PropulsionPerLevelFractionOfBase;
            }
            else if (ShipFamilyPartTypes.IsTurn(type))
            {
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    turnSpeed = ShipComponentTurnSpeedSuggestions.GetSuggestedTailTurnSpeed(1),
                };
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    turnSpeed = ShipComponentTurnSpeedSuggestions.GetSuggestedTailTurnSpeed(2)
                        - ShipComponentTurnSpeedSuggestions.GetSuggestedTailTurnSpeed(1),
                };
            }
            else if (string.Equals(type, ShipFamilyPartTypes.WeaponBullet, StringComparison.OrdinalIgnoreCase))
            {
                // 3 shots/sec. Bullet speed grows per ship level; fire rate stays flat.
                // Energy: short burst cap; regen &lt; sustained drain (see ApplyBulletBalancedEnergy).
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    firePower = ShipComponentWeaponSuggestions.FirePowerV1,
                    firePowerPerLevel = ShipComponentWeaponSuggestions.GetSuggestedFirePowerPerLevel(1),
                    bulletSpeed = ShipComponentWeaponSuggestions.BulletSpeedV1,
                    bulletSpeedPerLevel = ShipComponentWeaponSuggestions.GetSuggestedBulletSpeedPerLevel(1),
                    fireRate = ShipComponentWeaponSuggestions.FireRate,
                    fireRatePerLevel = 0f,
                };
                ShipComponentWeaponSuggestions.ApplyBulletBalancedEnergy(ref profile.baseAtVersion1);
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    firePower = ShipComponentWeaponSuggestions.FirePowerV1,
                    bulletSpeed = ShipComponentWeaponSuggestions.BulletSpeedV1,
                    fireRate = 0f,
                    fireRatePerLevel = 0f,
                    energyCap = profile.baseAtVersion1.energyCap,
                    energyRegen = profile.baseAtVersion1.energyRegen,
                };
            }
            else if (string.Equals(type, ShipFamilyPartTypes.WeaponCannon, StringComparison.OrdinalIgnoreCase))
            {
                // 1 shot/sec, ~4× bullet fire power. Cap ≈ one max Fire Power attribute shot.
                profile.baseAtVersion1 = new ShipComponentAbilityStats
                {
                    firePower = ShipComponentWeaponSuggestions.CannonFirePowerV1,
                    firePowerPerLevel = ShipComponentWeaponSuggestions.GetSuggestedCannonFirePowerPerLevel(1),
                    bulletSpeed = ShipComponentWeaponSuggestions.CannonBulletSpeedV1,
                    bulletSpeedPerLevel = ShipComponentWeaponSuggestions.GetSuggestedCannonBulletSpeedPerLevel(1),
                    fireRate = ShipComponentWeaponSuggestions.CannonFireRate,
                    fireRatePerLevel = 0f,
                };
                ShipComponentWeaponSuggestions.ApplyCannonBalancedEnergy(ref profile.baseAtVersion1);
                profile.perVersionIncrement = new ShipComponentAbilityStats
                {
                    firePower = ShipComponentWeaponSuggestions.CannonFirePowerV1,
                    bulletSpeed = ShipComponentWeaponSuggestions.CannonBulletSpeedV1 * 0.25f,
                    fireRate = 0f,
                    fireRatePerLevel = 0f,
                    energyCap = profile.baseAtVersion1.energyCap,
                    energyRegen = profile.baseAtVersion1.energyRegen,
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
