using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Component counts and weapon transforms parsed from a chassis prefab hierarchy
    /// (e.g. AstroEagle_Weapon, AstroEagle_Engine_2). Used to apply real stats and effects.
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
        /// <summary>Transforms for Cockpit components (cannon fire positions). Any name starting with family_Cockpit counts, e.g. AstroEagle_Cockpit, AstroEagle_Cockpit_Base_1.</summary>
        public List<Transform> cockpitCannonTransforms = new List<Transform>();
        /// <summary>Engine component transforms (for engine VFX and attribute scaling).</summary>
        public List<Transform> engineTransforms = new List<Transform>();
        /// <summary>Thruster component transforms (for thruster VFX and attribute scaling).</summary>
        public List<Transform> thrusterTransforms = new List<Transform>();
        /// <summary>Cockpit component transforms (for attribute scaling: Health, People, Energy).</summary>
        public List<Transform> cockpitTransforms = new List<Transform>();
        /// <summary>Wing component transforms (for attribute scaling: Gems, Health, HealthRegen, TurnSpeed).</summary>
        public List<Transform> wingTransforms = new List<Transform>();
        /// <summary>Part/Hull component transforms (for attribute scaling: Health, HealthRegen, Gems, People).</summary>
        public List<Transform> partTransforms = new List<Transform>();

        /// <summary>Sum of scale factors (avg of x,y,z) per component; used as bonus multiplier.</summary>
        public float engineScaleTotal;
        public float thrusterScaleTotal;
        public float wingScaleTotal;
        public float tailScaleTotal;
        public float finScaleTotal;
        public float cockpitScaleTotal;
        public float partScaleTotal;
        /// <summary>Scale total for Cockpit cannon transforms (all Cockpit types).</summary>
        public float cockpitCannonScaleTotal;
        /// <summary>Per-weapon scale factor for muzzle/damage (same order as weaponTransforms).</summary>
        public List<float> weaponScales = new List<float>();

        /// <summary>True when we have at least one cannon (from Cockpit count).</summary>
        public bool HasCannons => cockpitCannonCount > 0;
        /// <summary>Cannon count from all Cockpit components (AstroEagle_Cockpit, AstroEagle_Cockpit_Base_1, etc.). One cannon per Cockpit.</summary>
        public int cockpitCannonCount;
        /// <summary>Legacy: true when we had weapon transforms. Cannons now come from Cockpit.</summary>
        public bool HasWeapons => weaponTransforms != null && weaponTransforms.Count > 0;

        /// <summary>Scale factor from a transform (average of localScale x,y,z).</summary>
        public static float GetScaleFactor(Transform t)
        {
            if (t == null) return 1f;
            Vector3 s = t.localScale;
            return (s.x + s.y + s.z) / 3f;
        }

        /// <summary>
        /// Scan direct children of root for names like "AstroEagle_Weapon", "AstroEagle_Engine_2".
        /// Second segment (after first '_') is the component type; family prefix is ignored.
        /// </summary>
        public static ChassisComponentStats FromTransform(Transform root, string familyPrefix = "AstroEagle")
        {
            var stats = new ChassisComponentStats();
            if (root == null) return stats;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null) continue;
                string name = child.name;
                if (string.IsNullOrEmpty(name)) continue;

                string componentType = ParseComponentType(name, familyPrefix);
                if (string.IsNullOrEmpty(componentType)) continue;

                string rest = name.Substring(familyPrefix.Length + 1);
                float scaleFactor = GetScaleFactor(child);

                switch (componentType)
                {
                    case "Engine": stats.engineCount++; stats.engineScaleTotal += scaleFactor; stats.engineTransforms.Add(child); break;
                    case "Thruster": stats.thrusterCount++; stats.thrusterScaleTotal += scaleFactor; stats.thrusterTransforms.Add(child); break;
                    case "Wing": stats.wingCount++; stats.wingScaleTotal += scaleFactor; stats.wingTransforms.Add(child); break;
                    case "Tail": stats.tailCount++; stats.tailScaleTotal += scaleFactor; break;
                    case "Fin": stats.finCount++; stats.finScaleTotal += scaleFactor; break;
                    case "Cockpit":
                        stats.cockpitCount++;
                        stats.cockpitScaleTotal += scaleFactor;
                        stats.cockpitTransforms.Add(child);
                        // All Cockpit components are cannon fire positions (AstroEagle_Cockpit, AstroEagle_Cockpit_Base_1, etc.)
                        stats.cockpitCannonCount++;
                        stats.cockpitCannonScaleTotal += scaleFactor;
                        stats.cockpitCannonTransforms.Add(child);
                        break;
                    case "Part": stats.partCount++; stats.partScaleTotal += scaleFactor; stats.partTransforms.Add(child); break;
                    case "Weapon":
                        // Handled below by name-contains-"Weapon" so any naming convention works
                        break;
                }
            }

            // Bullets only from components with "Weapon" in the name (any hierarchy level, any naming).
            CollectWeaponTransformsRecursive(root, stats.weaponTransforms, stats.weaponScales);

            return stats;
        }

        /// <summary>
        /// Recursively find transforms whose name contains "Weapon" (case-insensitive) and add to lists.
        /// Only adds the top-level weapon component (first "Weapon" in each branch); does not recurse into it,
        /// so nested nodes like "Weapon_Muzzle" or "Weapon_FX" are not counted as extra guns.
        /// </summary>
        private static void CollectWeaponTransformsRecursive(Transform parent, List<Transform> weaponTransforms, List<float> weaponScales)
        {
            if (parent == null || weaponTransforms == null || weaponScales == null) return;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == null) continue;
                if (child.name.IndexOf("Weapon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    weaponTransforms.Add(child);
                    weaponScales.Add(GetScaleFactor(child));
                    // Do not recurse into this weapon: we only want one fire point per weapon component, not per nested "Weapon" name.
                    continue;
                }
                CollectWeaponTransformsRecursive(child, weaponTransforms, weaponScales);
            }
        }

        /// <summary>
        /// Returns the component type (second segment) if name starts with familyPrefix, else null.
        /// e.g. "AstroEagle_Weapon" -> "Weapon", "AstroEagle_Cockpit_Base_1" -> "Cockpit".
        /// </summary>
        private static string ParseComponentType(string name, string familyPrefix)
        {
            if (string.IsNullOrEmpty(familyPrefix) || !name.StartsWith(familyPrefix + "_"))
                return null;
            string rest = name.Substring(familyPrefix.Length + 1);
            int idx = rest.IndexOf('_');
            return idx < 0 ? rest : rest.Substring(0, idx);
        }
    }
}
