using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Designer-authored planet profile: population caps, growth, upgrade costs, and visual prefab.
    /// ScriptableObject — created via Assets → Create → Titan Orbit → Planet Data. Used by legacy
    /// planet presentation and tuning tools; authoritative planet sim reads baked ECS components
    /// at runtime. Pair with <see cref="MapGenerationSettings"/> for procedural spawn counts/sizes.
    /// </summary>
    [CreateAssetMenu(fileName = "New Planet Data", menuName = "Titan Orbit/Planet Data")]
    public class PlanetData : ScriptableObject
    {
        [Header("Planet Stats")]
        /// <summary>[TITAN-ORBIT] Starting population cap before upgrades.</summary>
        public float baseMaxPopulation = 100f;

        /// <summary>[TITAN-ORBIT] People per second growth at base level.</summary>
        public float baseGrowthRate = 1f;

        /// <summary>[TITAN-ORBIT] Visual/world scale multiplier for this planet type.</summary>
        public float baseSize = 1f;

        [Header("Upgrade Costs")]
        /// <summary>Gem cost to raise max population one step.</summary>
        public float maxPopulationUpgradeCost = 500f;

        /// <summary>Gem cost to raise growth rate one step.</summary>
        public float growthRateUpgradeCost = 300f;

        [Header("Visual")]
        /// <summary>[UNITY] 2D sprite for UI/minimap when no 3D prefab is used.</summary>
        public Sprite planetSprite;

        /// <summary>[UNITY] 3D prefab spawned or referenced by visual proxies.</summary>
        public GameObject planetPrefab;

        /// <summary>[UNITY] Tint applied to planet material in presentation.</summary>
        public Color planetColor = Color.white;
    }
}
