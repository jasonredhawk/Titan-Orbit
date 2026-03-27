using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using System.Collections;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Generates procedural maps with seed-based randomization
    /// Uses parent containers for organization. Asteroids are clustered and never overlap; count scales with map size and cluster count is rolled per map.
    /// Supports progressive generation for loading screen visualization.
    /// </summary>
    public class MapGenerator : NetworkBehaviour
    {
        [Header("Map Settings")]
        [SerializeField] private int seed = 0;
        [Tooltip("Each match uses a random square map; side length is rolled between these bounds (inclusive).")]
        [SerializeField] private float minMapSize = 300f;
        [Tooltip("Each match uses a random square map; side length is rolled between these bounds (inclusive).")]
        [SerializeField] private float maxMapSize = 1000f;

        /// <summary>Rolled once per generation; square map (width == height).</summary>
        private float mapWidth;
        private float mapHeight;

        /// <summary>Computed after map size roll; scales with map dimensions.</summary>
        private int numberOfAsteroidsThisMap;
        /// <summary>Rolled once per map between <see cref="minAsteroidClusters"/> and <see cref="maxAsteroidClusters"/>.</summary>
        private int asteroidClustersThisMap;
        /// <summary>Rolled once per map between <see cref="minNeutralPlanets"/> and <see cref="maxNeutralPlanets"/>.</summary>
        private int numberOfNeutralPlanetsThisMap;

        [Header("Home Planet Settings")]
        [SerializeField] private GameObject homePlanetPrefab;
        [Tooltip("Fallback ring radius if random packed placement fails (keeps homes spread out).")]
        [SerializeField] private float homePlanetDistance = 80f;
        [Tooltip("Minimum distance between any two home planet centers (world units).")]
        [SerializeField] private float minHomePlanetPairSeparation = 90f;
        [Tooltip("Neutral planets, asteroids, and other spawns stay at least this far from each home planet.")]
        [SerializeField] private float clearanceRadiusAroundHomePlanet = 40f;
        [Tooltip("Randomized team count lower bound (inclusive). Supports 2..5 teams.")]
        [SerializeField] private int minTeamsPerMatch = 2;
        [Tooltip("Randomized team count upper bound (inclusive). Supports 2..5 teams.")]
        [SerializeField] private int maxTeamsPerMatch = 5;

        [Header("Neutral Planet Settings")]
        [SerializeField] private GameObject planetPrefab;
        [Tooltip("Each map rolls a random neutral planet count in this range (inclusive).")]
        [SerializeField] private int minNeutralPlanets = 9;
        [Tooltip("Each map rolls a random neutral planet count in this range (inclusive).")]
        [SerializeField] private int maxNeutralPlanets = 27;
        [SerializeField] private float minPlanetSize = 9f;
        [SerializeField] private float maxPlanetSize = 18f;

        [Header("Asteroid Settings")]
        [SerializeField] private GameObject asteroidPrefab;
        [Tooltip("Asteroid count when map side length equals min map size (see Map Settings). Scales up toward max.")]
        [SerializeField] private int asteroidsAtMinMapSize = 120;
        [Tooltip("Asteroid count when map side length equals max map size (see Map Settings).")]
        [SerializeField] private int asteroidsAtMaxMapSize = 400;
        [Tooltip("Each map rolls a random cluster count in this range (inclusive).")]
        [SerializeField] private int minAsteroidClusters = 8;
        [Tooltip("Each map rolls a random cluster count in this range (inclusive).")]
        [SerializeField] private int maxAsteroidClusters = 35;
        [SerializeField] private float minAsteroidSize = 1f;   // Gem value 1-70 (smallest = current small, largest = 15x current large)
        [SerializeField] private float maxAsteroidSize = 70f;
        [SerializeField] private float minAsteroidSpacing = 1.5f;

        /// <summary>Radius at gem value 1 (keep current smallest).</summary>
        private const float MIN_ASTEROID_RADIUS = 0.35f;
        /// <summary>Radius at gem value 70 = 10x smallest (largest/smallest ratio).</summary>
        private const float MAX_ASTEROID_RADIUS = 0.35f * 10f;

        [Header("Parent Containers")]
        [SerializeField] private Transform planetsParent;
        [SerializeField] private Transform asteroidsParent;
        [SerializeField] private Transform homePlanetsParent;

        [Header("Loading Screen")]
        [Tooltip("Delay per batch during progressive generation (seconds). 0 = instant generation (no lag).")]
        [SerializeField] private float batchDelaySeconds = 0f;
        [Tooltip("Asteroids per batch during progressive generation.")]
        [SerializeField] private int asteroidsPerBatch = 20;
        [Tooltip("When enabled, world objects spawn progressively for loading-screen visualization even if batch delay is zero.")]
        [SerializeField] private bool alwaysUseProgressiveGeneration = true;
        [Tooltip("If progressive generation is enabled and batch delay is zero, auto-compute a small delay so generation remains visible.")]
        [SerializeField] private float targetProgressiveDurationSeconds = 4.5f;
        [Tooltip("Shortens asteroid pacing during progressive generation so loading remains visually active without feeling too slow.")]
        [SerializeField] private bool accelerateAsteroidProgressive = true;
        [Tooltip("Maximum number of asteroid delay points during progressive generation when acceleration is enabled.")]
        [SerializeField] private int maxAsteroidDelayBatches = 8;

        /// <summary>Loading progress 0-1. Synced to clients for progress bar.</summary>
        private NetworkVariable<float> loadingProgress = new NetworkVariable<float>(0f);
        /// <summary>True when map generation is complete.</summary>
        private NetworkVariable<bool> loadingComplete = new NetworkVariable<bool>(false);

        public float LoadingProgress => loadingProgress.Value;
        public bool LoadingComplete => loadingComplete.Value;

        private System.Random random;
        private System.Collections.Generic.List<Vector3> asteroidPositions = new System.Collections.Generic.List<Vector3>();
        private System.Collections.Generic.List<Vector3> planetPositions = new System.Collections.Generic.List<Vector3>();
        /// <summary>Home world positions for this map; drives avoidance checks for neutrals/asteroids.</summary>
        private System.Collections.Generic.List<Vector3> homePlanetPositions = new System.Collections.Generic.List<Vector3>();
        private int nextPlanetId = 1;
        private bool hasGenerated;

        private const int MinSupportedTeams = 2;
        private const int MaxSupportedTeams = 5;

        /// <summary>Rolled per map (2–5). Drives home planet count and <see cref="TeamManager"/> active teams.</summary>
        private int homePlanetCountThisMap = 3;

        private static readonly TeamManager.Team[] HomeTeamsOrdered =
        {
            TeamManager.Team.TeamA,
            TeamManager.Team.TeamB,
            TeamManager.Team.TeamC,
            TeamManager.Team.TeamD,
            TeamManager.Team.TeamE
        };

        private void Start()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnServerStarted += OnServerStarted;
                if (NetworkManager.Singleton.IsServer)
                    OnServerStarted();
            }
        }

        private void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
        }

        private void OnServerStarted()
        {
            // Generate immediately; map generator is a scene object so it exists when server starts
            EnsureMapGenerated();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                EnsureMapGenerated();
            }
        }

        /// <summary>Called by NetworkGameManager when server starts so map is generated even if this object's OnNetworkSpawn didn't run (e.g. scene management disabled).</summary>
        public void EnsureMapGenerated()
        {
            BootTrace.Mark("MapGenerator.EnsureMapGenerated - enter");
            if (hasGenerated)
            {
                BootTrace.Mark("MapGenerator.EnsureMapGenerated - already generated, skipping");
                return;
            }
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            {
                BootTrace.Mark("MapGenerator.EnsureMapGenerated - not server or NetworkManager missing");
                return;
            }
            hasGenerated = true;
            EnsureParents();
            bool useProgressiveGeneration = (alwaysUseProgressiveGeneration || batchDelaySeconds > 0f) && gameObject.activeInHierarchy;
            if (useProgressiveGeneration)
            {
                BootTrace.Mark("MapGenerator.EnsureMapGenerated - starting progressive generation");
                StartCoroutine(GenerateMapProgressive());
            }
            else
            {
                BootTrace.Mark("MapGenerator.EnsureMapGenerated - calling GenerateMapImmediate");
                GenerateMapImmediate();
                loadingProgress.Value = 1f;
                loadingComplete.Value = true;
                BootTrace.Mark("MapGenerator.EnsureMapGenerated - immediate generation finished");
                int homeN = homePlanetPrefab != null ? homePlanetCountThisMap : 0;
                int total = homeN + (planetPrefab != null ? numberOfNeutralPlanetsThisMap : 0) + (asteroidPrefab != null ? numberOfAsteroidsThisMap : 0);
                Debug.Log($"[MapGenerator] Map generated. HomePlanets: {homeN}, Planets: {(planetPrefab != null ? numberOfNeutralPlanetsThisMap : 0)}, Asteroids: {(asteroidPrefab != null ? numberOfAsteroidsThisMap : 0)}. Total objects: {total}");
            }
        }

        private void EnsureParents()
        {
            if (planetsParent == null)
            {
                var go = new GameObject("Planets");
                go.transform.SetParent(transform);
                planetsParent = go.transform;
            }
            if (asteroidsParent == null)
            {
                var go = new GameObject("Asteroids");
                go.transform.SetParent(transform);
                asteroidsParent = go.transform;
            }
            if (homePlanetsParent == null)
            {
                var go = new GameObject("HomePlanets");
                go.transform.SetParent(transform);
                homePlanetsParent = go.transform;
            }
        }

        private IEnumerator GenerateMapProgressive()
        {
            BootTrace.Mark("MapGenerator.GenerateMapProgressive - begin");
            if (seed == 0) seed = System.Environment.TickCount;
            random = new System.Random(seed);

            RollAndApplyMapSize();
            ComputeAsteroidParameters();
            RollHomeTeamCount();
            RollNeutralPlanetCount();
            asteroidPositions.Clear();
            planetPositions.Clear();
            homePlanetPositions.Clear();
            nextPlanetId = 1;

            if (homePlanetPrefab == null)
                Debug.LogWarning("MapGenerator: homePlanetPrefab is not assigned. Assign it in the Inspector (e.g. use Titan Orbit > Setup Game Scene or assign prefabs to MapGenerator).");
            if (planetPrefab == null)
                Debug.LogWarning("MapGenerator: planetPrefab is not assigned. Assign it in the Inspector.");
            if (asteroidPrefab == null)
                Debug.LogWarning("MapGenerator: asteroidPrefab is not assigned. Assign it in the Inspector.");

            int homeSteps = homePlanetPrefab != null ? homePlanetCountThisMap : 0;
            int totalSteps = homeSteps + (planetPrefab != null ? numberOfNeutralPlanetsThisMap : 0) + (asteroidPrefab != null ? numberOfAsteroidsThisMap : 0);
            if (totalSteps == 0) totalSteps = 1;
            int completed = 0;
            float effectiveBatchDelay = ComputeEffectiveProgressiveDelay(totalSteps);
            float effectiveAsteroidDelay = ComputeEffectiveAsteroidDelay(effectiveBatchDelay);

            if (homePlanetPrefab != null)
            {
                int n = Mathf.Clamp(homePlanetCountThisMap, 2, 5);
                BuildRandomHomePositionsOrFallback(n);
                if (TeamManager.Instance != null)
                    TeamManager.Instance.SetActiveTeamCountFromServer(n);

                for (int i = 0; i < n; i++)
                {
                    GenerateSingleHomePlanet(i);
                    completed++;
                    loadingProgress.Value = (float)completed / totalSteps;
                    if (effectiveBatchDelay > 0f)
                        yield return new WaitForSeconds(effectiveBatchDelay);
                }
            }
            BootTrace.Mark("MapGenerator.GenerateMapProgressive - after home planets");

            for (int i = 0; i < numberOfNeutralPlanetsThisMap; i++)
            {
                if (planetPrefab != null)
                {
                    GenerateSingleNeutralPlanet(i);
                    completed++;
                    loadingProgress.Value = (float)completed / totalSteps;
                    if (effectiveBatchDelay > 0f)
                        yield return new WaitForSeconds(effectiveBatchDelay);
                }
            }
            BootTrace.Mark("MapGenerator.GenerateMapProgressive - after neutral planets");

            if (asteroidPrefab != null)
            {
                if (AsteroidRespawnManager.Instance != null)
                    AsteroidRespawnManager.Instance.SetPrefab(asteroidPrefab);

                if (numberOfAsteroidsThisMap > 0)
                {
                    Vector3[] clusterCenters = new Vector3[asteroidClustersThisMap];
                    for (int c = 0; c < asteroidClustersThisMap; c++)
                        clusterCenters[c] = GetRandomPositionAvoiding(15f, planetPositions, new System.Collections.Generic.List<Vector3>());

                    int perCluster = Mathf.CeilToInt((float)numberOfAsteroidsThisMap / Mathf.Max(1, asteroidClustersThisMap));
                    int spawned = 0;
                    int effectiveAsteroidsPerBatch = ComputeEffectiveAsteroidsPerBatch(numberOfAsteroidsThisMap);
                    for (int c = 0; c < asteroidClustersThisMap && spawned < numberOfAsteroidsThisMap; c++)
                    {
                        Vector3 center = clusterCenters[c];
                        for (int i = 0; i < perCluster && spawned < numberOfAsteroidsThisMap; i++)
                        {
                            Vector3 position = GetPositionInCluster(center);
                            if (IsTooCloseToAny(position, minAsteroidSpacing, asteroidPositions)) continue;
                            if (IsTooCloseToAny(position, 20f, planetPositions)) continue;

                            asteroidPositions.Add(position);
                            float size = GetRandomFloat(minAsteroidSize, maxAsteroidSize);
                            float linearScale = Mathf.Lerp(MIN_ASTEROID_RADIUS, MAX_ASTEROID_RADIUS, (size - 1f) / (maxAsteroidSize - 1f));
                            Vector3 scale = new Vector3(
                                linearScale * (0.8f + (float)random.NextDouble() * 0.4f),
                                linearScale * (0.9f + (float)random.NextDouble() * 0.2f),
                                linearScale * (0.85f + (float)random.NextDouble() * 0.3f)
                            );

                            GameObject asteroidObj = Instantiate(asteroidPrefab, position, Quaternion.Euler(0, GetRandomFloat(0, 360f), 0));
                            asteroidObj.transform.localScale = scale;
                            NetworkObject netObj = asteroidObj.GetComponent<NetworkObject>();
                            if (netObj != null) netObj.Spawn();
                            spawned++;
                            completed++;
                            loadingProgress.Value = (float)completed / totalSteps;

                            if (effectiveAsteroidDelay > 0f && spawned % effectiveAsteroidsPerBatch == 0)
                                yield return new WaitForSeconds(effectiveAsteroidDelay);
                        }
                    }
                }
            }

            loadingProgress.Value = 1f;
            loadingComplete.Value = true;
            int homeN = homePlanetPrefab != null ? homePlanetCountThisMap : 0;
            int total = homeN + (planetPrefab != null ? numberOfNeutralPlanetsThisMap : 0) + (asteroidPrefab != null ? numberOfAsteroidsThisMap : 0);
            Debug.Log($"[MapGenerator] Map generated. HomePlanets: {homeN}, Planets: {(planetPrefab != null ? numberOfNeutralPlanetsThisMap : 0)}, Asteroids: {(asteroidPrefab != null ? numberOfAsteroidsThisMap : 0)}. Total objects: {total}");
            BootTrace.Mark("MapGenerator.GenerateMapProgressive - finished");
        }

        private void GenerateMapImmediate()
        {
            BootTrace.Mark("MapGenerator.GenerateMapImmediate - begin");
            if (seed == 0) seed = System.Environment.TickCount;
            random = new System.Random(seed);

            RollAndApplyMapSize();
            ComputeAsteroidParameters();
            RollHomeTeamCount();
            RollNeutralPlanetCount();
            asteroidPositions.Clear();
            planetPositions.Clear();
            homePlanetPositions.Clear();
            nextPlanetId = 1;

            if (homePlanetPrefab == null)
                Debug.LogWarning("MapGenerator: homePlanetPrefab is not assigned. Assign it in the Inspector (e.g. use Titan Orbit > Setup Game Scene or assign prefabs to MapGenerator).");
            if (planetPrefab == null)
                Debug.LogWarning("MapGenerator: planetPrefab is not assigned. Assign it in the Inspector.");
            if (asteroidPrefab == null)
                Debug.LogWarning("MapGenerator: asteroidPrefab is not assigned. Assign it in the Inspector.");

            GenerateHomePlanets();
            BootTrace.Mark("MapGenerator.GenerateMapImmediate - after home planets");
            GenerateNeutralPlanets();
            BootTrace.Mark("MapGenerator.GenerateMapImmediate - after neutral planets");
            GenerateAsteroids();
            BootTrace.Mark("MapGenerator.GenerateMapImmediate - after asteroids");
        }

        private void GenerateHomePlanets()
        {
            if (homePlanetPrefab == null) return;

            int n = Mathf.Clamp(homePlanetCountThisMap, MinSupportedTeams, MaxSupportedTeams);
            BuildRandomHomePositionsOrFallback(n);

            for (int i = 0; i < n; i++)
                GenerateSingleHomePlanet(i);

            if (TeamManager.Instance != null)
                TeamManager.Instance.SetActiveTeamCountFromServer(n);
        }

        private void GenerateSingleHomePlanet(int index)
        {
            if (homePlanetPrefab == null) return;
            if (index < 0 || index >= homePlanetPositions.Count) return;

            Vector3 position = homePlanetPositions[index];
            GameObject homePlanetObj = Instantiate(homePlanetPrefab, position, Quaternion.identity);
            HomePlanet homePlanet = homePlanetObj.GetComponent<HomePlanet>();
            NetworkObject netObj = homePlanetObj.GetComponent<NetworkObject>();
            if (homePlanet != null)
                homePlanet.InitForTeam(HomeTeamsOrdered[index]);
            if (netObj != null) netObj.Spawn();

            planetPositions.Add(position);
        }

        /// <summary>
        /// Random packed placement with minimum pairwise spacing; relaxes separation slightly on failure,
        /// then falls back to a rotated regular polygon so generation always succeeds.
        /// </summary>
        private void BuildRandomHomePositionsOrFallback(int n)
        {
            homePlanetPositions.Clear();
            float minSep = Mathf.Max(25f, minHomePlanetPairSeparation);
            const int attemptsPerPlanet = 500;

            for (int relax = 0; relax < 16; relax++)
            {
                homePlanetPositions.Clear();
                bool complete = true;
                for (int i = 0; i < n; i++)
                {
                    Vector3 chosen = Vector3.zero;
                    bool found = false;
                    for (int attempt = 0; attempt < attemptsPerPlanet; attempt++)
                    {
                        Vector3 candidate = RandomHomeCandidateOnMap();
                        if (!IsTooCloseToAny(candidate, minSep, homePlanetPositions))
                        {
                            chosen = candidate;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        complete = false;
                        break;
                    }
                    homePlanetPositions.Add(chosen);
                }
                if (complete)
                    return;
                minSep *= 0.92f;
            }

            homePlanetPositions.Clear();
            PlaceHomePlanetsFallbackRing(n);
        }

        private Vector3 RandomHomeCandidateOnMap()
        {
            float margin = Mathf.Clamp(minHomePlanetPairSeparation * 0.4f, 24f, mapWidth * 0.42f);
            float halfW = Mathf.Max(8f, mapWidth * 0.5f - margin);
            float halfH = Mathf.Max(8f, mapHeight * 0.5f - margin);
            return new Vector3(
                GetRandomFloat(-halfW, halfW),
                0f,
                GetRandomFloat(-halfH, halfH));
        }

        /// <summary>Evenly spaced ring with random rotation; pairwise distance scales with radius.</summary>
        private void PlaceHomePlanetsFallbackRing(int n)
        {
            float margin = Mathf.Max(28f, clearanceRadiusAroundHomePlanet + 20f, minHomePlanetPairSeparation * 0.35f);
            float halfSpace = Mathf.Min(mapWidth, mapHeight) * 0.5f - margin;
            if (halfSpace < 20f)
                halfSpace = Mathf.Max(15f, Mathf.Min(mapWidth, mapHeight) * 0.5f - 10f);

            float minChord = Mathf.Max(28f, minHomePlanetPairSeparation * 0.55f);
            float sinHalf = Mathf.Sin(Mathf.PI / Mathf.Max(2, n));
            float rFromChord = minChord / (2f * Mathf.Max(0.01f, sinHalf));
            float rPreferred = Mathf.Max(rFromChord, homePlanetDistance * 0.45f);
            float r = Mathf.Clamp(rPreferred, 35f, halfSpace);

            float rot = (float)random.NextDouble() * Mathf.PI * 2f;
            for (int i = 0; i < n; i++)
            {
                float ang = rot + (Mathf.PI * 2f * i) / n;
                homePlanetPositions.Add(new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r));
            }
        }

        private void GenerateSingleNeutralPlanet(int index)
        {
            if (planetPrefab == null) return;

            float minDist = 30f;
            Vector3 position = GetRandomPositionAvoiding(minDist, planetPositions, asteroidPositions);
            planetPositions.Add(position);
            float size = GetRandomFloat(minPlanetSize, maxPlanetSize);

            GameObject planetObj = Instantiate(planetPrefab, position, Quaternion.identity);
            planetObj.transform.localScale = Vector3.one * size;

            Planet planet = planetObj.GetComponent<Planet>();
            if (planet != null)
            {
                planet.SetTemplatePlanetId(nextPlanetId);
                nextPlanetId++;
            }

            NetworkObject netObj = planetObj.GetComponent<NetworkObject>();
            if (netObj != null) netObj.Spawn();
        }

        private void GenerateNeutralPlanets()
        {
            if (planetPrefab == null) return;

            for (int i = 0; i < numberOfNeutralPlanetsThisMap; i++)
                GenerateSingleNeutralPlanet(i);
        }

        private void GenerateAsteroids()
        {
            if (asteroidPrefab == null) return;

            // Ensure respawn manager can respawn asteroids (same prefab)
            if (AsteroidRespawnManager.Instance != null)
                AsteroidRespawnManager.Instance.SetPrefab(asteroidPrefab);

            if (numberOfAsteroidsThisMap <= 0) return;

            // Create cluster centers
            Vector3[] clusterCenters = new Vector3[asteroidClustersThisMap];
            for (int c = 0; c < asteroidClustersThisMap; c++)
            {
                clusterCenters[c] = GetRandomPositionAvoiding(15f, planetPositions, new System.Collections.Generic.List<Vector3>());
            }

            int perCluster = Mathf.CeilToInt((float)numberOfAsteroidsThisMap / Mathf.Max(1, asteroidClustersThisMap));
            for (int c = 0; c < asteroidClustersThisMap; c++)
            {
                Vector3 center = clusterCenters[c];
                for (int i = 0; i < perCluster && asteroidPositions.Count < numberOfAsteroidsThisMap; i++)
                {
                    Vector3 position = GetPositionInCluster(center);
                    if (IsTooCloseToAny(position, minAsteroidSpacing, asteroidPositions)) continue;
                    if (IsTooCloseToAny(position, 20f, planetPositions)) continue; // Keep asteroids away from larger planets

                    asteroidPositions.Add(position);
                    float size = GetRandomFloat(minAsteroidSize, maxAsteroidSize);
                    float linearScale = Mathf.Lerp(MIN_ASTEROID_RADIUS, MAX_ASTEROID_RADIUS, (size - 1f) / (maxAsteroidSize - 1f));
                    Vector3 scale = new Vector3(
                        linearScale * (0.8f + (float)random.NextDouble() * 0.4f),
                        linearScale * (0.9f + (float)random.NextDouble() * 0.2f),
                        linearScale * (0.85f + (float)random.NextDouble() * 0.3f)
                    );

                    GameObject asteroidObj = Instantiate(asteroidPrefab, position, Quaternion.Euler(0, GetRandomFloat(0, 360f), 0));
                    asteroidObj.transform.localScale = scale;
                    NetworkObject netObj = asteroidObj.GetComponent<NetworkObject>();
                    if (netObj != null) netObj.Spawn();
                }
            }
        }

        private Vector3 GetPositionInCluster(Vector3 center)
        {
            float radius = 12f + (float)random.NextDouble() * 8f;
            float angle = (float)random.NextDouble() * Mathf.PI * 2f;
            return center + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
        }

        private bool IsTooCloseToAny(Vector3 pos, float minDist, System.Collections.Generic.List<Vector3> positions)
        {
            foreach (var p in positions)
            {
                if (Vector3.Distance(pos, p) < minDist) return true;
            }
            return false;
        }

        private Vector3 GetRandomPositionAvoiding(float minDist, System.Collections.Generic.List<Vector3> avoid1, System.Collections.Generic.List<Vector3> avoid2)
        {
            for (int attempts = 0; attempts < 100; attempts++)
            {
                Vector3 pos = new Vector3(
                    GetRandomFloat(-mapWidth / 2f, mapWidth / 2f),
                    0f,
                    GetRandomFloat(-mapHeight / 2f, mapHeight / 2f)
                );
                if (!IsTooCloseToHomePlanets(pos) && !IsTooCloseToAny(pos, minDist, avoid1) && !IsTooCloseToAny(pos, minDist, avoid2))
                    return pos;
            }
            return new Vector3(GetRandomFloat(-mapWidth / 2f, mapWidth / 2f), 0, GetRandomFloat(-mapHeight / 2f, mapHeight / 2f));
        }


        private bool IsTooCloseToHomePlanets(Vector3 position)
        {
            float minDistance = Mathf.Max(1f, clearanceRadiusAroundHomePlanet);
            foreach (var hp in homePlanetPositions)
            {
                if (Vector3.Distance(position, hp) < minDistance)
                    return true;
            }
            return false;
        }

        private float GetRandomFloat(float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }

        private float ComputeEffectiveProgressiveDelay(int totalSteps)
        {
            if (batchDelaySeconds > 0f)
                return batchDelaySeconds;

            if (!alwaysUseProgressiveGeneration)
                return 0f;

            int safeSteps = Mathf.Max(1, totalSteps);
            float targetDuration = Mathf.Max(0f, targetProgressiveDurationSeconds);
            return targetDuration / safeSteps;
        }

        private float ComputeEffectiveAsteroidDelay(float baseDelay)
        {
            if (!accelerateAsteroidProgressive)
                return baseDelay;
            return baseDelay * 0.35f;
        }

        private int ComputeEffectiveAsteroidsPerBatch(int totalAsteroids)
        {
            int baseBatch = Mathf.Max(1, asteroidsPerBatch);
            if (!accelerateAsteroidProgressive)
                return baseBatch;

            int maxBatches = Mathf.Max(1, maxAsteroidDelayBatches);
            int acceleratedBatch = Mathf.CeilToInt((float)Mathf.Max(1, totalAsteroids) / maxBatches);
            return Mathf.Max(baseBatch, acceleratedBatch);
        }

        /// <summary>Rolls team/home-planet count using inspector-configured min/max bounds (inclusive), constrained to supported 2..5.</summary>
        private void RollHomeTeamCount()
        {
            int lo = Mathf.Clamp(Mathf.Min(minTeamsPerMatch, maxTeamsPerMatch), MinSupportedTeams, MaxSupportedTeams);
            int hi = Mathf.Clamp(Mathf.Max(minTeamsPerMatch, maxTeamsPerMatch), MinSupportedTeams, MaxSupportedTeams);
            homePlanetCountThisMap = random.Next(lo, hi + 1);
        }

        /// <summary>Rolled once per map between <see cref="minNeutralPlanets"/> and <see cref="maxNeutralPlanets"/> (inclusive).</summary>
        private void RollNeutralPlanetCount()
        {
            int lo = Mathf.Min(minNeutralPlanets, maxNeutralPlanets);
            int hi = Mathf.Max(minNeutralPlanets, maxNeutralPlanets);
            numberOfNeutralPlanetsThisMap = random.Next(lo, hi + 1);
        }

        /// <summary>Derives asteroid count from rolled map side length (linear between min/max map size bounds) and rolls cluster count.</summary>
        private void ComputeAsteroidParameters()
        {
            float lo = Mathf.Min(minMapSize, maxMapSize);
            float hi = Mathf.Max(minMapSize, maxMapSize);
            float t = hi > lo ? Mathf.InverseLerp(lo, hi, mapWidth) : 0f;
            int aLo = Mathf.Min(asteroidsAtMinMapSize, asteroidsAtMaxMapSize);
            int aHi = Mathf.Max(asteroidsAtMinMapSize, asteroidsAtMaxMapSize);
            numberOfAsteroidsThisMap = Mathf.RoundToInt(Mathf.Lerp(aLo, aHi, t));
            numberOfAsteroidsThisMap = Mathf.Max(0, numberOfAsteroidsThisMap);

            int cLo = Mathf.Min(minAsteroidClusters, maxAsteroidClusters);
            int cHi = Mathf.Max(minAsteroidClusters, maxAsteroidClusters);
            asteroidClustersThisMap = random.Next(cLo, cHi + 1);
            if (numberOfAsteroidsThisMap > 0)
                asteroidClustersThisMap = Mathf.Max(1, asteroidClustersThisMap);
        }

        /// <summary>Picks a random square map side length in [minMapSize, maxMapSize] and syncs <see cref="ToroidalMap"/>.</summary>
        private void RollAndApplyMapSize()
        {
            float lo = Mathf.Min(minMapSize, maxMapSize);
            float hi = Mathf.Max(minMapSize, maxMapSize);
            float size = GetRandomFloat(lo, hi);
            mapWidth = size;
            mapHeight = size;
            ToroidalMap.SetMapSize(mapWidth, mapHeight);
            Debug.Log($"[MapGenerator] Map size (random square): {mapWidth:F0} x {mapHeight:F0}");
        }
    }
}
