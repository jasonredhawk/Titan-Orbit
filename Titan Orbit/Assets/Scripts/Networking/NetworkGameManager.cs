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
using Unity.Services.Authentication;
using Unity.Services.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TitanOrbit.Data;
using TitanOrbit.Diagnostics;
using TitanOrbit.Entities;
using UnityEngine.Networking;
using System.Text;

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
        private const string LobbyRelayProtocolKey = "RelayProtocol";
        private const string LobbyServerListenAddressKey = "ServerListenAddress";
        private Lobby currentLobby;
        private float nextLobbyHeartbeatTime;
        private Coroutine pendingTeamRequestCoroutine;
        private static DateTime _dbgNextLobbyQueryAllowedUtc = DateTime.MinValue;
        private static DateTime _dbgNextUnfilteredProbeAllowedUtc = DateTime.MinValue;
        private Coroutine _dbgJoinMonitorCoroutine;
        private bool _dbgNetcodeCallbacksHooked;
        /// <summary>Debug: ordering of disconnect vs OnTransportFailure (session e2a466).</summary>
        private static float _dbgLastLocalDisconnectRealtime = -1f;
        private static float _dbgLastTransportFailureRealtime = -1f;

        // #region agent log
        private static string EscapeJsonE2a466(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        internal static void DebugSessionE2a466Log(string runId, string hypothesisId, string location, string message, string dataJson)
        {
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                if (string.IsNullOrEmpty(root))
                    return;
                string path = Path.Combine(root, "debug-e2a466.log");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line =
                    "{\"sessionId\":\"e2a466\",\"runId\":\"" + EscapeJsonE2a466(runId) +
                    "\",\"hypothesisId\":\"" + EscapeJsonE2a466(hypothesisId) +
                    "\",\"location\":\"" + EscapeJsonE2a466(location) +
                    "\",\"message\":\"" + EscapeJsonE2a466(message) +
                    "\",\"data\":" + dataJson +
                    ",\"timestamp\":" + ts + "}\n";
                File.AppendAllText(path, line);
            }
            catch
            {
            }
        }
        // #endregion

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

        /// <summary>UTP snapshot for Relay transport-failure logs (session e695ff / R1).</summary>
        private static string BuildTransportSnapshotJsonForDebug()
        {
            try
            {
                var nm = ResolveNetworkManagerForGameplay();
                if (nm == null) return "{\"nmNull\":true}";
                var t = nm.GetComponent<UnityTransport>();
                if (t == null) return "{\"transportNull\":true}";
                return "{\"connectTimeoutMs\":" + t.ConnectTimeoutMS + ",\"heartbeatTimeoutMs\":" + t.HeartbeatTimeoutMS
                    + ",\"maxPacketQueueSize\":" + t.MaxPacketQueueSize + ",\"useWebSockets\":" + (t.UseWebSockets ? "true" : "false")
                    + ",\"isListening\":" + (nm.IsListening ? "true" : "false")
                    + ",\"isConnectedClient\":" + (nm.IsConnectedClient ? "true" : "false")
                    + ",\"isServer\":" + (nm.IsServer ? "true" : "false") + "}";
            }
            catch
            {
                return "{\"transportSnapshotError\":true}";
            }
        }

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
                    if (!_dbgNetcodeCallbacksHooked)
                    {
                        NetworkManager.Singleton.OnClientConnectedCallback -= DebugOnAnyClientConnected;
                        NetworkManager.Singleton.OnClientDisconnectCallback -= DebugOnAnyClientDisconnected;
                        NetworkManager.Singleton.OnClientConnectedCallback += DebugOnAnyClientConnected;
                        NetworkManager.Singleton.OnClientDisconnectCallback += DebugOnAnyClientDisconnected;
                        _dbgNetcodeCallbacksHooked = true;
                    }
                }
            }
            catch
            {
            }
        }

        private void AgentOnTransportFailure()
        {
            _dbgLastTransportFailureRealtime = Time.realtimeSinceStartup;
            float secSinceLocalDisc = _dbgLastLocalDisconnectRealtime < 0f
                ? -1f
                : Time.realtimeSinceStartup - _dbgLastLocalDisconnectRealtime;
            string t = RelayConnectionTypeForCurrentPlatform();
            string snap = BuildTransportSnapshotJsonForDebug();
#if UNITY_EDITOR
            bool editorPaused = UnityEditor.EditorApplication.isPaused;
#else
            const bool editorPaused = false;
#endif
            AgentDebugLog("H1", "NetworkGameManager.AgentOnTransportFailure", "OnTransportFailure",
                "{\"relayConnectionType\":\"" + t + "\",\"isHost\":" + (IsHost ? "true" : "false") + ",\"isClient\":" + (IsClient ? "true" : "false") + "}");
            // #region agent log
            string r1Data = "{\"relayConnectionType\":\"" + EscapeJsonE2a466(t) + "\",\"isHost\":" + (IsHost ? "true" : "false") + ",\"isClient\":" + (IsClient ? "true" : "false")
                + ",\"isFocused\":" + (Application.isFocused ? "true" : "false") + ",\"runInBackground\":" + (Application.runInBackground ? "true" : "false")
                + ",\"editorPaused\":" + (editorPaused ? "true" : "false") + ",\"realtimeSinceStartup\":" + Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture)
                + ",\"secSinceLocalDisconnect\":" + (secSinceLocalDisc < 0f ? "-1" : secSinceLocalDisc.ToString("F3", CultureInfo.InvariantCulture))
                + ",\"transport\":" + snap + "}";
            DebugSessionE2a466Log("post-fix", "H11", "NetworkGameManager.AgentOnTransportFailure", "transport_failure",
                "{\"relayConnectionType\":\"" + EscapeJsonE2a466(t) + "\",\"isHost\":" + (IsHost ? "true" : "false") + ",\"isClient\":" + (IsClient ? "true" : "false") + "}");
            DebugSessionE2a466Log("relay-repro", "R1", "NetworkGameManager.AgentOnTransportFailure", "OnTransportFailure_detail", r1Data);
            DebugSessionE695ffLog("R1", "NetworkGameManager.AgentOnTransportFailure", "OnTransportFailure_detail", r1Data);
            // #endregion
        }

        private void DebugOnAnyClientConnected(ulong clientId)
        {
            var nm = ResolveNetworkManagerForGameplay();
            // #region agent log
            DebugSessionE2a466Log("post-fix", "H11", "NetworkGameManager.DebugOnAnyClientConnected", "client_connected_callback",
                "{\"clientId\":" + clientId + ",\"localClientId\":" + (nm != null ? nm.LocalClientId : 0UL) + ",\"isConnectedClient\":" + (nm != null && nm.IsConnectedClient ? "true" : "false") + "}");
            // #endregion
        }

        private void DebugOnAnyClientDisconnected(ulong clientId)
        {
            var nm = ResolveNetworkManagerForGameplay();
            if (nm != null && clientId == nm.LocalClientId)
                _dbgLastLocalDisconnectRealtime = Time.realtimeSinceStartup;
            float secSinceTf = _dbgLastTransportFailureRealtime < 0f
                ? -1f
                : Time.realtimeSinceStartup - _dbgLastTransportFailureRealtime;
            string reason = nm != null ? nm.DisconnectReason : string.Empty;
            // #region agent log
            DebugSessionE2a466Log("post-fix", "H11", "NetworkGameManager.DebugOnAnyClientDisconnected", "client_disconnected_callback",
                "{\"clientId\":" + clientId + ",\"localClientId\":" + (nm != null ? nm.LocalClientId : 0UL) + ",\"isConnectedClient\":" + (nm != null && nm.IsConnectedClient ? "true" : "false") + ",\"disconnectReason\":\"" + EscapeJsonE2a466(reason) + "\"}");
            string r3Data = "{\"clientId\":" + clientId + ",\"disconnectReason\":\"" + EscapeJsonE2a466(reason) + "\",\"secSinceTransportFailure\":" + (secSinceTf < 0f ? "-1" : secSinceTf.ToString("F3", CultureInfo.InvariantCulture)) + ",\"transport\":" + BuildTransportSnapshotJsonForDebug() + "}";
            DebugSessionE2a466Log("relay-repro", "R3", "NetworkGameManager.DebugOnAnyClientDisconnected", "client_disconnect_detail", r3Data);
            DebugSessionE695ffLog("R3", "NetworkGameManager.DebugOnAnyClientDisconnected", "client_disconnect_detail", r3Data);
            try
            {
                StartCoroutine(CoDisconnectReasonFollowUp(clientId));
            }
            catch
            {
            }
            // #endregion
            if (clientId == 0 && currentLobby != null && !string.IsNullOrWhiteSpace(currentLobby.Id))
                _ = DebugFetchLobbyStateAfterDisconnectAsync(currentLobby.Id);
        }

        private async Task DebugFetchLobbyStateAfterDisconnectAsync(string lobbyId)
        {
            try
            {
                Lobby lobby = await LobbyService.Instance.GetLobbyAsync(lobbyId);
                int players = lobby != null && lobby.Players != null ? lobby.Players.Count : -1;
                // #region agent log
                DebugSessionE2a466Log("post-fix", "H15", "NetworkGameManager.DebugFetchLobbyStateAfterDisconnectAsync", "post_disconnect_get_lobby_ok",
                    "{\"lobbyId\":\"" + EscapeJsonE2a466(lobby != null ? lobby.Id : lobbyId) + "\",\"players\":" + players + "}");
                // #endregion
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionE2a466Log("post-fix", "H15", "NetworkGameManager.DebugFetchLobbyStateAfterDisconnectAsync", "post_disconnect_get_lobby_failed",
                    "{\"lobbyId\":\"" + EscapeJsonE2a466(lobbyId) + "\",\"message\":\"" + EscapeJsonE2a466(ex.Message) + "\"}");
                // #endregion
            }
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
        /// Applies the configured server port to UnityTransport so it's used when starting a listen server.
        /// Call this before StartServer (or LAN StartClient address) so "port already in use" can be avoided by changing serverPort in the inspector.
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
        /// Call before joining or starting a listen server. Use menu Titan Orbit > Fix Player Prefab & Materials to assign in editor.
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
            // UnityTransport default heartbeat is often 500ms; Relay/NGO needs a longer ping interval or allocations drop with "inactivity" / empty DisconnectReason.
            if (transport.HeartbeatTimeoutMS > 0 && transport.HeartbeatTimeoutMS < 3000)
            {
                int prevHb = transport.HeartbeatTimeoutMS;
                transport.HeartbeatTimeoutMS = 3000;
                // #region agent log
                DebugSessionE2a466Log("post-fix", "R7", "NetworkGameManager.ApplyRelayFriendlyTransportSettings", "heartbeat_min_clamped",
                    "{\"prev\":" + prevHb + ",\"now\":" + transport.HeartbeatTimeoutMS + "}");
                // #endregion
            }
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
                // #region agent log
                DebugSessionE2a466Log("post-fix", "H12", "NetworkGameManager.StartClientWithRelayAsync", "join_allocation_configured",
                    "{\"allocationId\":\"" + EscapeJsonE2a466(joinAllocation.AllocationId.ToString()) + "\",\"useWebSockets\":" + (transport.UseWebSockets ? "true" : "false") + ",\"relayConnectionType\":\"" + EscapeJsonE2a466(RelayConnectionTypeForCurrentPlatform()) + "\"}");
                // #endregion
                bool started = NetworkManager.Singleton.StartClient();
                #region agent log
                AgentDebugLog("H3", "StartClientWithRelayAsync", "StartClient",
                    "{\"started\":" + (started ? "true" : "false") + ",\"useWebSockets\":" + (transport.UseWebSockets ? "true" : "false") + "}");
                #endregion
                if (started)
                {
                    StartDebugJoinMonitor("join_code", "manual_code");
                    Debug.Log("Client started with Relay.");
                }
                return started;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }

        /// <summary>
        /// Quick-join an open UGS lobby using the same indexed filters as <see cref="QueryWebGLOpenLobbiesAsync"/>, then connect as Relay client only.
        /// Falls back to <see cref="LobbyService.QueryLobbiesAsync"/> + <see cref="PlayWebGLJoinByLobbyIdAsync"/> when Quick Join errors (common on WebGL).
        /// Requires a dedicated server (or other lobby host) to have created the lobby; never creates allocations or listen servers here.
        /// </summary>
        private async Task<bool> TryQuickJoinOpenLobbyAsClientAsync()
        {
            EnsurePlayerPrefabSet();
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("Player Prefab not set on NetworkManager! Add a Starship prefab to Resources/Prefabs/Starship.prefab or use menu: Titan Orbit > Fix Player Prefab & Materials");
                return false;
            }

            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                {
                    // #region agent log
                    F38c7dDebugLog.Write("H1", "NetworkGameManager.TryQuickJoinOpenLobbyAsClientAsync", "ugs_not_ready", "{}");
                    // #endregion
                    return false;
                }

                #region agent log
                RegisterAgentTransportFailureHandler();
                F38c7dDebugLog.Write("H4", "NetworkGameManager.TryQuickJoinOpenLobbyAsClientAsync", "quickjoin_enter", "{}");
                #endregion

                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
                }

                try
                {
                    Lobby joinedLobby = null;
                    foreach (bool latestOnly in new[] { true, false })
                    {
                        try
                        {
                            string playerId = AuthenticationService.Instance.PlayerId;
                            var quickOptions = new QuickJoinLobbyOptions
                            {
                                Filter = BuildDedicatedLobbyQueryFilters(latestOnly),
                            };
                            if (!string.IsNullOrEmpty(playerId))
                                quickOptions.Player = new Player(id: playerId);

                            joinedLobby = await LobbyService.Instance.QuickJoinLobbyAsync(quickOptions);
                            if (joinedLobby != null)
                                break;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning(
                                "[NetworkGameManager] QuickJoinLobbyAsync (latestOnly=" + latestOnly + "): " +
                                ex.GetType().Name + " — " + ex.Message);
                            joinedLobby = null;
                        }
                    }

                    if (joinedLobby != null && joinedLobby.Data != null && joinedLobby.Data.ContainsKey(LobbyRelayCodeKey))
                    {
                        try
                        {
                            string joinCode = joinedLobby.Data[LobbyRelayCodeKey].Value;
                            if (!string.IsNullOrEmpty(joinCode))
                            {
                                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
                                ConfigureUnityTransportRelay(transport, joinAllocation);
                                if (NetworkManager.Singleton.StartClient())
                                {
                                    currentLobby = joinedLobby;
                                    StartDebugJoinMonitor("quick_join", joinedLobby.Id);
                                    Debug.Log("Joined existing game via Lobby quick join (client only).");
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning("[NetworkGameManager] Quick join Relay/StartClient step failed: " + ex.Message);
                        }
                    }

                    foreach (bool latestOnly in new[] { true, false })
                    {
                        try
                        {
                            QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                            {
                                Count = 15,
                                Filters = BuildDedicatedLobbyQueryFilters(latestOnly),
                                Order = new List<QueryOrder>
                                {
                                    new QueryOrder(asc: false, field: QueryOrder.FieldOptions.Created),
                                },
                            });
                            if (response?.Results == null || response.Results.Count == 0)
                                continue;

                            foreach (Lobby candidate in response.Results)
                            {
                                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id))
                                    continue;
                                try
                                {
                                    if (await PlayWebGLJoinByLobbyIdAsync(candidate.Id))
                                    {
                                        Debug.Log("[NetworkGameManager] Joined via lobby query fallback (by lobby id).");
                                        return true;
                                    }
                                }
                                catch (Exception joinEx)
                                {
                                    Debug.LogWarning(
                                        "[NetworkGameManager] Join candidate " + candidate.Id + " failed: " + joinEx.Message);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning(
                                "[NetworkGameManager] Query join fallback (latestOnly=" + latestOnly + "): " + ex.Message);
                        }
                    }

                    Debug.LogWarning(
                        "[NetworkGameManager] No open dedicated lobby to join. Start the headless server (or use Host match (browser) for a player-hosted test room).");
                    return false;
                }
                catch (Exception inner)
                {
                    Debug.LogWarning("[NetworkGameManager] TryQuickJoinOpenLobbyAsClientAsync (inner): " + inner);
                    return false;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] TryQuickJoinOpenLobbyAsClientAsync failed. " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Desktop / editor: quick-join an existing dedicated lobby via Relay. Does not create lobbies or start a listen server.
        /// </summary>
        public async Task<bool> PlayQuickJoinOrCreateAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogWarning("PlayQuickJoinOrCreateAsync is not supported in WebGL builds. Use PlayWebGLJoinAsync instead.");
            return false;
#else
            return await TryQuickJoinOpenLobbyAsClientAsync();
#endif
        }

        /// <summary>
        /// Quick-join an existing lobby and connect as client via Relay. Does not create lobbies or start a listen server.
        /// </summary>
        public async Task<bool> PlayWebGLJoinAsync()
        {
            return await TryQuickJoinOpenLobbyAsClientAsync();
        }

        /// <summary>
        /// Creates a Relay allocation + UGS lobby (same indexed lobby data as <see cref="DedicatedMatchServerBootstrap"/>), then starts Netcode as host.
        /// Runs in the browser/editor — it does not start a process on a GCE VM. Use for testing when no headless lobby exists, or for temporary player-hosted rooms.
        /// </summary>
        public async Task<bool> PlayWebGLHostRelayMatchAsync()
        {
            EnsurePlayerPrefabSet();
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("Player Prefab not set on NetworkManager.");
                return false;
            }

            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return false;

                RegisterAgentTransportFailureHandler();
                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
                }

                ApplyServerPort();
                int cap = Mathf.Max(2, maxPlayers);
                int relayMaxConnections = Mathf.Max(1, cap - 1);
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(relayMaxConnections);
                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                ConfigureUnityTransportRelay(transport, allocation);

                long createdAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                const bool isLatest = true;
                Lobby createdLobby = await LobbyService.Instance.CreateLobbyAsync(
                    GameNames.GetRandomRoomName(),
                    cap,
                    new CreateLobbyOptions
                    {
                        IsPrivate = false,
                        Data = new Dictionary<string, DataObject>
                        {
                            { LobbyRelayCodeKey, new DataObject(DataObject.VisibilityOptions.Member, joinCode) },
                            { LobbyGameNameKey, new DataObject(DataObject.VisibilityOptions.Public, LobbyGameNameValue, DataObject.IndexOptions.S1) },
                            { LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N1) },
                            { LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, isLatest ? "1" : "0", DataObject.IndexOptions.N2) },
                            {
                                LobbyCreatedAtEpochKey,
                                new DataObject(
                                    DataObject.VisibilityOptions.Public,
                                    createdAtEpochSeconds.ToString(CultureInfo.InvariantCulture),
                                    DataObject.IndexOptions.N3
                                )
                            },
                        },
                    });

                bool started = NetworkManager.Singleton.StartHost();
                if (!started)
                {
                    Debug.LogError("[NetworkGameManager] StartHost failed for Relay browser host.");
                    return false;
                }

                currentLobby = createdLobby;
                Debug.Log("[NetworkGameManager] Relay host started; lobby id=" + createdLobby.Id);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] PlayWebGLHostRelayMatchAsync failed. " + e.Message);
                return false;
            }
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
            // #region agent log
            F38c7dDebugLog.Write("H4", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "enter",
                "{\"lobbyIdLen\":" + (lobbyId != null ? lobbyId.Length : 0) + "}");
            // #endregion
            if (string.IsNullOrWhiteSpace(lobbyId))
            {
                Debug.LogError("LobbyId is empty.");
                return false;
            }

            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                {
                    // #region agent log
                    F38c7dDebugLog.Write("H4", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "ugs_not_ready", "{}");
                    // #endregion
                    return false;
                }
                #region agent log
                RegisterAgentTransportFailureHandler();
                #endregion

                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    // #region agent log
                    F38c7dDebugLog.Write("H4", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "no_transport", "{}");
                    // #endregion
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
                        // #region agent log
                        F38c7dDebugLog.Write("H4", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "join_lobby_failed",
                            "{\"reason\":" + (int)e.Reason + "}");
                        // #endregion
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
                // #region agent log
                DebugSessionE2a466Log("post-fix", "H12", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "join_allocation_configured",
                    "{\"allocationId\":\"" + EscapeJsonE2a466(joinAllocation.AllocationId.ToString()) + "\",\"useWebSockets\":" + (transport.UseWebSockets ? "true" : "false") + ",\"relayConnectionType\":\"" + EscapeJsonE2a466(RelayConnectionTypeForCurrentPlatform()) + "\"}");
                // #endregion

                bool startedLobby = NetworkManager.Singleton.StartClient();
                #region agent log
                AgentDebugLog("H3", "PlayWebGLJoinByLobbyIdAsync", "StartClient",
                    "{\"started\":" + (startedLobby ? "true" : "false") + ",\"useWebSockets\":" + (transport.UseWebSockets ? "true" : "false") + "}");
                #endregion
                if (startedLobby)
                {
                    currentLobby = joinedLobby;
                    long createdAtEpoch = 0;
                    bool hasCreatedAt = joinedLobby.Data != null &&
                        joinedLobby.Data.TryGetValue(LobbyCreatedAtEpochKey, out DataObject createdAtObj) &&
                        long.TryParse(createdAtObj?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out createdAtEpoch);
                    long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long ageSec = hasCreatedAt ? Mathf.Max(0, (int)(nowEpoch - createdAtEpoch)) : -1;
                    bool isOpen = joinedLobby.Data != null &&
                        joinedLobby.Data.TryGetValue(LobbyIsOpenKey, out DataObject openObj) &&
                        string.Equals(openObj?.Value, "1", StringComparison.Ordinal);
                    bool isLatest = joinedLobby.Data != null &&
                        joinedLobby.Data.TryGetValue(LobbyIsLatestKey, out DataObject latestObj) &&
                        string.Equals(latestObj?.Value, "1", StringComparison.Ordinal);
                    string lobbyRelayProtocol = joinedLobby.Data != null &&
                        joinedLobby.Data.TryGetValue(LobbyRelayProtocolKey, out DataObject relayProtocolObj)
                        ? (relayProtocolObj?.Value ?? "")
                        : "";
                    string lobbyServerListenAddress = joinedLobby.Data != null &&
                        joinedLobby.Data.TryGetValue(LobbyServerListenAddressKey, out DataObject serverListenAddressObj)
                        ? (serverListenAddressObj?.Value ?? "")
                        : "";
                    // #region agent log
                    DebugSessionE2a466Log("post-fix", "H13", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "joined_lobby_metadata",
                        "{\"lobbyId\":\"" + EscapeJsonE2a466(joinedLobby.Id) + "\",\"name\":\"" + EscapeJsonE2a466(joinedLobby.Name) + "\",\"players\":" + (joinedLobby.Players != null ? joinedLobby.Players.Count : 0) + ",\"maxPlayers\":" + joinedLobby.MaxPlayers + ",\"isOpen\":" + (isOpen ? "true" : "false") + ",\"isLatest\":" + (isLatest ? "true" : "false") + ",\"ageSec\":" + ageSec + ",\"lobbyRelayProtocol\":\"" + EscapeJsonE2a466(lobbyRelayProtocol) + "\",\"lobbyServerListenAddress\":\"" + EscapeJsonE2a466(lobbyServerListenAddress) + "\"}");
                    // #endregion
                    StartDebugJoinMonitor("join_lobby_id", id);
                    // #region agent log
                    F38c7dDebugLog.Write("H4", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "join_ok", "{}");
                    // #endregion
                    Debug.Log("Joined lobby by id via Relay (WebGL client).");
                    return true;
                }

                // #region agent log
                F38c7dDebugLog.Write("H4", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "start_client_false", "{}");
                // #endregion
                return false;
            }
            catch (LobbyServiceException e)
            {
                // #region agent log
                F38c7dDebugLog.Write("H4", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "lobby_service_ex",
                    "{\"reason\":" + (int)e.Reason + "}");
                // #endregion
                Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinByLobbyIdAsync failed: " + e.Message);
                return false;
            }
            catch (System.Exception e)
            {
                // #region agent log
                F38c7dDebugLog.Write("H4", "NetworkGameManager.PlayWebGLJoinByLobbyIdAsync", "general_ex",
                    "{\"exType\":\"" + e.GetType().Name + "\"}");
                // #endregion
                Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinByLobbyIdAsync failed. " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Indexed lobby fields written by <see cref="DedicatedMatchServerBootstrap"/>; shared by query and Quick Join.
        /// </summary>
        private static List<QueryFilter> BuildDedicatedLobbyQueryFilters(bool latestOnly)
        {
            var filters = new List<QueryFilter>
            {
                new QueryFilter(QueryFilter.FieldOptions.S1, LobbyGameNameValue, QueryFilter.OpOptions.EQ),
                new QueryFilter(QueryFilter.FieldOptions.N1, "1", QueryFilter.OpOptions.EQ),
            };
            if (latestOnly)
                filters.Add(new QueryFilter(QueryFilter.FieldOptions.N2, "1", QueryFilter.OpOptions.EQ));
            return filters;
        }

        public async Task<List<LobbySummary>> QueryOpenLobbiesAsync(bool latestOnly, int count = 20)
        {
            var results = new List<LobbySummary>();
            if (DateTime.UtcNow < _dbgNextLobbyQueryAllowedUtc)
            {
                // #region agent log
                DebugSessionE2a466Log("post-fix", "H9", "NetworkGameManager.QueryOpenLobbiesAsync", "query_skipped_backoff",
                    "{\"remainingMs\":" + (_dbgNextLobbyQueryAllowedUtc - DateTime.UtcNow).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + "}");
                // #endregion
                return results;
            }
            // #region agent log
            DebugSessionE2a466Log("pre-fix", "H5", "NetworkGameManager.QueryOpenLobbiesAsync", "query_enter",
                "{\"latestOnly\":" + (latestOnly ? "true" : "false") + ",\"count\":" + count + "}");
            // #endregion
            // #region agent log
            F38c7dDebugLog.Write("H2", "NetworkGameManager.QueryOpenLobbiesAsync", "enter",
                "{\"latestOnly\":" + (latestOnly ? "true" : "false") + ",\"count\":" + count + "}");
            // #endregion
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                {
                    // #region agent log
                    bool signedIn = false;
                    bool authorized = false;
                    try
                    {
                        if (UnityServices.State == ServicesInitializationState.Initialized)
                        {
                            signedIn = AuthenticationService.Instance.IsSignedIn;
                            authorized = AuthenticationService.Instance.IsAuthorized;
                        }
                    }
                    catch
                    {
                    }
                    F38c7dDebugLog.Write("H1", "NetworkGameManager.QueryOpenLobbiesAsync", "ugs_not_ready",
                        "{\"ugsState\":" + (int)UnityServices.State + ",\"signedIn\":" + (signedIn ? "true" : "false") +
                        ",\"authorized\":" + (authorized ? "true" : "false") + "}");
                    // #endregion
                    return results;
                }

                var filters = BuildDedicatedLobbyQueryFilters(latestOnly);

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
                // #region agent log
                int rawCount = response?.Results != null ? response.Results.Count : -1;
                F38c7dDebugLog.Write("H2", "NetworkGameManager.QueryOpenLobbiesAsync", "query_done",
                    "{\"responseNull\":" + (response == null ? "true" : "false") + ",\"rawResultCount\":" + rawCount + "}");
                // #endregion
                if (response?.Results == null)
                {
                    // #region agent log
                    DebugSessionE2a466Log("pre-fix", "H5", "NetworkGameManager.QueryOpenLobbiesAsync", "query_null_results",
                        "{\"responseNull\":" + (response == null ? "true" : "false") + "}");
                    // #endregion
                    return results;
                }

                foreach (var lobby in response.Results)
                {
                    if (lobby == null)
                        continue;
                    results.Add(ToLobbySummary(lobby));
                }

                var sb = new StringBuilder();
                sb.Append("{\"resultCount\":").Append(results.Count).Append(",\"lobbies\":[");
                for (int i = 0; i < results.Count && i < 8; i++)
                {
                    var r = results[i];
                    if (i > 0) sb.Append(",");
                    string relayProtocol = "";
                    string serverListenAddress = "";
                    if (i < response.Results.Count)
                    {
                        var src = response.Results[i];
                        if (src != null && src.Data != null)
                        {
                            if (src.Data.TryGetValue(LobbyRelayProtocolKey, out DataObject rpObj))
                                relayProtocol = rpObj?.Value ?? "";
                            if (src.Data.TryGetValue(LobbyServerListenAddressKey, out DataObject slaObj))
                                serverListenAddress = slaObj?.Value ?? "";
                        }
                    }
                    sb.Append("{\"name\":\"").Append(EscapeJsonE2a466(r.Name))
                        .Append("\",\"players\":").Append(r.CurrentPlayers)
                        .Append(",\"max\":").Append(r.MaxPlayers)
                        .Append(",\"isOpen\":").Append(r.IsOpen ? "true" : "false")
                        .Append(",\"isLatest\":").Append(r.IsLatest ? "true" : "false")
                        .Append(",\"relayProtocol\":\"").Append(EscapeJsonE2a466(relayProtocol)).Append("\"")
                        .Append(",\"serverListenAddress\":\"").Append(EscapeJsonE2a466(serverListenAddress)).Append("\"")
                        .Append("}");
                }
                sb.Append("]}");
                // #region agent log
                DebugSessionE2a466Log("pre-fix", "H5", "NetworkGameManager.QueryOpenLobbiesAsync", "query_results",
                    sb.ToString());
                // #endregion

                if (results.Count == 0)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    // Extra unfiltered query helps debug filter mismatches but counts toward Lobby rate limits ("Too Many Requests") and can make the list look empty while throttled.
                    if (DateTime.UtcNow < _dbgNextUnfilteredProbeAllowedUtc)
                    {
                        // #region agent log
                        DebugSessionE2a466Log("post-fix", "H9", "NetworkGameManager.QueryOpenLobbiesAsync", "query_unfiltered_probe_skipped_backoff",
                            "{\"remainingMs\":" + (_dbgNextUnfilteredProbeAllowedUtc - DateTime.UtcNow).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + "}");
                        // #endregion
                        return results;
                    }

                    try
                    {
                        QueryResponse unfiltered = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                        {
                            Count = 5,
                            Order = new List<QueryOrder> { new QueryOrder(asc: false, field: QueryOrder.FieldOptions.Created) }
                        });
                        _dbgNextUnfilteredProbeAllowedUtc = DateTime.UtcNow.AddSeconds(60);

                        var ub = new StringBuilder();
                        ub.Append("{\"unfilteredCount\":").Append(unfiltered?.Results != null ? unfiltered.Results.Count : 0).Append(",\"sample\":[");
                        if (unfiltered?.Results != null)
                        {
                            for (int i = 0; i < unfiltered.Results.Count && i < 5; i++)
                            {
                                Lobby l = unfiltered.Results[i];
                                if (i > 0) ub.Append(",");
                                bool hasGameName = l?.Data != null && l.Data.ContainsKey(LobbyGameNameKey);
                                bool hasIsOpen = l?.Data != null && l.Data.ContainsKey(LobbyIsOpenKey);
                                string gameName = hasGameName ? l.Data[LobbyGameNameKey].Value : "";
                                string isOpen = hasIsOpen ? l.Data[LobbyIsOpenKey].Value : "";
                                ub.Append("{\"name\":\"").Append(EscapeJsonE2a466(l?.Name ?? ""))
                                    .Append("\",\"players\":").Append(l?.Players != null ? l.Players.Count : 0)
                                    .Append(",\"hasGameName\":").Append(hasGameName ? "true" : "false")
                                    .Append(",\"gameName\":\"").Append(EscapeJsonE2a466(gameName))
                                    .Append("\",\"hasIsOpen\":").Append(hasIsOpen ? "true" : "false")
                                    .Append(",\"isOpen\":\"").Append(EscapeJsonE2a466(isOpen)).Append("\"}");
                            }
                        }
                        ub.Append("]}");
                        // #region agent log
                        DebugSessionE2a466Log("pre-fix", "H6", "NetworkGameManager.QueryOpenLobbiesAsync", "query_unfiltered_probe",
                            ub.ToString());
                        // #endregion
                    }
                    catch (Exception probeEx)
                    {
                        if (probeEx is LobbyServiceException && string.Equals(probeEx.Message, "Too Many Requests", StringComparison.OrdinalIgnoreCase))
                        {
                            _dbgNextUnfilteredProbeAllowedUtc = DateTime.UtcNow.AddSeconds(60);
                        }
                        // #region agent log
                        DebugSessionE2a466Log("pre-fix", "H6", "NetworkGameManager.QueryOpenLobbiesAsync", "query_unfiltered_probe_exception",
                            "{\"exType\":\"" + EscapeJsonE2a466(probeEx.GetType().Name) + "\",\"message\":\"" + EscapeJsonE2a466(probeEx.Message) + "\"}");
                        // #endregion
                    }
#endif
                }
            }
            catch (Exception e)
            {
                if (e is LobbyServiceException && string.Equals(e.Message, "Too Many Requests", StringComparison.OrdinalIgnoreCase))
                {
                    _dbgNextLobbyQueryAllowedUtc = DateTime.UtcNow.AddSeconds(20);
                    // #region agent log
                    DebugSessionE2a466Log("post-fix", "H9", "NetworkGameManager.QueryOpenLobbiesAsync", "query_backoff_set",
                        "{\"backoffMs\":20000}");
                    // #endregion
                }
                // #region agent log
                DebugSessionE2a466Log("pre-fix", "H5", "NetworkGameManager.QueryOpenLobbiesAsync", "query_exception",
                    "{\"exType\":\"" + EscapeJsonE2a466(e.GetType().Name) + "\",\"message\":\"" + EscapeJsonE2a466(e.Message) + "\"}");
                // #endregion
                // #region agent log
                F38c7dDebugLog.Write("H2", "NetworkGameManager.QueryOpenLobbiesAsync", "query_exception",
                    "{\"exType\":\"" + e.GetType().Name + "\"}");
                // #endregion
                Debug.LogWarning("[NetworkGameManager] QueryOpenLobbiesAsync failed: " + e.Message);
            }

            // #region agent log
            F38c7dDebugLog.Write("H2", "NetworkGameManager.QueryOpenLobbiesAsync", "exit",
                "{\"summaryCount\":" + results.Count + "}");
            // #endregion
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

                var filters = BuildDedicatedLobbyQueryFilters(latestOnly);

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

        /// <summary>Same-machine / LAN test without Relay: listen server on <see cref="serverPort"/> (default 7777). Use <see cref="StartLocalClientForLanTest"/> from a second instance.</summary>
        public bool StartLocalHostForLanTest()
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
                return false;
            EnsurePlayerPrefabSet();
            ApplyServerPort();
            if (!NetworkManager.Singleton.StartServer())
                return false;
            Debug.Log($"[NetworkGameManager] LAN listen server started on port {serverPort}.");
            return true;
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

        private void StartDebugJoinMonitor(string source, string lobbyIdOrTag)
        {
            // #region agent log
            DebugSessionE2a466Log("post-fix", "H10", "NetworkGameManager.StartDebugJoinMonitor", "join_monitor_start",
                "{\"source\":\"" + EscapeJsonE2a466(source) + "\",\"tag\":\"" + EscapeJsonE2a466(lobbyIdOrTag) + "\"}");
            // #endregion
            if (_dbgJoinMonitorCoroutine != null)
                StopCoroutine(_dbgJoinMonitorCoroutine);
            _dbgJoinMonitorCoroutine = StartCoroutine(CoDebugJoinMonitor(source, lobbyIdOrTag));
        }

        private void LogJoinMonitorRelayRepro(string messageTag, string source, string lobbyIdOrTag, float t0)
        {
#if UNITY_EDITOR
            bool editorPaused = UnityEditor.EditorApplication.isPaused;
#else
            const bool editorPaused = false;
#endif
            var nm = ResolveNetworkManagerForGameplay();
            string data = "{\"source\":\"" + EscapeJsonE2a466(source) + "\",\"tag\":\"" + EscapeJsonE2a466(lobbyIdOrTag) + "\",\"messageTag\":\"" + EscapeJsonE2a466(messageTag) + "\",\"connected\":" + (nm != null && nm.IsConnectedClient ? "true" : "false") + ",\"listening\":" + (nm != null && nm.IsListening ? "true" : "false") + ",\"isFocused\":" + (Application.isFocused ? "true" : "false") + ",\"editorPaused\":" + (editorPaused ? "true" : "false") + ",\"elapsedMs\":" + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0", CultureInfo.InvariantCulture) + ",\"lobbyPlayers\":" + (currentLobby != null && currentLobby.Players != null ? currentLobby.Players.Count : -1) + ",\"transport\":" + BuildTransportSnapshotJsonForDebug() + "}";
            DebugSessionE2a466Log("relay-repro", "R4", "NetworkGameManager.CoDebugJoinMonitor", messageTag, data);
        }

        private IEnumerator CoDisconnectReasonFollowUp(ulong clientId)
        {
            yield return null;
            yield return null;
            var nm = ResolveNetworkManagerForGameplay();
            string reason = nm != null ? nm.DisconnectReason : string.Empty;
            bool stillClient = nm != null && nm.IsConnectedClient;
            DebugSessionE2a466Log("relay-repro", "R6", "NetworkGameManager.CoDisconnectReasonFollowUp", "disconnect_followup_2frames",
                "{\"clientId\":" + clientId + ",\"disconnectReason\":\"" + EscapeJsonE2a466(reason) + "\",\"isConnectedClient\":" + (stillClient ? "true" : "false") + "}");
        }

        private IEnumerator CoDebugJoinMonitor(string source, string lobbyIdOrTag)
        {
            float t0 = Time.realtimeSinceStartup;
            yield return new WaitForSeconds(5f);
            var nm = ResolveNetworkManagerForGameplay();
            bool connectedAt5s = nm != null && nm.IsConnectedClient;
            int playersAt5s = currentLobby != null && currentLobby.Players != null ? currentLobby.Players.Count : -1;
            // #region agent log
            DebugSessionE2a466Log("post-fix", "H10", "NetworkGameManager.CoDebugJoinMonitor", "join_monitor_5s",
                "{\"source\":\"" + EscapeJsonE2a466(source) + "\",\"tag\":\"" + EscapeJsonE2a466(lobbyIdOrTag) + "\",\"connected\":" + (connectedAt5s ? "true" : "false") + ",\"lobbyPlayers\":" + playersAt5s + "}");
            // #endregion

            yield return new WaitForSeconds(10f);
            nm = ResolveNetworkManagerForGameplay();
            bool connectedAt15s = nm != null && nm.IsConnectedClient;
            bool listeningAt15s = nm != null && nm.IsListening;
            int playersAt15s = currentLobby != null && currentLobby.Players != null ? currentLobby.Players.Count : -1;
            // #region agent log
            DebugSessionE2a466Log("post-fix", "H10", "NetworkGameManager.CoDebugJoinMonitor", "join_monitor_15s",
                "{\"source\":\"" + EscapeJsonE2a466(source) + "\",\"tag\":\"" + EscapeJsonE2a466(lobbyIdOrTag) + "\",\"connected\":" + (connectedAt15s ? "true" : "false") + ",\"listening\":" + (listeningAt15s ? "true" : "false") + ",\"lobbyPlayers\":" + playersAt15s + ",\"elapsedMs\":" + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0", CultureInfo.InvariantCulture) + "}");
            // #endregion

            yield return new WaitForSeconds(15f);
            LogJoinMonitorRelayRepro("join_monitor_30s", source, lobbyIdOrTag, t0);
            yield return new WaitForSeconds(15f);
            LogJoinMonitorRelayRepro("join_monitor_45s", source, lobbyIdOrTag, t0);
            yield return new WaitForSeconds(15f);
            nm = ResolveNetworkManagerForGameplay();
            bool connectedAt60s = nm != null && nm.IsConnectedClient;
            // #region agent log
            DebugSessionE2a466Log("post-fix", "H14", "NetworkGameManager.CoDebugJoinMonitor", "join_monitor_60s",
                "{\"source\":\"" + EscapeJsonE2a466(source) + "\",\"tag\":\"" + EscapeJsonE2a466(lobbyIdOrTag) + "\",\"connected\":" + (connectedAt60s ? "true" : "false") + ",\"isClient\":" + (nm != null && nm.IsClient ? "true" : "false") + "}");
            // #endregion

            if (currentLobby != null && !string.IsNullOrWhiteSpace(currentLobby.Id))
            {
                var getTask = LobbyService.Instance.GetLobbyAsync(currentLobby.Id);
                while (!getTask.IsCompleted)
                    yield return null;

                if (getTask.IsFaulted)
                {
                    string exMessage = getTask.Exception != null ? getTask.Exception.GetBaseException().Message : "unknown";
                    // #region agent log
                    DebugSessionE2a466Log("post-fix", "H14", "NetworkGameManager.CoDebugJoinMonitor", "join_monitor_60s_get_lobby_failed",
                        "{\"message\":\"" + EscapeJsonE2a466(exMessage) + "\"}");
                    // #endregion
                }
                else
                {
                    Lobby fetched = getTask.Result;
                    int fetchedPlayers = fetched != null && fetched.Players != null ? fetched.Players.Count : -1;
                    // #region agent log
                    DebugSessionE2a466Log("post-fix", "H14", "NetworkGameManager.CoDebugJoinMonitor", "join_monitor_60s_get_lobby_ok",
                        "{\"lobbyId\":\"" + EscapeJsonE2a466(fetched != null ? fetched.Id : "") + "\",\"players\":" + fetchedPlayers + "}");
                    // #endregion
                }
            }

            _dbgJoinMonitorCoroutine = null;
        }

#if UNITY_EDITOR
        [ContextMenu("Debug/Start LAN listen server (no Relay)")]
        private void Editor_StartLanHost()
        {
            if (StartLocalHostForLanTest())
                Debug.Log($"[NetworkGameManager] LAN listen server on port {serverPort}. Run a second instance and use Debug/Start LAN Client, or call StartLocalClientForLanTest.");
        }

        [ContextMenu("Debug/Start LAN Client → 127.0.0.1")]
        private void Editor_StartLanClient()
        {
            StartLocalClientForLanTest("127.0.0.1");
        }
#endif
    }
}
