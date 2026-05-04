using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Networking;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using System.Collections;
using System.Collections.Generic;

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
        [SerializeField] private float targetProgressiveDurationSeconds = 2.5f;
        [Tooltip("Shortens asteroid pacing during progressive generation so loading remains visually active without feeling too slow.")]
        [SerializeField] private bool accelerateAsteroidProgressive = true;
        [Tooltip("Maximum number of asteroid delay points during progressive generation when acceleration is enabled.")]
        [SerializeField] private int maxAsteroidDelayBatches = 8;

        /// <summary>Loading progress 0-1. Synced to clients for progress bar.</summary>
        private NetworkVariable<float> loadingProgress = new NetworkVariable<float>(0f);
        /// <summary>True when map generation is complete.</summary>
        private NetworkVariable<bool> loadingComplete = new NetworkVariable<bool>(false);
        /// <summary>Server-only rolled size; replicated so clients frame the camera and <see cref="ToroidalMap"/> matches the match.</summary>
        private readonly NetworkVariable<float> syncedMapWidth = new NetworkVariable<float>(1000f);
        private readonly NetworkVariable<float> syncedMapHeight = new NetworkVariable<float>(1000f);
        /// <summary>Home + neutral planets + asteroids spawned for this match; used by joining clients to gauge replication.</summary>
        private readonly NetworkVariable<int> syncedWorldObjectCount = new NetworkVariable<int>(0);

        /// <summary>
        /// Single source-of-truth blueprint of every map entity (home + neutral + asteroid) the server placed this match.
        /// NGO automatically syncs the full list to any joining client at connect time, so every player builds the same
        /// world from one authoritative description. Server appends as it generates; clients only ever read.
        /// </summary>
        private readonly NetworkList<MapLayoutEntry> blueprint = new NetworkList<MapLayoutEntry>();
        /// <summary>Random seed used by the server for this match; replicated so clients can log/identify the same match.</summary>
        private readonly NetworkVariable<int> blueprintSeed = new NetworkVariable<int>(0);
        /// <summary>UTC unix-seconds when the server process generated this map. Used by client diagnostics to tell same-server rejoin from new-server rejoin.</summary>
        private readonly NetworkVariable<long> serverBootEpochUtc = new NetworkVariable<long>(0);

        public float LoadingProgress => loadingProgress.Value;
        public bool LoadingComplete => loadingComplete.Value;

        /// <summary>Read-only count of authoritative map entities the server has published so far.</summary>
        public int BlueprintEntryCount => blueprint != null ? blueprint.Count : 0;
        /// <summary>Server-published seed for this match (0 until <see cref="OnNetworkSpawn"/> on server, replicated to clients).</summary>
        public int BlueprintSeed => blueprintSeed != null ? blueprintSeed.Value : 0;
        /// <summary>UTC unix-seconds when the server generated this map. Same value across rejoins to the same server process.</summary>
        public long ServerBootEpochUtc => serverBootEpochUtc != null ? serverBootEpochUtc.Value : 0;

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

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer)
            {
                // Stamp identity for this server-process map so clients can tell same-server rejoin from new-server rejoin.
                if (serverBootEpochUtc.Value == 0)
                    serverBootEpochUtc.Value = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                EnsureMapGenerated();
                PushSyncedMapDimensionsToNetworkIfReady();
            }
            else
            {
                ApplySyncedToroidalMapFromNetwork();
                syncedMapWidth.OnValueChanged += OnSyncedMapDimensionsChanged;
                syncedMapHeight.OnValueChanged += OnSyncedMapDimensionsChanged;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (!IsServer)
            {
                syncedMapWidth.OnValueChanged -= OnSyncedMapDimensionsChanged;
                syncedMapHeight.OnValueChanged -= OnSyncedMapDimensionsChanged;
            }
            base.OnNetworkDespawn();
        }

        /// <summary>
        /// Joining clients: blueprint metadata is ready when the server has finished generation and the
        /// <see cref="NetworkList{MapLayoutEntry}"/> has been synced. NGO syncs the full list to a new client
        /// at connect time, so we just wait for <see cref="loadingComplete"/> AND a non-empty list.
        /// </summary>
        public bool HasClientJoinLayoutReady() =>
            IsClient && !IsServer && loadingComplete.Value && blueprint != null && blueprint.Count > 0;

        /// <summary>
        /// Joining clients: progress at end of home phase and end of neutral phase (0–1), matching spawn order in the blueprint.
        /// Used by the loading UI so "Placing planets..." / "Scattering asteroids..." align with the replay.
        /// </summary>
        public void GetJoinReplayPhaseEndProgress(out float endHomesProgress, out float endNeutralsProgress)
        {
            endHomesProgress = 0.33f;
            endNeutralsProgress = 0.66f;
            if (!IsClient || IsServer || blueprint == null || blueprint.Count == 0)
                return;
            int n = blueprint.Count;
            int homes = 0, neutrals = 0;
            for (int i = 0; i < n; i++)
            {
                MapLayoutKind k = blueprint[i].Kind;
                if (k == MapLayoutKind.Home) homes++;
                else if (k == MapLayoutKind.Neutral) neutrals++;
            }
            float inv = 1f / n;
            endHomesProgress = homes * inv;
            endNeutralsProgress = (homes + neutrals) * inv;
        }

        /// <summary>Client-only: progressive instantiate preview copies (network components stripped) in original spawn order, driven by the server-published blueprint.</summary>
        public IEnumerator CoPlayJoinLayout(Transform previewParent, System.Action<float> onProgress)
        {
            if (!IsClient || IsServer || blueprint == null || blueprint.Count == 0)
                yield break;

            // Snapshot the list so the iteration is stable even if the server appends late entries during playback.
            int snapshotCount = blueprint.Count;
            var entries = new MapLayoutEntry[snapshotCount];
            for (int i = 0; i < snapshotCount; i++)
                entries[i] = blueprint[i];

            int totalSteps = Mathf.Max(1, entries.Length);
            float stepDelay = ComputeEffectiveProgressiveDelay(totalSteps);
            float asteroidDelay = ComputeEffectiveAsteroidDelay(stepDelay);
            int neutralTemplateId = 0;

            for (int i = 0; i < entries.Length; i++)
            {
                MapLayoutEntry e = entries[i];
                GameObject prefab = e.Kind switch
                {
                    MapLayoutKind.Home => homePlanetPrefab,
                    MapLayoutKind.Neutral => planetPrefab,
                    MapLayoutKind.Asteroid => asteroidPrefab,
                    _ => null
                };
                if (prefab != null)
                {
                    GameObject go = UnityEngine.Object.Instantiate(prefab, e.Position, e.Rotation, previewParent);
                    go.transform.localScale = e.Scale;
                    if (e.Kind == MapLayoutKind.Neutral)
                    {
                        var pl = go.GetComponent<Planet>();
                        if (pl != null)
                            pl.SetTemplatePlanetId(++neutralTemplateId);
                    }
                    StripNetworkForLocalPreview(go);
                }

                onProgress?.Invoke((float)(i + 1) / totalSteps);

                bool isAst = e.Kind == MapLayoutKind.Asteroid;
                float w = isAst ? asteroidDelay : stepDelay;
                if (w > 0f)
                    yield return new WaitForSeconds(w);
                else if (isAst && (i + 1) % 12 == 0)
                    yield return null;
                else
                    yield return null;
            }

            onProgress?.Invoke(1f);
        }

        private static void StripNetworkForLocalPreview(GameObject go)
        {
            if (go == null) return;
            foreach (var nb in go.GetComponentsInChildren<NetworkBehaviour>(true))
                nb.enabled = false;
            var net = go.GetComponent<NetworkObject>();
            if (net != null)
                UnityEngine.Object.Destroy(net);
            // Preview instances must not participate in physics — they overlap replicated world objects and can steal hits.
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
            {
                if (c != null) c.enabled = false;
            }
            foreach (var r in go.GetComponentsInChildren<Rigidbody>(true))
            {
                if (r != null) r.detectCollisions = false;
            }
        }

        /// <summary>
        /// Server-only: append a single authoritative map entity to the replicated blueprint.
        /// NGO automatically syncs the addition to all connected clients (and to any future joiner via initial-state sync).
        /// </summary>
        private void RecordLayoutEntry(in MapLayoutEntry entry)
        {
            if (!IsServer) return;
            if (blueprint == null) return;
            blueprint.Add(entry);
        }


        private void OnSyncedMapDimensionsChanged(float previous, float current) => ApplySyncedToroidalMapFromNetwork();

        private void ApplySyncedToroidalMapFromNetwork()
        {
            float w = syncedMapWidth.Value;
            float h = syncedMapHeight.Value;
            if (w > 1f && h > 1f)
                ToroidalMap.SetMapSize(w, h);
        }

        /// <summary>Ensures joiners receive map bounds even if <see cref="RollAndApplyMapSize"/> ran before <see cref="NetworkObject"/> was spawned.</summary>
        private void PushSyncedMapDimensionsToNetworkIfReady()
        {
            if (!IsServer || !IsSpawned || mapWidth <= 1f || mapHeight <= 1f)
                return;
            syncedMapWidth.Value = mapWidth;
            syncedMapHeight.Value = mapHeight;
        }

        /// <summary>For clients joining an in-progress match: fraction of planets/asteroids that have spawned in locally.</summary>
        public float GetClientWorldReplicationProgress()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
                return 1f;
            int expected = syncedWorldObjectCount.Value;
            if (expected <= 0)
                return LoadingComplete ? 1f : 0f;
            int current = CountSpawnedMapContentFromSpawnManager();
            return Mathf.Clamp01(current / (float)expected);
        }

        private static int CountSpawnedMapContentFromSpawnManager()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null)
                return 0;
            int c = 0;
            foreach (var netObj in nm.SpawnManager.SpawnedObjects.Values)
            {
                if (netObj == null || !netObj.IsSpawned)
                    continue;
                if (netObj.GetComponentInChildren<Asteroid>(true) != null)
                {
                    c++;
                    continue;
                }
                if (netObj.GetComponentInChildren<Planet>(true) != null)
                    c++;
            }
            return c;
        }

        /// <summary>Called by NetworkGameManager when server starts so map is generated even if this object's OnNetworkSpawn didn't run (e.g. scene management disabled).</summary>
        public void EnsureMapGenerated()
        {
            BootTrace.Mark("MapGenerator.EnsureMapGenerated - enter");
            #region agent log
            bool srv = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
            NetworkGameManager.AgentDebugLog("H5", "MapGenerator.EnsureMapGenerated", "entry",
                "{\"isServer\":" + (srv ? "true" : "false") + ",\"hasGenerated\":" + (hasGenerated ? "true" : "false") + "}");
            #endregion
            if (hasGenerated)
            {
                // The blueprint is one-shot per server process: any second call indicates an unintended
                // regeneration path (scene reload, duplicate spawn, etc.) which would change the layout
                // mid-match and break the "single source of truth" contract clients depend on.
                Debug.LogError(
                    "[MapGenerator] EnsureMapGenerated called more than once on this server process. " +
                    "Map generation is one-shot per match — ignoring this call. If you see this on a live server, "
                    + "the previous match's blueprint is being preserved and re-served to clients (which is correct), "
                    + "but the trigger should be investigated to avoid layout churn.");
                BootTrace.Mark("MapGenerator.EnsureMapGenerated - already generated, skipping");
                return;
            }
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            {
                BootTrace.Mark("MapGenerator.EnsureMapGenerated - not server or NetworkManager missing");
                return;
            }
            hasGenerated = true;
            // Clear any pre-existing blueprint state (defensive — a fresh server process starts empty,
            // but if the NetworkBehaviour was respawned without the process restarting we still want a clean slate).
            if (blueprint != null)
                blueprint.Clear();
            if (IsSpawned && serverBootEpochUtc.Value == 0)
                serverBootEpochUtc.Value = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
                int homeN = homePlanetPrefab != null ? homePlanetCountThisMap : 0;
                int plannedAsteroids = asteroidPrefab != null ? numberOfAsteroidsThisMap : 0;
                int blueprintCount = blueprint != null ? blueprint.Count : 0;
                if (IsSpawned)
                    syncedWorldObjectCount.Value = blueprintCount;
                BootTrace.Mark("MapGenerator.EnsureMapGenerated - immediate generation finished");
                Debug.Log($"[MapGenerator] Map generated. HomePlanets: {homeN}, Planets: {(planetPrefab != null ? numberOfNeutralPlanetsThisMap : 0)}, Asteroids (planned): {plannedAsteroids}. Blueprint entries (spawned): {blueprintCount}. Seed: {blueprintSeed.Value}. ServerBootEpochUtc: {serverBootEpochUtc.Value}.");
            }

            PushSyncedMapDimensionsToNetworkIfReady();
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
            if (IsSpawned)
                blueprintSeed.Value = seed;

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
                yield return null;
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
                            Vector3 position = GetPositionInCluster(center, perCluster);
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
                            RecordLayoutEntry(new MapLayoutEntry
                            {
                                Kind = MapLayoutKind.Asteroid,
                                Position = asteroidObj.transform.position,
                                Rotation = asteroidObj.transform.rotation,
                                Scale = asteroidObj.transform.localScale,
                                HomeTeamIndex = 0,
                                ExtraFloat = size
                            });
                            spawned++;
                            completed++;
                            loadingProgress.Value = (float)completed / totalSteps;

                            if ((spawned % 12) == 0)
                                yield return null;

                            if (effectiveAsteroidDelay > 0f && spawned % effectiveAsteroidsPerBatch == 0)
                                yield return new WaitForSeconds(effectiveAsteroidDelay);
                        }
                    }
                }
            }

            loadingProgress.Value = 1f;
            SyncTeamManagerActiveTeamCountFromGeneratedHomes();
            loadingComplete.Value = true;
            int homeN = homePlanetPrefab != null ? homePlanetCountThisMap : 0;
            int plannedAsteroids = asteroidPrefab != null ? numberOfAsteroidsThisMap : 0;
            int blueprintCountFinal = blueprint != null ? blueprint.Count : 0;
            if (IsSpawned)
                syncedWorldObjectCount.Value = blueprintCountFinal;
            Debug.Log($"[MapGenerator] Map generated. HomePlanets: {homeN}, Planets: {(planetPrefab != null ? numberOfNeutralPlanetsThisMap : 0)}, Asteroids (planned): {plannedAsteroids}. Blueprint entries (spawned): {blueprintCountFinal}. Seed: {blueprintSeed.Value}. ServerBootEpochUtc: {serverBootEpochUtc.Value}.");
            BootTrace.Mark("MapGenerator.GenerateMapProgressive - finished");
        }

        private void GenerateMapImmediate()
        {
            BootTrace.Mark("MapGenerator.GenerateMapImmediate - begin");
            if (seed == 0) seed = System.Environment.TickCount;
            random = new System.Random(seed);
            if (IsSpawned)
                blueprintSeed.Value = seed;

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
            SyncTeamManagerActiveTeamCountFromGeneratedHomes();
            GenerateNeutralPlanets();
            BootTrace.Mark("MapGenerator.GenerateMapImmediate - after neutral planets");
            GenerateAsteroids();
            BootTrace.Mark("MapGenerator.GenerateMapImmediate - after asteroids");
        }

        /// <summary>Aligns <see cref="TeamManager"/> active team count with spawned home worlds (fixes missed SetActiveTeamCount when TeamManager.Instance was null during generation).</summary>
        private void SyncTeamManagerActiveTeamCountFromGeneratedHomes()
        {
            if (TeamManager.Instance == null) return;
            int n = Mathf.Clamp(homePlanetCountThisMap, MinSupportedTeams, MaxSupportedTeams);
            if (HomePlanet.AllHomePlanets != null && HomePlanet.AllHomePlanets.Count > 0)
                n = Mathf.Clamp(HomePlanet.AllHomePlanets.Count, MinSupportedTeams, MaxSupportedTeams);
            TeamManager.Instance.SetActiveTeamCountFromServer(n);
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

            RecordLayoutEntry(new MapLayoutEntry
            {
                Kind = MapLayoutKind.Home,
                Position = homePlanetObj.transform.position,
                Rotation = homePlanetObj.transform.rotation,
                Scale = homePlanetObj.transform.localScale,
                HomeTeamIndex = (byte)Mathf.Clamp(index, 0, HomeTeamsOrdered.Length - 1),
                ExtraFloat = 0f
            });
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

            RecordLayoutEntry(new MapLayoutEntry
            {
                Kind = MapLayoutKind.Neutral,
                Position = planetObj.transform.position,
                Rotation = planetObj.transform.rotation,
                Scale = planetObj.transform.localScale,
                HomeTeamIndex = 0,
                ExtraFloat = size
            });
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
                    Vector3 position = GetPositionInCluster(center, perCluster);
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
                    RecordLayoutEntry(new MapLayoutEntry
                    {
                        Kind = MapLayoutKind.Asteroid,
                        Position = asteroidObj.transform.position,
                        Rotation = asteroidObj.transform.rotation,
                        Scale = asteroidObj.transform.localScale,
                        HomeTeamIndex = 0,
                        ExtraFloat = size
                    });
                }
            }
        }

        private Vector3 GetPositionInCluster(Vector3 center, int targetClusterCount)
        {
            // Sublinear growth still scales spread with cluster size, but allows larger overall footprints.
            float coreRadius = Mathf.Clamp(8f + Mathf.Sqrt(Mathf.Max(1, targetClusterCount)) * 2.8f, 9f, 28f);
            // Keep center bias for organic clusters, but lighten it so points spread out more.
            float radius = coreRadius * Mathf.Pow((float)random.NextDouble(), 1.15f);
            // Occasional outskirts points add natural irregularity without creating hollow rings.
            if (random.NextDouble() < 0.25)
                radius += coreRadius * GetRandomFloat(0.4f, 1.1f);
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
            if (IsServer && IsSpawned)
            {
                syncedMapWidth.Value = mapWidth;
                syncedMapHeight.Value = mapHeight;
            }
            Debug.Log($"[MapGenerator] Map size (random square): {mapWidth:F0} x {mapHeight:F0}");
        }
    }
}
