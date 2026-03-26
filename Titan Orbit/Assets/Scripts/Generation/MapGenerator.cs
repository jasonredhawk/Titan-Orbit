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
    /// Uses parent containers for organization. Asteroids are clustered and never overlap.
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

        [Header("Home Planet Settings")]
        [SerializeField] private GameObject homePlanetPrefab;
        [SerializeField] private float homePlanetDistance = 80f;

        [Header("Neutral Planet Settings")]
        [SerializeField] private GameObject planetPrefab;
        [SerializeField] private int numberOfPlanets = 17;
        [SerializeField] private float minPlanetSize = 9f;
        [SerializeField] private float maxPlanetSize = 18f;

        [Header("Asteroid Settings")]
        [SerializeField] private GameObject asteroidPrefab;
        [SerializeField] private int numberOfAsteroids = 400;
        [SerializeField] private int asteroidClusters = 25;
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

        /// <summary>Loading progress 0-1. Synced to clients for progress bar.</summary>
        private NetworkVariable<float> loadingProgress = new NetworkVariable<float>(0f);
        /// <summary>True when map generation is complete.</summary>
        private NetworkVariable<bool> loadingComplete = new NetworkVariable<bool>(false);

        public float LoadingProgress => loadingProgress.Value;
        public bool LoadingComplete => loadingComplete.Value;

        private System.Random random;
        private System.Collections.Generic.List<Vector3> asteroidPositions = new System.Collections.Generic.List<Vector3>();
        private System.Collections.Generic.List<Vector3> planetPositions = new System.Collections.Generic.List<Vector3>();
        private int nextPlanetId = 1;
        private bool hasGenerated;

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
            if (batchDelaySeconds > 0f && gameObject.activeInHierarchy)
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
                int total = homeN + (planetPrefab != null ? numberOfPlanets : 0) + (asteroidPrefab != null ? numberOfAsteroids : 0);
                Debug.Log($"[MapGenerator] Map generated. HomePlanets: {homeN}, Planets: {(planetPrefab != null ? numberOfPlanets : 0)}, Asteroids: {(asteroidPrefab != null ? numberOfAsteroids : 0)}. Total objects: {total}");
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
            homePlanetCountThisMap = random.Next(2, 6); // inclusive 2..5
            asteroidPositions.Clear();
            planetPositions.Clear();
            nextPlanetId = 1;

            if (homePlanetPrefab == null)
                Debug.LogWarning("MapGenerator: homePlanetPrefab is not assigned. Assign it in the Inspector (e.g. use Titan Orbit > Setup Game Scene or assign prefabs to MapGenerator).");
            if (planetPrefab == null)
                Debug.LogWarning("MapGenerator: planetPrefab is not assigned. Assign it in the Inspector.");
            if (asteroidPrefab == null)
                Debug.LogWarning("MapGenerator: asteroidPrefab is not assigned. Assign it in the Inspector.");

            int homeSteps = homePlanetPrefab != null ? homePlanetCountThisMap : 0;
            int totalSteps = homeSteps + (planetPrefab != null ? numberOfPlanets : 0) + (asteroidPrefab != null ? numberOfAsteroids : 0);
            if (totalSteps == 0) totalSteps = 1;
            int completed = 0;

            GenerateHomePlanets();
            completed += homeSteps;
            loadingProgress.Value = (float)completed / totalSteps;
            BootTrace.Mark("MapGenerator.GenerateMapProgressive - after home planets");
            yield return new WaitForSeconds(batchDelaySeconds);

            for (int i = 0; i < numberOfPlanets; i++)
            {
                if (planetPrefab != null)
                {
                    GenerateSingleNeutralPlanet(i);
                    completed++;
                    loadingProgress.Value = (float)completed / totalSteps;
                }
                if (batchDelaySeconds > 0f && i % 2 == 1)
                    yield return new WaitForSeconds(batchDelaySeconds * 0.5f);
            }
            BootTrace.Mark("MapGenerator.GenerateMapProgressive - after neutral planets");

            if (asteroidPrefab != null)
            {
                if (AsteroidRespawnManager.Instance != null)
                    AsteroidRespawnManager.Instance.SetPrefab(asteroidPrefab);

                Vector3[] clusterCenters = new Vector3[asteroidClusters];
                for (int c = 0; c < asteroidClusters; c++)
                    clusterCenters[c] = GetRandomPositionAvoiding(15f, planetPositions, new System.Collections.Generic.List<Vector3>());

                int perCluster = Mathf.CeilToInt((float)numberOfAsteroids / asteroidClusters);
                int spawned = 0;
                for (int c = 0; c < asteroidClusters && spawned < numberOfAsteroids; c++)
                {
                    Vector3 center = clusterCenters[c];
                    for (int i = 0; i < perCluster && spawned < numberOfAsteroids; i++)
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

                        if (spawned % asteroidsPerBatch == 0)
                            yield return new WaitForSeconds(batchDelaySeconds);
                    }
                }
            }

            loadingProgress.Value = 1f;
            loadingComplete.Value = true;
            int homeN = homePlanetPrefab != null ? homePlanetCountThisMap : 0;
            int total = homeN + (planetPrefab != null ? numberOfPlanets : 0) + (asteroidPrefab != null ? numberOfAsteroids : 0);
            Debug.Log($"[MapGenerator] Map generated. HomePlanets: {homeN}, Planets: {(planetPrefab != null ? numberOfPlanets : 0)}, Asteroids: {(asteroidPrefab != null ? numberOfAsteroids : 0)}. Total objects: {total}");
            BootTrace.Mark("MapGenerator.GenerateMapProgressive - finished");
        }

        private void GenerateMapImmediate()
        {
            BootTrace.Mark("MapGenerator.GenerateMapImmediate - begin");
            if (seed == 0) seed = System.Environment.TickCount;
            random = new System.Random(seed);

            RollAndApplyMapSize();
            homePlanetCountThisMap = random.Next(2, 6); // inclusive 2..5
            asteroidPositions.Clear();
            planetPositions.Clear();
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

            int n = Mathf.Clamp(homePlanetCountThisMap, 2, 5);
            float angleStep = (Mathf.PI * 2f) / n;

            for (int i = 0; i < n; i++)
            {
                float angle = i * angleStep;
                Vector3 position = new Vector3(
                    Mathf.Cos(angle) * homePlanetDistance,
                    0f,
                    Mathf.Sin(angle) * homePlanetDistance
                );

                GameObject homePlanetObj = Instantiate(homePlanetPrefab, position, Quaternion.identity);
                HomePlanet homePlanet = homePlanetObj.GetComponent<HomePlanet>();
                NetworkObject netObj = homePlanetObj.GetComponent<NetworkObject>();
                if (homePlanet != null)
                    homePlanet.InitForTeam(HomeTeamsOrdered[i]);
                if (netObj != null) netObj.Spawn();

                planetPositions.Add(position);
            }

            if (TeamManager.Instance != null)
                TeamManager.Instance.SetActiveTeamCountFromServer(n);
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

            for (int i = 0; i < numberOfPlanets; i++)
                GenerateSingleNeutralPlanet(i);
        }

        private void GenerateAsteroids()
        {
            if (asteroidPrefab == null) return;

            // Ensure respawn manager can respawn asteroids (same prefab)
            if (AsteroidRespawnManager.Instance != null)
                AsteroidRespawnManager.Instance.SetPrefab(asteroidPrefab);

            // Create cluster centers
            Vector3[] clusterCenters = new Vector3[asteroidClusters];
            for (int c = 0; c < asteroidClusters; c++)
            {
                clusterCenters[c] = GetRandomPositionAvoiding(15f, planetPositions, new System.Collections.Generic.List<Vector3>());
            }

            int perCluster = Mathf.CeilToInt((float)numberOfAsteroids / asteroidClusters);
            for (int c = 0; c < asteroidClusters; c++)
            {
                Vector3 center = clusterCenters[c];
                for (int i = 0; i < perCluster && asteroidPositions.Count < numberOfAsteroids; i++)
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
            float minDistance = homePlanetDistance * 0.5f;
            int n = Mathf.Clamp(homePlanetCountThisMap, 2, 5);
            float angleStep = (Mathf.PI * 2f) / n;

            for (int i = 0; i < n; i++)
            {
                float angle = i * angleStep;
                Vector3 homePos = new Vector3(
                    Mathf.Cos(angle) * homePlanetDistance,
                    0f,
                    Mathf.Sin(angle) * homePlanetDistance
                );

                if (Vector3.Distance(position, homePos) < minDistance)
                    return true;
            }

            return false;
        }

        private float GetRandomFloat(float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
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
