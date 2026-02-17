using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Input;
using System.Collections.Generic;

namespace TitanOrbit.AI
{
    /// <summary>
    /// Manages AI-driven enemy starships. Works regardless of debug mode.
    /// Spawns random number of AI ships per team with mining and transport behaviors
    /// </summary>
    public class AIStarshipManager : NetworkBehaviour
    {
        public static AIStarshipManager Instance { get; private set; }

        [Header("AI Spawn Settings")]
        [SerializeField] private GameObject starshipPrefab;
        [Tooltip("Minimum number of AI ships per team")]
        [SerializeField] private int minAIShipsPerTeam = 10;
        [Tooltip("Maximum number of AI ships per team (uses TeamManager.MaxPlayersPerTeam if 0)")]
        [SerializeField] private int maxAIShipsPerTeam = 0;
        [Tooltip("Percentage of ships that are miners (rest are transporters)")]
        [SerializeField] private float minerPercentage = 0.6f;

        private Dictionary<TeamManager.Team, List<Starship>> aiShipsByTeam = new Dictionary<TeamManager.Team, List<Starship>>();
        private bool hasSpawnedAI = false;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        public override void OnNetworkSpawn()
        {
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
            }
        }

        private void Update()
        {
            if (!IsServer) return;
            if (hasSpawnedAI) return;

            // Wait a bit for the scene to fully initialize
            if (Time.time < 2f) return;

            // Spawn AI ships for each team
            SpawnAIShipsForAllTeams();
            hasSpawnedAI = true;
        }

        private void SpawnAIShipsForAllTeams()
        {
            if (TeamManager.Instance == null) return;

            foreach (TeamManager.Team team in System.Enum.GetValues(typeof(TeamManager.Team)))
            {
                if (team == TeamManager.Team.None) continue;

                // Find home planet for this team
                HomePlanet homePlanet = FindHomePlanetForTeam(team);
                if (homePlanet == null) continue;

                // Random number of ships for this team (up to max players per team)
                int maxPerTeam = maxAIShipsPerTeam > 0 ? maxAIShipsPerTeam : (TeamManager.Instance?.MaxPlayersPerTeam ?? 20);
                int numShips = Random.Range(minAIShipsPerTeam, Mathf.Max(minAIShipsPerTeam, maxPerTeam) + 1);
                
                // Spawn ships
                for (int i = 0; i < numShips; i++)
                {
                    // Determine behavior type
                    AIStarshipController.AIBehaviorType behaviorType = 
                        Random.value < minerPercentage 
                            ? AIStarshipController.AIBehaviorType.Mining 
                            : AIStarshipController.AIBehaviorType.Transport;

                    SpawnAIShip(team, homePlanet, behaviorType);
                }

                Debug.Log($"Spawned {numShips} AI ships for team {team}");
            }
        }

        private void SpawnAIShip(TeamManager.Team team, HomePlanet homePlanet, AIStarshipController.AIBehaviorType behaviorType)
        {
            if (starshipPrefab == null)
            {
                // Try to load prefab from path
                starshipPrefab = Resources.Load<GameObject>("Prefabs/Starship");
                if (starshipPrefab == null)
                {
                    #if UNITY_EDITOR
                    starshipPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Starship.prefab");
                    #endif
                }
            }

            if (starshipPrefab == null)
            {
                Debug.LogError("AIStarshipManager: Starship prefab not found!");
                return;
            }

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
            GameObject shipObj = Instantiate(starshipPrefab, spawnPosition, spawnRotation);
            if (shipObj == null) return;

            // Add marker before Spawn so Starship.OnNetworkSpawn / StartInOrbitAroundHomePlanet skips repositioning
            shipObj.AddComponent<AIShipMarker>();
            // Add debug sync for visualization (line + text) - only visible when DebugMode is enabled
            shipObj.AddComponent<AIStarshipDebugSync>();

            // Get NetworkObject and spawn
            NetworkObject netObj = shipObj.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Debug.LogError("AIStarshipManager: Starship prefab missing NetworkObject component!");
                Destroy(shipObj);
                return;
            }

            // Spawn on network
            netObj.Spawn();

            // Get Starship component
            Starship starship = shipObj.GetComponent<Starship>();
            if (starship == null)
            {
                Debug.LogError("AIStarshipManager: Starship prefab missing Starship component!");
                return;
            }

            // Disable PlayerInputHandler for AI ships (they don't need player input)
            PlayerInputHandler inputHandler = shipObj.GetComponent<PlayerInputHandler>();
            if (inputHandler != null)
            {
                inputHandler.enabled = false;
            }

            // Assign team only (AssignTeamAndStartInOrbit would overwrite our random spawn position)
            starship.AssignTeamOnly(team);

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

            // Set behavior type and init (OnNetworkSpawn is not called when AddComponent happens after Spawn)
            aiController.SetBehaviorType(behaviorType);
            aiController.InitFromServer(team, homePlanet);

            // Track this AI ship
            if (!aiShipsByTeam.ContainsKey(team))
            {
                aiShipsByTeam[team] = new List<Starship>();
            }
            aiShipsByTeam[team].Add(starship);

            Debug.Log($"Spawned AI {behaviorType} ship for team {team} at {spawnPosition}");
        }

        private HomePlanet FindHomePlanetForTeam(TeamManager.Team team)
        {
            HomePlanet[] homePlanets = Object.FindObjectsOfType<HomePlanet>();
            foreach (var hp in homePlanets)
            {
                if (hp != null && hp.AssignedTeam == team)
                {
                    return hp;
                }
            }
            return null;
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
