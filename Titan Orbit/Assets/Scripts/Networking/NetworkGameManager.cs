using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Services;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using UnityEngine.Networking;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Manages network game state and player connections
    /// </summary>
    public class NetworkGameManager : NetworkBehaviour
    {
        [Serializable]
        public class LobbySummary
        {
            public string LobbyId;
            public string Name;
            public int CurrentPlayers;
            public int MaxPlayers;
            public bool IsOpen;
            public bool IsLatest;
            public long CreatedAtEpochSeconds;
        }

        public static NetworkGameManager Instance { get; private set; }

        [Header("Network Settings")]
        [SerializeField] private int maxPlayers = 60;
        [SerializeField] private bool autoStartServer = false;
        [Tooltip("UDP port for host/server. Change to e.g. 7778 if 7777 is already in use (e.g. previous play session).")]
        [SerializeField] private ushort serverPort = 7777;

        private const string LobbyRelayCodeKey = "RelayJoinCode";
        private const string LobbyGameNameKey = "GameName";
        private const string LobbyGameNameValue = "TitanOrbit";
        // Queryable indexed fields so WebGL clients can list/filter lobbies without knowing join codes.
        private const string LobbyIsOpenKey = "IsOpen";
        private const string LobbyIsLatestKey = "IsLatest";
        private const string LobbyCreatedAtEpochKey = "CreatedAtEpoch";
        private Lobby currentLobby;
        private float nextLobbyHeartbeatTime;
        private Coroutine pendingTeamRequestCoroutine;

        #region agent log
        /// <summary>Debug session NDJSON (session ab7145). WebGL posts to local ingest; other platforms append to project debug-ab7145.log.</summary>
        public static void AgentDebugLog(string hypothesisId, string location, string message, string dataJson)
        {
#if UNITY_WEBGL && !UNITY_EDITOR && !DEVELOPMENT_BUILD
            return;
#elif UNITY_WEBGL && !UNITY_EDITOR
            if (Instance != null)
                Instance.StartCoroutine(Instance.AgentDebugPostNdjsonCo(hypothesisId, location, message, dataJson));
#else
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                if (string.IsNullOrEmpty(root))
                    return;
                string path = Path.Combine(root, "debug-ab7145.log");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line = "{\"sessionId\":\"ab7145\",\"hypothesisId\":\"" + hypothesisId + "\",\"location\":\"" + location + "\",\"message\":\"" + message + "\",\"data\":" + dataJson + ",\"timestamp\":" + ts + "}\n";
                File.AppendAllText(path, line);
            }
            catch
            {
            }
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR && DEVELOPMENT_BUILD
        private IEnumerator AgentDebugPostNdjsonCo(string hypothesisId, string location, string message, string dataJson)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string payload = "{\"sessionId\":\"ab7145\",\"hypothesisId\":\"" + hypothesisId + "\",\"location\":\"" + location + "\",\"message\":\"" + message + "\",\"data\":" + dataJson + ",\"timestamp\":" + ts + "}";
            byte[] body = System.Text.Encoding.UTF8.GetBytes(payload);
            using (var req = new UnityWebRequest("http://127.0.0.1:7533/ingest/b84a2d75-b633-4e78-818d-d67b0d01c661", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("X-Debug-Session-Id", "ab7145");
                yield return req.SendWebRequest();
            }
        }
#endif
        #endregion

        #region agent log e695ff
        internal static void DebugSessionE695ffLog(string hypothesisId, string location, string message, string dataJson)
        {
#if UNITY_WEBGL && !UNITY_EDITOR && !DEVELOPMENT_BUILD
            return;
#elif UNITY_WEBGL && !UNITY_EDITOR
            var inst = Instance ?? UnityEngine.Object.FindAnyObjectByType<NetworkGameManager>(FindObjectsInactive.Include);
            if (inst != null)
                inst.StartCoroutine(inst.DebugSessionE695ffPostCo(hypothesisId, location, message, dataJson));
#else
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                if (string.IsNullOrEmpty(root)) return;
                string path = Path.Combine(root, "debug-e695ff.log");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line = "{\"sessionId\":\"e695ff\",\"hypothesisId\":\"" + hypothesisId + "\",\"location\":\"" + location + "\",\"message\":\"" + message + "\",\"data\":" + dataJson + ",\"timestamp\":" + ts + "}\n";
                File.AppendAllText(path, line);
#if UNITY_EDITOR
                Debug.Log("[e695ff] " + location + " | " + message + " | " + dataJson);
#endif
            }
            catch { }
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR && DEVELOPMENT_BUILD
        private IEnumerator DebugSessionE695ffPostCo(string hypothesisId, string location, string message, string dataJson)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string payload = "{\"sessionId\":\"e695ff\",\"hypothesisId\":\"" + hypothesisId + "\",\"location\":\"" + location + "\",\"message\":\"" + message + "\",\"data\":" + dataJson + ",\"timestamp\":" + ts + "}";
            byte[] body = System.Text.Encoding.UTF8.GetBytes(payload);
            using (var req = new UnityWebRequest("http://127.0.0.1:7533/ingest/b84a2d75-b633-4e78-818d-d67b0d01c661", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("X-Debug-Session-Id", "e695ff");
                yield return req.SendWebRequest();
            }
        }
#endif

        /// <summary>NDJSON diagnostics for team-join failures (session e695ff).</summary>
        private static string BuildLocalPlayerShipDiagJson()
        {
            var nm = ResolveNetworkManagerForGameplay();
            var sing = NetworkManager.Singleton;
            if (nm == null) return "{\"nmNull\":true}";
            bool lcOk = nm.LocalClient != null;
            NetworkObject po = null;
            if (lcOk) po = nm.LocalClient.PlayerObject;
            bool usedSpawnMgr = false;
            if (po == null && nm.SpawnManager != null)
            {
                po = nm.SpawnManager.GetLocalPlayerObject();
                usedSpawnMgr = po != null;
            }
            bool shipOnRoot = po != null && po.GetComponent<Starship>() != null;
            bool shipInChildren = po != null && po.GetComponentInChildren<Starship>(true) != null;
            int sid = sing != null ? sing.GetInstanceID() : 0;
            int rid = nm.GetInstanceID();
            return "{\"nmNull\":false,\"singletonNmId\":" + sid + ",\"resolvedNmId\":" + rid + ",\"resolvedIsSingleton\":" + (sing == nm ? "true" : "false") + ",\"localClientOk\":" + (lcOk ? "true" : "false") + ",\"playerObjFromLocal\":" + (lcOk && nm.LocalClient.PlayerObject != null ? "true" : "false") + ",\"usedSpawnMgrGetLocal\":" + (usedSpawnMgr ? "true" : "false") + ",\"resolvedNetworkObject\":" + (po != null ? "true" : "false") + ",\"starshipOnRoot\":" + (shipOnRoot ? "true" : "false") + ",\"starshipInHierarchy\":" + (shipInChildren ? "true" : "false") + ",\"isClient\":" + (nm.IsClient ? "true" : "false") + ",\"isServer\":" + (nm.IsServer ? "true" : "false") + ",\"isListening\":" + (nm.IsListening ? "true" : "false") + "}";
        }
        #endregion

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
            #region agent log
            RegisterAgentTransportFailureHandler();
            #endregion
            if (autoStartServer && Application.isEditor)
            {
                // Auto-start server in editor for testing
                StartServer();
            }
        }

        #region agent log
        private void RegisterAgentTransportFailureHandler()
        {
            try
            {
                if (NetworkManager.Singleton != null)
                {
                    NetworkManager.Singleton.OnTransportFailure -= AgentOnTransportFailure;
                    NetworkManager.Singleton.OnTransportFailure += AgentOnTransportFailure;
                }
            }
            catch
            {
            }
        }

        private void AgentOnTransportFailure()
        {
            string t = RelayConnectionTypeForCurrentPlatform();
            AgentDebugLog("H1", "NetworkGameManager.AgentOnTransportFailure", "OnTransportFailure",
                "{\"relayConnectionType\":\"" + t + "\",\"isHost\":" + (IsHost ? "true" : "false") + ",\"isClient\":" + (IsClient ? "true" : "false") + "}");
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                try
                {
                    if (NetworkManager.Singleton != null)
                        NetworkManager.Singleton.OnTransportFailure -= AgentOnTransportFailure;
                }
                catch
                {
                }
            }
        }
        #endregion

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
        /// Ensures Unity Services are initialized and the player is signed in (guest/anonymous or Unity account). Call before any Relay calls.
        /// </summary>
        /// <returns>True if initialized and signed in; false if Services failed (e.g. offline or build not linked).</returns>
        private static async Task<bool> EnsureUnityServicesInitializedAsync()
        {
            return await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync();
        }

        /// <summary>
        /// Relay connection type for <see cref="AllocationUtils.ToRelayServerData"/>: WebGL only allows WSS; other platforms use UDP.
        /// </summary>
        static string RelayConnectionTypeForCurrentPlatform()
        {
#if UNITY_WEBGL
            return "wss";
#else
            return "udp";
#endif
        }

        /// <summary>
        /// Relay keeps allocations alive with periodic pings; UTP passes <see cref="UnityTransport.HeartbeatTimeoutMS"/>
        /// to Relay as the ping interval. Slow WSS joins benefit from a less aggressive connect retry than LAN defaults.
        /// </summary>
        /// <summary>UTP default receive/send queue is 128; heavy NGO replication over Relay/WSS can overflow it and drop packets (wrong map on clients).</summary>
        const int MinRelayPacketQueueSize = 1024;

        public static void ApplyRelayFriendlyTransportSettings(UnityTransport transport)
        {
            if (transport.ConnectTimeoutMS < 3000)
                transport.ConnectTimeoutMS = 5000;
            if (transport.HeartbeatTimeoutMS <= 0 || transport.HeartbeatTimeoutMS > 9000)
                transport.HeartbeatTimeoutMS = 3000;
            if (transport.MaxPacketQueueSize < MinRelayPacketQueueSize)
            {
                #region agent log e695ff
                int prev = transport.MaxPacketQueueSize;
                #endregion
                transport.MaxPacketQueueSize = MinRelayPacketQueueSize;
                #region agent log e695ff
                DebugSessionE695ffLog("H6", "ApplyRelayFriendlyTransportSettings", "MaxPacketQueueSize raised for Relay",
                    "{\"prev\":" + prev + ",\"now\":" + transport.MaxPacketQueueSize + "}");
                #endregion
            }
        }

        /// <summary>Transport is up; pure clients must also be connection-approved or team RPCs / loading gates misbehave.</summary>
        public static bool IsNetcodeTransportReadyForGameplay(NetworkManager nm)
        {
            if (nm == null || !nm.IsListening)
                return false;
            if (nm.IsClient && !nm.IsServer)
                return nm.IsConnectedClient;
            return true;
        }

        /// <summary>
        /// Sets Relay server data and <see cref="UnityTransport.UseWebSockets"/> so they match (WSS requires WebSockets on the transport).
        /// </summary>
        static void ConfigureUnityTransportRelay(UnityTransport transport, Allocation allocation)
        {
            string t = RelayConnectionTypeForCurrentPlatform();
            transport.UseWebSockets = string.Equals(t, "wss", StringComparison.OrdinalIgnoreCase);
            transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, t));
            ApplyRelayFriendlyTransportSettings(transport);
        }

        static void ConfigureUnityTransportRelay(UnityTransport transport, JoinAllocation allocation)
        {
            string t = RelayConnectionTypeForCurrentPlatform();
            transport.UseWebSockets = string.Equals(t, "wss", StringComparison.OrdinalIgnoreCase);
            transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, t));
            ApplyRelayFriendlyTransportSettings(transport);
        }

        /// <summary>
        /// Start as host using Unity Relay. Creates a Relay allocation and returns the join code for others. Uses UDP or WSS per platform.
        /// Also registers a Unity Lobby (same metadata as Browse / Quick Join) so other players can find this session without the raw Relay code.
        /// </summary>
        /// <param name="lobbyDisplayName">Visible name in the lobby list; if null/empty, a random room name is used.</param>
        /// <returns>Relay join code to share with clients, or null on failure.</returns>
        public async Task<string> StartHostWithRelayAsync(string lobbyDisplayName = null)
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
                #region agent log
                RegisterAgentTransportFailureHandler();
                #endregion
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
                #region agent log
                AgentDebugLog("H4", "StartHostWithRelayAsync", "allocation created",
                    "{\"allocationId\":\"" + allocation.AllocationId + "\",\"relayConnectionType\":\"" + RelayConnectionTypeForCurrentPlatform() + "\",\"maxConnections\":" + maxConnections + "}");
                #endregion
                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return null;
                }
                ConfigureUnityTransportRelay(transport, allocation);
                if (!NetworkManager.Singleton.StartHost())
                {
                    Debug.LogError("StartHost failed after Relay setup.");
                    return null;
                }
                #region agent log
                AgentDebugLog("H4", "StartHostWithRelayAsync", "StartHost returned true",
                    "{\"relayConnectionType\":\"" + RelayConnectionTypeForCurrentPlatform() + "\",\"useWebSockets\":" + (transport.UseWebSockets ? "true" : "false") + "}");
                #endregion
                #region agent log
                AgentDebugLog("H2", "StartHostWithRelayAsync", "join code ready",
                    "{\"joinCodeLength\":" + (joinCode != null ? joinCode.Length : 0) + "}");
                #endregion
                Debug.Log($"Host started with Relay. Join code: {joinCode}");

                bool listed = await TryRegisterLobbyForHostAsync(joinCode, lobbyDisplayName);
                if (!listed)
                    Debug.LogWarning("Relay host is running, but Unity Lobby listing failed — others can still join with the Relay code if you share it.");

                return joinCode;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                return null;
            }
        }

        /// <summary>
        /// Creates a Unity Lobby entry pointing at this host's Relay join code so the match appears in <see cref="QueryOpenLobbiesAsync"/>.
        /// Uses IsLatest=0 so many user-hosted games can coexist; dedicated / quick-create flows may still use IsLatest=1.
        /// </summary>
        private async Task<bool> TryRegisterLobbyForHostAsync(string relayJoinCode, string lobbyDisplayName)
        {
            if (string.IsNullOrWhiteSpace(relayJoinCode))
                return false;

            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return false;

                string displayName = string.IsNullOrWhiteSpace(lobbyDisplayName)
                    ? GameNames.GetRandomRoomName()
                    : lobbyDisplayName.Trim();
                if (displayName.Length > 64)
                    displayName = displayName.Substring(0, 64);

                long createdAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var createOptions = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Data = new Dictionary<string, DataObject>
                    {
                        { LobbyRelayCodeKey, new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode) },
                        { LobbyGameNameKey, new DataObject(DataObject.VisibilityOptions.Public, LobbyGameNameValue, DataObject.IndexOptions.S1) },
                        { LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N1) },
                        { LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, "0", DataObject.IndexOptions.N2) },
                        {
                            LobbyCreatedAtEpochKey,
                            new DataObject(
                                DataObject.VisibilityOptions.Public,
                                createdAtEpochSeconds.ToString(CultureInfo.InvariantCulture),
                                DataObject.IndexOptions.N3)
                        }
                    }
                };

                currentLobby = await LobbyService.Instance.CreateLobbyAsync(displayName, maxPlayers, createOptions);
                nextLobbyHeartbeatTime = Time.realtimeSinceStartup + 15f;
                Debug.Log($"Unity Lobby listed: \"{displayName}\" (id {currentLobby.Id}). Others can join from Browse Open Matches or Quick Join.");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] TryRegisterLobbyForHostAsync: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Start as client using Unity Relay by joining the allocation for the given join code. Uses UDP or WSS per platform.
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
                #region agent log
                RegisterAgentTransportFailureHandler();
                #endregion
                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode.Trim());
                #region agent log
                AgentDebugLog("H4", "StartClientWithRelayAsync", "JoinAllocation ok",
                    "{\"allocationId\":\"" + joinAllocation.AllocationId + "\",\"relayConnectionType\":\"" + RelayConnectionTypeForCurrentPlatform() + "\",\"joinCodeLength\":" + joinCode.Trim().Length + "}");
                #endregion
                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
                }
                ConfigureUnityTransportRelay(transport, joinAllocation);
                bool started = NetworkManager.Singleton.StartClient();
                #region agent log
                AgentDebugLog("H3", "StartClientWithRelayAsync", "StartClient",
                    "{\"started\":" + (started ? "true" : "false") + ",\"useWebSockets\":" + (transport.UseWebSockets ? "true" : "false") + "}");
                #endregion
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
        /// This method is intended for desktop/server builds only (not WebGL).
        /// </summary>
        /// <returns>True if we joined or created a game and started successfully.</returns>
        public async Task<bool> PlayQuickJoinOrCreateAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogWarning("PlayQuickJoinOrCreateAsync is not supported in WebGL builds. Use PlayWebGLJoinAsync instead.");
            return false;
