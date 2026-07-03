using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Component transforms parsed from a chassis prefab hierarchy
    /// (e.g. AstroEagle_Engine_2, AstroEagle_Thruster). Used for propulsion VFX placement.
    /// </summary>
    public class ChassisComponentStats
    {
        public List<Transform> engineTransforms = new List<Transform>();
        public List<Transform> thrusterTransforms = new List<Transform>();

        public static ChassisComponentStats FromTransform(Transform root, string familyPrefix = "AstroEagle")
        {
            var stats = new ChassisComponentStats();
            if (root == null)
                return stats;

            CollectComponentTransformsDirectOnly(root, stats, familyPrefix);
            CollectComponentTransformsRecursive(root, stats, familyPrefix, rootForSkip: root);
            return stats;
        }

        static void CollectComponentTransformsDirectOnly(Transform root, ChassisComponentStats stats, string familyPrefix)
        {
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

                switch (componentType)
                {
                    case "Engine":
                        stats.engineTransforms.Add(child);
                        break;
                    case "Thruster":
                        stats.thrusterTransforms.Add(child);
                        break;
                }
            }
        }

        static void CollectComponentTransformsRecursive(
            Transform parent,
            ChassisComponentStats stats,
            string familyPrefix,
            Transform rootForSkip)
        {
            bool isDirectChildOfRoot = parent == rootForSkip;

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
                    if (!isDirectChildOfRoot)
                    {
                        switch (componentType)
                        {
                            case "Engine":
                                stats.engineTransforms.Add(child);
                                break;
                            case "Thruster":
                                stats.thrusterTransforms.Add(child);
                                break;
                        }
                    }

                    CollectComponentTransformsRecursive(child, stats, familyPrefix, rootForSkip);
                    continue;
                }

                CollectComponentTransformsRecursive(child, stats, familyPrefix, rootForSkip);
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
            if (n.IndexOf("_engine", System.StringComparison.Ordinal) >= 0 || n.StartsWith("engine_"))
                return "Engine";
            if (n.IndexOf("_thruster", System.StringComparison.Ordinal) >= 0 || n.StartsWith("thruster_"))
                return "Thruster";
            return null;
        }
    }
}
