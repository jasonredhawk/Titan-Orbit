using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Inspector-tunable bounds for procedural map generation. Each match rolls random values within
    /// these ranges on the server via <see cref="ECS.MapGenerationLogic"/>. Sole asset:
    /// <c>Assets/Resources/MapGenerationSettings.asset</c>, loaded through
    /// <see cref="MapGenerationSettingsCache"/> at boot (Editor + player via <c>Resources.Load</c>).
    /// Pair with <see cref="Game.MapGenerationSettingsLoader"/> for scene assignment.
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
        [Tooltip(
            "How many non-home (neutral) planets each team starts owning. 0 = all neutrals stay unowned. " +
            "Claims are applied round-robin during map generation (each team gets one closest-to-home " +
            "neutral per pass) so planet-connection lines form like live captures. " +
            "If there are not enough neutrals for every team to get this many, ownership is spread evenly " +
            "(e.g. 4 wanted × 4 teams but only 12 neutrals → 3 each). Leftover neutrals stay unowned.")]
        [Min(0)]
        public int startingOwnedNeutralPlanetsPerTeam = 0;
        [Tooltip(
            "For each home planet and each starting owned neutral, randomly seed defense turrets. " +
            "0 = none. N = place a random count of 0..N turrets on that planet, each at a random " +
            "level from 1..N (also capped by the planet’s slot count / max turret level). " +
            "Example: 3 → up to three turrets, each level 1–3.")]
        [Min(0)]
        public int startingRandomDefenseTurretsMax = 0;
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
        [Tooltip(
            "LEGACY — asteroid Size / HP / gems are now driven by Assets/Resources/AsteroidSettings.asset " +
            "(MinSize–MaxSize, HealthPerSize, GemsPerSize). These fields are unused for spawn math " +
            "but kept so old scenes do not lose serialized data.")]
        public float minAsteroidGemValue = 1f;
        [Tooltip("LEGACY — see Assets/Resources/AsteroidSettings.asset for gem capacity (Size × GemsPerSize).")]
        public float maxAsteroidGemValue = 70f;
        public float minAsteroidSpacing = 1.5f;
    }
}
