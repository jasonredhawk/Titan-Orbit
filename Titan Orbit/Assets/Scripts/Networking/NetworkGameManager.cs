using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Services;
using Unity.Services.Multiplayer;
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
using System.Threading;
using System.Threading.Tasks;
using TitanOrbit.Data;
using TitanOrbit.Entities;

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
            /// <summary>UTC unix-seconds from dedicated server heartbeat; 0 if not published yet.</summary>
            public long ServerAliveAtEpochSeconds;
            /// <summary>Live Netcode player count from dedicated server heartbeat; -1 if not published yet.</summary>
            public int ActivePlayers = -1;
        }

        /// <summary>Skip dedicated lobbies whose server has not heartbeated recently (ghost listing after process death).</summary>
        public const int DedicatedLobbyStaleSeconds = 120;

        public static NetworkGameManager Instance { get; private set; }

        /// <summary>
        /// Unity Lobbies SDK is not safe under concurrent calls (observed <see cref="NullReferenceException"/> and WebGL hangs
        /// when <see cref="LobbyService.Instance.QueryLobbiesAsync"/> overlaps <see cref="LobbyService.Instance.JoinLobbyByIdAsync"/>).
        /// </summary>
        static readonly SemaphoreSlim LobbyApiGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Serializes join-browser refresh (strict query + stabilization recursion) so concurrent UI callers cannot interleave.
        /// </summary>
        static readonly SemaphoreSlim OpenLobbyRefreshGate = new SemaphoreSlim(1, 1);

        static async Task AcquireLobbyApiGateAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            while (!LobbyApiGate.Wait(0))
                await Task.Yield();
#else
            await LobbyApiGate.WaitAsync();
#endif
        }

        static async Task<T> WithLobbyApiTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string operationName)
        {
            Task delay = Task.Delay(timeout);
            Task finished = await Task.WhenAny(task, delay);
            if (finished == delay)
            {
                Debug.LogWarning("[NetworkGameManager] TIMEOUT after " + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s: " + operationName);
                throw new TimeoutException(operationName);
            }
            return await task;
        }

        static async Task<QueryResponse> QueryLobbiesAsyncUnguarded(QueryLobbiesOptions options)
        {
            try
            {
                return await LobbyService.Instance.QueryLobbiesAsync(options);
            }
            catch (NullReferenceException)
            {
                return new QueryResponse(new List<Lobby>(), null);
            }
        }

        static async Task<QueryResponse> QueryLobbiesSerializedAsync(QueryLobbiesOptions options)
        {
            await AcquireLobbyApiGateAsync();
            try
            {
                return await QueryLobbiesAsyncUnguarded(options);
            }
            finally
            {
                LobbyApiGate.Release();
            }
        }

        static JoinLobbyByIdOptions BuildJoinLobbyByIdOptions()
        {
            var options = new JoinLobbyByIdOptions();
            string playerId = AuthenticationService.Instance.PlayerId;
            if (!string.IsNullOrEmpty(playerId))
                options.Player = new Player(id: playerId);
            return options;
        }

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
        private const string LobbyServerAliveEpochKey = "ServerAliveAt";
        private const string LobbyRelayProtocolKey = "RelayProtocol";
        private const string LobbyServerListenAddressKey = "ServerListenAddress";
        private const string LobbyActivePlayersKey = DedicatedMatchServerBootstrap.LobbyActivePlayersKey;
        private Lobby currentLobby;
        private float nextLobbyHeartbeatTime;
        private Coroutine pendingTeamRequestCoroutine;
        private bool _leaveLobbyInProgress;
        private static DateTime _dbgNextLobbyQueryAllowedUtc = DateTime.MinValue;

        /// <summary>Why the last <see cref="QueryOpenLobbiesAsync"/> returned zero rows (for lobby UI; avoids showing "no games" during client throttle).</summary>
        public enum OpenLobbyQueryResultKind
        {
            Ok,
            RateLimitBackoff,
            UnityServicesNotReady,
            Error
        }

        public static OpenLobbyQueryResultKind LastOpenLobbyQueryKind { get; private set; }
        public static string LastOpenLobbyQueryErrorDetail { get; private set; }
        /// <summary>Seconds until <see cref="QueryOpenLobbiesAsync"/> will run again after a rate-limit backoff (approximate).</summary>
        public static float LobbyRateLimitRemainingSeconds { get; private set; }

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
            RegisterLocalClientLobbyCleanupHandlers();
            if (autoStartServer && Application.isEditor)
            {
                // Auto-start server in editor for testing
                StartServer();
            }
        }

        private void RegisterLocalClientLobbyCleanupHandlers()
        {
            try
            {
                if (NetworkManager.Singleton == null)
                    return;
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnLocalGameplayLeaveLobbyDisconnect;
                NetworkManager.Singleton.OnClientDisconnectCallback += OnLocalGameplayLeaveLobbyDisconnect;
            }
            catch
            {
            }
        }

        private void OnLocalGameplayLeaveLobbyDisconnect(ulong clientId)
        {
            var nm = ResolveNetworkManagerForGameplay();
            if (nm != null && clientId == nm.LocalClientId)
                _ = LeaveCurrentLobbyIfMemberAsync("local_client_disconnected");
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                try
                {
                    if (NetworkManager.Singleton != null)
                        NetworkManager.Singleton.OnClientDisconnectCallback -= OnLocalGameplayLeaveLobbyDisconnect;
                }
                catch
                {
                }
            }

            if (currentLobby != null)
                _ = LeaveCurrentLobbyIfMemberAsync("network_game_manager_destroyed");
        }

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

        /// <summary>Call before StartServer/StartClient/StartHost so players are not spawned until they join a team.</summary>
        private static void PrepareNetworkManagerForSessionStart()
        {
            EnsurePlayerPrefabSet();
            if (NetworkManager.Singleton != null)
                DeferredPlayerShipSpawn.Configure(NetworkManager.Singleton);
        }

        /// <summary>
        /// Netcode refuses <see cref="NetworkManager.StartClient"/> / <see cref="NetworkManager.StartHost"/> if already
        /// listening (e.g. Editor <see cref="autoStartServer"/> runs <see cref="StartServer"/> on Play).
        /// </summary>
        private static async Task EnsureShutdownIfNetcodeRunningAsync()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                return;
            }
            if (!nm.IsListening && !nm.IsClient && !nm.IsServer)
            {
                return;
            }
            Debug.Log("[NetworkGameManager] Shutting down existing network session before starting a new one.");
            nm.Shutdown();
            await Task.Delay(150);
        }

        public void StartServer()
        {
            PrepareNetworkManagerForSessionStart();
            ApplyServerPort();
            NetworkManager.Singleton.StartServer();
            Debug.Log($"Server started on port {serverPort}");
        }

        public void StartClient()
        {
            PrepareNetworkManagerForSessionStart();
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
        /// Relay connection type for <see cref="AllocationUtils.ToRelayServerData"/>.
        /// Real WebGL <i>players</i> require WSS. The Unity Editor defines <c>UNITY_WEBGL</c> when the active build target
        /// is WebGL, so we must exclude <c>UNITY_EDITOR</c> — otherwise Play Mode in the Editor wrongly forces WSS and
        /// breaks joins to dedicated UDP Relay matches.
        /// </summary>
        static string RelayConnectionTypeForCurrentPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "wss";
#else
            return "dtls";
#endif
        }

        /// <summary>
        /// Prefer lobby-advertised relay protocol when available; fall back to platform default.
        /// Dedicated hosts store <c>udp</c> (their UTP mode); browser WebGL clients must use <c>wss</c> to the same Relay allocation.
        /// </summary>
        static string ResolveRelayConnectionType(string lobbyRelayProtocol)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "wss";
