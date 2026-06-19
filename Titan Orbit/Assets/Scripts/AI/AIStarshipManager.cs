using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Input;
using System.Collections;
using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Systems;

namespace TitanOrbit.AI
{
    /// <summary>
    /// Manages AI-driven enemy starships. Works regardless of debug mode.
    /// Spawns random number of AI ships per team with mining and transport behaviors
    /// </summary>
    public class AIStarshipManager : NetworkBehaviour
    {
        public static AIStarshipManager Instance { get; private set; }

        [Tooltip("Enable AI ships (editor-only; set in Inspector). No main-menu toggle.")]
        [SerializeField] private bool aiShipsEnabled = true;

        /// <summary>True when AI ships should be spawned (host/server uses this). Set in Inspector on AIStarshipManager.</summary>
        public static bool AIShipsEnabled => Instance != null && Instance.aiShipsEnabled;

        [Header("AI Spawn Settings")]
        [SerializeField] private GameObject starshipPrefab;
        [Tooltip("Ship data for AI ships (same model and weapon as your ship). Assign the same ShipData asset your player uses (e.g. Starter). If unset, uses first player ship's data when available.")]
        [SerializeField] private ShipData aiShipData;
        [Tooltip("Minimum number of AI ships per team")]
        [SerializeField] private int minAIShipsPerTeam = 2;
        [Tooltip("Maximum number of AI ships per team (uses TeamManager.MaxPlayersPerTeam if 0)")]
        [SerializeField] private int maxAIShipsPerTeam = 4;
        [Tooltip("Percentage of ships that are miners (rest are transporters)")]
        [SerializeField] private float minerPercentage = 0.6f;
        [Tooltip("Spawn at most this many AI ships per frame (avoids editor OOM / hard crashes).")]
        [SerializeField] private int shipsPerSpawnFrame = 2;
        [Tooltip("Hard cap on total AI ships for the whole match.")]
        [SerializeField] private int maxTotalAiShips = 20;

        private Dictionary<TeamManager.Team, List<Starship>> aiShipsByTeam = new Dictionary<TeamManager.Team, List<Starship>>();
        private bool hasSpawnedAI = false;
        private float serverListenStartTime = -1f;
        private float lastSpawnBlockedLogTime = -999f;
        private Coroutine spawnRoutine;
        [Tooltip("Max seconds to keep retrying AI spawn after the server starts listening.")]
        [SerializeField] private float spawnRetryTimeoutSeconds = 120f;
        [Tooltip("Minimum home planets required before spawning AI.")]
        [SerializeField] private int minHomePlanetsForSpawn = 2;

        /// <summary>Server: clear spawned AI and allow a fresh spawn after map / Netcode session reset.</summary>
        public void ResetForNewMatchSession()
        {
            if (!IsServer) return;

            foreach (var kvp in aiShipsByTeam)
            {
                if (kvp.Value == null) continue;
                for (int i = kvp.Value.Count - 1; i >= 0; i--)
                {
                    Starship ship = kvp.Value[i];
                    if (ship == null) continue;
                    NetworkObject netObj = ship.NetworkObject;
                    if (netObj != null && netObj.IsSpawned)
                        netObj.Despawn(true);
                    else
                        Destroy(ship.gameObject);
                }
                kvp.Value.Clear();
            }

            hasSpawnedAI = false;
            serverListenStartTime = Time.time;
            lastSpawnBlockedLogTime = -999f;
            if (spawnRoutine != null)
            {
                StopCoroutine(spawnRoutine);
                spawnRoutine = null;
            }
        }

