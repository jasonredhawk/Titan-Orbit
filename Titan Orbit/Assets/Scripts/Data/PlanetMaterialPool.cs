using UnityEngine;
using System.Collections.Generic;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Assign all CW PLANETS pack materials to Materials (neutral/regular planets).
    /// WaterMaterials = Tropical only (water + atmosphere); home planets use only this list.
    /// Leave WaterMaterials empty to use Materials for home planets too.
    /// </summary>
    [CreateAssetMenu(fileName = "PlanetMaterialPool", menuName = "Titan Orbit/Planet Material Pool")]
    public class PlanetMaterialPool : ScriptableObject
    {
        [Tooltip("All planet surface materials (used for neutral planets and random pick).")]
        public List<Material> Materials = new List<Material>();

        [Tooltip("Tropical-only materials (water + atmosphere) for home planets. If empty, home planets use Materials.")]
        public List<Material> WaterMaterials = new List<Material>();

        public Material GetRandom(bool preferWater)
        {
            var list = (preferWater && WaterMaterials != null && WaterMaterials.Count > 0) ? WaterMaterials : Materials;
            if (list == null || list.Count == 0) return null;
            return list[Random.Range(0, list.Count)];
        }

        public int GetRandomIndex(bool preferWater)
        {
            var list = (preferWater && WaterMaterials != null && WaterMaterials.Count > 0) ? WaterMaterials : Materials;
            if (list == null || list.Count == 0) return -1;
            return Random.Range(0, list.Count);
        }

        public Material GetMaterial(int index, bool useWaterList)
        {
            var list = (useWaterList && WaterMaterials != null && WaterMaterials.Count > 0) ? WaterMaterials : Materials;
            if (list == null || list.Count == 0 || index < 0) return null;
            return list[index % list.Count];
        }
    }
}