#else
            if (!string.IsNullOrWhiteSpace(lobbyRelayProtocol))
            {
                string lp = lobbyRelayProtocol.Trim().ToLowerInvariant();
                if (lp == "wss" || lp == "udp" || lp == "dtls")
                    return lp;
            }
            return RelayConnectionTypeForCurrentPlatform();
#endif
        }

        /// <summary>
        /// Canonical tokens for <see cref="UseWebSockets"/> and <see cref="MapToRelayProtocolEnum"/>. Legacy lobby <c>udp</c> is
        /// mapped to <c>dtls</c>: MPS 2.0 + <see cref="JoinAllocation"/> rejects plain <see cref="RelayProtocol.UDP"/> (runtime ArgumentException).
        /// </summary>
        static string NormalizeRelaySdkConnectionType(string t)
        {
            if (string.IsNullOrWhiteSpace(t))
                return "dtls";
            string x = t.Trim().ToLowerInvariant();
            if (x == "wss")
                return "wss";
            if (x == "udp" || x == "dtls")
                return "dtls";
            return "dtls";
        }

        static RelayProtocol MapToRelayProtocolEnum(string normalizedT)
        {
            if (string.IsNullOrWhiteSpace(normalizedT))
                return RelayProtocol.DTLS;
            switch (normalizedT.Trim().ToLowerInvariant())
            {
                case "wss":
                    return RelayProtocol.WSS;
                case "dtls":
                case "udp":
                    return RelayProtocol.DTLS;
                default:
                    return RelayProtocol.Default;
            }
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
            }
            if (transport.MaxPacketQueueSize < MinRelayPacketQueueSize)
            {
                transport.MaxPacketQueueSize = MinRelayPacketQueueSize;
            }
        }

        /// <summary>Transport is up; pure clients must also be connection-approved or team RPCs / loading gates misbehave.</summary>
        public static bool IsNetcodeTransportReadyForGameplay(NetworkManager nm)
        {
            if (nm == null) return false;
            // Pure client: rely on IsConnectedClient (connected, approved, synced). Requiring IsListening here
            // caused false negatives in the Editor (loading screen stuck at "Connecting multiplayer session..."
            // with netcode_wait_timeout while world load had already completed).
            if (nm.IsClient && !nm.IsServer)
                return nm.IsConnectedClient;
            return nm.IsListening;
        }

        /// <summary>
        /// Sets Relay server data and <see cref="UnityTransport.UseWebSockets"/> so they match (WSS requires WebSockets on the transport).
        /// </summary>
        static void ConfigureUnityTransportRelay(UnityTransport transport, Allocation allocation, string relayConnectionType)
        {
            string t = NormalizeRelaySdkConnectionType(ResolveRelayConnectionType(relayConnectionType));
            RelayProtocol proto = MapToRelayProtocolEnum(t);
            transport.UseWebSockets = string.Equals(t, "wss", StringComparison.OrdinalIgnoreCase);
            transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, proto));
            ApplyRelayFriendlyTransportSettings(transport);
        }

        static void ConfigureUnityTransportRelay(UnityTransport transport, JoinAllocation allocation, string relayConnectionType)
        {
            // WebGL players must use WSS; desktop/editor clients use DTLS to match dedicated DTLS hosts (WSS fallback).
            string t = NormalizeRelaySdkConnectionType(ResolveRelayConnectionType(relayConnectionType));
#if UNITY_EDITOR
            // UTP WebSockets are unavailable in Editor Play Mode regardless of active build target.
            if (string.Equals(t, "wss", StringComparison.OrdinalIgnoreCase))
                t = "dtls";
#endif
            if (string.Equals(t, "wss", StringComparison.OrdinalIgnoreCase))
            {
                transport.UseWebSockets = true;
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, RelayProtocol.WSS));
                ApplyRelayFriendlyTransportSettings(transport);
                return;
            }

            try
            {
                transport.UseWebSockets = false;
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, RelayProtocol.DTLS));
            }
            catch (ArgumentException ex)
            {
                Debug.LogWarning("[NetworkGameManager] JoinAllocation DTLS unavailable (" + (ex.Message ?? "") + "); using WSS.");
                transport.UseWebSockets = true;
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, RelayProtocol.WSS));
            }
            ApplyRelayFriendlyTransportSettings(transport);
        }

        /// <summary>
        /// Start as client using Unity Relay by joining the allocation for the given join code (WebGL: WSS; other targets: DTLS with WSS fallback).
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
                EnsurePlayerPrefabSet();
                if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
                {
                    Debug.LogError("Player Prefab not set on NetworkManager! Add a Starship prefab to Resources/Prefabs/Starship.prefab or use menu: Titan Orbit > Fix Player Prefab & Materials");
                    return false;
                }
                if (!await EnsureUnityServicesInitializedAsync())
                {
                    return false;
                }
                await EnsureShutdownIfNetcodeRunningAsync();
                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode.Trim());
                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
                }
                ConfigureUnityTransportRelay(transport, joinAllocation, null);
                PrepareNetworkManagerForSessionStart();
                bool started = NetworkManager.Singleton.StartClient();
                if (started)
                {
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
                    return false;
                }

                await EnsureShutdownIfNetcodeRunningAsync();

                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
                }

                await AcquireLobbyApiGateAsync();
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

                            joinedLobby = await WithLobbyApiTimeoutAsync(
                                LobbyService.Instance.QuickJoinLobbyAsync(quickOptions),
                                TimeSpan.FromSeconds(30),
                                "LobbyService.QuickJoinLobbyAsync");
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
                        if (!IsDedicatedLobbyJoinable(joinedLobby, out string quickJoinReject))
                        {
                            Debug.LogWarning(
                                "[NetworkGameManager] Quick join skipped stale/closed lobby: " + quickJoinReject);
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
                                string lobbyRelayProtocol = joinedLobby.Data.TryGetValue(LobbyRelayProtocolKey, out DataObject relayProtocolObj)
                                    ? (relayProtocolObj?.Value ?? "")
                                    : "";
                                string resolvedRelayConnectionType = ResolveRelayConnectionType(lobbyRelayProtocol);
                                JoinAllocation joinAllocation = await WithLobbyApiTimeoutAsync(
                                    RelayService.Instance.JoinAllocationAsync(joinCode),
                                    TimeSpan.FromSeconds(30),
                                    "RelayService.JoinAllocationAsync");
                                ConfigureUnityTransportRelay(transport, joinAllocation, resolvedRelayConnectionType);
                                PrepareNetworkManagerForSessionStart();
                                bool startedQj = NetworkManager.Singleton.StartClient();
                                if (startedQj)
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
                            QueryResponse response = await QueryLobbiesAsyncUnguarded(new QueryLobbiesOptions
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
                                if (!IsDedicatedLobbyJoinable(candidate, out _))
                                    continue;
                                try
                                {
                                    if (await PlayWebGLJoinByLobbyIdCoreAsync(candidate.Id, transport, lobbyApiGateAlreadyHeld: true))
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
                finally
                {
                    LobbyApiGate.Release();
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

                await EnsureShutdownIfNetcodeRunningAsync();

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
                ConfigureUnityTransportRelay(transport, allocation, null);

                long createdAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                const bool isLatest = true;
                await AcquireLobbyApiGateAsync();
                Lobby createdLobby;
                try
                {
                    createdLobby = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.CreateLobbyAsync(
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
                                    {
                                        LobbyRelayProtocolKey,
                                        new DataObject(DataObject.VisibilityOptions.Public, RelayConnectionTypeForCurrentPlatform())
                                    },
                                },
                            }),
                        TimeSpan.FromSeconds(45),
                        "LobbyService.CreateLobbyAsync");
                }
                finally
                {
                    LobbyApiGate.Release();
                }

                PrepareNetworkManagerForSessionStart();
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
            if (string.IsNullOrWhiteSpace(lobbyId))
            {
                Debug.LogError("LobbyId is empty.");
                return false;
            }

            try
            {
                EnsurePlayerPrefabSet();
                if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
                {
                    Debug.LogError("Player Prefab not set on NetworkManager! Add a Starship prefab to Resources/Prefabs/Starship.prefab or use menu: Titan Orbit > Fix Player Prefab & Materials");
                    return false;
                }
                if (!await EnsureUnityServicesInitializedAsync())
                {
                    return false;
                }
                await EnsureShutdownIfNetcodeRunningAsync();

                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("UnityTransport not found on NetworkManager.");
                    return false;
                }

                await AcquireLobbyApiGateAsync();
                try
                {
                    return await PlayWebGLJoinByLobbyIdCoreAsync(lobbyId.Trim(), transport, lobbyApiGateAlreadyHeld: true);
                }
                finally
                {
                    LobbyApiGate.Release();
                }
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

        /// <param name="lobbyApiGateAlreadyHeld">When true, caller already holds <see cref="LobbyApiGate"/> (quick-join fallback).</param>
        async Task<bool> PlayWebGLJoinByLobbyIdCoreAsync(string lobbyId, UnityTransport transport, bool lobbyApiGateAlreadyHeld)
        {
            await LeaveCurrentLobbyIfMemberAsync("before_join_by_id", lobbyApiGateAlreadyHeld);

            string id = lobbyId.Trim();
            Lobby joinedLobby = null;
            try
            {
                joinedLobby = await WithLobbyApiTimeoutAsync(
                    LobbyService.Instance.JoinLobbyByIdAsync(id, BuildJoinLobbyByIdOptions()),
                    TimeSpan.FromSeconds(30),
                    "LobbyService.JoinLobbyByIdAsync");
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
                    joinedLobby = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.GetLobbyAsync(id),
                        TimeSpan.FromSeconds(20),
                        "LobbyService.GetLobbyAsync");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinByLobbyIdAsync: could not GetLobby after join failure: " + ex.Message);
                    return false;
                }
            }
            catch (TimeoutException)
            {
                return false;
            }

            // RelayJoinCode is Member visibility; some WebGL SDK responses omit it until GetLobby.
            if (joinedLobby == null || joinedLobby.Data == null || !joinedLobby.Data.ContainsKey(LobbyRelayCodeKey))
            {
                try
                {
                    joinedLobby = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.GetLobbyAsync(id),
                        TimeSpan.FromSeconds(20),
                        "LobbyService.GetLobbyAsync(relay_code)");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[NetworkGameManager] PlayWebGLJoinByLobbyIdAsync: GetLobby for RelayJoinCode failed: " + ex.Message);
                    return false;
                }
            }

            if (joinedLobby == null || joinedLobby.Data == null || !joinedLobby.Data.ContainsKey(LobbyRelayCodeKey))
            {
                Debug.LogWarning("Joined lobby, but RelayJoinCode was missing.");
                return false;
            }

            if (!IsDedicatedLobbyJoinable(joinedLobby, out string rejectReason))
            {
                Debug.LogWarning("[NetworkGameManager] Refusing join to stale/closed lobby " + id + ": " + rejectReason);
                return false;
            }

            string joinCode = joinedLobby.Data[LobbyRelayCodeKey].Value;
            if (string.IsNullOrEmpty(joinCode))
            {
                Debug.LogWarning("Joined lobby, but RelayJoinCode was empty.");
                return false;
            }

            string lobbyRelayProtocol = joinedLobby.Data != null &&
                joinedLobby.Data.TryGetValue(LobbyRelayProtocolKey, out DataObject relayProtocolObjPre)
                ? (relayProtocolObjPre?.Value ?? "")
                : "";
            string resolvedRelayConnectionType = ResolveRelayConnectionType(lobbyRelayProtocol);

            JoinAllocation joinAllocation;
            try
            {
                joinAllocation = await WithLobbyApiTimeoutAsync(
                    RelayService.Instance.JoinAllocationAsync(joinCode),
                    TimeSpan.FromSeconds(30),
                    "RelayService.JoinAllocationAsync");
            }
            catch (TimeoutException)
            {
                return false;
            }

            ConfigureUnityTransportRelay(transport, joinAllocation, resolvedRelayConnectionType);

            PrepareNetworkManagerForSessionStart();
            bool startedLobby = NetworkManager.Singleton.StartClient();
            if (startedLobby)
            {
                currentLobby = joinedLobby;
                StartDebugJoinMonitor("join_lobby_id", id);
                Debug.Log("Joined lobby by id via Relay (WebGL client).");
                return true;
            }

            return false;
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

        /// <summary>
        /// Some hosts lag updating indexed lobby data (N1/N2). After the strict query returns nothing, re-query by game name only and filter in memory.
        /// </summary>
        /// <summary>
        /// Keeps only open, fresh, latest dedicated lobbies that the headless server is actively hosting.
        /// </summary>
        public static List<LobbySummary> FilterToJoinableDedicatedLobbies(List<LobbySummary> lobbies)
        {
            if (lobbies == null || lobbies.Count == 0)
                return lobbies ?? new List<LobbySummary>();

            var joinable = new List<LobbySummary>();
            for (int i = 0; i < lobbies.Count; i++)
            {
                LobbySummary l = lobbies[i];
                if (l == null || !l.IsOpen || !l.IsLatest || IsDedicatedLobbySummaryStale(l))
                    continue;
                joinable.Add(l);
            }

            return joinable;
        }

        private static bool LobbyPassesJoinBrowserFilters(Lobby lobby, bool latestOnly)
        {
            if (lobby?.Data == null) return true;
            if (lobby.Data.TryGetValue(LobbyGameNameKey, out DataObject gn) && gn != null &&
                !string.Equals(gn.Value, LobbyGameNameValue, StringComparison.Ordinal))
                return false;
            if (lobby.Data.TryGetValue(LobbyIsOpenKey, out DataObject io) && io != null &&
                !string.Equals(io.Value, "1", StringComparison.Ordinal))
                return false;
            if (latestOnly &&
                lobby.Data.TryGetValue(LobbyIsLatestKey, out DataObject il) && il != null &&
                !string.Equals(il.Value, "1", StringComparison.Ordinal))
                return false;
            return true;
        }

        private static bool IsDedicatedLobbyJoinable(Lobby lobby, out string rejectReason)
        {
            rejectReason = null;
            if (lobby?.Data == null)
                return true;

            bool isDedicated = lobby.Data.ContainsKey(LobbyServerListenAddressKey);
            if (!isDedicated)
                return true;

            if (lobby.Data.TryGetValue(LobbyIsOpenKey, out DataObject io) && io != null &&
                !string.Equals(io.Value, "1", StringComparison.Ordinal))
            {
                rejectReason = "lobby is closed";
                return false;
            }

            if (IsDedicatedLobbyStale(lobby))
            {
                rejectReason = "server heartbeat is stale (match may have ended)";
                return false;
            }

            if (lobby.Data.TryGetValue(LobbyIsLatestKey, out DataObject latestObj) && latestObj != null &&
                !string.Equals(latestObj.Value, "1", StringComparison.Ordinal))
            {
                rejectReason = "lobby is no longer the active match";
                return false;
            }

            return true;
        }

        private static bool IsDedicatedLobbyStale(Lobby lobby)
        {
            if (lobby?.Data == null)
                return false;
            if (!lobby.Data.ContainsKey(LobbyServerListenAddressKey))
                return false;
            if (!lobby.Data.TryGetValue(LobbyServerAliveEpochKey, out DataObject aliveObj) || aliveObj == null)
                return false;
            if (!long.TryParse(aliveObj.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long aliveEpoch) ||
                aliveEpoch <= 0)
                return false;

            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return nowEpoch - aliveEpoch > DedicatedLobbyStaleSeconds;
        }

        private static bool IsDedicatedLobbySummaryStale(LobbySummary summary)
        {
            if (summary == null || summary.ServerAliveAtEpochSeconds <= 0)
                return false;
            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return nowEpoch - summary.ServerAliveAtEpochSeconds > DedicatedLobbyStaleSeconds;
        }

        private enum RelaxedLobbyMergeResult
        {
            Completed,
            RateLimited,
            Failed
        }

        private static bool IsLikelyLobbyRateLimitException(Exception e)
        {
            if (e == null) return false;
            if (e is LobbyServiceException lse && lse.Reason == LobbyExceptionReason.RateLimited)
                return true;
            string m = e.Message ?? string.Empty;
            if (string.Equals(m, "Too Many Requests", StringComparison.OrdinalIgnoreCase))
                return true;
            if (m.IndexOf("429", StringComparison.Ordinal) >= 0)
                return true;
            if (m.IndexOf("throttl", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (m.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private Task<RelaxedLobbyMergeResult> MergeRelaxedGameNameQueryAsync(bool latestOnly, int count, List<LobbySummary> results)
        {
            // com.unity.services.multiplayer 2.0.0: a second QueryLobbiesAsync in the same refresh (merge-after-strict)
            // consistently throws NullReferenceException after the first call succeeds. Strict query + stabilization
            // retries must carry index lag; skip this extra SDK call.
            return Task.FromResult(RelaxedLobbyMergeResult.Completed);
        }

        /// <summary>Queries UGS for joinable dedicated lobbies (optionally only &quot;latest&quot;).</summary>
        /// <param name="emptyStabilizationAttempt">Leave default; used internally to retry empty-but-successful results while UGS indexes catch up.</param>
        /// <param name="maxEmptyStabilizationAttemptsOverride">When &gt;= 0, caps stabilization retries (0 = no retries).</param>
        public async Task<List<LobbySummary>> QueryOpenLobbiesAsync(
            bool latestOnly,
            int count = 20,
            int emptyStabilizationAttempt = 0,
            int maxEmptyStabilizationAttemptsOverride = -1)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            while (!OpenLobbyRefreshGate.Wait(0))
                await Task.Yield();
#else
            await OpenLobbyRefreshGate.WaitAsync();
#endif
            try
            {
                return await QueryOpenLobbiesInternalAsync(
                    latestOnly,
                    count,
                    emptyStabilizationAttempt,
                    maxEmptyStabilizationAttemptsOverride);
            }
            finally
            {
                OpenLobbyRefreshGate.Release();
            }
        }

        /// <summary>Queries all open dedicated lobbies and applies in-memory joinability filters.</summary>
        public async Task<List<LobbySummary>> QueryJoinableDedicatedLobbiesAsync(
            int count = 40,
            bool skipEmptyStabilization = false)
        {
            int stabilizationCap = skipEmptyStabilization ? 0 : -1;
            List<LobbySummary> raw = await QueryOpenLobbiesAsync(
                latestOnly: false,
                count: count,
                emptyStabilizationAttempt: 0,
                maxEmptyStabilizationAttemptsOverride: stabilizationCap);
            return FilterToJoinableDedicatedLobbies(raw);
        }

        /// <summary>
        /// Signals the headless server to publish a fresh dedicated match when none is listed.
        /// </summary>
        public async Task<bool> RequestDedicatedMatchCreationAsync()
        {
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                    return false;

                await AcquireLobbyApiGateAsync();
                try
                {
                    long requestedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string requestName = "DedicatedMatchRequest-" + requestedAtEpoch.ToString(CultureInfo.InvariantCulture);
                    await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.CreateLobbyAsync(
                            requestName,
                            2,
                            new CreateLobbyOptions
                            {
                                IsPrivate = true,
                                Data = new Dictionary<string, DataObject>
                                {
                                    {
                                        LobbyGameNameKey,
                                        new DataObject(
                                            DataObject.VisibilityOptions.Public,
                                            DedicatedMatchServerBootstrap.LobbyMatchRequestGameName,
                                            DataObject.IndexOptions.S1)
                                    },
                                    {
                                        DedicatedMatchServerBootstrap.LobbyMatchRequestEpochKey,
                                        new DataObject(
                                            DataObject.VisibilityOptions.Public,
                                            requestedAtEpoch.ToString(CultureInfo.InvariantCulture),
                                            DataObject.IndexOptions.N1)
                                    }
                                }
                            }),
                        TimeSpan.FromSeconds(30),
                        "LobbyService.CreateLobbyAsync(match_request)");
                    Debug.Log("[NetworkGameManager] Dedicated match creation request published.");
                    return true;
                }
                finally
                {
                    LobbyApiGate.Release();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] RequestDedicatedMatchCreationAsync failed: " + e.Message);
                return false;
            }
        }

        private async Task<List<LobbySummary>> QueryOpenLobbiesInternalAsync(
            bool latestOnly,
            int count = 20,
            int emptyStabilizationAttempt = 0,
            int maxEmptyStabilizationAttemptsOverride = -1)
        {
            var results = new List<LobbySummary>();
            LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.Ok;
            LastOpenLobbyQueryErrorDetail = null;
            LobbyRateLimitRemainingSeconds = 0f;
            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                {
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.UnityServicesNotReady;
                    return results;
                }

                if (LobbyService.Instance == null)
                {
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.UnityServicesNotReady;
                    LastOpenLobbyQueryErrorDetail =
                        "Lobby service is not ready yet. Wait a moment and tap Refresh (Unity Gaming Services may still be finishing setup).";
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

                QueryResponse response = await QueryLobbiesSerializedAsync(options);
                // A query reached UGS successfully — clear post-429 client throttle so the list is not blank until an arbitrary window ends.
                _dbgNextLobbyQueryAllowedUtc = DateTime.MinValue;
                LobbyRateLimitRemainingSeconds = 0f;

                if (response?.Results == null)
                {
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.Error;
                    LastOpenLobbyQueryErrorDetail = "Lobby query returned no result set.";
                    return results;
                }

                foreach (var lobby in response.Results)
                {
                    if (lobby == null)
                        continue;
                    results.Add(ToLobbySummary(lobby));
                }

                if (results.Count == 0)
                {
                    RelaxedLobbyMergeResult mergeResult = await MergeRelaxedGameNameQueryAsync(latestOnly, count, results);
                    if (mergeResult == RelaxedLobbyMergeResult.RateLimited)
                    {
                        LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.RateLimitBackoff;
                        LobbyRateLimitRemainingSeconds = 12f;
                    }
                }
            }
            catch (Exception e)
            {
                if (IsLikelyLobbyRateLimitException(e))
                {
                    _dbgNextLobbyQueryAllowedUtc = DateTime.UtcNow.AddSeconds(12);
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.RateLimitBackoff;
                    LobbyRateLimitRemainingSeconds = 12f;
                }
                else
                {
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.Error;
                    LastOpenLobbyQueryErrorDetail = e.Message ?? e.GetType().Name;
                }
                Debug.LogWarning("[NetworkGameManager] QueryOpenLobbiesAsync failed: " + e.Message);
            }

            // After a fresh headless deploy or systemd restart, the first client query often returns zero rows
            // while Relay/Lobby creation finishes or while indexed filters (N1/N2) catch up — not "no server".
            const int maxEmptyStabilizationAttemptsBroad = 14;
            const int maxEmptyStabilizationAttemptsLatestOnly = 5;
            int maxEmptyStabilizationAttempts = maxEmptyStabilizationAttemptsOverride >= 0
                ? maxEmptyStabilizationAttemptsOverride
                : (latestOnly ? maxEmptyStabilizationAttemptsLatestOnly : maxEmptyStabilizationAttemptsBroad);
            if (results.Count == 0 && LastOpenLobbyQueryKind == OpenLobbyQueryResultKind.Ok &&
                maxEmptyStabilizationAttempts > 0 &&
                emptyStabilizationAttempt < maxEmptyStabilizationAttempts - 1)
            {
                int backoffMs = 1200 + Mathf.Min(emptyStabilizationAttempt * 150, 900);
                await Task.Delay(backoffMs);
                return await QueryOpenLobbiesInternalAsync(
                    latestOnly,
                    count,
                    emptyStabilizationAttempt + 1,
                    maxEmptyStabilizationAttemptsOverride);
            }

            if (results.Count == 0 && LastOpenLobbyQueryKind == OpenLobbyQueryResultKind.Ok &&
                maxEmptyStabilizationAttempts > 0 &&
                emptyStabilizationAttempt >= maxEmptyStabilizationAttempts - 1)
            {
                Debug.LogWarning(
                    "[NetworkGameManager] Lobby list still empty after stabilization retries. " +
                    "If a dedicated server should be running, on the VM read TitanOrbitDedicatedServer.log next to the server build " +
                    "and Player.log. Confirm the Unity cloud project id matches this editor/player: " +
                    (string.IsNullOrEmpty(Application.cloudProjectId) ? "(none)" : Application.cloudProjectId) + ".");
            }

            return results;
        }

        public async Task<bool> JoinLobbyByIdAsync(string lobbyId)
        {
            return await PlayWebGLJoinByLobbyIdAsync(lobbyId);
        }

        /// <summary>Disconnect Netcode and leave UGS lobby after detecting a dead/stale dedicated match.</summary>
        public void AbortStaleClientSession(string reason)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && (nm.IsClient || nm.IsListening))
            {
                Debug.Log("[NetworkGameManager] Aborting stale client session: " + reason);
                nm.Shutdown();
            }

            _ = LeaveCurrentLobbyIfMemberAsync(reason ?? "stale_session");
        }

        private LobbySummary ToLobbySummary(Lobby lobby)
        {
            int maxPlayerCapacity = Mathf.Max(1, lobby.MaxPlayers);
            int playersFromMemberList = lobby.Players != null ? lobby.Players.Count : 0;
            int playersFromAvailableSlots = Mathf.Clamp(maxPlayerCapacity - lobby.AvailableSlots, 0, maxPlayerCapacity);
            bool isDedicatedServerLobby = lobby.Data != null && lobby.Data.ContainsKey(LobbyServerListenAddressKey);
            int normalizedPlayerCount = playersFromAvailableSlots > 0 ? playersFromAvailableSlots : playersFromMemberList;
            if (isDedicatedServerLobby)
                normalizedPlayerCount = Mathf.Max(0, normalizedPlayerCount - 1);

            int activePlayersFromServer = -1;
            if (lobby.Data != null &&
                lobby.Data.TryGetValue(LobbyActivePlayersKey, out DataObject activePlayersObj) &&
                activePlayersObj != null &&
                int.TryParse(
                    activePlayersObj.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsedActivePlayers))
            {
                activePlayersFromServer = Mathf.Max(0, parsedActivePlayers);
            }

            if (isDedicatedServerLobby && activePlayersFromServer >= 0)
                normalizedPlayerCount = activePlayersFromServer;

            var summary = new LobbySummary
            {
                LobbyId = lobby.Id,
                Name = string.IsNullOrWhiteSpace(lobby.Name) ? "Unnamed Room" : lobby.Name,
                // Dedicated lobbies include the server owner member in UGS membership;
                // subtract it so browser rows show actual connected game players.
                CurrentPlayers = normalizedPlayerCount,
                MaxPlayers = maxPlayerCapacity,
                IsOpen = true,
                IsLatest = false,
                CreatedAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ActivePlayers = activePlayersFromServer
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

            if (lobby.Data.TryGetValue(LobbyServerAliveEpochKey, out DataObject aliveAtObj) &&
                long.TryParse(aliveAtObj?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long aliveAt))
            {
                summary.ServerAliveAtEpochSeconds = aliveAt;
            }

            return summary;
        }

        /// <summary>
        /// Removes this authenticated player from the active lobby membership so room counts stay in sync.
        /// </summary>
        private async Task LeaveCurrentLobbyIfMemberAsync(string reason, bool lobbyApiGateAlreadyHeld = false)
        {
            if (_leaveLobbyInProgress)
                return;
            if (currentLobby == null || string.IsNullOrWhiteSpace(currentLobby.Id))
                return;
            if (UnityServices.State != ServicesInitializationState.Initialized)
                return;
            if (!AuthenticationService.Instance.IsSignedIn || !AuthenticationService.Instance.IsAuthorized)
                return;

            string lobbyId = currentLobby.Id;
            string playerId = AuthenticationService.Instance.PlayerId;
            if (string.IsNullOrWhiteSpace(playerId))
                return;

            _leaveLobbyInProgress = true;
            bool acquiredGate = false;
            try
            {
                if (!lobbyApiGateAlreadyHeld)
                {
                    await AcquireLobbyApiGateAsync();
                    acquiredGate = true;
                }

                await LobbyService.Instance.RemovePlayerAsync(lobbyId, playerId);
            }
            catch (LobbyServiceException e)
            {
                // Already gone, lobby missing, or host-owned restriction are safe to ignore.
                if (e.Reason != LobbyExceptionReason.PlayerNotFound &&
                    e.Reason != LobbyExceptionReason.LobbyNotFound &&
                    e.Reason != LobbyExceptionReason.Forbidden)
                {
                    Debug.LogWarning("[NetworkGameManager] LeaveCurrentLobbyIfMemberAsync failed (" + reason + "): " + e.Message);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NetworkGameManager] LeaveCurrentLobbyIfMemberAsync error (" + reason + "): " + e.Message);
            }
            finally
            {
                if (acquiredGate)
                    LobbyApiGate.Release();
                currentLobby = null;
                _leaveLobbyInProgress = false;
            }
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

                if (LobbyService.Instance == null)
                    return new System.Collections.Generic.List<Lobby>();

                var filters = BuildDedicatedLobbyQueryFilters(latestOnly);

                QueryResponse response = await QueryLobbiesSerializedAsync(new QueryLobbiesOptions
                {
                    Count = count,
                    Filters = filters,
                    Order = new System.Collections.Generic.List<QueryOrder>
                    {
                        new QueryOrder(asc: false, field: QueryOrder.FieldOptions.Created)
                    }
                });
                var list = response?.Results ?? new System.Collections.Generic.List<Lobby>();
                if (list.Count > 0)
                    return list;

                // Second QueryLobbiesAsync (relaxed merge) omitted: same com.unity.services.multiplayer 2.0.0 NRE-after-first-query
                // behavior as desktop; rely on strict filters + caller retries.
                return list;
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

            if (currentLobby != null)
                _ = LeaveCurrentLobbyIfMemberAsync("network_despawn");
        }

        private void OnClientConnected(ulong clientId)
        {
            Debug.Log($"Client {clientId} connected");
            // Do not assign team or move ship here. Player sees team selection first; assignment happens when they click Join (Starship.RequestJoinTeamServerRpc → TeamManager.ApplyTeamChoiceFromServer).
        }

        private void OnClientDisconnected(ulong clientId)
        {
            Debug.Log($"Client {clientId} disconnected");
            SaveMapInstanceShipProgressForDisconnectingClient(clientId);
            PlayerDisplayNames.RemoveClient(clientId);
            if (TeamManager.Instance != null)
                TeamManager.Instance.RemovePlayer(clientId);
            TitanOrbit.Systems.MapInstanceShipProgressStore.UnregisterClient(clientId);
        }

        /// <summary>Server: persist human ship loadout for this map instance so the same auth player can restore after reconnect.</summary>
        private static void SaveMapInstanceShipProgressForDisconnectingClient(ulong clientId)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            Starship ship = TeamManager.GetPlayerStarshipForClient(clientId);
            if (ship == null || ship.GetComponent<TitanOrbit.AI.AIShipMarker>() != null)
                return;

            string authPlayerId = TitanOrbit.Systems.MapInstanceShipProgressStore.ResolveAuthPlayerId(clientId);
            TitanOrbit.Systems.MapInstanceShipProgressStore.SaveSnapshot(authPlayerId, ship.CaptureMapInstanceProgress());
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

        /// <summary>Fired on client when their team was eliminated and their ship was scuttled (rejoin team selection).</summary>
        public static System.Action OnPlayerTeamScuttled;

        public enum ShipRestoreChoice
        {
            Unset = 0,
            Rescue = 1,
            StartAnew = 2
        }

        /// <summary>Client-side choice from the Rescue Old Ship screen; consumed when the ship spawns.</summary>
        public static ShipRestoreChoice PendingRestoreChoice { get; set; } = ShipRestoreChoice.Unset;

        /// <summary>Summary of a returning player's saved ship (client-side, from server query).</summary>
        public struct ReturningShipInfo
        {
            public bool HasRescuableShip;
            public int ShipLevel;
            public TeamManager.Team Team;
            public string ChassisDisplayName;
            public float CurrentGems;
        }

        /// <summary>Fired on client after <see cref="QueryReturningShipFromLocalPlayer"/> completes.</summary>
        public static System.Action<ReturningShipInfo> OnReturningShipQueryResult;

        /// <summary>
        /// <see cref="NetworkManager.Singleton"/> can reference an inactive duplicate (e.g. Multiplayer Play Mode / extra scene object) that never started Netcode,
        /// while another <see cref="NetworkManager"/> in the hierarchy is the real host/client. Prefer the instance that is actually running.
        /// </summary>
        public static NetworkManager ResolveNetworkManagerForGameplay()
        {
            // Prefer the project's Singleton when it is (or is becoming) a pure client — avoids picking another NM that is only "listening"
            // before IsConnectedClient flips true, which kept the loading screen on "Connecting multiplayer session...".
            var s = NetworkManager.Singleton;
            if (s != null && s.IsClient && !s.IsServer)
                return s;

            var all = UnityEngine.Object.FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            NetworkManager anyInRole = null;
            for (int i = 0; i < all.Length; i++)
            {
                var nm = all[i];
                if (nm == null || !(nm.IsClient || nm.IsServer)) continue;
                if (anyInRole == null) anyInRole = nm;
                if (nm.IsClient && !nm.IsServer && nm.IsConnectedClient)
                    return nm;
                if (nm.IsServer && nm.IsListening)
                    return nm;
            }

            if (s != null && (s.IsClient || s.IsServer))
                return s;
            return anyInRole;
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

        private IEnumerator CoRequestTeamWhenTeamManagerReady(TeamManager.Team team)
        {
            const float timeoutSeconds = 25f;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
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
                if (TeamManager.Instance != null && TeamManager.Instance.IsSpawned)
                {
                    TeamManager.Instance.RequestTeamServerRpc(team);
                    pendingTeamRequestCoroutine = null;
                    yield break;
                }
                yield return null;
            }

            pendingTeamRequestCoroutine = null;
            OnTeamChoiceFailed?.Invoke("Cannot join a team — connection still loading. Try again.");
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
                OnTeamChoiceFailed?.Invoke("Multiplayer is not running. Return to the main menu, create or join a match from the list, or join with a relay code, then enter the match again.");
                return;
            }
            var ship = TryGetLocalStarship();
            if (ship != null)
            {
                ship.RequestJoinTeamFromClient(team);
                return;
            }

            if (TeamManager.Instance != null && TeamManager.Instance.IsSpawned)
            {
                TeamManager.Instance.RequestTeamServerRpc(team);
                return;
            }

            var runner = Instance ?? UnityEngine.Object.FindAnyObjectByType<NetworkGameManager>(FindObjectsInactive.Include);
            if (runner != null)
            {
                if (runner.pendingTeamRequestCoroutine != null)
                    runner.StopCoroutine(runner.pendingTeamRequestCoroutine);
                runner.pendingTeamRequestCoroutine = runner.StartCoroutine(runner.CoRequestTeamWhenTeamManagerReady(team));
                return;
            }

            Debug.LogError("[NetworkGameManager] Cannot request team: TeamManager not ready.");
            OnTeamChoiceFailed?.Invoke("Cannot join a team yet — connection still loading. Try again.");
        }

        /// <summary>Same-machine / LAN test without Relay: listen server on <see cref="serverPort"/> (default 7777). Use <see cref="StartLocalClientForLanTest"/> from a second instance.</summary>
        public bool StartLocalHostForLanTest()
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
                return false;
            PrepareNetworkManagerForSessionStart();
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
            PrepareNetworkManagerForSessionStart();
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

        /// <summary>Client: ask the server whether this player has a rescuable ship for the current map instance.</summary>
        public static void QueryReturningShipFromLocalPlayer()
        {
            var ngm = Instance ?? UnityEngine.Object.FindAnyObjectByType<NetworkGameManager>(FindObjectsInactive.Include);
            if (ngm == null || !ngm.IsSpawned)
            {
                OnReturningShipQueryResult?.Invoke(default);
                return;
            }

            string authPlayerId = UnityGameServicesBootstrap.PlayerId ?? string.Empty;
            ngm.QueryReturningShipServerRpc(authPlayerId);
        }

        /// <summary>Client: abandon saved ship progress and start fresh on the next spawn.</summary>
        public static void AbandonOldShipFromLocalPlayer()
        {
            PendingRestoreChoice = ShipRestoreChoice.StartAnew;
            var ngm = Instance ?? UnityEngine.Object.FindAnyObjectByType<NetworkGameManager>(FindObjectsInactive.Include);
            if (ngm == null || !ngm.IsSpawned)
                return;
            string authPlayerId = UnityGameServicesBootstrap.PlayerId ?? string.Empty;
            ngm.AbandonOldShipServerRpc(authPlayerId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void QueryReturningShipServerRpc(string authPlayerId, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            string key = MapInstanceShipProgressStore.NormalizeAuthPlayerId(authPlayerId, clientId);
            MapInstanceShipProgressStore.RegisterClientAuthId(clientId, key);

            if (!MapInstanceShipProgressStore.TryGetSnapshot(key, out PlayerShipProgressSnapshot snapshot)
                || snapshot.Team == TeamManager.Team.None)
            {
                NotifyReturningShipClientRpc(clientId, false, 0, (int)TeamManager.Team.None, string.Empty, 0f);
                return;
            }

            if (TeamManager.Instance != null && TeamManager.Instance.IsTeamEliminated(snapshot.Team))
            {
                MapInstanceShipProgressStore.RemoveSnapshot(key);
                NotifyReturningShipClientRpc(clientId, false, 0, (int)TeamManager.Team.None, string.Empty, 0f);
                return;
            }

            string displayName = snapshot.ChassisId;
            if (CardShopSystem.Instance != null)
            {
                var chassis = CardShopSystem.Instance.GetChassisDefinitionByChassisId(snapshot.ChassisId);
                if (chassis != null && !string.IsNullOrEmpty(chassis.displayName))
                    displayName = chassis.displayName.Trim();
            }

            NotifyReturningShipClientRpc(
                clientId,
                true,
                snapshot.ShipLevel,
                (int)snapshot.Team,
                displayName ?? string.Empty,
                snapshot.CurrentGems);
        }

        [ServerRpc(RequireOwnership = false)]
        private void AbandonOldShipServerRpc(string authPlayerId, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            string key = MapInstanceShipProgressStore.NormalizeAuthPlayerId(authPlayerId, clientId);
            MapInstanceShipProgressStore.RegisterClientAuthId(clientId, key);
            MapInstanceShipProgressStore.RemoveSnapshot(key);
        }

        [ClientRpc]
        private void NotifyReturningShipClientRpc(
            ulong clientId,
            bool hasRescuableShip,
            int shipLevel,
            int teamInt,
            string chassisDisplayName,
            float currentGems)
        {
            var nm = ResolveNetworkManagerForGameplay();
            if (nm == null || nm.LocalClientId != clientId)
                return;

            var info = new ReturningShipInfo
            {
                HasRescuableShip = hasRescuableShip,
                ShipLevel = shipLevel,
                Team = (TeamManager.Team)teamInt,
                ChassisDisplayName = chassisDisplayName ?? string.Empty,
                CurrentGems = currentGems
            };
            OnReturningShipQueryResult?.Invoke(info);
        }

        /// <summary>Server: tell a connected player their team was eliminated and they must pick a new team.</summary>
        public void NotifyPlayerTeamScuttled(ulong clientId)
        {
            if (!IsServer) return;
            NotifyPlayerTeamScuttledClientRpc(clientId);
        }

        [ClientRpc]
        private void NotifyPlayerTeamScuttledClientRpc(ulong clientId)
        {
            var nm = ResolveNetworkManagerForGameplay();
            if (nm == null || nm.LocalClientId != clientId)
                return;
            PendingRestoreChoice = ShipRestoreChoice.Unset;
            OnPlayerTeamScuttled?.Invoke();
        }

        public bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        public bool IsClient => NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;
        public bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

        private void StartDebugJoinMonitor(string source, string lobbyIdOrTag)
        {
            // Independent blueprint-identity probe: tells us which server process the client landed on.
            StartCoroutine(CoLogBlueprintIdentityWhenReady(source, lobbyIdOrTag));
        }

        /// <summary>
        /// Client-side: waits up to ~30s for the server-published <see cref="TitanOrbit.Generation.MapGenerator"/> blueprint
        /// to replicate, then logs <c>lobbyId</c>, <c>serverBootEpochUtc</c>, <c>blueprintSeed</c>, and <c>blueprintCount</c>
        /// once. This makes "rejoin shows different map" trivially diagnosable from the client log: identical values across
        /// rejoins prove the client landed on the same server process; differing values prove it landed on a new one.
        /// </summary>
        private IEnumerator CoLogBlueprintIdentityWhenReady(string source, string lobbyIdOrTag)
        {
            const float timeoutSeconds = 45f;
            float t0 = Time.realtimeSinceStartup;
            TitanOrbit.Generation.MapGenerator mapGen = null;
            while (Time.realtimeSinceStartup - t0 < timeoutSeconds)
            {
                if (mapGen == null)
                    mapGen = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Generation.MapGenerator>();
                if (mapGen != null && mapGen.IsSpawned && (mapGen.LoadingComplete || mapGen.BlueprintEntryCount > 0))
                    break;
                yield return null;
            }

            string lobbyId = currentLobby != null ? (currentLobby.Id ?? string.Empty) : string.Empty;
            float elapsed = Time.realtimeSinceStartup - t0;
            if (mapGen == null || !mapGen.IsSpawned)
            {
                Debug.LogWarning(
                    "[NetworkGameManager] Blueprint identity probe: MapGenerator did not spawn within "
                    + timeoutSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s after join. lobbyId="
                    + lobbyId + " source=" + source + " tag=" + lobbyIdOrTag);
                yield break;
            }

            long bootEpoch = mapGen.ServerBootEpochUtc;
            int seedValue = mapGen.BlueprintSeed;
            int entryCount = mapGen.BlueprintEntryCount;
            bool loadingComplete = mapGen.LoadingComplete;
            Debug.Log(
                "[NetworkGameManager] Blueprint identity: lobbyId=" + lobbyId
                + " serverBootEpochUtc=" + bootEpoch
                + " blueprintSeed=" + seedValue
                + " blueprintCount=" + entryCount
                + " loadingComplete=" + (loadingComplete ? "true" : "false")
                + " source=" + source
                + " tag=" + lobbyIdOrTag
                + " waitedSeconds=" + elapsed.ToString("F2", CultureInfo.InvariantCulture));
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