#else
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
                        ConfigureUnityTransportRelay(transport, joinAllocation);
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
                long createdAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var createOptions = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Data = new System.Collections.Generic.Dictionary<string, DataObject>
                    {
                        { LobbyRelayCodeKey, new DataObject(DataObject.VisibilityOptions.Member, code) },
                        { LobbyGameNameKey, new DataObject(DataObject.VisibilityOptions.Public, LobbyGameNameValue, DataObject.IndexOptions.S1) },
                        { LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N1) },
                        { LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N2) },
                        {
                            LobbyCreatedAtEpochKey,
                            new DataObject(
                                DataObject.VisibilityOptions.Public,
                                createdAtEpochSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                DataObject.IndexOptions.N3
                            )
                        }
                    }
                };
                currentLobby = await LobbyService.Instance.CreateLobbyAsync(GameNames.GetRandomRoomName(), maxPlayers, createOptions);
                ConfigureUnityTransportRelay(transport, allocation);
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
#endif
        }

        /// <summary>
        /// WebGL-safe play entry: quick-join an existing lobby and connect as client via Relay.
        /// Never creates a new host or starts server-side UnityTransport.
        /// Returns false if no suitable lobby/allocation is available.
        /// </summary>
        public async Task<bool> PlayWebGLJoinAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return false;

                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
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
                        ConfigureUnityTransportRelay(transport, joinAllocation);
                        if (NetworkManager.Singleton.StartClient())
                        {
                            currentLobby = joinedLobby;
                            Debug.Log("Joined existing game via Lobby (WebGL client).");
                            return true;
                        }
                    }
                }

                Debug.LogWarning("No suitable lobby found to join from WebGL client.");
                return false;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinAsync failed. " + e.Message);
                return false;
            }
