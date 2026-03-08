using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Maps each planet to a ship family (ModularExamples subfolder). Each planet gets its own unique ship collection
    /// with the same tier progression (1-20 ships, levels 1-6). Home planet (planetId 0) uses index 0.
    /// </summary>
    [CreateAssetMenu(fileName = "PlanetShipFamilyConfig", menuName = "Titan Orbit/Planet Ship Family Config")]
    public class PlanetShipFamilyConfig : ScriptableObject
    {
        [Serializable]
        public class ShipFamilyEntry
        {
            [Tooltip("Family name matching ModularExamples subfolder (e.g. AstroEagle, CraizanStar).")]
            public string familyName;
            [Tooltip("Planet ID this family is for. 0 = home. 1, 2, 3... = captured planets.")]
            public int planetId;
            [Tooltip("Ship prefabs (1-20) from ModularExamples/{familyName}/. Populated by editor menu.")]
            public GameObject[] prefabs = new GameObject[20];
        }

        [Tooltip("Ordered list: index 0 = home planet family, index 1 = planet 1, etc. Same tier progression per family.")]
        public List<ShipFamilyEntry> families = new List<ShipFamilyEntry>();

        /// <summary>Gets the family entry for the given planet. PlanetId 0 = home; 1,2,3... map to subsequent families (cycles if more planets than families).</summary>
        public ShipFamilyEntry GetFamilyForPlanet(int planetId)
        {
            if (families == null || families.Count == 0) return null;
            int index = planetId % families.Count;
            return families[index];
        }

        /// <summary>Gets the ship prefab for chassisId (e.g. CraizanStar_05). Uses planetId to resolve family, or finds by family name in chassisId.</summary>
        public GameObject GetPrefabForChassisAndPlanet(string chassisId, int planetId)
        {
            if (string.IsNullOrEmpty(chassisId)) return null;
            ShipFamilyEntry family = GetFamilyForPlanet(planetId);
            if (family == null) return null;

            int underscoreIdx = chassisId.LastIndexOf('_');
            if (underscoreIdx < 0) return null;
            string numPart = chassisId.Substring(underscoreIdx + 1).TrimStart('0');
            if (string.IsNullOrEmpty(numPart)) numPart = "1";
            if (!int.TryParse(numPart, out int num) || num < 1 || num > 20) return null;
            int index = num - 1;

            if (family.prefabs != null && index < family.prefabs.Length && family.prefabs[index] != null)
                return family.prefabs[index];
            return null;
        }

        /// <summary>Gets prefab by chassisId, finding the family by name in chassisId (e.g. CraizanStar_05 -> CraizanStar).</summary>
        public GameObject GetPrefabByChassisId(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId) || families == null) return null;
            int underscoreIdx = chassisId.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string familyName = chassisId.Substring(0, underscoreIdx);
            string numPart = chassisId.Substring(underscoreIdx + 1).TrimStart('0');
            if (string.IsNullOrEmpty(numPart)) numPart = "1";
            if (!int.TryParse(numPart, out int num) || num < 1 || num > 20) return null;
            int index = num - 1;

            foreach (var f in families)
            {
                if (f != null && f.familyName == familyName && f.prefabs != null && index < f.prefabs.Length && f.prefabs[index] != null)
                    return f.prefabs[index];
            }
            return null;
        }

        /// <summary>Gets chassis ID for the given planet and ship index (0-19).</summary>
        public string GetChassisIdForPlanetAndIndex(int planetId, int index)
        {
            ShipFamilyEntry family = GetFamilyForPlanet(planetId);
            if (family == null || string.IsNullOrEmpty(family.familyName)) return null;
            return $"{family.familyName}_{(index + 1):00}";
        }
    }
}
