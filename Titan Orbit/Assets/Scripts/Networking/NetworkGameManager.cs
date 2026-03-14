using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using System.Threading.Tasks;
using TitanOrbit.Data;

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

        private const string LobbyRelayCodeKey = "RelayJoinCode";
        private const string LobbyGameNameKey = "GameName";
        private const string LobbyGameNameValue = "TitanOrbit";
        private Lobby currentLobby;
        private float nextLobbyHeartbeatTime;

        /// <summary>Name of the current game room (lobby), or empty if not in a lobby.</summary>
        public string CurrentLobbyName => currentLobby?.Name ?? "";

        private void Awake()
        {
            BootTrace.Mark("NetworkGameManager.Awake - enter");
            if (Instance == null)
            {
                Instance = this;
                BootTrace.Mark("NetworkGameManager.Awake - instance set");
            }
            else
            {
                BootTrace.Mark("NetworkGameManager.Awake - duplicate instance, destroying");
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

        /// <summary>
        /// If PlayerPrefab is not set, tries to assign from Resources/Prefabs/Starship so Play doesn't fail.
        /// Call before StartHost / Play. Use menu Titan Orbit > Fix Player Prefab & Materials to assign in editor.
        /// </summary>
        private static void EnsurePlayerPrefabSet()
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab != null)
                return;
            GameObject fallback = Resources.Load<GameObject>("Prefabs/Starship");
            if (fallback != null && fallback.GetComponent<NetworkObject>() != null)
            {
                NetworkManager.Singleton.NetworkConfig.PlayerPrefab = fallback;
                Debug.Log("Player Prefab was missing; assigned from Resources/Prefabs/Starship.");
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
            EnsurePlayerPrefabSet();
            if (NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("Player Prefab not set on NetworkManager! Add a Starship prefab to Resources/Prefabs/Starship.prefab or use menu: Titan Orbit > Fix Player Prefab & Materials");
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

        /// <summary>
        /// Ensures Unity Services are initialized and the player is signed in (anonymous). Call before any Relay calls.
        /// </summary>
        /// <returns>True if initialized and signed in; false if Services failed (e.g. offline or build not linked).</returns>
        private static async Task<bool> EnsureUnityServicesInitializedAsync()
        {
            try
            {
                if (UnityServices.State == ServicesInitializationState.Initialized && AuthenticationService.Instance.IsSignedIn)
                    return true;
                if (UnityServices.State != ServicesInitializationState.Initialized)
                    await UnityServices.InitializeAsync();
                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] Unity Services failed (offline or build not linked). You can still play as local host. " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Start as host using Unity Relay. Creates a Relay allocation and returns the join code for others. Uses "udp" connection type.
        /// </summary>
        /// <returns>Join code to share with clients, or null on failure.</returns>
        public async Task<string> StartHostWithRelayAsync()
        {
            EnsurePlayerPrefabSet();
            if (NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("Player Prefab not set on NetworkManager! Add a Starship prefab to Resources/Prefabs/Starship.prefab or use menu: Titan Orbit > Fix Player Prefab & Materials");
                return null;
            }
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return null;
                int maxConnections = Mathf.Max(1, maxPlayers - 1);
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return null;
                }
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "udp"));
                if (!NetworkManager.Singleton.StartHost())
                {
                    Debug.LogError("StartHost failed after Relay setup.");
                    return null;
                }
                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                Debug.Log($"Host started with Relay. Join code: {joinCode}");
                return joinCode;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                return null;
            }
        }

        /// <summary>
        /// Start as client using Unity Relay by joining the allocation for the given join code. Uses "udp" connection type.
        /// </summary>
        /// <returns>True if StartClient was called successfully.</returns>
        public async Task<bool> StartClientWithRelayAsync(string joinCode)
        {
            if (string.IsNullOrWhiteSpace(joinCode))
            {
                Debug.LogError("Join code is empty.");
                return false;
            }
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return false;
                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode.Trim());
                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
                }
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(joinAllocation, "udp"));
                bool started = NetworkManager.Singleton.StartClient();
                if (started)
                    Debug.Log("Client started with Relay.");
                return started;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }

        /// <summary>
        /// Play online: quick-join an existing game or create one if none exists. Uses Lobby + Relay. No manual join code.
        /// If Unity Services fail (e.g. desktop build offline or not linked), falls back to local host so the game still starts.
        /// </summary>
        /// <returns>True if we joined or created a game and started successfully.</returns>
        public async Task<bool> PlayQuickJoinOrCreateAsync()
        {
            EnsurePlayerPrefabSet();
            if (NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("Player Prefab not set on NetworkManager! Add a Starship prefab to Resources/Prefabs/Starship.prefab or use menu: Titan Orbit > Fix Player Prefab & Materials");
                return false;
            }
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return TryStartLocalHost();

                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return TryStartLocalHost();
                }

                Lobby joinedLobby = null;
                try
                {
                    var quickJoinOptions = new QuickJoinLobbyOptions();
                    joinedLobby = await LobbyService.Instance.QuickJoinLobbyAsync(quickJoinOptions);
                }
                catch (LobbyServiceException)
                {
                    joinedLobby = null;
                }

                if (joinedLobby != null && joinedLobby.Data != null && joinedLobby.Data.ContainsKey(LobbyRelayCodeKey))
                {
                    string joinCode = joinedLobby.Data[LobbyRelayCodeKey].Value;
                    if (!string.IsNullOrEmpty(joinCode))
                    {
                        JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
                        transport.SetRelayServerData(AllocationUtils.ToRelayServerData(joinAllocation, "udp"));
                        if (NetworkManager.Singleton.StartClient())
                        {
                            currentLobby = joinedLobby;
                            Debug.Log("Joined existing game via Lobby.");
                            return true;
                        }
                    }
                }

                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(Mathf.Max(1, maxPlayers - 1));
                string code = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                var createOptions = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Data = new System.Collections.Generic.Dictionary<string, DataObject>
                    {
                        { LobbyRelayCodeKey, new DataObject(DataObject.VisibilityOptions.Member, code) },
                        { LobbyGameNameKey, new DataObject(DataObject.VisibilityOptions.Public, LobbyGameNameValue, DataObject.IndexOptions.S1) }
                    }
                };
                currentLobby = await LobbyService.Instance.CreateLobbyAsync(GameNames.GetRandomRoomName(), maxPlayers, createOptions);
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "udp"));
                if (!NetworkManager.Singleton.StartHost())
                {
                    Debug.LogError("StartHost failed after creating Lobby.");
                    return TryStartLocalHost();
                }
                nextLobbyHeartbeatTime = Time.realtimeSinceStartup + 15f;
                Debug.Log("Created new game. Others can join via Play.");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] Play (Lobby/Relay) failed. Starting as local host so you can still play. " + e.Message);
                return TryStartLocalHost();
            }
        }

        /// <summary>Start as local host (no Relay/Lobby). Used when Unity Services fail so the desktop build still runs.</summary>
        private bool TryStartLocalHost()
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
                return false;
            EnsurePlayerPrefabSet();
            ApplyServerPort();
            if (NetworkManager.Singleton.StartHost())
            {
                Debug.Log("Started as local host (no online services). Game is playable.");
                return true;
            }
            return false;
        }

        private void Update()
        {
            if (currentLobby != null && IsHost && Time.realtimeSinceStartup >= nextLobbyHeartbeatTime)
            {
                nextLobbyHeartbeatTime = Time.realtimeSinceStartup + 15f;
                _ = LobbyService.Instance.SendHeartbeatPingAsync(currentLobby.Id);
            }
        }

        public override void OnNetworkSpawn()
        {
            BootTrace.Mark("NetworkGameManager.OnNetworkSpawn - enter (IsServer=" + IsServer + ")");
            if (IsServer)
            {
                BootTrace.Mark("NetworkGameManager.OnNetworkSpawn - EnsureScoreSystemExists");
                EnsureScoreSystemExists();
                BootTrace.Mark("NetworkGameManager.OnNetworkSpawn - EnsureMapGenerated");
                EnsureMapGenerated();
                BootTrace.Mark("NetworkGameManager.OnNetworkSpawn - subscribing client callbacks");
                NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            }
        }

        private void EnsureMapGenerated()
        {
            BootTrace.Mark("NetworkGameManager.EnsureMapGenerated - locating MapGenerator");
            var mapGen = Object.FindFirstObjectByType<TitanOrbit.Generation.MapGenerator>();
            if (mapGen != null)
            {
                BootTrace.Mark("NetworkGameManager.EnsureMapGenerated - calling MapGenerator.EnsureMapGenerated");
                mapGen.EnsureMapGenerated();
            }
            else
            {
                BootTrace.Mark("NetworkGameManager.EnsureMapGenerated - no MapGenerator found");
            }
        }

        private void EnsureScoreSystemExists()
        {
            ScoreSystem existing = ScoreSystem.Instance;
            if (existing == null)
                existing = Object.FindFirstObjectByType<ScoreSystem>();

            if (existing == null)
            {
                GameObject go = new GameObject("ScoreSystem");
                go.AddComponent<NetworkObject>();
                existing = go.AddComponent<ScoreSystem>();
                NetworkObject no = existing.GetComponent<NetworkObject>();
                if (!no.IsSpawned)
                    no.Spawn();
                return;
            }

            NetworkObject existingNo = existing.GetComponent<NetworkObject>();
            if (existingNo == null)
                existing.gameObject.AddComponent<NetworkObject>();
            // Do not spawn: scene-placed ScoreSystem is spawned by ServerSpawnSceneObjectsOnStartSweep; spawning here causes "Object is already spawned".
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
            // Do not assign team or move ship here. Player sees team selection first; assignment happens in TeamManager.RequestTeamServerRpc when they click Join.
        }

        private void OnClientDisconnected(ulong clientId)
        {
            Debug.Log($"Client {clientId} disconnected");
            PlayerDisplayNames.RemoveClient(clientId);
            if (TeamManager.Instance != null)
                TeamManager.Instance.RemovePlayer(clientId);
        }

        [ClientRpc]
        private void AssignTeamClientRpc(ulong clientId, TeamManager.Team team)
        {
            if (NetworkManager.Singleton.LocalClientId == clientId)
            {
                Debug.Log($"You have been assigned to {team}");
                LastAssignedTeam = team;
            }
        }

        public TeamManager.Team LastAssignedTeam { get; private set; } = TeamManager.Team.None;
        public bool LastTeamRequestGranted { get; private set; } = true;

        /// <summary>Display name for the local player (set from main menu or auto-assigned). Used when syncing to server.</summary>
        public static string LocalPlayerDisplayName { get; set; } = "";

        /// <summary>Fired on client when they successfully chose a team (Join accepted). Use to hide team selection UI.</summary>
        public static System.Action<TeamManager.Team> OnTeamChosen;

        public void OnTeamAssignmentResult(TeamManager.Team assignedTeam, bool requestGranted)
        {
            LastAssignedTeam = assignedTeam;
            LastTeamRequestGranted = requestGranted;
            if (requestGranted && assignedTeam != TeamManager.Team.None)
                OnTeamChosen?.Invoke(assignedTeam);
        }

        public bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        public bool IsClient => NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;
        public bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;
    }
}
