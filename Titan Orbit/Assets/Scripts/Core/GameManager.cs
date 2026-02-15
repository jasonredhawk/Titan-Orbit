using UnityEngine;
using Unity.Netcode;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Main game state manager that handles overall game flow and state
    /// </summary>
    public class GameManager : NetworkBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("Debug")]
        [Tooltip("When enabled: bullets one-shot asteroids, gem value is 100x. Toggle off for normal play.")]
        [SerializeField] private bool debugMode = true;

        [Header("Game Settings")]
        [SerializeField] private int maxPlayersPerTeam = 20;

        public bool DebugMode => debugMode;
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
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
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
