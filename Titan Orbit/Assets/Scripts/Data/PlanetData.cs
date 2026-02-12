using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// ScriptableObject containing planet statistics and configuration
    /// </summary>
    [CreateAssetMenu(fileName = "New Planet Data", menuName = "Titan Orbit/Planet Data")]
    public class PlanetData : ScriptableObject
    {
        [Header("Planet Stats")]
        public float baseMaxPopulation = 100f;
        public float baseGrowthRate = 1f;
        public float baseSize = 1f;

        [Header("Upgrade Costs")]
        public float maxPopulationUpgradeCost = 500f;
        public float growthRateUpgradeCost = 300f;

        [Header("Visual")]
        public Sprite planetSprite;
        public GameObject planetPrefab;
        public Color planetColor = Color.white;
    }
}