#else
            // In non-WebGL builds, reuse the full play flow.
            return await PlayQuickJoinOrCreateAsync();
#endif
        }

        /// <summary>
        /// True when JoinLobbyById failed but the player is already in this lobby (SDK conflict resolver did not return a lobby).
        /// We recover by fetching the lobby and reading the Relay join code from lobby data.
        /// </summary>
        private static bool IsLobbyJoinAlreadyMemberFailure(LobbyServiceException e)
        {
            if (e == null) return false;
            if (e.Reason == LobbyExceptionReason.LobbyConflict || e.Reason == LobbyExceptionReason.Conflict)
                return true;
            string m = e.Message ?? string.Empty;
            return m.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0
                && m.IndexOf("member", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// WebGL-safe play entry: join an existing lobby by lobby id and connect as client via Relay.
        /// Never creates a host/server-side Netcode instance.
        /// </summary>
        public async Task<bool> PlayWebGLJoinByLobbyIdAsync(string lobbyId)
        {
            if (string.IsNullOrWhiteSpace(lobbyId))
            {
                Debug.LogError("LobbyId is empty.");
                return false;
            }

            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return false;
                #region agent log
                RegisterAgentTransportFailureHandler();
                #endregion

                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
                }

                string id = lobbyId.Trim();
                Lobby joinedLobby = null;
                try
                {
                    joinedLobby = await LobbyService.Instance.JoinLobbyByIdAsync(id);
                }
                catch (LobbyServiceException e)
                {
                    if (!IsLobbyJoinAlreadyMemberFailure(e))
                    {
                        Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinByLobbyIdAsync failed: " + e.Message);
                        return false;
                    }

                    try
                    {
                        joinedLobby = await LobbyService.Instance.GetLobbyAsync(id);
                        #region agent log e695ff
                        DebugSessionE695ffLog("H5", "PlayWebGLJoinByLobbyIdAsync", "GetLobbyAsync after join conflict",
                            "{\"lobbyId\":\"" + id + "\",\"reason\":" + (int)e.Reason + ",\"recovered\":" + (joinedLobby != null ? "true" : "false") + "}");
                        #endregion
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinByLobbyIdAsync: could not GetLobby after join failure: " + ex.Message);
                        return false;
                    }
                }

                #region agent log
                bool hasKey = joinedLobby != null && joinedLobby.Data != null && joinedLobby.Data.ContainsKey(LobbyRelayCodeKey);
                AgentDebugLog("H2", "PlayWebGLJoinByLobbyIdAsync", "after JoinLobbyById",
                    "{\"hasLobbyData\":" + (joinedLobby?.Data != null ? "true" : "false") + ",\"hasRelayKey\":" + (hasKey ? "true" : "false") + "}");
                #endregion
                if (joinedLobby == null || joinedLobby.Data == null || !joinedLobby.Data.ContainsKey(LobbyRelayCodeKey))
                {
                    Debug.LogWarning("Joined lobby, but RelayJoinCode was missing.");
                    return false;
                }

                string joinCode = joinedLobby.Data[LobbyRelayCodeKey].Value;
                if (string.IsNullOrEmpty(joinCode))
                {
                    Debug.LogWarning("Joined lobby, but RelayJoinCode was empty.");
                    return false;
                }

                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
                #region agent log
                AgentDebugLog("H4", "PlayWebGLJoinByLobbyIdAsync", "JoinAllocation ok",
                    "{\"allocationId\":\"" + joinAllocation.AllocationId + "\",\"relayConnectionType\":\"" + RelayConnectionTypeForCurrentPlatform() + "\"}");
                #endregion
                ConfigureUnityTransportRelay(transport, joinAllocation);

                bool startedLobby = NetworkManager.Singleton.StartClient();
                #region agent log
                AgentDebugLog("H3", "PlayWebGLJoinByLobbyIdAsync", "StartClient",
                    "{\"started\":" + (startedLobby ? "true" : "false") + ",\"useWebSockets\":" + (transport.UseWebSockets ? "true" : "false") + "}");
                #endregion
                if (startedLobby)
                {
                    currentLobby = joinedLobby;
                    Debug.Log("Joined lobby by id via Relay (WebGL client).");
                    return true;
                }

                return false;
            }
            catch (LobbyServiceException e)
            {
                Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinByLobbyIdAsync failed: " + e.Message);
                return false;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinByLobbyIdAsync failed. " + e.Message);
                return false;
            }
        }

        public async Task<List<LobbySummary>> QueryOpenLobbiesAsync(bool latestOnly, int count = 20)
        {
            var results = new List<LobbySummary>();
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return results;

                var filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.S1, LobbyGameNameValue, QueryFilter.OpOptions.EQ),
                    new QueryFilter(QueryFilter.FieldOptions.N1, "1", QueryFilter.OpOptions.EQ),
                };

                if (latestOnly)
                    filters.Add(new QueryFilter(QueryFilter.FieldOptions.N2, "1", QueryFilter.OpOptions.EQ));

                var options = new QueryLobbiesOptions
                {
                    Count = count,
                    Filters = filters,
                    Order = new List<QueryOrder>
                    {
                        new QueryOrder(asc: false, field: QueryOrder.FieldOptions.Created)
                    }
                };

                QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(options);
                if (response?.Results == null)
                    return results;

                foreach (var lobby in response.Results)
                {
                    if (lobby == null)
                        continue;
                    results.Add(ToLobbySummary(lobby));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] QueryOpenLobbiesAsync failed: " + e.Message);
            }

            return results;
        }

        public async Task<bool> JoinLobbyByIdAsync(string lobbyId)
        {
            return await PlayWebGLJoinByLobbyIdAsync(lobbyId);
        }

        private LobbySummary ToLobbySummary(Lobby lobby)
        {
            var summary = new LobbySummary
            {
                LobbyId = lobby.Id,
                Name = string.IsNullOrWhiteSpace(lobby.Name) ? "Unnamed Room" : lobby.Name,
                CurrentPlayers = lobby.Players != null ? lobby.Players.Count : 0,
                MaxPlayers = Mathf.Max(1, lobby.MaxPlayers),
                IsOpen = true,
                IsLatest = false,
                CreatedAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            if (lobby.Data == null)
                return summary;

            if (lobby.Data.TryGetValue(LobbyIsOpenKey, out DataObject isOpenObj))
                summary.IsOpen = string.Equals(isOpenObj?.Value, "1", StringComparison.Ordinal);

            if (lobby.Data.TryGetValue(LobbyIsLatestKey, out DataObject isLatestObj))
                summary.IsLatest = string.Equals(isLatestObj?.Value, "1", StringComparison.Ordinal);

            if (lobby.Data.TryGetValue(LobbyCreatedAtEpochKey, out DataObject createdAtObj) &&
                long.TryParse(createdAtObj?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long created))
            {
                summary.CreatedAtEpochSeconds = created;
            }

            return summary;
        }

        /// <summary>
        /// WebGL-safe: query open lobbies for this game and (optionally) only those marked as latest.
        /// </summary>
        public async Task<System.Collections.Generic.List<Lobby>> QueryWebGLOpenLobbiesAsync(bool latestOnly, int count = 20)
        {
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return new System.Collections.Generic.List<Lobby>();

                var filters = new System.Collections.Generic.List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.S1, LobbyGameNameValue, QueryFilter.OpOptions.EQ),
                    new QueryFilter(QueryFilter.FieldOptions.N1, "1", QueryFilter.OpOptions.EQ),
                };
                if (latestOnly)
                    filters.Add(new QueryFilter(QueryFilter.FieldOptions.N2, "1", QueryFilter.OpOptions.EQ));

                QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                {
                    Count = count,
                    Filters = filters,
                    Order = new System.Collections.Generic.List<QueryOrder>
                    {
                        new QueryOrder(asc: false, field: QueryOrder.FieldOptions.Created)
                    }
                });
                return response?.Results ?? new System.Collections.Generic.List<Lobby>();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] QueryWebGLOpenLobbiesAsync failed: " + e.Message);
                return new System.Collections.Generic.List<Lobby>();
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
            var mapGen = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Generation.MapGenerator>();
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
                existing = UnityEngine.Object.FindFirstObjectByType<ScoreSystem>();

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
            if (pendingTeamRequestCoroutine != null)
            {
                StopCoroutine(pendingTeamRequestCoroutine);
                pendingTeamRequestCoroutine = null;
            }
            if (IsServer)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            Debug.Log($"Client {clientId} connected");
            #region agent log
            AgentDebugLog("H3", "NetworkGameManager.OnClientConnected", "server saw client",
                "{\"clientId\":" + clientId + ",\"realtimeSinceStartup\":" + Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture) + "}");
            #endregion
            // Do not assign team or move ship here. Player sees team selection first; assignment happens when they click Join (Starship.RequestJoinTeamServerRpc → TeamManager.ApplyTeamChoiceFromServer).
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

        /// <summary>Fired on client when Join was rejected (team full, not in match, etc.).</summary>
        public static System.Action<string> OnTeamChoiceFailed;

        /// <summary>
        /// <see cref="NetworkManager.Singleton"/> can reference an inactive duplicate (e.g. Multiplayer Play Mode / extra scene object) that never started Netcode,
        /// while another <see cref="NetworkManager"/> in the hierarchy is the real host/client. Prefer the instance that is actually running.
        /// </summary>
        public static NetworkManager ResolveNetworkManagerForGameplay()
        {
            var s = NetworkManager.Singleton;
            if (s != null && (s.IsClient || s.IsServer))
                return s;
            var all = UnityEngine.Object.FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                var nm = all[i];
                if (nm != null && (nm.IsClient || nm.IsServer))
                    return nm;
            }
            return s;
        }

        /// <summary>Resolves the local player's ship even if <see cref="NetworkClient.PlayerObject"/> is not set yet (uses <see cref="NetworkSpawnManager.GetLocalPlayerObject"/> and child search).</summary>
        private static Starship TryGetLocalStarship()
        {
            var nm = ResolveNetworkManagerForGameplay();
            if (nm == null) return null;
            NetworkObject po = null;
            if (nm.LocalClient != null)
                po = nm.LocalClient.PlayerObject;
            if (po == null && nm.SpawnManager != null)
                po = nm.SpawnManager.GetLocalPlayerObject();
            if (po == null) return null;
            var ship = po.GetComponent<Starship>();
            if (ship == null)
                ship = po.GetComponentInChildren<Starship>(true);
            return ship;
        }

        private IEnumerator CoRequestTeamWhenLocalPlayerReady(TeamManager.Team team)
        {
            const float timeoutSeconds = 25f;
            float t0 = Time.realtimeSinceStartup;
            float deadline = t0 + timeoutSeconds;
            #region agent log e695ff
            DebugSessionE695ffLog("H1", "NetworkGameManager.CoRequestTeamWhenLocalPlayerReady", "coroutine started",
                "{\"team\":" + (int)team + ",\"timeoutSeconds\":" + timeoutSeconds.ToString("F1", CultureInfo.InvariantCulture) + "}");
            #endregion
            var nm0 = ResolveNetworkManagerForGameplay();
            if (nm0 == null || !IsNetcodeTransportReadyForGameplay(nm0))
            {
                pendingTeamRequestCoroutine = null;
                OnTeamChoiceFailed?.Invoke("Multiplayer is not running. Return to the main menu, create or join a match from the list, or join with a relay code, then enter the match again.");
                yield break;
            }
            while (Time.realtimeSinceStartup < deadline)
            {
                var nmLoop = ResolveNetworkManagerForGameplay();
                if (nmLoop == null || !IsNetcodeTransportReadyForGameplay(nmLoop))
                {
                    pendingTeamRequestCoroutine = null;
                    OnTeamChoiceFailed?.Invoke("Lost connection before joining a team. Return to the menu and rejoin the match.");
                    yield break;
                }
                var ship = TryGetLocalStarship();
                if (ship != null)
                {
                    #region agent log e695ff
                    DebugSessionE695ffLog("H1", "NetworkGameManager.CoRequestTeamWhenLocalPlayerReady", "ship ready before timeout",
                        "{\"team\":" + (int)team + ",\"waitedSec\":" + (Time.realtimeSinceStartup - t0).ToString("F2", CultureInfo.InvariantCulture) + "}");
                    #endregion
                    ship.RequestJoinTeamFromClient(team);
                    pendingTeamRequestCoroutine = null;
                    yield break;
                }
                yield return null;
            }

            pendingTeamRequestCoroutine = null;
            #region agent log e695ff
            var tm = TeamManager.Instance;
            DebugSessionE695ffLog("H2", "NetworkGameManager.CoRequestTeamWhenLocalPlayerReady", "timeout branch",
                BuildLocalPlayerShipDiagJson().TrimEnd('}') + ",\"teamMgrNull\":" + (tm == null ? "true" : "false") + ",\"teamMgrSpawned\":" + (tm != null && tm.IsSpawned ? "true" : "false") + "}");
            #endregion
            if (TeamManager.Instance != null && TeamManager.Instance.IsSpawned)
            {
                Debug.LogWarning("[NetworkGameManager] Local player object not ready after wait; using TeamManager team RPC fallback.");
                TeamManager.Instance.RequestTeamServerRpc(team);
            }
            else
            {
                #region agent log e695ff
                DebugSessionE695ffLog("H2", "NetworkGameManager.CoRequestTeamWhenLocalPlayerReady", "invoke fail — no teamMgr fallback",
                    "{\"reason\":\"timeout_and_no_team_mgr_fallback\"}");
                #endregion
                OnTeamChoiceFailed?.Invoke("Cannot join a team — connection still loading. Try again.");
            }
        }

        /// <summary>Team UI should call this instead of <see cref="TeamManager.RequestTeamServerRpc"/> so the request uses the local player ship (reliable for late join / in-progress matches).</summary>
        public static void RequestTeamFromLocalPlayer(TeamManager.Team team)
        {
            var nm = ResolveNetworkManagerForGameplay();
            if (nm == null)
            {
                Debug.LogError("[NetworkGameManager] RequestTeamFromLocalPlayer: NetworkManager missing.");
                OnTeamChoiceFailed?.Invoke("Not connected.");
                return;
            }
            if (!IsNetcodeTransportReadyForGameplay(nm))
            {
                #region agent log e695ff
                DebugSessionE695ffLog("H4", "NetworkGameManager.RequestTeamFromLocalPlayer", "reject — Netcode not ready",
                    "{\"reason\":\"transport_not_ready\",\"isListening\":" + (nm.IsListening ? "true" : "false") + ",\"isConnectedClient\":" + (nm.IsConnectedClient ? "true" : "false") + "}");
                #endregion
                OnTeamChoiceFailed?.Invoke("Multiplayer is not running. Return to the main menu, create or join a match from the list, or join with a relay code, then enter the match again.");
                return;
            }
            var ship = TryGetLocalStarship();
            #region agent log e695ff
            var tm0 = TeamManager.Instance;
            DebugSessionE695ffLog("H3", "NetworkGameManager.RequestTeamFromLocalPlayer", "entry",
                BuildLocalPlayerShipDiagJson().TrimEnd('}') + ",\"team\":" + (int)team + ",\"hasShip\":" + (ship != null ? "true" : "false") + ",\"staticNgmInstance\":" + (Instance != null ? "true" : "false") + ",\"teamMgrNull\":" + (tm0 == null ? "true" : "false") + ",\"teamMgrSpawned\":" + (tm0 != null && tm0.IsSpawned ? "true" : "false") + "}");
            #endregion
            if (ship != null)
            {
                ship.RequestJoinTeamFromClient(team);
                return;
            }

            var runner = Instance ?? UnityEngine.Object.FindAnyObjectByType<NetworkGameManager>(FindObjectsInactive.Include);
            if (runner != null)
            {
                #region agent log e695ff
                DebugSessionE695ffLog("H3", "NetworkGameManager.RequestTeamFromLocalPlayer", "starting wait coroutine",
                    "{\"runnerFound\":true}");
                #endregion
                if (runner.pendingTeamRequestCoroutine != null)
                    runner.StopCoroutine(runner.pendingTeamRequestCoroutine);
                runner.pendingTeamRequestCoroutine = runner.StartCoroutine(runner.CoRequestTeamWhenLocalPlayerReady(team));
                return;
            }

            if (TeamManager.Instance != null && TeamManager.Instance.IsSpawned)
            {
                Debug.LogWarning("[NetworkGameManager] Local player object not ready; using TeamManager team RPC fallback.");
                TeamManager.Instance.RequestTeamServerRpc(team);
                return;
            }
            #region agent log e695ff
            DebugSessionE695ffLog("H3", "NetworkGameManager.RequestTeamFromLocalPlayer", "invoke fail — no runner no teamMgr",
                BuildLocalPlayerShipDiagJson().TrimEnd('}') + ",\"runnerFound\":false}");
            #endregion
            Debug.LogError("[NetworkGameManager] Cannot request team: no player object and no TeamManager.");
            OnTeamChoiceFailed?.Invoke("Cannot join a team yet — connection still loading. Try again.");
        }

        /// <summary>Same-machine / LAN test without Relay. Host listens on <see cref="serverPort"/> (default 7777).</summary>
        public bool StartLocalHostForLanTest()
        {
            return TryStartLocalHost();
        }

        /// <summary>Join a host on the LAN using direct UDP (no Relay). Use 127.0.0.1 for two instances on one PC.</summary>
        public bool StartLocalClientForLanTest(string address = "127.0.0.1")
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogWarning("[NetworkGameManager] LAN client test is not supported in WebGL; use Relay or test from desktop/Editor.");
            return false;