        public override void OnDestroy()
        {
            if (spawnRoutine != null)
                StopCoroutine(spawnRoutine);
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        private readonly struct PendingAiSpawn
        {
            public readonly TeamManager.Team Team;
            public readonly HomePlanet Home;
            public readonly AIStarshipController.AIBehaviorType Behavior;

            public PendingAiSpawn(TeamManager.Team team, HomePlanet home, AIStarshipController.AIBehaviorType behavior)
            {
                Team = team;
                Home = home;
                Behavior = behavior;
            }
        }

        private void Awake()
        {
            BootTrace.Mark("AIStarshipManager.Awake - enter");
            if (Instance == null)
            {
                Instance = this;
                BootTrace.Mark("AIStarshipManager.Awake - instance set");
            }
            else
            {
                BootTrace.Mark("AIStarshipManager.Awake - duplicate instance, destroying");
                Destroy(gameObject);
            }
        }

        public override void OnNetworkSpawn()
        {
            BootTrace.Mark("AIStarshipManager.OnNetworkSpawn - enter (IsServer=" + IsServer + ")");
            if (IsServer)
            {
                // Initialize team lists
                foreach (TeamManager.Team team in System.Enum.GetValues(typeof(TeamManager.Team)))
                {
                    if (team != TeamManager.Team.None)
                    {
                        aiShipsByTeam[team] = new List<Starship>();
                    }
                }
                BootTrace.Mark("AIStarshipManager.OnNetworkSpawn - team lists initialized");
            }
        }

        private void Update()
        {
            if (!IsServer) return;
            if (hasSpawnedAI) return;

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
                return;

            if (serverListenStartTime < 0f)
                serverListenStartTime = Time.time;

            if (!AIShipsEnabled)
            {
                BootTrace.Mark("AIStarshipManager.Update - AIShipsDisabled, skipping spawn");
                hasSpawnedAI = true;
                return;
            }

            if (spawnRoutine == null)
                spawnRoutine = StartCoroutine(SpawnAIShipsWhenReadyRoutine());
        }

        private IEnumerator SpawnAIShipsWhenReadyRoutine()
        {
            int perFrame = Mathf.Max(1, shipsPerSpawnFrame);
            float deadline = serverListenStartTime + Mathf.Max(5f, spawnRetryTimeoutSeconds);

            while (Time.time < deadline)
            {
                if (TryGetSpawnBlockReason(out string blockReason))
                {
                    if (Time.time - lastSpawnBlockedLogTime >= 10f)
                    {
                        lastSpawnBlockedLogTime = Time.time;
                        Debug.Log("[AIStarshipManager] Waiting to spawn AI ships: " + blockReason);
                    }
                    yield return null;
                    continue;
                }

                List<PendingAiSpawn> queue = BuildSpawnQueue();
                if (queue.Count == 0)
                {
                    Debug.LogError("[AIStarshipManager] World ready but spawn queue is empty (check team/home planet mapping).");
                    break;
                }

                GameObject prefab = ResolveStarshipPrefab();
                ShipData sharedShipData = ResolveSharedShipData();
                int spawned = 0;

                for (int i = 0; i < queue.Count; i++)
                {
                    PendingAiSpawn entry = queue[i];
                    if (SpawnAIShip(entry.Team, entry.Home, entry.Behavior, prefab, sharedShipData))
                        spawned++;

                    if ((i + 1) % perFrame == 0)
                        yield return null;
                }

                hasSpawnedAI = true;
                Debug.Log($"[AIStarshipManager] Spawned {spawned} AI ships over {(spawned + perFrame - 1) / perFrame} frame(s).");
                BootTrace.Mark("AIStarshipManager - finished spawning AI ships count=" + spawned);
                spawnRoutine = null;
                yield break;
            }

            Debug.LogError("[AIStarshipManager] Timed out waiting to spawn AI ships.");
            hasSpawnedAI = true;
            spawnRoutine = null;
        }

        private List<PendingAiSpawn> BuildSpawnQueue()
        {
            var queue = new List<PendingAiSpawn>(16);
            if (TeamManager.Instance == null)
                return queue;

            int remaining = Mathf.Max(0, maxTotalAiShips);
            foreach (TeamManager.Team team in System.Enum.GetValues(typeof(TeamManager.Team)))
            {
                if (team == TeamManager.Team.None || remaining <= 0) continue;

                HomePlanet homePlanet = FindHomePlanetForTeam(team);
                if (homePlanet == null) continue;

                int maxPerTeam = maxAIShipsPerTeam > 0 ? maxAIShipsPerTeam : (TeamManager.Instance?.MaxPlayersPerTeam ?? 4);
                int minPerTeam = Mathf.Min(minAIShipsPerTeam, maxPerTeam);
                int numShips = Random.Range(minPerTeam, maxPerTeam + 1);
                numShips = Mathf.Min(numShips, remaining);
                remaining -= numShips;

                for (int i = 0; i < numShips; i++)
                {
                    AIStarshipController.AIBehaviorType behaviorType =
                        Random.value < minerPercentage
                            ? AIStarshipController.AIBehaviorType.Mining
                            : AIStarshipController.AIBehaviorType.Transport;
                    queue.Add(new PendingAiSpawn(team, homePlanet, behaviorType));
                }
            }

            return queue;
        }

        private ShipData ResolveSharedShipData()
        {
            if (aiShipData != null)
                return aiShipData;

            for (int i = 0; i < Starship.AllStarships.Count; i++)
            {
                Starship s = Starship.AllStarships[i];
                if (s == null || !s.IsSpawned) continue;
                if (s.GetComponent<AIShipMarker>() != null) continue;
                if (s.CurrentShipData != null)
                    return s.CurrentShipData;
            }

            return null;
        }

        private bool TryGetSpawnBlockReason(out string reason)
        {
            reason = null;
            if (TeamManager.Instance == null)
            {
                reason = "TeamManager not ready";
                return true;
            }

            int homeCount = HomePlanet.AllHomePlanets != null ? HomePlanet.AllHomePlanets.Count : 0;
            if (homeCount < minHomePlanetsForSpawn)
            {
                reason = "home planets not ready (have " + homeCount + ", need " + minHomePlanetsForSpawn + ")";
                return true;
            }

            bool anyAssignedHome = false;
            foreach (var hp in HomePlanet.AllHomePlanets)
            {
                if (hp != null && hp.AssignedTeam != TeamManager.Team.None)
                {
                    anyAssignedHome = true;
                    break;
                }
            }
            if (!anyAssignedHome)
            {
                reason = "no home planets assigned to teams yet";
                return true;
            }

            if (ResolveStarshipPrefab() == null)
            {
                reason = "Starship prefab not found (assign on AIStarshipManager or NetworkManager PlayerPrefab)";
                return true;
            }

            return false;
        }

        private GameObject ResolveStarshipPrefab()
        {
            if (starshipPrefab != null)
                return starshipPrefab;

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.NetworkConfig != null && nm.NetworkConfig.PlayerPrefab != null)
            {
                starshipPrefab = nm.NetworkConfig.PlayerPrefab;
                return starshipPrefab;
            }

            starshipPrefab = Resources.Load<GameObject>("Prefabs/Starship");
            if (starshipPrefab != null)
                return starshipPrefab;

#if UNITY_EDITOR
            starshipPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Starship.prefab");
#endif
            return starshipPrefab;
        }

