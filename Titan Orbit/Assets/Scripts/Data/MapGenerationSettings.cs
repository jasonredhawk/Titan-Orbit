using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Inspector-tunable bounds for procedural map generation. Each match rolls random values within
    /// these ranges on the server via <see cref="ECS.MapGenerationLogic"/>. ScriptableObject loaded
    /// through <see cref="MapGenerationSettingsCache"/> at boot. Pair with
    /// <see cref="Game.MapGenerationSettingsLoader"/> for runtime assignment.
    /// </summary>
    [CreateAssetMenu(fileName = "MapGenerationSettings", menuName = "Titan Orbit/Map Generation Settings")]
    public class MapGenerationSettings : ScriptableObject
    {
        [Header("Seed")]
        [Tooltip("0 = random seed each match. Non-zero fixes the seed for reproducible maps.")]
        public int seed;

        [Header("Map Size")]
        [Tooltip("Each match uses a random square map; side length is rolled between these bounds (inclusive).")]
        public float minMapSize = 300f;
        [Tooltip("Each match uses a random square map; side length is rolled between these bounds (inclusive).")]
        public float maxMapSize = 1000f;

        [Header("Home Planets / Teams")]
        [Tooltip("Randomized team count lower bound (inclusive). Supports 2..5 teams.")]
        public int minTeamsPerMatch = 2;
        [Tooltip("Randomized team count upper bound (inclusive). Supports 2..5 teams.")]
        public int maxTeamsPerMatch = 5;
        [Tooltip("Uniform scale for spawned home planets.")]
        public float homePlanetSize = 15f;
        [Tooltip("Starting level for home planets.")]
        public int homePlanetLevel = 3;
        [Tooltip("Fallback ring radius if random packed placement fails.")]
        public float homePlanetDistance = 80f;
        [Tooltip("Minimum toroidal distance between any two home planet centers.")]
        public float minHomePlanetPairSeparation = 90f;
        [Tooltip("Neutral planets and asteroids stay at least this far from each home planet center.")]
        public float clearanceRadiusAroundHomePlanet = 40f;

        [Header("Neutral Planets")]
        [Tooltip("Each map rolls a random neutral planet count in this range (inclusive).")]
        public int minNeutralPlanets = 9;
        [Tooltip("Each map rolls a random neutral planet count in this range (inclusive).")]
        public int maxNeutralPlanets = 27;
        public float minPlanetSize = 9f;
        public float maxPlanetSize = 18f;
        [Tooltip("When enabled, neutral starting levels are spread evenly across the level range.")]
        public bool randomizeNeutralStartingLevel = true;
        public int minNeutralStartingLevel = 1;
        public int maxNeutralStartingLevel = 3;
        [Tooltip("Extra world-space gap between planet orbit/ring zones when placing homes, neutrals, and asteroids.")]
        public float planetRingPlacementMargin = 3f;

        [Header("Asteroids")]
        [Tooltip("Asteroid count when map side length equals min map size. Scales up toward max map size.")]
        public int asteroidsAtMinMapSize = 444;
        [Tooltip("Asteroid count when map side length equals max map size.")]
        public int asteroidsAtMaxMapSize = 888;
        [Tooltip("Each map rolls a random cluster count in this range (inclusive).")]
        public int minAsteroidClusters = 8;
        [Tooltip("Each map rolls a random cluster count in this range (inclusive).")]
        public int maxAsteroidClusters = 35;
        [Tooltip("Gem value range rolled per asteroid (drives size and remaining gems).")]
        public float minAsteroidGemValue = 1f;
        public float maxAsteroidGemValue = 70f;
        public float minAsteroidSpacing = 1.5f;
    }
}
