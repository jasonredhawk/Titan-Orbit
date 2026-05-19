using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Systems;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Main game state manager that handles overall game flow and state
    /// </summary>
    public class GameManager : NetworkBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("Debug")]
        [Tooltip("When enabled: one-shot asteroids, fast economy/combat tuning, instant gem deposit and people load/unload, and unload counts as 100x people impact. Toggle off for normal play.")]
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
            }
            else
            {
                BootTrace.Mark("GameManager.Awake - duplicate instance, destroying");
                Destroy(gameObject);
                return;
            }

            EnsureWorldShapeVisuals();
        }

        /// <summary>Creates shared world-space Shapes drawers (idempotent; safe if Awake order varies).</summary>
        public static void EnsureWorldShapeVisuals()
        {
            var systemsGo = GameObject.Find("PlanetConnectionSystems");
            if (systemsGo == null)
            {
                systemsGo = new GameObject("PlanetConnectionSystems");
                DontDestroyOnLoad(systemsGo);
            }

            if (systemsGo.GetComponent<PlanetConnectionSystem>() == null)
                systemsGo.AddComponent<PlanetConnectionSystem>();
            if (systemsGo.GetComponent<AsteroidTerritoryHighlighter>() == null)
                systemsGo.AddComponent<AsteroidTerritoryHighlighter>();
            if (systemsGo.GetComponent<PlanetConnectionShapesVisual>() == null)
                systemsGo.AddComponent<PlanetConnectionShapesVisual>();
            if (systemsGo.GetComponent<GemTractorBeamVisual>() == null)
                systemsGo.AddComponent<GemTractorBeamVisual>();
        }

        public override void OnNetworkSpawn()
        {
            EnsureWorldShapeVisuals();

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