        private bool SpawnAIShip(
            TeamManager.Team team,
            HomePlanet homePlanet,
            AIStarshipController.AIBehaviorType behaviorType,
            GameObject prefab,
            ShipData dataToApply)
        {
            if (prefab == null)
                return false;

            // Spawn OUTSIDE orbit zone (0.5–0.85 planet size) so AI doesn't start orbiting home
            float orbitRadius = homePlanet.PlanetSize * (1.2f + Random.Range(0f, 0.3f)); // 1.2–1.5 of planet size
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 spawnPosition = homePlanet.transform.position + new Vector3(
                Mathf.Cos(angle) * orbitRadius,
                0f,
                Mathf.Sin(angle) * orbitRadius
            );
            spawnPosition.y = 0f;
            spawnPosition = ToroidalMap.WrapPosition(spawnPosition);
            Quaternion spawnRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            // Instantiate starship (don't call AssignTeamAndStartInOrbit - it overwrites position)
            GameObject shipObj = Instantiate(prefab, spawnPosition, spawnRotation);
            if (shipObj == null) return false;

            // Add marker before Spawn so Starship.OnNetworkSpawn / StartInOrbitAroundHomePlanet skips repositioning
            shipObj.AddComponent<AIShipMarker>();
            // Debug sync is server-only MonoBehaviour (never add NetworkBehaviours here — breaks NGO spawn sync).
            if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                shipObj.AddComponent<AIStarshipDebugSync>();

            // Get NetworkObject and spawn
            NetworkObject netObj = shipObj.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Debug.LogError("AIStarshipManager: Starship prefab missing NetworkObject component!");
                Destroy(shipObj);
                return false;
            }

