using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Component transforms parsed from a chassis prefab hierarchy
    /// (e.g. AstroEagle_Weapon, AstroEagle_Engine_2). Used for propulsion VFX and attribute-upgrade scaling.
    /// </summary>
    public class ChassisComponentStats
    {
        public int engineCount;
        public int thrusterCount;
        public int wingCount;
        public int tailCount;
        public int finCount;
        public int cockpitCount;
        public int partCount;
        public List<Transform> weaponTransforms = new List<Transform>();
        public List<Transform> cockpitCannonTransforms = new List<Transform>();
        public List<Transform> engineTransforms = new List<Transform>();
        public List<Transform> thrusterTransforms = new List<Transform>();
        public List<Transform> cockpitTransforms = new List<Transform>();
        public List<Transform> wingTransforms = new List<Transform>();
        public List<Transform> partTransforms = new List<Transform>();

        public float engineScaleTotal;
        public float engineScaleMax;
        public float thrusterScaleTotal;
        public float wingScaleTotal;
        public float tailScaleTotal;
        public float finScaleTotal;
        public float cockpitScaleTotal;
        public float partScaleTotal;
        public float cockpitCannonScaleTotal;
        public List<float> weaponScales = new List<float>();

        public bool HasCannons => cockpitCannonCount > 0;
        public int cockpitCannonCount;
        public bool HasWeapons => weaponTransforms != null && weaponTransforms.Count > 0;

        public static float GetScaleFactor(Transform t)
        {
            if (t == null) return 1f;
            Vector3 s = t.localScale;
            return (s.x + s.y + s.z) / 3f;
        }

        public static ChassisComponentStats FromTransform(Transform root, string familyPrefix = "AstroEagle")
        {
            var stats = new ChassisComponentStats();
            if (root == null)
                return stats;

            CollectComponentTransformsDirectOnly(root, stats, familyPrefix);
            CollectComponentTransformsRecursive(root, stats, familyPrefix, addToTotals: false, rootForSkip: root);
            CollectWeaponTransformsRecursive(root, stats.weaponTransforms, stats.weaponScales);
            return stats;
        }

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

        public static float ComputeComponentMassFromTransform(Transform prefabRoot, string familyPrefix = "AstroEagle")
        {
            if (prefabRoot == null)
                return 0f;
            return FromTransform(prefabRoot, familyPrefix).ComputeComponentMass();
        }

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

        static void CollectComponentTransformsRecursive(
            Transform parent,
            ChassisComponentStats stats,
            string familyPrefix,
            bool addToTotals,
            Transform rootForSkip = null)
        {
            if (parent == null || stats == null)
                return;

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

        static string ParseComponentType(string name, string familyPrefix)
        {
            if (string.IsNullOrEmpty(familyPrefix) || !name.StartsWith(familyPrefix + "_"))
                return null;

            string rest = name.Substring(familyPrefix.Length + 1);
            int idx = rest.IndexOf('_');
            return idx < 0 ? rest : rest.Substring(0, idx);
        }

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
