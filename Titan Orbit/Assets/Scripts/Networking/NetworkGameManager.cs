using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
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
        [Tooltip("UDP port for host/server. Change to e.g. 7778 if 7777 is already in use (e.g. previous play session).")]
        [SerializeField] private ushort serverPort = 7777;

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

        /// <summary>
        /// Applies the configured server port to UnityTransport so it's used when starting host/server.
        /// Call this before StartHost or StartServer so "port already in use" can be avoided by changing serverPort in the inspector.
        /// </summary>
        private void ApplyServerPort()
        {
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport != null)
            {
                transport.SetConnectionData(transport.ConnectionData.Address, serverPort, transport.ConnectionData.ServerListenAddress);
                Debug.Log($"Network port set to {serverPort}. If you get 'address already in use', try another port (e.g. 7778) in NetworkGameManager.");
            }
        }

        public void StartServer()
        {
            ApplyServerPort();
            NetworkManager.Singleton.StartServer();
            Debug.Log($"Server started on port {serverPort}");
        }

        public void StartHost()
        {
            if (NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("Player Prefab not set on NetworkManager! Use menu: Titan Orbit > Fix Player Prefab & Materials");
                return;
            }
            ApplyServerPort();
            NetworkManager.Singleton.StartHost();
            Debug.Log($"Host started on port {serverPort}");
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
            
            // Assign player to team (happens after player object spawns, so ship may have Team.None until now)
            if (TeamManager.Instance != null)
            {
                TeamManager.Team team = TeamManager.Instance.AssignPlayerToTeam(clientId);
                Debug.Log($"Client {clientId} assigned to {team}");
                
                // Set ship's team and move to home planet orbit (team is assigned here, after spawn)
                NetworkObject playerObj = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);
                if (playerObj != null)
                {
                    var ship = playerObj.GetComponent<TitanOrbit.Entities.Starship>();
                    if (ship != null && team != TeamManager.Team.None)
                        ship.AssignTeamAndStartInOrbit(team);
                }
                
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