#endif
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
                return false;
            EnsurePlayerPrefabSet();
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport == null)
            {
                Debug.LogError("[NetworkGameManager] UnityTransport missing.");
                return false;
            }
#if !UNITY_WEBGL
            transport.UseWebSockets = false;
#endif
            transport.SetConnectionData(address, serverPort);
            bool ok = NetworkManager.Singleton.StartClient();
            if (ok)
                Debug.Log($"[NetworkGameManager] LAN client connecting to {address}:{serverPort}");
            return ok;
        }

        /// <summary>Server-only: called from <see cref="TeamManager.ApplyTeamChoiceFromServer"/> so the team result is delivered via this NetworkObject’s ClientRpc (reliable path for UI).</summary>
        public void SendTeamAssignmentResultToClient(ulong clientId, TeamManager.Team assignedTeam, bool requestGranted, string failMessage)
        {
            if (!IsServer) return;
            NotifyLocalClientTeamAssignmentClientRpc(clientId, assignedTeam, requestGranted, failMessage ?? "");
        }

        [ClientRpc]
        private void NotifyLocalClientTeamAssignmentClientRpc(ulong clientId, TeamManager.Team assignedTeam, bool requestGranted, string failMessage)
        {
            var nm = ResolveNetworkManagerForGameplay();
            if (nm == null || nm.LocalClientId != clientId)
                return;
            OnTeamAssignmentResult(assignedTeam, requestGranted, failMessage ?? "");
        }

        public void OnTeamAssignmentResult(TeamManager.Team assignedTeam, bool requestGranted, string failMessage = "")
        {
            LastAssignedTeam = assignedTeam;
            LastTeamRequestGranted = requestGranted;
            if (requestGranted && assignedTeam != TeamManager.Team.None)
                OnTeamChosen?.Invoke(assignedTeam);
            else if (!requestGranted)
            {
                string msg = string.IsNullOrEmpty(failMessage) ? "Could not join that team." : failMessage;
                OnTeamChoiceFailed?.Invoke(msg);
                Debug.LogWarning("[NetworkGameManager] Team choice denied: " + msg);
            }
            else if (requestGranted)
            {
                Debug.LogWarning("[NetworkGameManager] Team choice granted but team was None — UI may stay open. Check server AddPlayerToTeam / ClientRpc path.");
                OnTeamChoiceFailed?.Invoke("Team assignment incomplete. Try again or rejoin.");
            }
        }

        public bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        public bool IsClient => NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;
        public bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

#if UNITY_EDITOR
        [ContextMenu("Debug/Start LAN Host (no Relay)")]
        private void Editor_StartLanHost()
        {
            if (TryStartLocalHost())
                Debug.Log($"[NetworkGameManager] LAN host started on port {serverPort}. Run a second editor/player and use Debug/Start LAN Client, or call StartLocalClientForLanTest.");
        }

        [ContextMenu("Debug/Start LAN Client → 127.0.0.1")]
        private void Editor_StartLanClient()
        {
            StartLocalClientForLanTest("127.0.0.1");
        }
#endif
    }
}
