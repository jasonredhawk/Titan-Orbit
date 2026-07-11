using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [HYBRID] Parsed component transforms and scale totals from a USC chassis prefab hierarchy
    /// (e.g. AstroEagle_Weapon, AstroEagle_Engine_2). Used by <see cref="ShipPropulsionVisualApplier"/>
    /// for engine VFX placement and <see cref="ShipComponentAttributeScaleApplier"/> for upgrade-driven
    /// mesh scaling. Not ECS data — built once from GameObject hierarchy at load or in editor.
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
        public List<Transform> thrusterTransforms = new List<Transform>();
        public List<Transform> cockpitTransforms = new List<Transform>();
        public List<Transform> wingTransforms = new List<Transform>();
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
        /// Scans <paramref name="root"/> prefab hierarchy for USC-named children. Two-pass collection:
        /// direct children contribute to counts/totals; recursive pass fills transform lists for VFX.
        /// </summary>
        /// <param name="familyPrefix">Leading token before underscore, e.g. AstroEagle in AstroEagle_Engine_2.</param>
        public static ChassisComponentStats FromTransform(Transform root, string familyPrefix = "AstroEagle")
        {
            var stats = new ChassisComponentStats();
            if (root == null)
                return stats;

            // --- Pass 1: direct children only → authoritative counts and scale totals ---
            CollectComponentTransformsDirectOnly(root, stats, familyPrefix);
            // --- Pass 2: all descendants → transform lists without double-counting direct children ---
            CollectComponentTransformsRecursive(root, stats, familyPrefix, addToTotals: false, rootForSkip: root);
            // --- Pass 3: any transform whose name contains "Weapon" ---
            CollectWeaponTransformsRecursive(root, stats.weaponTransforms, stats.weaponScales);
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
        static void CollectComponentTransformsDirectOnly(Transform root, ChassisComponentStats stats, string familyPrefix)
        {
            if (root == null || stats == null)
                return;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null)
                    continue;

                string componentType = ParseComponentType(child.name, familyPrefix);
                if (string.IsNullOrEmpty(componentType))
                    componentType = ParseComponentTypeBySubstring(child.name);
                if (string.IsNullOrEmpty(componentType))
                    continue;

                float scaleFactor = GetScaleFactor(child);
                switch (componentType)
                {
                    case "Engine":
                        stats.engineCount++;
                        stats.engineScaleTotal += scaleFactor;
                        stats.engineScaleMax = Mathf.Max(stats.engineScaleMax, scaleFactor);
                        stats.engineTransforms.Add(child);
                        break;
                    case "Thruster":
                        stats.thrusterCount++;
                        stats.thrusterScaleTotal += scaleFactor;
                        stats.thrusterTransforms.Add(child);
                        break;
                    case "Wing":
                        stats.wingCount++;
                        stats.wingScaleTotal += scaleFactor;
                        stats.wingTransforms.Add(child);
                        break;
                    case "Tail":
                        stats.tailCount++;
                        stats.tailScaleTotal += scaleFactor;
                        break;
                    case "Fin":
                        stats.finCount++;
                        stats.finScaleTotal += scaleFactor;
                        break;
                    case "Cockpit":
                        stats.cockpitCount++;
                        stats.cockpitScaleTotal += scaleFactor;
                        stats.cockpitTransforms.Add(child);
                        stats.cockpitCannonCount++;
                        stats.cockpitCannonScaleTotal += scaleFactor;
                        stats.cockpitCannonTransforms.Add(child);
                        break;
                    case "Part":
                        stats.partCount++;
                        stats.partScaleTotal += scaleFactor;
                        stats.partTransforms.Add(child);
                        break;
                }
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
            Transform rootForSkip = null)
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

                string componentType = ParseComponentType(child.name, familyPrefix);
                if (string.IsNullOrEmpty(componentType))
                    componentType = ParseComponentTypeBySubstring(child.name);

                if (!string.IsNullOrEmpty(componentType))
                {
                    bool skipAdd = isDirectChildOfRoot;
                    if (!skipAdd)
                    {
                        float scaleFactor = GetScaleFactor(child);
                        if (addToTotals)
                        {
                            switch (componentType)
                            {
                                case "Engine":
                                    stats.engineCount++;
                                    stats.engineScaleTotal += scaleFactor;
                                    stats.engineScaleMax = Mathf.Max(stats.engineScaleMax, scaleFactor);
                                    stats.engineTransforms.Add(child);
                                    break;
                                case "Thruster":
                                    stats.thrusterCount++;
                                    stats.thrusterScaleTotal += scaleFactor;
                                    stats.thrusterTransforms.Add(child);
                                    break;
                                case "Wing":
                                    stats.wingCount++;
                                    stats.wingScaleTotal += scaleFactor;
                                    stats.wingTransforms.Add(child);
                                    break;
                                case "Tail":
                                    stats.tailCount++;
                                    stats.tailScaleTotal += scaleFactor;
                                    break;
                                case "Fin":
                                    stats.finCount++;
                                    stats.finScaleTotal += scaleFactor;
                                    break;
                                case "Cockpit":
                                    stats.cockpitCount++;
                                    stats.cockpitScaleTotal += scaleFactor;
                                    stats.cockpitTransforms.Add(child);
                                    stats.cockpitCannonCount++;
                                    stats.cockpitCannonScaleTotal += scaleFactor;
                                    stats.cockpitCannonTransforms.Add(child);
                                    break;
                                case "Part":
                                    stats.partCount++;
                                    stats.partScaleTotal += scaleFactor;
                                    stats.partTransforms.Add(child);
                                    break;
                            }
                        }
                        else
                        {
                            switch (componentType)
                            {
                                case "Engine": stats.engineTransforms.Add(child); break;
                                case "Thruster": stats.thrusterTransforms.Add(child); break;
                                case "Wing": stats.wingTransforms.Add(child); break;
                                case "Cockpit":
                                    stats.cockpitTransforms.Add(child);
                                    stats.cockpitCannonTransforms.Add(child);
                                    break;
                                case "Part": stats.partTransforms.Add(child); break;
                            }
                        }
                    }

                    CollectComponentTransformsRecursive(child, stats, familyPrefix, addToTotals, rootForSkip);
                    continue;
                }

                CollectComponentTransformsRecursive(child, stats, familyPrefix, addToTotals, rootForSkip);
            }
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
    }
}
