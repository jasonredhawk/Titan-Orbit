using UnityEngine;
using System.Collections.Generic;

namespace TitanOrbit.Data
{
    /// <summary>
    /// CW PLANETS pack material lists for procedural planet visuals. Neutral planets draw from
    /// <see cref="Materials"/>; home planets prefer <see cref="WaterMaterials"/> (tropical water
    /// + atmosphere). Used by map generation and <see cref="Game.PlanetSpinVisualProxy"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "PlanetMaterialPool", menuName = "Titan Orbit/Planet Material Pool")]
    public class PlanetMaterialPool : ScriptableObject
    {
        [Tooltip("All planet surface materials (used for neutral planets and random pick).")]
        public List<Material> Materials = new List<Material>();

        [Tooltip("Tropical-only materials (water + atmosphere) for home planets. If empty, home planets use Materials.")]
        public List<Material> WaterMaterials = new List<Material>();

        /// <summary>
        /// Picks a random material from the appropriate list (water-preferred for home planets).
        /// </summary>
        public Material GetRandom(bool preferWater)
        {
            // --- Compute value ---
            var list = (preferWater && WaterMaterials != null && WaterMaterials.Count > 0) ? WaterMaterials : Materials;
            if (list == null || list.Count == 0) return null;
            return list[Random.Range(0, list.Count)];
        }

        public int GetRandomIndex(bool preferWater)
        {
            // --- Compute value ---
            var list = (preferWater && WaterMaterials != null && WaterMaterials.Count > 0) ? WaterMaterials : Materials;
            if (list == null || list.Count == 0) return -1;
            return Random.Range(0, list.Count);
        }

        public Material GetMaterial(int index, bool useWaterList)
        {
            // --- Compute value ---
            var list = (useWaterList && WaterMaterials != null && WaterMaterials.Count > 0) ? WaterMaterials : Materials;
            if (list == null || list.Count == 0 || index < 0) return null;
            return list[index % list.Count];
        }
    }
}
