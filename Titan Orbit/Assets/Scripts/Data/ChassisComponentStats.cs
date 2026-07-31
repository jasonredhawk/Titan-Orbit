using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [HYBRID] Parsed component transforms and scale totals from a USC chassis prefab hierarchy
    /// (e.g. AstroEagle_Weapon, AstroEagle_Engine_2). Used by <see cref="ShipPropulsionVisualApplier"/>
    /// for engine VFX placement and <see cref="ShipComponentAttributeScaleApplier"/> for upgrade-driven
    /// mesh scaling. Not ECS data — built once from GameObject hierarchy at load or in editor.
    /// <para>
    /// [TITAN-ORBIT] <see cref="FromTransform"/> classifies with Part Calc ProfileSet (mass + VFX).
    /// Attribute mesh grow must call <see cref="FilterLegacyAttributeScaleTransforms"/> so only
    /// classic USC tokens (Wing/Engine/Thruster/Cockpit/Tail/Fin/Part) scale — not Body/Cover/EngineComp.
    /// </para>
    /// </summary>
    public class ChassisComponentStats
    {
        /// <summary>Count of direct or nested Engine_* transforms discovered.</summary>
        public int engineCount;
        /// <summary>Count of Thruster_* transforms (often smaller maneuver jets).</summary>
        public int thrusterCount;
        /// <summary>Count of Wing_* transforms.</summary>
        public int wingCount;
        /// <summary>Count of Tail_* decorative transforms.</summary>
        public int tailCount;
        /// <summary>Count of Fin_* decorative transforms.</summary>
        public int finCount;
        /// <summary>Count of Cockpit_* transforms (may double as forward cannons).</summary>
        public int cockpitCount;
        /// <summary>Count of generic Part_* filler modules.</summary>
        public int partCount;

        /// <summary>World-space weapon mount transforms (name contains "Weapon").</summary>
        public List<Transform> weaponTransforms = new List<Transform>();
        /// <summary>Cockpit transforms treated as forward-firing cannons for VFX.</summary>
        public List<Transform> cockpitCannonTransforms = new List<Transform>();
        public List<Transform> engineTransforms = new List<Transform>();
        /// <summary>
        /// All Thruster-group meshes for attribute upgrade grow (includes covers/plates/holders).
        /// Not the same as <see cref="thrusterVfxTransforms"/> (jet particle mounts only).
        /// </summary>
        public List<Transform> thrusterTransforms = new List<Transform>();
        /// <summary>
        /// Mounts that spawn propulsion jet particles (<c>enablePropulsionVfx</c> only).
        /// Parallel to <see cref="thrusterVfxScales"/>.
        /// </summary>
        public List<Transform> thrusterVfxTransforms = new List<Transform>();
        public List<Transform> cockpitTransforms = new List<Transform>();
        public List<Transform> wingTransforms = new List<Transform>();
        /// <summary>
        /// Tail + Fin mounts for RotationSpeed attribute mesh grow (shared Tail Part Profile).
        /// Mass still uses separate <see cref="tailCount"/> / <see cref="finCount"/> buckets.
        /// </summary>
        public List<Transform> tailTransforms = new List<Transform>();
        public List<Transform> partTransforms = new List<Transform>();

        /// <summary>Sum of average local-scale factors across all engines — drives thrust VFX intensity.</summary>
        public float engineScaleTotal;
        /// <summary>Largest single engine scale — caps particle size on the biggest nozzle.</summary>
        public float engineScaleMax;
        public float thrusterScaleTotal;
        public float wingScaleTotal;
        public float tailScaleTotal;
        public float finScaleTotal;
        public float cockpitScaleTotal;
        public float partScaleTotal;
        public float cockpitCannonScaleTotal;
        /// <summary>Per-weapon average scale factors parallel to <see cref="weaponTransforms"/>.</summary>
        public List<float> weaponScales = new List<float>();

        /// <summary>True when at least one cockpit is counted as a forward cannon.</summary>
        public bool HasCannons => cockpitCannonCount > 0;
        /// <summary>Cockpits that also register as cannon origins.</summary>
        public int cockpitCannonCount;
        /// <summary>True when dedicated Weapon_* mounts exist on the prefab.</summary>
        public bool HasWeapons => weaponTransforms != null && weaponTransforms.Count > 0;

        /// <summary>
        /// Average of localScale x/y/z — proxy for visual "size" when modules are uniformly scaled.
        /// Returns 1 when the transform is null.
        /// </summary>
        public static float GetScaleFactor(Transform t)
        {
            if (t == null) return 1f;
            Vector3 s = t.localScale;
            return (s.x + s.y + s.z) / 3f;
        }

        /// <summary>
        /// Per-mount propulsion VFX scale parallel to <see cref="thrusterVfxTransforms"/>
        /// (from ProfileSet / family component bake). Defaults to 1 when unset.
        /// </summary>
        public List<float> thrusterVfxScales = new List<float>();

        /// <summary>
        /// Scans <paramref name="root"/> prefab hierarchy for USC-named children.
        /// Direct children contribute to counts/totals; recursive pass fills transform lists;
        /// thruster attribute-scale group is all partType Thruster; VFX mounts are separate.
        /// </summary>
        /// <param name="familyPrefix">Leading token before underscore, e.g. AstroEagle in AstroEagle_Engine_2.</param>
        /// <param name="family">Optional family for baked VFX flags; when null, uses ProfileSet / heuristics.</param>
        public static ChassisComponentStats FromTransform(
            Transform root,
            string familyPrefix = "AstroEagle",
            ShipFamilyDefinition family = null)
        {
            var stats = new ChassisComponentStats();
            if (root == null)
                return stats;

            // --- Pass 1: direct children only → authoritative counts and scale totals ---
            CollectComponentTransformsDirectOnly(root, stats, familyPrefix, family);
            // --- Pass 2: all descendants → transform lists without double-counting direct children ---
            CollectComponentTransformsRecursive(root, stats, familyPrefix, addToTotals: false, rootForSkip: root, family: family);
            // --- Pass 3: any transform whose name contains "Weapon" ---
            CollectWeaponTransformsRecursive(root, stats.weaponTransforms, stats.weaponScales);
            // --- Pass 4: jet particle mounts only (does not clear thrusterTransforms scale group) ---
            CollectPropulsionVfxMounts(root, stats, familyPrefix, family);
            return stats;
        }

        /// <summary>
        /// [TITAN-ORBIT] Heuristic mass from summed module scales. Floored at 0.5 so empty scans
        /// still produce a playable default.
        /// </summary>
        public float ComputeComponentMass()
        {
            float weaponScaleTotal = 0f;
            for (int w = 0; w < weaponScales.Count; w++)
                weaponScaleTotal += weaponScales[w];

            float mass = engineScaleTotal +
                         thrusterScaleTotal +
                         wingScaleTotal +
                         cockpitScaleTotal +
                         partScaleTotal +
                         tailScaleTotal +
                         finScaleTotal +
                         weaponScaleTotal;
            return Mathf.Max(0.5f, mass);
        }

        /// <summary>Convenience wrapper: scan prefab root then return <see cref="ComputeComponentMass"/>.</summary>
        public static float ComputeComponentMassFromTransform(Transform prefabRoot, string familyPrefix = "AstroEagle")
        {
            if (prefabRoot == null)
                return 0f;
            return FromTransform(prefabRoot, familyPrefix).ComputeComponentMass();
        }

        /// <summary>Inspects only immediate children of <paramref name="root"/> — populates counts and scale totals.</summary>
        static void CollectComponentTransformsDirectOnly(
            Transform root,
            ChassisComponentStats stats,
            string familyPrefix,
            ShipFamilyDefinition family)
        {
            if (root == null || stats == null)
                return;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null)
                    continue;

                string componentType = ResolveCanonicalPartType(child.name, familyPrefix);
                if (string.IsNullOrEmpty(componentType))
                    continue;

                float scaleFactor = GetScaleFactor(child);
                AddComponentByCanonicalType(child, componentType, scaleFactor, stats, addToTotals: true);
            }
        }

        /// <summary>
        /// Depth-first walk. When <paramref name="addToTotals"/> is false, only fills transform lists
        /// (used after direct-only pass). Skips re-adding direct children of <paramref name="rootForSkip"/>.
        /// </summary>
        static void CollectComponentTransformsRecursive(
            Transform parent,
            ChassisComponentStats stats,
            string familyPrefix,
            bool addToTotals,
            Transform rootForSkip = null,
            ShipFamilyDefinition family = null)
        {
            if (parent == null || stats == null)
                return;

            // [TITAN-ORBIT] Direct chassis children were already counted in CollectComponentTransformsDirectOnly.
            bool isDirectChildOfRoot = rootForSkip != null && parent == rootForSkip;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == null)
                    continue;

                string componentType = ResolveCanonicalPartType(child.name, familyPrefix);

                if (!string.IsNullOrEmpty(componentType))
                {
                    bool skipAdd = isDirectChildOfRoot;
                    if (!skipAdd)
                    {
                        float scaleFactor = GetScaleFactor(child);
                        AddComponentByCanonicalType(child, componentType, scaleFactor, stats, addToTotals);
                    }

                    CollectComponentTransformsRecursive(child, stats, familyPrefix, addToTotals, rootForSkip, family);
                    continue;
                }

                CollectComponentTransformsRecursive(child, stats, familyPrefix, addToTotals, rootForSkip, family);
            }
        }

        /// <summary>
        /// Fills <see cref="thrusterVfxTransforms"/> / <see cref="thrusterVfxScales"/> only.
        /// Does <b>not</b> clear <see cref="thrusterTransforms"/> — covers stay in the scale group
        /// while Place / Cover mounts stay dark for particles.
        /// </summary>
        static void CollectPropulsionVfxMounts(
            Transform root,
            ChassisComponentStats stats,
            string familyPrefix,
            ShipFamilyDefinition family)
        {
            if (root == null || stats == null)
                return;

            stats.thrusterVfxTransforms.Clear();
            stats.thrusterVfxScales.Clear();

            var profileSet = ShipFamilyPartCalcProfileSet.LoadShared();
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null || t == root)
                    continue;

                if (!TryGetComponentIdFromName(t.name, familyPrefix, out string componentId))
                    continue;

                float scale = 1f;
                bool enable = false;

                // Prefer baked family entry (populated by Scan).
                if (family != null && family.TryGetComponentEntry(componentId, out ShipFamilyComponentEntry entry)
                    && entry != null)
                {
                    enable = entry.enablePropulsionVfx;
                    scale = entry.propulsionVfxScale > 0.0001f ? entry.propulsionVfxScale : 1f;
                }
                else if (profileSet != null)
                {
                    enable = profileSet.ShouldEnablePropulsionVfx(componentId, out scale);
                }
                else
                {
                    // Last resort: Engine/Thrust mounts that look like thrusters, not cosmetics.
                    string type = ResolveCanonicalPartType(t.name, familyPrefix, profileSet);
                    enable = ShipFamilyPartTypes.IsPropulsion(type)
                        && IsThrusterLikeComponentId(componentId)
                        && !ShipFamilyPartCalcProfileSet.IsCosmeticPartName(componentId);
                }

                if (!enable)
                    continue;

                stats.thrusterVfxTransforms.Add(t);
                stats.thrusterVfxScales.Add(scale);
            }
        }

        /// <summary>Resolves Family_Rest → Rest component id (strips Unity <c>(N)</c> duplicate suffix).</summary>
        static bool TryGetComponentIdFromName(string name, string familyPrefix, out string componentId)
        {
            componentId = string.Empty;
            if (string.IsNullOrEmpty(name))
                return false;

            // [UNITY] Hierarchy duplicates append " (1)" — strip so ids match ProfileSet keys.
            string cleaned = name.Trim();
            int paren = cleaned.LastIndexOf(" (", System.StringComparison.Ordinal);
            if (paren > 0 && cleaned.EndsWith(")", System.StringComparison.Ordinal))
                cleaned = cleaned.Substring(0, paren).Trim();

            string normalized = ShipFamilyDefinition.NormalizeComponentId(cleaned);
            if (!string.IsNullOrEmpty(familyPrefix)
                && normalized.StartsWith(familyPrefix + "_", System.StringComparison.OrdinalIgnoreCase))
            {
                componentId = normalized.Substring(familyPrefix.Length + 1);
                // Strip _L / _R symmetry suffixes for lookup consistency with Discover.
                if (componentId.EndsWith("_L", System.StringComparison.OrdinalIgnoreCase)
                    || componentId.EndsWith("_R", System.StringComparison.OrdinalIgnoreCase))
                    componentId = componentId.Substring(0, componentId.Length - 2);
                return !string.IsNullOrWhiteSpace(componentId);
            }

            // No prefix — use full normalized name as id.
            componentId = normalized;
            return !string.IsNullOrWhiteSpace(componentId);
        }

        /// <summary>
        /// Canonical part type for counting (Thrusters_Big → Thruster via ProfileSet / alias).
        /// </summary>
        static string ResolveCanonicalPartType(string name, string familyPrefix) =>
            ResolveCanonicalPartType(name, familyPrefix, ShipFamilyPartCalcProfileSet.LoadShared());

        /// <summary>
        /// Canonical part type for counting. ProfileSet mapping wins so Thruster Cover stays Thruster.
        /// </summary>
        static string ResolveCanonicalPartType(
            string name,
            string familyPrefix,
            ShipFamilyPartCalcProfileSet profileSet)
        {
            if (TryGetComponentIdFromName(name, familyPrefix, out string componentId))
            {
                if (profileSet != null)
                {
                    string fromSet = profileSet.ResolvePartType(componentId);
                    // [TITAN-ORBIT] Ignore = designer opted out — do not fall through to Hull heuristics.
                    if (string.Equals(fromSet, ShipFamilyPartTypes.Ignore, System.StringComparison.OrdinalIgnoreCase))
                        return null;
                    if (!string.IsNullOrEmpty(fromSet)
                        && !string.Equals(fromSet, ShipFamilyPartTypes.Unmapped, System.StringComparison.OrdinalIgnoreCase))
                        return ShipFamilyPartTypes.Normalize(fromSet, componentId);
                }

                string fromAbility = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
                if (!string.IsNullOrEmpty(fromAbility))
                    return ShipFamilyPartTypes.Normalize(fromAbility, componentId);
            }

            string parsed = ParseComponentType(name, familyPrefix);
            if (string.IsNullOrEmpty(parsed))
                parsed = ParseComponentTypeBySubstring(name);
            if (string.IsNullOrEmpty(parsed))
                return null;

            return ShipFamilyPartTypes.Normalize(parsed, name);
        }

        /// <summary>
        /// Buckets a chassis child into engine vs thruster scale lists, wing, cockpit, tail/fin mass, or hull.
        /// </summary>
        static void AddComponentByCanonicalType(
            Transform child,
            string componentType,
            float scaleFactor,
            ChassisComponentStats stats,
            bool addToTotals)
        {
            if (child == null || stats == null || string.IsNullOrEmpty(componentType))
                return;

            string type = ShipFamilyPartTypes.Normalize(componentType, child.name);

            if (ShipFamilyPartTypes.IsPropulsion(type))
            {
                // Same Part Profile; thruster-like names use thruster attribute-scale + VFX path.
                if (IsThrusterLikeComponentId(child.name))
                {
                    if (addToTotals)
                    {
                        stats.thrusterCount++;
                        stats.thrusterScaleTotal += scaleFactor;
                    }
                    stats.thrusterTransforms.Add(child);
                }
                else
                {
                    if (addToTotals)
                    {
                        stats.engineCount++;
                        stats.engineScaleTotal += scaleFactor;
                        stats.engineScaleMax = Mathf.Max(stats.engineScaleMax, scaleFactor);
                    }
                    stats.engineTransforms.Add(child);
                }

                return;
            }

            if (string.Equals(type, ShipFamilyPartTypes.Wing, System.StringComparison.OrdinalIgnoreCase))
            {
                if (addToTotals)
                {
                    stats.wingCount++;
                    stats.wingScaleTotal += scaleFactor;
                }
                stats.wingTransforms.Add(child);
                return;
            }

            if (ShipFamilyPartTypes.IsTurn(type))
            {
                // --- Tail / Fin attribute-scale mounts ---
                // [TITAN-ORBIT] Both share the Tail Part Profile (turnSpeed → RotationSpeed grow).
                // Always collect transforms (like wings); mass totals only when addToTotals.
                stats.tailTransforms.Add(child);

                if (addToTotals)
                {
                    // Fin keyword keeps a separate mass bucket; both share Tail profile stats.
                    if (child.name.IndexOf("Fin", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        stats.finCount++;
                        stats.finScaleTotal += scaleFactor;
                    }
                    else
                    {
                        stats.tailCount++;
                        stats.tailScaleTotal += scaleFactor;
                    }
                }

                return;
            }

            if (string.Equals(type, ShipFamilyPartTypes.Cockpit, System.StringComparison.OrdinalIgnoreCase))
            {
                if (addToTotals)
                {
                    stats.cockpitCount++;
                    stats.cockpitScaleTotal += scaleFactor;
                    stats.cockpitCannonCount++;
                    stats.cockpitCannonScaleTotal += scaleFactor;
                }
                stats.cockpitTransforms.Add(child);
                stats.cockpitCannonTransforms.Add(child);
                return;
            }

            if (ShipFamilyPartTypes.IsWeapon(type))
                return; // Weapon mounts collected by name scan.

            // --- Hull / Body / Cover / Armor / Part_* (ProfileSet catch-all) ---
            // [TITAN-ORBIT] Mass totals still count every Hull piece for power-score scans.
            // Attribute mesh grow must NOT — before Part Calc, only USC Part_* modules were in
            // partTransforms. Dumping Body/Cover/Detail here made every ability upgrade swell the
            // whole ship (and compound under already-scaled wings/engines).
            if (addToTotals
                && (string.Equals(type, ShipFamilyPartTypes.Hull, System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "Part", System.StringComparison.OrdinalIgnoreCase)))
            {
                stats.partCount++;
                stats.partScaleTotal += scaleFactor;
            }

            // Only legacy Part_* modules get bottom-bar attribute scale (old case "Part").
            if (IsLegacyPartModuleName(child.name))
                stats.partTransforms.Add(child);
        }

        /// <summary>
        /// True for USC filler modules (AstroEagle_Part_1, Part_2) — the only Hull names that
        /// historically grew with gem/health attribute upgrades.
        /// </summary>
        static bool IsLegacyPartModuleName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            // [UNITY] Hierarchy duplicates append " (1)".
            string cleaned = name.Trim();
            int paren = cleaned.LastIndexOf(" (", System.StringComparison.Ordinal);
            if (paren > 0 && cleaned.EndsWith(")", System.StringComparison.Ordinal))
                cleaned = cleaned.Substring(0, paren).Trim();

            if (cleaned.StartsWith("Part_", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (cleaned.IndexOf("_Part_", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // Family_Part (no index) — rare but match old ParseComponentType "Part".
            if (cleaned.EndsWith("_Part", System.StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        static bool IsThrusterLikeComponentId(string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId))
                return false;
            return nameOrId.IndexOf("Thruster", System.StringComparison.OrdinalIgnoreCase) >= 0
                || nameOrId.IndexOf("Exhaust", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Finds transforms whose name contains "Weapon" (case-insensitive) at any depth.</summary>
        static void CollectWeaponTransformsRecursive(
            Transform parent,
            List<Transform> weaponTransforms,
            List<float> weaponScales)
        {
            if (parent == null || weaponTransforms == null || weaponScales == null)
                return;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == null)
                    continue;

                if (child.name.IndexOf("Weapon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    weaponTransforms.Add(child);
                    weaponScales.Add(GetScaleFactor(child));
                    continue;
                }

                CollectWeaponTransformsRecursive(child, weaponTransforms, weaponScales);
            }
        }

        /// <summary>Parses USC convention Family_Type_Index → returns Type (e.g. Engine from AstroEagle_Engine_2).</summary>
        static string ParseComponentType(string name, string familyPrefix)
        {
            if (string.IsNullOrEmpty(familyPrefix) || !name.StartsWith(familyPrefix + "_"))
                return null;

            string rest = name.Substring(familyPrefix.Length + 1);
            int idx = rest.IndexOf('_');
            return idx < 0 ? rest : rest.Substring(0, idx);
        }

        /// <summary>Fallback when family prefix does not match — searches for _engine, _wing, etc.</summary>
        static string ParseComponentTypeBySubstring(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string n = name.ToLowerInvariant();
            if (n.IndexOf("_cockpit", System.StringComparison.Ordinal) >= 0 || n.StartsWith("cockpit_"))
                return "Cockpit";
            if (n.IndexOf("_wing", System.StringComparison.Ordinal) >= 0 || n.StartsWith("wing_"))
                return "Wing";
            if (n.IndexOf("_engine", System.StringComparison.Ordinal) >= 0 || n.StartsWith("engine_"))
                return "Engine";
            if (n.IndexOf("_thruster", System.StringComparison.Ordinal) >= 0 || n.StartsWith("thruster_"))
                return "Thruster";
            if (n.IndexOf("_part", System.StringComparison.Ordinal) >= 0 || n.StartsWith("part_"))
                return "Part";
            if (n.IndexOf("_tail", System.StringComparison.Ordinal) >= 0 || n.StartsWith("tail_"))
                return "Tail";
            if (n.IndexOf("_fin", System.StringComparison.Ordinal) >= 0 || n.StartsWith("fin_"))
                return "Fin";
            return null;
        }

        /// <summary>
        /// Filters a transform list down to modules the pre–Part-Calc classifier would have scaled.
        /// ProfileSet maps EngineComp / CockpitCover / Tiny_Wing etc. into buckets for mass + VFX;
        /// attribute mesh grow must stay on classic USC tokens
        /// (Wing, Engine, Thruster, Cockpit, Tail, Fin, Part) or the whole ship swells on every
        /// ability upgrade.
        /// </summary>
        /// <param name="source">Transforms from <see cref="FromTransform"/> (may include ProfileSet extras).</param>
        /// <param name="familyPrefix">Family prefix used for USC <c>Family_Type_Index</c> parsing.</param>
        /// <param name="legacyType">Expected old switch label: Wing, Engine, Thruster, Cockpit, Tail, Fin, or Part.</param>
        public static List<Transform> FilterLegacyAttributeScaleTransforms(
            List<Transform> source,
            string familyPrefix,
            string legacyType)
        {
            var result = new List<Transform>();
            if (source == null || string.IsNullOrEmpty(legacyType))
                return result;

            for (int i = 0; i < source.Count; i++)
            {
                Transform t = source[i];
                if (t == null)
                    continue;
                if (!MatchesLegacyAttributeScaleType(t.name, familyPrefix, legacyType))
                    continue;
                result.Add(t);
            }

            return result;
        }

        /// <summary>
        /// Filters Tail + Fin USC tokens from <see cref="tailTransforms"/> into one list for
        /// RotationSpeed attribute mesh grow (shared Tail Part Profile).
        /// </summary>
        public static List<Transform> FilterLegacyTailAttributeScaleTransforms(
            List<Transform> source,
            string familyPrefix)
        {
            var result = FilterLegacyAttributeScaleTransforms(source, familyPrefix, "Tail");
            List<Transform> fins = FilterLegacyAttributeScaleTransforms(source, familyPrefix, "Fin");
            for (int i = 0; i < fins.Count; i++)
            {
                Transform t = fins[i];
                if (t == null || result.Contains(t))
                    continue;
                result.Add(t);
            }

            return result;
        }

        /// <summary>
        /// True when <paramref name="name"/> matches the old DirectOnly/Recursive switch label.
        /// Uses the first USC token only when it is an exact known type — does not fall through
        /// EngineComp1 → substring Engine (that path never ran before because the token was non-empty).
        /// </summary>
        public static bool MatchesLegacyAttributeScaleType(string name, string familyPrefix, string legacyType)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(legacyType))
                return false;

            string token = ParseComponentType(name, familyPrefix);
            if (!string.IsNullOrEmpty(token))
            {
                // Exact USC type token (AstroEagle_Wing_4 → Wing). Unknown tokens (EngineComp1) = no.
                return string.Equals(token, legacyType, System.StringComparison.OrdinalIgnoreCase);
            }

            // No family prefix — substring fallback (same as old Collect when Parse returned null).
            string bySub = ParseComponentTypeBySubstring(name);
            return string.Equals(bySub, legacyType, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
