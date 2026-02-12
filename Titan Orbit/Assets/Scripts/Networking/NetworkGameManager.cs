using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Manages network game state and player connections
    /// </summary>
    public class NetworkGameManager : NetworkBehaviour
    {
        public static NetworkGameManager Instance { get; private set; }

        [Header("Network Settings")]
        [SerializeField] private int maxPlayers = 60;
        [SerializeField] private bool autoStartServer = false;

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

        private void Start()
        {
            if (autoStartServer && Application.isEditor)
            {
                // Auto-start server in editor for testing
                StartServer();
            }
        }

        public void StartServer()
        {
            NetworkManager.Singleton.StartServer();
            Debug.Log("Server started");
        }

        public void StartHost()
        {
            if (NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("Player Prefab not set on NetworkManager! Use menu: Titan Orbit > Fix Player Prefab & Materials");
                return;
            }
            NetworkManager.Singleton.StartHost();
            Debug.Log("Host started");
        }

        public void StartClient()
        {
            NetworkManager.Singleton.StartClient();
            Debug.Log("Client started");
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            Debug.Log($"Client {clientId} connected");
            
            // Assign player to team
            if (TeamManager.Instance != null)
            {
                TeamManager.Team team = TeamManager.Instance.AssignPlayerToTeam(clientId);
                Debug.Log($"Client {clientId} assigned to {team}");
                
                // Notify client of team assignment
                AssignTeamClientRpc(clientId, team);
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            Debug.Log($"Client {clientId} disconnected");
            
            // Remove player from team
            if (TeamManager.Instance != null)
            {
                TeamManager.Instance.RemovePlayer(clientId);
            }
        }

        [ClientRpc]
        private void AssignTeamClientRpc(ulong clientId, TeamManager.Team team)
        {
            if (NetworkManager.Singleton.LocalClientId == clientId)
            {
                Debug.Log($"You have been assigned to {team}");
                // Handle team assignment on client
            }
        }

        public bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        public bool IsClient => NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;
        public bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;
    }
}
