using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Systems;
using TitanOrbit.Entities;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Main game state manager that handles overall game flow and state
    /// </summary>
    public class GameManager : NetworkBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("Debug")]
        [Tooltip("When enabled: one-shot asteroids, 100x gems/mining/deposit/people/growth/respawn/regen. Toggle off for normal play.")]
        [SerializeField] private bool debugMode = true;

        [Header("Game Settings")]
        [SerializeField] private int maxPlayersPerTeam = 20;
        [Tooltip("Scale exaggeration per attribute level for all ships. 0.15 = 15% bigger per upgrade. Overrides per-ship value when set > 0.")]
        [SerializeField] private float attributeScaleExaggeration = 0.15f;

        public bool DebugMode => debugMode;
        /// <summary>Attribute scale exaggeration for ship components (15% default). Ships use this when > 0, else their own value.</summary>
        public float AttributeScaleExaggeration => attributeScaleExaggeration;
        [SerializeField] private int numberOfTeams = 3;
        [SerializeField] private float matchDuration = 3600f; // 60 minutes default

        private NetworkVariable<GameState> currentGameState = new NetworkVariable<GameState>(GameState.Lobby);
        private NetworkVariable<float> matchTimer = new NetworkVariable<float>(0f);
        // #region agent log
        private static float s_lastPerfLogTime = -999f;
        private static int s_lastFrameLog = -999;
        // #endregion

        public enum GameState
        {
            Lobby,
            Starting,
            InProgress,
            Paused,
            Ended
        }

        public GameState CurrentGameState => currentGameState.Value;
        public float MatchTimer => matchTimer.Value;

        private void Awake()
        {
            BootTrace.Clear();
            BootTrace.Mark("GameManager.Awake - enter");

            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);

                BootTrace.Mark("GameManager.Awake - creating PlanetConnectionSystems");
                var systemsGo = new GameObject("PlanetConnectionSystems");
                DontDestroyOnLoad(systemsGo);
                systemsGo.AddComponent<PlanetConnectionSystem>();
                systemsGo.AddComponent<AsteroidTerritoryHighlighter>();
                systemsGo.AddComponent<PlanetConnectionShapesVisual>();
                BootTrace.Mark("GameManager.Awake - PlanetConnectionSystems created");
            }
            else
            {
                BootTrace.Mark("GameManager.Awake - duplicate instance, destroying");
                Destroy(gameObject);
            }
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                currentGameState.Value = GameState.Lobby;
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void StartMatchServerRpc()
        {
            if (currentGameState.Value == GameState.Lobby)
            {
                currentGameState.Value = GameState.Starting;
                StartMatchClientRpc();
            }
        }

        [ClientRpc]
        private void StartMatchClientRpc()
        {
            // Match starting logic
            Debug.Log("Match starting!");
        }

        private void Update()
        {
            // #region agent log
            int frame = Time.frameCount;
            if (frame % 60 == 0)
            {
                float t = Time.realtimeSinceStartup;
                float dt = Time.deltaTime;
                float fps = dt > 0f ? (1f / dt) : 0f;
                DebugSessionLog.Write(
                    "GameManager.Update",
                    "frame timing",
                    "{\"frame\":" + frame + ",\"realtime\":" + t + ",\"deltaTime\":" + dt + ",\"fps\":" + fps + "}",
                    "C");
                s_lastFrameLog = frame;
            }
            if (Time.realtimeSinceStartup - s_lastPerfLogTime >= 2f)
            {
                s_lastPerfLogTime = Time.realtimeSinceStartup;
                int bullets = UnityEngine.Object.FindObjectsByType<Bullet>(UnityEngine.FindObjectsSortMode.None).Length;
                int gems = UnityEngine.Object.FindObjectsByType<Gem>(UnityEngine.FindObjectsSortMode.None).Length;
                int asteroids = UnityEngine.Object.FindObjectsByType<Asteroid>(UnityEngine.FindObjectsSortMode.None).Length;
                int ships = UnityEngine.Object.FindObjectsByType<Starship>(UnityEngine.FindObjectsSortMode.None).Length;
                DebugSessionLog.Write("GameManager.Update", "object counts", "{\"bullets\":" + bullets + ",\"gems\":" + gems + ",\"asteroids\":" + asteroids + ",\"starships\":" + ships + "}", "E");
            }
            // #endregion
            if (IsServer && currentGameState.Value == GameState.InProgress)
            {
                matchTimer.Value += Time.deltaTime;
                
                if (matchTimer.Value >= matchDuration)
                {
                    EndMatch();
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void EndMatchServerRpc()
        {
            EndMatch();
        }

        private void EndMatch()
        {
            if (IsServer)
            {
                currentGameState.Value = GameState.Ended;
                EndMatchClientRpc();
            }
        }

        [ClientRpc]
        private void EndMatchClientRpc()
        {
            Debug.Log("Match ended!");
        }
    }
}