            // Spawn on network
            netObj.Spawn();

            // Get Starship component
            Starship starship = shipObj.GetComponent<Starship>();
            if (starship == null)
            {
                Debug.LogError("AIStarshipManager: Starship prefab missing Starship component!");
                return false;
            }

            // Disable PlayerInputHandler for AI ships (they don't need player input)
            PlayerInputHandler inputHandler = shipObj.GetComponent<PlayerInputHandler>();
            if (inputHandler != null)
            {
                inputHandler.enabled = false;
            }

            // Use same ship model and weapon as player when a shared ShipData was resolved once for the batch.
            if (dataToApply != null)
                starship.SetShipData(dataToApply);

            // Assign team only
            starship.AssignTeamOnly(team);
            ApplyTeamStarterChassisForAi(starship, homePlanet);

            // Ensure Rigidbody is at our spawn position (Spawn/OnNetworkSpawn might not preserve it)
            Rigidbody shipRb = shipObj.GetComponent<Rigidbody>();
            if (shipRb != null)
            {
                shipRb.position = spawnPosition;
                shipRb.linearVelocity = Vector3.zero;
                shipRb.rotation = spawnRotation;
            }

            // Add AI controller (must add before Spawn for OnNetworkSpawn, but we Spawn first - so init manually)
            AIStarshipController aiController = shipObj.GetComponent<AIStarshipController>();
            if (aiController == null)
            {
                aiController = shipObj.AddComponent<AIStarshipController>();
            }

            starship.RefreshAIControlledFlag();

            // Set behavior type and init (OnNetworkSpawn is not called when AddComponent happens after Spawn)
            aiController.SetBehaviorType(behaviorType);
            aiController.InitFromServer(team, homePlanet);

            // Track this AI ship
            if (!aiShipsByTeam.ContainsKey(team))
            {
                aiShipsByTeam[team] = new List<Starship>();
            }
            aiShipsByTeam[team].Add(starship);

            return true;
        }

        private HomePlanet FindHomePlanetForTeam(TeamManager.Team team)
        {
            foreach (var hp in HomePlanet.AllHomePlanets)
            {
                if (hp != null && hp.AssignedTeam == team)
                {
                    return hp;
                }
            }
            return null;
        }

        /// <summary>Apply the home planet's level-1 hull from the ship-family upgrade ladder (matches player store ships).</summary>
        private static void ApplyTeamStarterChassisForAi(Starship starship, HomePlanet homePlanet)
        {
            if (starship == null || homePlanet == null || CardShopSystem.Instance == null) return;

            string chassisId = CardShopSystem.Instance.GetChassisIdForUpgradeLadderSlot(
                starship, homePlanet.PlanetId, 1, 0);
            if (string.IsNullOrEmpty(chassisId))
            {
                starship.EnsureSyncedChassisForAiVisual();
                return;
            }

            GameObject prefab = CardShopSystem.Instance.GetShipPrefabForChassisId(chassisId);
            if (prefab != null)
                starship.ApplyShipVisualFromPrefab(prefab);
            starship.SetCurrentChassisId(chassisId);
            starship.SetCurrentChassisIndex(0);
        }

        public List<Starship> GetAIShipsForTeam(TeamManager.Team team)
        {
            if (aiShipsByTeam.ContainsKey(team))
            {
                return new List<Starship>(aiShipsByTeam[team]);
            }
            return new List<Starship>();
        }

        public int GetAIShipCountForTeam(TeamManager.Team team)
        {
            if (aiShipsByTeam.ContainsKey(team))
            {
                return aiShipsByTeam[team].Count;
            }
            return 0;
        }
    }
}
