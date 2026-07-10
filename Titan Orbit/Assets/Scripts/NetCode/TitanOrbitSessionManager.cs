using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Diagnostics;
using TitanOrbit.ECS;
using TitanOrbit.Services;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>Session orchestration replacing NGO NetworkGameManager + DedicatedMatchServerBootstrap.</summary>
    public class TitanOrbitSessionManager : MonoBehaviour
    {
        public static TitanOrbitSessionManager Instance { get; private set; }

        public static bool PendingLanHost { get; set; }

        const ushort DefaultServerPort = 7777;

        [SerializeField] int maxPlayers = 60;
        [SerializeField] ushort serverPort = DefaultServerPort;

        string _activeLobbyId;
        Coroutine _connectWatch;
        Coroutine _mppmLanConnectCoroutine;
        TitanOrbitServerCommandLine _serverConfig;
        bool _recreateDedicatedMatchInProgress;

        public bool IsInGame { get; private set; }
        public string LastStatusMessage { get; private set; }
        public string CurrentLobbyId => _activeLobbyId;

        /// <summary>True while the editor/client is connected (or connecting) to a remote dedicated host via Relay.</summary>
        public static bool IsDedicatedOnlineClient { get; private set; }

        /// <summary>Dedicated Relay join started but NetCode has not reached in-game yet.</summary>
        public static bool IsDedicatedJoinConnecting =>
            IsDedicatedOnlineClient && Instance != null && !Instance.IsInGame;

        static bool s_EditorLocalServerSuspendedForOnline;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            if (ShouldRunHeadlessServerBoot())
            {
                EnsureDedicatedBootStarted();
#if UNITY_SERVER
                if (!ShouldAutoBootDedicatedRelay())
                    StartCoroutine(BootMppmLanServer());
#endif
                Debug.Log("[TitanOrbitSessionManager] Headless server boot (no client UI flow).");
                return;
            }

            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
            {
                TitanOrbitPlayModeUtility.WarnIfMppmServerBuildClone();
                bool localLan = TitanOrbitMultiplayerConfig.ShowLocalPlayOptions;
                Debug.Log("[TitanOrbitSessionManager] MPPM Player " + TitanOrbitPlayModeUtility.GetMppmPlayerNumber() +
                          " (buildSubTarget=" + TitanOrbitPlayModeUtility.GetMppmBuildSubtarget() +
                          ") — " + (localLan
                              ? "use Local client on the menu to connect to the host on port " + serverPort + "."
                              : "ready for dedicated join via Join game (no LAN auto-connect)."));

                IsDedicatedOnlineClient = false;
                IsInGame = false;
                return;
            }

            Debug.Log("[TitanOrbitSessionManager] Client play instance ready — use Join game for dedicated servers or Play for local.");
            IsDedicatedOnlineClient = false;
            IsInGame = false;
#if UNITY_EDITOR
            if (!TitanOrbit.Data.TitanOrbitMultiplayerConfig.ShowLocalPlayOptions)
                SuspendEditorLocalServerUntilLocalPlay();
#endif
        }

#if UNITY_SERVER
        void Update()
        {
            // Headless dedicated builds do not auto-tick ECS worlds; without this the server never
            // processes Relay packets and clients stay at connections=1 withNetworkId=0.
            TickServerWorld();
        }
#endif

        /// <summary>Stops the editor's local ServerWorld sim until local play is started (online join testing).</summary>
        public static void SuspendEditorLocalServerUntilLocalPlay()
        {
#if UNITY_EDITOR
            if (ShouldRunHeadlessServerBoot())
                return;

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            var simulation = server.GetExistingSystemManaged<SimulationSystemGroup>();
            if (simulation == null || !simulation.Enabled)
                return;

            simulation.Enabled = false;
            s_EditorLocalServerSuspendedForOnline = true;
            Debug.Log("[TitanOrbitSessionManager] Suspended local ServerWorld simulation (online dedicated testing).");
#endif
        }

        static void ResumeEditorLocalServerForLocalPlay()
        {
#if UNITY_EDITOR
            if (!s_EditorLocalServerSuspendedForOnline)
                return;

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            var simulation = server.GetExistingSystemManaged<SimulationSystemGroup>();
            if (simulation != null)
                simulation.Enabled = true;

            s_EditorLocalServerSuspendedForOnline = false;
            Debug.Log("[TitanOrbitSessionManager] Resumed local ServerWorld simulation for local play.");
#endif
        }

        static bool ShouldRunHeadlessServerBoot()
        {
#if UNITY_EDITOR
            return HasExplicitDedicatedServerArg();
#else
#if UNITY_SERVER
            return true;
#else
            return TitanOrbitServerCommandLine.HasDedicatedFlag() || Application.isBatchMode;
#endif
#endif
        }

        static bool HasExplicitDedicatedServerArg() => TitanOrbitServerCommandLine.HasDedicatedFlag();

        static bool ShouldAutoBootDedicatedRelay()
        {
#if UNITY_EDITOR
            return false;
#elif UNITY_SERVER
            return true;
#else
            return TitanOrbitServerCommandLine.HasDedicatedFlag() || Application.isBatchMode;
#endif
        }

        static bool s_DedicatedBootStarted;

        /// <summary>Idempotent dedicated-server boot (scene Start + <see cref="TitanOrbitDedicatedServerAutoBoot"/>).</summary>
        public void EnsureDedicatedBootStarted()
        {
            if (s_DedicatedBootStarted)
                return;
            if (!ShouldRunHeadlessServerBoot())
            {
                Debug.Log("[TitanOrbitSessionManager] EnsureDedicatedBootStarted skipped (not a headless server process).");
                return;
            }

            if (!ShouldAutoBootDedicatedRelay())
            {
                Debug.Log("[TitanOrbitSessionManager] EnsureDedicatedBootStarted skipped (relay auto-boot disabled).");
                return;
            }

            s_DedicatedBootStarted = true;
            DedicatedServerFileLog.Append("boot", "EnsureDedicatedBootStarted -> BootDedicatedServer coroutine.");
            Debug.Log("[TitanOrbitSessionManager] Starting BootDedicatedServer coroutine.");
            StartCoroutine(BootDedicatedServer());
        }

        IEnumerator BootMppmLanServer()
        {
            float readyDeadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < readyDeadline)
            {
                var serverWorld = ClientServerBootstrap.ServerWorld;
                if (serverWorld != null && serverWorld.IsCreated &&
                    serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0)
                    break;
                yield return null;
            }

            TitanOrbitRelayState.Clear();
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null)
            {
                Debug.LogError("[TitanOrbitSessionManager] Server world missing for MPPM server player.");
                yield break;
            }

            // Bootstrap AutoConnectPort already listens; only mark connections in-game.
            RequestGoInGame(server);
            IsInGame = true;
            Debug.Log("[TitanOrbitSessionManager] Dedicated-server play instance ready (listening on port " + serverPort + "). Use the main Editor Game tab to play as client.");
        }

        bool _localBootRunning;

        IEnumerator MaintainClientSession()
        {
            float deadline = Time.realtimeSinceStartup + 45f;
            while (Time.realtimeSinceStartup < deadline && !HasClientInGame())
            {
                var client = ClientServerBootstrap.ClientWorld;
                if (client != null && client.IsCreated)
                {
                    if (!HasClientConnection(client))
                        ConnectLocalClient(serverPort);
                    else
                        RequestGoInGame(client);
                }

                if (HasClientInGame())
                {
                    IsInGame = true;
                    LastStatusMessage = TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance()
                        ? "Connected to host — choose a team."
                        : "Connected.";
                    Debug.Log("[TitanOrbitSessionManager] Client in-game.");
                    yield break;
                }


                yield return null;
            }

            if (!HasClientInGame())
            {
                var client = ClientServerBootstrap.ClientWorld;
                var server = ClientServerBootstrap.ServerWorld;
                Debug.LogWarning("[TitanOrbitSessionManager] Client never reached in-game. client=" +
                                 (client != null && client.IsCreated ? client.Name : "missing") +
                                 " server=" + (server != null && server.IsCreated ? server.Name : "missing") +
                                 ". Press Play on the main Editor Game view, or disable the MPPM Server virtual player.");
            }
        }

        public void StartLocalPlay()
        {
            LastStatusMessage = "Starting local play...";
            if (_localBootRunning || HasClientInGame())
                return;
            StartCoroutine(BootLanHost());
        }

        public bool StartLanHostForLocalTest()
        {
#if UNITY_EDITOR
            return StartLocalHostForLanTest();
#else
            PendingLanHost = true;
            return true;
#endif
        }

        /// <summary>Editor/MPPM: listen on <see cref="serverPort"/> without a local client connection.</summary>
        public bool StartLocalHostForLanTest()
        {
            LastStatusMessage = "Starting local LAN host...";
            if (_localBootRunning)
                return false;

            var server = ClientServerBootstrap.ServerWorld;
            if (server != null && server.IsCreated && IsServerWorldListening(server))
            {
                LastStatusMessage = "Already hosting on port " + serverPort + ".";
                Debug.Log("[TitanOrbitSessionManager] Local LAN host already listening on port " + serverPort + ".");
                return true;
            }

            StartCoroutine(BootLanHostOnly());
            return true;
        }

        IEnumerator BootLanHostOnly()
        {
            _localBootRunning = true;
            try
            {
                ResumeEditorLocalServerForLocalPlay();
                TitanOrbitRelayState.Clear();

                float readyDeadline = Time.realtimeSinceStartup + 15f;
                while (Time.realtimeSinceStartup < readyDeadline)
                {
                    var serverWorld = ClientServerBootstrap.ServerWorld;
                    if (serverWorld != null && serverWorld.IsCreated &&
                        serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0)
                        break;
                    yield return null;
                }

                var server = ClientServerBootstrap.ServerWorld;
                if (server == null || !server.IsCreated)
                {
                    LastStatusMessage = "ServerWorld missing.";
                    Debug.LogError("[TitanOrbitSessionManager] BootLanHostOnly: ServerWorld missing.");
                    yield break;
                }

                ListenServer(server, serverPort);

                float listenDeadline = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < listenDeadline && !IsServerWorldListening(server))
                    yield return null;

                if (!IsServerWorldListening(server))
                {
                    LastStatusMessage = "Failed to listen on port " + serverPort + ".";
                    Debug.LogError("[TitanOrbitSessionManager] BootLanHostOnly: listen failed on port " + serverPort + ".");
                    yield break;
                }

                RequestGoInGame(server);
                LastStatusMessage = "Hosting on port " + serverPort +
                                    " — other players: Local client. You: Local play or Local client.";
                Debug.Log("[TitanOrbitSessionManager] Local LAN host listening on port " + serverPort +
                          ". Connect additional players with Local client.");
            }
            finally
            {
                _localBootRunning = false;
            }
        }

        public bool StartLocalClientForLanTest(string address = "127.0.0.1")
        {
            LastStatusMessage = "Connecting to local server...";
            if (_localBootRunning || HasClientInGame())
                return false;
            StartCoroutine(BootLanClient(address));
            return true;
        }

        IEnumerator BootLanClient(string address)
        {
            _localBootRunning = true;
            try
            {
                TitanOrbitRelayState.Clear();
                float readyDeadline = Time.realtimeSinceStartup + 15f;
                while (Time.realtimeSinceStartup < readyDeadline)
                {
                    var clientWorld = ClientServerBootstrap.ClientWorld;
                    if (clientWorld != null && clientWorld.IsCreated &&
                        clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0)
                        break;
                    yield return null;
                }

                var client = ClientServerBootstrap.ClientWorld;
                if (client == null || !client.IsCreated)
                {
                    LastStatusMessage = "ClientWorld missing.";
                    yield break;
                }

                if (!ushort.TryParse(address.Contains(":") ? address.Split(':')[^1] : serverPort.ToString(), out ushort port))
                    port = serverPort;
                ConnectLocalClient(port);

                float deadline = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (HasClientConnection(client))
                        RequestGoInGame(client);
                    if (HasClientInGame())
                    {
                        IsInGame = true;
                        LastStatusMessage = "Connected to local server.";
                        yield break;
                    }

                    yield return null;
                }

                LastStatusMessage = "Local client connection timed out.";
            }
            finally
            {
                _localBootRunning = false;
            }
        }

        public async Task<bool> QuickJoinDedicatedAsync()
        {
            LastStatusMessage = "Finding a dedicated match...";
            try
            {
                Lobby lobby = await TitanOrbitLobbyService.QuickJoinLatestDedicatedLobbyAsync();
                if (lobby == null)
                {
                    LastStatusMessage = "No dedicated match found.";
                    return false;
                }

                return await JoinDedicatedLobbyAsync(lobby.Id);
            }
            catch (Exception ex)
            {
                LastStatusMessage = "Quick join failed.";
                Debug.LogError("[TitanOrbitSessionManager] QuickJoin failed: " + ex.Message);
                return false;
            }
        }

        IEnumerator BootLanHost()
        {
            _localBootRunning = true;
            try
            {
            ResumeEditorLocalServerForLocalPlay();
            LastStatusMessage = "Waiting for NetCode worlds...";
            float readyDeadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < readyDeadline)
            {
                var clientWorld = ClientServerBootstrap.ClientWorld;
                if (clientWorld != null && clientWorld.IsCreated &&
                    clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0)
                    break;
                yield return null;
            }

            TitanOrbitRelayState.Clear();

            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
            {
                LastStatusMessage = "ClientWorld missing. Use the main Editor Game view.";
                Debug.LogError("[TitanOrbitSessionManager] ClientWorld required to connect.");
                yield break;
            }

            var server = ClientServerBootstrap.ServerWorld;
            bool localHost = server != null && server.IsCreated;

            LastStatusMessage = localHost ? "Starting local host..." : "Connecting to game server...";
            if (localHost)
                ListenServer(server, serverPort);
            ConnectLocalClient(serverPort);

            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (HasClientConnection(client))
                    RequestGoInGame(client);

                if (HasClientInGame())
                {
                    if (localHost)
                        RequestGoInGame(server);
                    IsInGame = true;
                    LastStatusMessage = "Connected.";
                    Debug.Log(localHost
                        ? "[TitanOrbitSessionManager] Local Client+Server connected."
                        : "[TitanOrbitSessionManager] Connected to game server on port " + serverPort + ".");
                    yield break;
                }

                if (localHost && HasLocalConnection(server, client))
                {
                    RequestGoInGame(server);
                    RequestGoInGame(client);
                }

                yield return null;
            }

            LastStatusMessage = "Connection timed out. Is port 7777 in use? Try disabling the MPPM Server player.";
            Debug.LogError("[TitanOrbitSessionManager] Timed out waiting for network connection.");
            }
            finally
            {
                _localBootRunning = false;
            }
        }

        static bool HasClientConnection(World client)
        {
            if (client == null || !client.IsCreated) return false;
            return client.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0;
        }

        static bool HasClientInGame()
        {
            return IsClientGameplayReady(ClientServerBootstrap.ClientWorld);
        }

        public static bool IsClientConnectionReady(World world)
        {
            if (world == null || !world.IsCreated)
                return false;

            return world.EntityManager
                .CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .CalculateEntityCount() > 0;
        }

        /// <summary>
        /// Dedicated Relay clients must not treat a stale loopback connection as in-game.
        /// </summary>
        public static bool IsClientGameplayReady(World world)
        {
            if (!IsClientConnectionReady(world))
                return false;

            if (IsDedicatedOnlineClient && !TitanOrbitRelayState.TryGetClientRelay(out _))
                return false;

            return true;
        }

        static bool HasLocalConnection(World server, World client)
        {
            if (server != null && server.IsCreated &&
                server.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0)
                return true;
            if (client != null && client.IsCreated &&
                client.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0)
                return true;
            return false;
        }

        public bool IsRecreateDedicatedMatchInProgress => _recreateDedicatedMatchInProgress;

        IEnumerator BootDedicatedServer()
        {
            _serverConfig = TitanOrbitServerCommandLine.Parse();
            maxPlayers = _serverConfig.MaxPlayers;
            serverPort = _serverConfig.ServerPort;

            DedicatedServerFileLog.Append("boot", "BootDedicatedServer starting.");
            Debug.Log("[TitanOrbitSessionManager] BootDedicatedServer maxPlayers=" + maxPlayers +
                      " port=" + serverPort + " project=" + (Application.cloudProjectId ?? "(none)"));

            float worldDeadline = Time.realtimeSinceStartup + _serverConfig.WaitNetworkManagerSeconds;
            int waitFrames = 0;
            while (Time.realtimeSinceStartup < worldDeadline)
            {
                TickServerWorld();
                if (IsServerWorldReady())
                    break;

                if (waitFrames % 300 == 0)
                    LogServerWorldWaitStatus(waitFrames);
                waitFrames++;
                yield return null;
            }

            if (!IsServerWorldReady())
            {
                LogServerWorldWaitStatus(waitFrames);
                Debug.LogError("[TitanOrbitSessionManager] ServerWorld/NetworkStreamDriver not ready after " +
                               _serverConfig.WaitNetworkManagerSeconds + "s.");
                Application.Quit(1);
                yield break;
            }

            DedicatedServerFileLog.Append("boot", "ServerWorld ready after " + waitFrames + " frame(s).");

            int maxAttempts = _serverConfig.BootMaxAttempts;
            int delaySeconds = _serverConfig.BootRetryDelaySeconds;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Task<DedicatedServerPrep> prepTask = PrepareDedicatedRelayAsync(_serverConfig);
                while (!prepTask.IsCompleted)
                {
                    TickServerWorld();
                    yield return null;
                }

                if (!prepTask.IsFaulted && prepTask.Result != null)
                {
                    var prep = prepTask.Result;
                    TitanOrbitRelayState.SetServerRelay(prep.Relay);

                    var serverWorld = ClientServerBootstrap.ServerWorld;
                    if (serverWorld == null || !serverWorld.IsCreated)
                    {
                        Debug.LogWarning("[TitanOrbitSessionManager] Server world lost after relay prep; retrying.");
                    }
                    else
                    {
                        yield return ClearNetworkConnections(serverWorld);

                        ResetServerDriverIfNeeded();
                        ListenServer(serverWorld, serverPort);

                        float listenDeadline = Time.realtimeSinceStartup + 15f;
                        while (Time.realtimeSinceStartup < listenDeadline && !IsServerWorldListening(serverWorld))
                        {
                            TickServerWorld(serverWorld);
                            yield return null;
                        }

                        if (!IsServerWorldListening(serverWorld))
                        {
                            // Driver bind can lag a few frames after ResetDriverStore; retry once.
                            ListenServer(serverWorld, serverPort);
                            for (int i = 0; i < 90; i++)
                            {
                                TickServerWorld(serverWorld);
                                yield return null;
                                if (IsServerWorldListening(serverWorld))
                                    break;
                            }
                        }

                        if (!IsServerWorldListening(serverWorld))
                        {
                            Debug.LogWarning("[TitanOrbitSessionManager] Relay listen not confirmed — publishing UGS lobby anyway.");
                            if (TitanOrbitRelayState.TryGetServerRelay(out var relay))
                                LogServerRelayListenDiagnostics(serverWorld, relay, listenOk: false);
                        }

                        RequestGoInGame(serverWorld);

                        Task<Lobby> lobbyTask = CreateDedicatedLobbyAsync(
                            prep.JoinCode,
                            prep.RelayProtocol,
                            prep.CreatedAtEpochSeconds,
                            prep.MaxPlayers,
                            prep.ServerListenAddress,
                            prep.IsLatest,
                            prep.HostAllocationId);
                        while (!lobbyTask.IsCompleted)
                        {
                            TickServerWorld(serverWorld);
                            yield return null;
                        }

                        if (lobbyTask.IsFaulted || lobbyTask.Result == null)
                        {
                            if (lobbyTask.Exception != null)
                                Debug.LogError("[TitanOrbitSessionManager] " + lobbyTask.Exception.GetBaseException());
                            else
                                Debug.LogError("[TitanOrbitSessionManager] Failed to publish dedicated UGS lobby.");
                        }
                        else
                        {
                            prep.Lobby = lobbyTask.Result;
                            _activeLobbyId = prep.Lobby.Id;
                            StartCoroutine(LobbyHeartbeatLoop());
                            IsInGame = true;
                            TitanOrbitDedicatedServerHost.Begin(_serverConfig, prep.Lobby.Id, prep.CreatedAtEpochSeconds, prep.IsLatest);
                            DedicatedServerFileLog.Append(
                                "lobby",
                                "Dedicated server live lobby=" + prep.Lobby.Id + " name=" + (prep.Lobby.Name ?? "") +
                                " relay=" + prep.JoinCode + " listening=" + IsServerWorldListening(serverWorld));
                            Debug.Log("[TitanOrbitSessionManager] Dedicated server live. Relay=" + prep.JoinCode +
                                      " protocol=" + prep.RelayProtocol + " Lobby=" + prep.Lobby.Id +
                                      " name=" + prep.Lobby.Name +
                                      " listening=" + IsServerWorldListening(serverWorld));
                            StartCoroutine(MaintainDedicatedServerGoInGame());
                            StartCoroutine(CloseSupersededDedicatedLobbies(prep.Lobby.Id));
                            yield break;
                        }
                    }
                }
                else if (prepTask.Exception != null)
                {
                    Debug.LogError("[TitanOrbitSessionManager] " + prepTask.Exception.GetBaseException());
                }
                else
                {
                    Debug.LogWarning("[TitanOrbitSessionManager] Dedicated boot attempt " + attempt + "/" + maxAttempts +
                                     " failed (UGS/Relay/Lobby).");
                }

                if (attempt >= maxAttempts)
                {
                    Debug.LogError("[TitanOrbitSessionManager] Dedicated server boot failed after all attempts.");
                    Application.Quit(1);
                    yield break;
                }

                float deadline = Time.realtimeSinceStartup + delaySeconds;
                while (Time.realtimeSinceStartup < deadline)
                    yield return null;
            }
        }

        static bool IsServerWorldReady()
        {
            var serverWorld = ClientServerBootstrap.ServerWorld;
            if (serverWorld == null || !serverWorld.IsCreated)
                return false;
            return serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
        }

        static int s_LastServerTickFrame = -1;

        static void TickServerWorld(World world = null)
        {
            world ??= ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated)
                return;

            // One simulation step per Unity frame even if Update and coroutines both pump the server.
            if (Time.frameCount == s_LastServerTickFrame)
                return;
            s_LastServerTickFrame = Time.frameCount;
            world.Update();
        }

        static void LogServerWorldWaitStatus(int waitFrames)
        {
            var serverWorld = ClientServerBootstrap.ServerWorld;
            int driverCount = 0;
            if (serverWorld != null && serverWorld.IsCreated)
                driverCount = serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount();

            string msg = "[TitanOrbitSessionManager] Waiting for ServerWorld… frames=" + waitFrames +
                         " serverWorld=" + (serverWorld != null && serverWorld.IsCreated ? "ok" : "missing") +
                         " networkStreamDriverEntities=" + driverCount;
            Debug.Log(msg);
            DedicatedServerFileLog.Append("boot", msg);
        }

        public sealed class DedicatedMatchRecreateResult
        {
            public string LobbyId;
            public long CreatedAtEpochSeconds;
            public bool IsLatest;
        }

        sealed class DedicatedServerPrep
        {
            public RelayServerData Relay;
            public string JoinCode;
            public string HostAllocationId;
            public Lobby Lobby;
            public long CreatedAtEpochSeconds;
            public bool IsLatest;
            public int MaxPlayers;
            public string RelayProtocol;
            public string ServerListenAddress;
        }

        async Task<DedicatedServerPrep> PrepareDedicatedRelayAsync(TitanOrbitServerCommandLine config)
        {
            try
            {
                if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                {
                    DedicatedServerFileLog.Append("boot", "PrepareDedicatedRelay failed: UGS guest session not ready.");
                    Debug.LogError("[TitanOrbitSessionManager] PrepareDedicatedRelay failed: UGS not ready. project=" +
                                   (Application.cloudProjectId ?? "(none)"));
                    return null;
                }

                int cap = Mathf.Max(2, config.MaxPlayers);
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(Mathf.Max(1, cap - 1));
                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                string protocol = TitanOrbitRelayUtility.HostConnectionTypeForPlatform(
                    TitanOrbitServerCommandLine.SanitizeRelayProtocol(config.RelayProtocol));
                long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                DedicatedServerFileLog.Append("boot", "Relay allocation ok joinCode=" + joinCode + " cap=" + cap +
                                                  " protocol=" + protocol);
                return new DedicatedServerPrep
                {
                    Relay = TitanOrbitRelayUtility.FromAllocation(allocation, protocol),
                    JoinCode = joinCode,
                    HostAllocationId = allocation.AllocationId.ToString(),
                    CreatedAtEpochSeconds = createdAt,
                    IsLatest = config.IsLatest,
                    MaxPlayers = cap,
                    RelayProtocol = protocol,
                    ServerListenAddress = config.ServerListenAddress,
                };
            }
            catch (Exception ex)
            {
                DedicatedServerFileLog.Append("boot", "PrepareDedicatedRelay exception", ex);
                Debug.LogError("[TitanOrbitSessionManager] PrepareDedicatedRelay failed: " + ex.Message);
                return null;
            }
        }

        public async Task<DedicatedMatchRecreateResult> RecreateDedicatedMatchAsync(TitanOrbitServerCommandLine config)
        {
            if (_recreateDedicatedMatchInProgress)
                return null;
            if (GetServerConnectedPlayerCount() > 0)
                return null;

            _recreateDedicatedMatchInProgress = true;
            string oldLobbyId = _activeLobbyId;
            try
            {
                var prep = await PrepareDedicatedRelayAsync(config);
                if (prep == null)
                    return null;

                var serverWorld = ClientServerBootstrap.ServerWorld;
                if (serverWorld == null || !serverWorld.IsCreated)
                    return null;

                TitanOrbitRelayState.SetServerRelay(prep.Relay);
                await ClearNetworkConnectionsAsync(serverWorld);
                ResetServerDriverIfNeeded();
                ListenServer(serverWorld, config.ServerPort);
                for (int i = 0; i < 90; i++)
                {
                    TickServerWorld(serverWorld);
                    if (IsServerWorldListening(serverWorld))
                        break;
                    await Task.Delay(16);
                }

                if (!IsServerWorldListening(serverWorld))
                    Debug.LogWarning("[TitanOrbitSessionManager] Recreate: relay listen not confirmed; publishing lobby anyway.");

                RequestGoInGame(serverWorld);
                prep.Lobby = await CreateDedicatedLobbyAsync(
                    prep.JoinCode,
                    prep.RelayProtocol,
                    prep.CreatedAtEpochSeconds,
                    prep.MaxPlayers,
                    prep.ServerListenAddress,
                    prep.IsLatest,
                    prep.HostAllocationId);
                if (prep.Lobby == null)
                    return null;

                _activeLobbyId = prep.Lobby.Id;
                await CloseLobbyForNewJoinersAsync(oldLobbyId, "empty_match_recreate");
                try
                {
                    await LobbyService.Instance.DeleteLobbyAsync(oldLobbyId);
                }
                catch (Exception deleteEx)
                {
                    Debug.LogWarning("[TitanOrbitSessionManager] Could not delete old lobby: " + deleteEx.Message);
                }

                Debug.Log("[TitanOrbitSessionManager] Empty match recreated: " + prep.Lobby.Id);
                return new DedicatedMatchRecreateResult
                {
                    LobbyId = prep.Lobby.Id,
                    CreatedAtEpochSeconds = prep.CreatedAtEpochSeconds,
                    IsLatest = prep.IsLatest,
                };
            }
            finally
            {
                _recreateDedicatedMatchInProgress = false;
            }
        }

        public async Task CloseLobbyForNewJoinersAsync(string lobbyId, string reason)
        {
            if (string.IsNullOrWhiteSpace(lobbyId))
                return;
            try
            {
                await TitanOrbitLobbyService.AcquireLobbyApiGateAsync();
                try
                {
                    await LobbyService.Instance.UpdateLobbyAsync(lobbyId, new UpdateLobbyOptions
                    {
                        Data = new Dictionary<string, DataObject>
                        {
                            {
                                TitanOrbitLobbyService.LobbyIsOpenKey,
                                new DataObject(DataObject.VisibilityOptions.Public, "0", DataObject.IndexOptions.N1)
                            },
                            {
                                TitanOrbitLobbyService.LobbyIsLatestKey,
                                new DataObject(DataObject.VisibilityOptions.Public, "0", DataObject.IndexOptions.N2)
                            }
                        },
                        IsLocked = true
                    });
                }
                finally
                {
                    TitanOrbitLobbyService.ReleaseLobbyApiGate();
                }

                DedicatedServerFileLog.Append("lobby", "Closed lobby (" + reason + ") id=" + lobbyId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] CloseLobbyForNewJoiners failed: " + e.Message);
            }
        }

        public int GetServerConnectedPlayerCount()
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return 0;
            return server.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount();
        }

        public bool IsServerListening()
        {
            return IsServerWorldListening(ClientServerBootstrap.ServerWorld);
        }

        static void RequestDisconnectAllConnections(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            using var connections = em.CreateEntityQuery(typeof(NetworkStreamConnection)).ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; i++)
            {
                Entity connection = connections[i];
                if (!em.Exists(connection))
                    continue;
                if (!em.HasComponent<NetworkStreamRequestDisconnect>(connection))
                    em.AddComponent<NetworkStreamRequestDisconnect>(connection);
            }
        }

        static async Task ClearNetworkConnectionsAsync(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            for (int i = 0; i < 120; i++)
            {
                if (em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() == 0)
                    return;

                RequestDisconnectAllConnections(world);
                // Must tick THIS world (client or server). Shared TickServerWorld frame-gate can skip client ticks.
                world.Update();
                await Task.Yield();
            }
        }

        static IEnumerator ClearNetworkConnections(World world)
        {
            if (world == null || !world.IsCreated) yield break;
            var em = world.EntityManager;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() == 0)
                    yield break;

                RequestDisconnectAllConnections(world);
                TickServerWorld(world);
                yield return null;
            }
        }

        public async Task<bool> JoinDedicatedLobbyAsync(string lobbyId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lobbyId))
                    return false;
                if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                    return false;

                await TitanOrbitLobbyService.TryLeaveAllJoinedLobbiesAsync("before_dedicated_join");
                await PrepareClientForDedicatedRelayJoinAsync();

                string previousLobbyId = _activeLobbyId;
                Lobby lobby = await TitanOrbitLobbyService.JoinDedicatedLobbyByIdAsync(lobbyId, previousLobbyId);
                if (lobby == null)
                {
                    LastStatusMessage = "Could not join lobby.";
                    return false;
                }

                if (!TitanOrbitLobbyService.IsDedicatedLobbyJoinable(lobby, out string rejectReason))
                {
                    Debug.LogWarning("[TitanOrbitSessionManager] Join rejected: " + rejectReason);
                    LastStatusMessage = string.IsNullOrEmpty(rejectReason)
                        ? "This lobby is not joinable."
                        : "Join rejected: " + rejectReason;
                    return false;
                }

                // Member-only relay code — refresh after join so we never use a stale query snapshot.
                lobby = await LobbyService.Instance.GetLobbyAsync(lobby.Id);
                if (lobby == null)
                {
                    LastStatusMessage = "Lobby disappeared.";
                    return false;
                }

                if (TitanOrbitLobbyService.IsDedicatedLobbyHeartbeatTooOld(
                        lobby, TitanOrbitLobbyService.DedicatedLobbyJoinMaxHeartbeatAgeSeconds, out long heartbeatAge))
                {
                    LastStatusMessage = heartbeatAge <= 0
                        ? "Server heartbeat missing — tap Refresh, then join again."
                        : $"Server heartbeat is {heartbeatAge}s old — tap Refresh, then join again.";
                    Debug.LogWarning("[TitanOrbitSessionManager] Join rejected stale heartbeat age=" + heartbeatAge);
                    await TitanOrbitLobbyService.TryRemovePlayerFromLobbyAsync(lobby.Id, "stale_heartbeat");
                    return false;
                }

                if (!lobby.Data.TryGetValue(TitanOrbitLobbyService.LobbyRelayCodeKey, out var relayData) ||
                    string.IsNullOrWhiteSpace(relayData?.Value))
                {
                    Debug.LogError("[TitanOrbitSessionManager] Lobby missing relay join code.");
                    LastStatusMessage = "Lobby is missing relay data.";
                    return false;
                }

                string joinCode = relayData.Value;
                string hostProtocol = lobby.Data.TryGetValue(TitanOrbitLobbyService.LobbyRelayProtocolKey, out var proto)
                    ? TitanOrbitRelayUtility.SanitizeRelayProtocolForRelaySdk(proto.Value)
                    : TitanOrbitRelayUtility.ClientConnectionTypeForPlatform();
                // Host and editor client both use dtls to the same Relay allocation (legacy NGO behavior).
                string clientProtocol = TitanOrbitRelayUtility.ClientConnectionTypeForPlatform();

                Debug.Log("[TitanOrbitSessionManager] Joining Relay lobby=" + lobby.Id + " code=" + joinCode +
                          " hostProtocol=" + hostProtocol + " clientProtocol=" + clientProtocol);
                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
                var clientRelay = TitanOrbitRelayUtility.FromJoinAllocation(joinAllocation, clientProtocol);
                if (!TitanOrbitRelayUtility.IsRelayEndpointValid(clientRelay))
                {
                    Debug.LogError("[TitanOrbitSessionManager] Relay endpoint invalid for clientProtocol=" + clientProtocol);
                    LastStatusMessage = "Relay connection data invalid — tap Refresh, then join again.";
                    return false;
                }

                await TitanOrbitLobbyService.TryUpdatePlayerRelayAllocationAsync(
                    lobby.Id, joinAllocation.AllocationId.ToString());

                TitanOrbitRelayState.SetClientRelay(clientRelay);
                await EnsureClientReadyForRelayDriverResetAsync();
                ResetClientDriverIfNeeded();

                var clientWorld = ClientServerBootstrap.ClientWorld;
                if (clientWorld == null || !clientWorld.IsCreated)
                {
                    Debug.LogError("[TitanOrbitSessionManager] Client world missing.");
                    LastStatusMessage = "Client world missing.";
                    return false;
                }

                ConnectRelayClient(clientWorld);
                for (int i = 0; i < 30; i++)
                {
                    clientWorld.Update();
                    await Task.Yield();
                }

                _activeLobbyId = lobby.Id;
                LastStatusMessage = "Connecting to " + (lobby.Name ?? "match") + "...";
                Debug.Log("[TitanOrbitSessionManager] Joining dedicated lobby " + lobby.Id + " via Relay.");
                _connectWatch = StartCoroutine(ClientConnectWatch(60f, dedicatedJoin: true));
                return true;
            }
            catch (Exception ex)
            {
                IsDedicatedOnlineClient = false;
                IsInGame = false;
                var msg = ex.Message ?? string.Empty;
                if (msg.IndexOf("join code not found", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LastStatusMessage = "Server offline or restarted — tap Refresh, then join again or Request match.";
                    if (!string.IsNullOrWhiteSpace(lobbyId))
                        await TitanOrbitLobbyService.TryRemovePlayerFromLobbyAsync(lobbyId, "stale_relay");
                }
                else
                    LastStatusMessage = "Join failed: " + msg;

                Debug.LogError("[TitanOrbitSessionManager] Join failed: " + msg);
                return false;
            }
        }

        public async Task ResetDedicatedClientSessionAsync(string reason = null)
        {
            IsDedicatedOnlineClient = false;
            IsInGame = false;
            ClientTeamFlowState.Reset();
            if (_connectWatch != null)
            {
                StopCoroutine(_connectWatch);
                _connectWatch = null;
            }

            var clientWorld = ClientServerBootstrap.ClientWorld;
            if (clientWorld != null && clientWorld.IsCreated)
            {
                ClearNetworkStreamInGame(clientWorld);
                await ClearNetworkConnectionsAsync(clientWorld);
            }

            TitanOrbitRelayState.Clear();
            ResetClientDriverIfNeeded();

            if (!string.IsNullOrEmpty(reason))
            {
                LastStatusMessage = reason;
                Debug.LogWarning("[TitanOrbitSessionManager] Dedicated client session reset: " + reason);
            }
        }

        async Task PrepareClientForDedicatedRelayJoinAsync()
        {
            IsDedicatedOnlineClient = true;
            IsInGame = false;
            ClientTeamFlowState.Reset();
            StopMppmLanAutoConnect();

            await SuspendLocalServerForDedicatedClientAsync();
            await EnsureClientReadyForRelayDriverResetAsync();
            TitanOrbitRelayState.Clear();
        }

        void StopMppmLanAutoConnect()
        {
            if (_mppmLanConnectCoroutine == null)
                return;

            StopCoroutine(_mppmLanConnectCoroutine);
            _mppmLanConnectCoroutine = null;
            Debug.Log("[TitanOrbitSessionManager] Stopped MPPM LAN auto-connect before dedicated Relay join.");
        }

        /// <summary>
        /// Disconnect leftover LAN/loopback connections and wait until NetworkStreamConnection entities are gone.
        /// Required before NetworkStreamDriver.ResetDriverStore / Relay Connect.
        /// </summary>
        async Task EnsureClientReadyForRelayDriverResetAsync()
        {
            var clientWorld = ClientServerBootstrap.ClientWorld;
            if (clientWorld == null || !clientWorld.IsCreated)
                return;

            ClearNetworkStreamInGame(clientWorld);
            await ClearNetworkConnectionsAsync(clientWorld);

            for (int i = 0; i < 10; i++)
            {
                if (clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() == 0)
                    return;

                RequestDisconnectAllConnections(clientWorld);
                clientWorld.Update();
                await Task.Yield();
            }

            int remaining = clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount();
            if (remaining > 0)
                Debug.LogWarning("[TitanOrbitSessionManager] " + remaining +
                                 " NetworkStreamConnection(s) still present before Relay driver reset.");
        }

        static async Task SuspendLocalServerForDedicatedClientAsync()
        {
            var server = ClientServerBootstrap.ServerWorld;
            var client = ClientServerBootstrap.ClientWorld;

            if (server != null && server.IsCreated)
            {
                var simulation = server.GetExistingSystemManaged<SimulationSystemGroup>();
                if (simulation != null)
                    simulation.Enabled = false;

                ClearNetworkStreamInGame(server);
                RequestDisconnectAllConnections(server);
                ResetServerDriverIfNeeded();
                TickServerWorld(server);
            }

            if (client != null && client.IsCreated)
            {
                ClearNetworkStreamInGame(client);
                RequestDisconnectAllConnections(client);
            }

            for (int i = 0; i < 60; i++)
            {
                if (server != null && server.IsCreated)
                    TickServerWorld(server);
                if (client != null && client.IsCreated)
                    client.Update();
                await Task.Yield();
            }
        }

        async Task<Lobby> CreateDedicatedLobbyAsync(
            string joinCode,
            string protocol,
            long createdAt,
            int cap,
            string serverListenAddress,
            bool isLatest,
            string hostAllocationId)
        {
            await TitanOrbitLobbyService.AcquireLobbyApiGateAsync();
            try
            {
                string playerId = AuthenticationService.Instance.PlayerId;
                var createOptions = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Data = new Dictionary<string, DataObject>
                    {
                        { TitanOrbitLobbyService.LobbyRelayCodeKey, new DataObject(DataObject.VisibilityOptions.Member, joinCode) },
                        { TitanOrbitLobbyService.LobbyGameNameKey, new DataObject(DataObject.VisibilityOptions.Public, TitanOrbitLobbyService.LobbyGameNameValue, DataObject.IndexOptions.S1) },
                        { TitanOrbitLobbyService.LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N1) },
                        { TitanOrbitLobbyService.LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, isLatest ? "1" : "0", DataObject.IndexOptions.N2) },
                        { TitanOrbitLobbyService.LobbyCreatedAtEpochKey, new DataObject(DataObject.VisibilityOptions.Public, createdAt.ToString(CultureInfo.InvariantCulture), DataObject.IndexOptions.N3) },
                        { TitanOrbitLobbyService.LobbyServerAliveEpochKey, new DataObject(DataObject.VisibilityOptions.Public, createdAt.ToString(CultureInfo.InvariantCulture)) },
                        { TitanOrbitLobbyService.LobbyRelayProtocolKey, new DataObject(DataObject.VisibilityOptions.Public, protocol) },
                        { TitanOrbitLobbyService.LobbyServerListenAddressKey, new DataObject(DataObject.VisibilityOptions.Public, serverListenAddress) },
                        { TitanOrbitLobbyService.LobbyActivePlayersKey, new DataObject(DataObject.VisibilityOptions.Public, "0", DataObject.IndexOptions.N4) },
                    }
                };

                if (!string.IsNullOrEmpty(playerId) && !string.IsNullOrWhiteSpace(hostAllocationId))
                    createOptions.Player = new Player(id: playerId, allocationId: hostAllocationId);

                return await LobbyService.Instance.CreateLobbyAsync(
                    GameNames.GetRandomRoomName(),
                    cap,
                    createOptions);
            }
            finally
            {
                TitanOrbitLobbyService.ReleaseLobbyApiGate();
            }
        }

        IEnumerator LobbyHeartbeatLoop()
        {
            if (!string.IsNullOrEmpty(_activeLobbyId))
            {
                Task first = SendHeartbeatAsync();
                while (!first.IsCompleted)
                    yield return null;
            }

            var wait = new WaitForSeconds(15f);
            while (true)
            {
                if (!string.IsNullOrEmpty(_activeLobbyId))
                {
                    Task heartbeat = SendHeartbeatAsync();
                    while (!heartbeat.IsCompleted) yield return null;
                }
                yield return wait;
            }
        }

        async Task SendHeartbeatAsync()
        {
            try
            {
                await TitanOrbitLobbyService.AcquireLobbyApiGateAsync();
                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    int activePlayers = GetServerConnectedPlayerCount();
                    await LobbyService.Instance.SendHeartbeatPingAsync(_activeLobbyId);
                    await LobbyService.Instance.UpdateLobbyAsync(_activeLobbyId, new UpdateLobbyOptions
                    {
                        Data = new Dictionary<string, DataObject>
                        {
                            {
                                TitanOrbitLobbyService.LobbyServerAliveEpochKey,
                                new DataObject(DataObject.VisibilityOptions.Public, now.ToString(CultureInfo.InvariantCulture))
                            },
                            {
                                TitanOrbitLobbyService.LobbyActivePlayersKey,
                                new DataObject(DataObject.VisibilityOptions.Public, activePlayers.ToString(CultureInfo.InvariantCulture))
                            }
                        }
                    });
                }
                finally
                {
                    TitanOrbitLobbyService.ReleaseLobbyApiGate();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] Heartbeat failed: " + ex.Message);
            }
        }

        IEnumerator CloseSupersededDedicatedLobbies(string keepLobbyId)
        {
            Task<List<TitanOrbitLobbyService.LobbySummary>> queryTask =
                TitanOrbitLobbyService.QueryOpenLobbiesAsync(latestOnly: true, count: 20);
            while (!queryTask.IsCompleted)
                yield return null;

            if (queryTask.IsFaulted || queryTask.Result == null)
                yield break;

            foreach (var summary in queryTask.Result)
            {
                if (summary == null || !summary.IsDedicatedServer)
                    continue;
                if (string.Equals(summary.LobbyId, keepLobbyId, StringComparison.Ordinal))
                    continue;

                Task closeTask = CloseLobbyForNewJoinersAsync(summary.LobbyId, "superseded_by_new_boot");
                while (!closeTask.IsCompleted)
                    yield return null;
            }
        }

        IEnumerator MaintainDedicatedServerGoInGame()
        {
            int lastConnectionCount = -1;
            float lastPeriodicLog = 0f;
            while (true)
            {
                var server = ClientServerBootstrap.ServerWorld;
                if (server != null && server.IsCreated)
                {
                    // Server world is ticked once per frame from Update() on UNITY_SERVER builds.
                    RequestGoInGame(server);
                    var em = server.EntityManager;
                    int connections = em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount();
                    int withNetworkId = em.CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkId))
                        .CalculateEntityCount();
                    int inGame = em.CreateEntityQuery(typeof(NetworkStreamInGame)).CalculateEntityCount();
                    bool shouldLog = connections != lastConnectionCount ||
                                     (connections > 0 && Time.realtimeSinceStartup - lastPeriodicLog >= 10f);
                    if (shouldLog)
                    {
                        lastConnectionCount = connections;
                        if (connections > 0)
                            lastPeriodicLog = Time.realtimeSinceStartup;
                        string line = "Server connections=" + connections + " withNetworkId=" + withNetworkId +
                                      " inGame=" + inGame + " listening=" + IsServerWorldListening(server);
                        DedicatedServerFileLog.Append("netcode", line);
                        Debug.Log("[TitanOrbitSessionManager] " + line);
                    }
                }

                yield return null;
            }
        }

        static bool HasZombieRelayConnection(World client)
        {
            if (client == null || !client.IsCreated)
                return false;
            var em = client.EntityManager;
            int connections = em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount();
            int withNetworkId = em.CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkId))
                .CalculateEntityCount();
            return connections > 0 && withNetworkId == 0;
        }

        IEnumerator ClientConnectWatch(float timeoutSeconds, bool dedicatedJoin = false)
        {
            float started = Time.realtimeSinceStartup;
            float deadline = started + timeoutSeconds;
            float lastDiag = 0f;
            const float zombieFailSeconds = 20f;
            var client = ClientServerBootstrap.ClientWorld;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (client != null && client.IsCreated)
                {
                    client.Update();

                    // Local/loopback: manual in-game. Dedicated uses TitanOrbitGoInGameClientSystem RPC handshake.
                    if (!dedicatedJoin && HasClientConnection(client))
                        RequestGoInGame(client);

                    if (dedicatedJoin && Time.realtimeSinceStartup - lastDiag >= 5f)
                    {
                        lastDiag = Time.realtimeSinceStartup;
                        LogClientConnectDiagnostics(client);
                    }

                    if (dedicatedJoin && Time.realtimeSinceStartup - started >= zombieFailSeconds &&
                        HasZombieRelayConnection(client))
                    {
                        LastStatusMessage =
                            "Could not reach dedicated server — tap Refresh (server may have restarted), then join again.";
                        Debug.LogError("[TitanOrbitSessionManager] Client stuck on pending Relay connection (no NetworkId).");
                        LogClientConnectDiagnostics(client);
                        StartCoroutine(ResetDedicatedClientSessionAfterTimeoutCoroutine());
                        yield break;
                    }

                    if (IsClientGameplayReady(client))
                    {
                        IsInGame = true;
                        LastStatusMessage = dedicatedJoin ? "Connected — choose a team." : LastStatusMessage;
                        Debug.Log("[TitanOrbitSessionManager] Client in-game" +
                                  (dedicatedJoin ? " (dedicated Relay)." : ".") +
                                  " relay=" + TitanOrbitRelayState.TryGetClientRelay(out _));
                        yield break;
                    }
                }

                yield return null;
            }

            if (dedicatedJoin && client != null && client.IsCreated)
                LogClientConnectDiagnostics(client);

            if (dedicatedJoin)
            {
                LastStatusMessage = "Connection timed out — dedicated server may be offline or needs redeploy.";
                StartCoroutine(ResetDedicatedClientSessionAfterTimeoutCoroutine());
            }

            Debug.LogError("[TitanOrbitSessionManager] Client connect watchdog timed out.");
        }

        static void LogClientConnectDiagnostics(World client)
        {
            if (client == null || !client.IsCreated)
                return;

            var em = client.EntityManager;
            int connections = em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount();
            int withNetworkId = em.CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkId))
                .CalculateEntityCount();
            int inGame = em.CreateEntityQuery(typeof(NetworkStreamInGame)).CalculateEntityCount();
            Debug.Log("[TitanOrbitSessionManager] Client connect diag: connections=" + connections +
                      " withNetworkId=" + withNetworkId + " inGame=" + inGame +
                      " relay=" + TitanOrbitRelayState.TryGetClientRelay(out _));
        }

        IEnumerator ResetDedicatedClientSessionAfterTimeoutCoroutine()
        {
            Task resetTask = ResetDedicatedClientSessionAsync(LastStatusMessage);
            while (!resetTask.IsCompleted)
                yield return null;
        }

        static bool HasNetworkStreamInGame(World world)
        {
            if (world == null || !world.IsCreated) return false;
            return world.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame)).CalculateEntityCount() > 0;
        }

        static void RequestGoInGame(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            using var connections = em.CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkId))
                .ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; i++)
            {
                if (!em.HasComponent<NetworkStreamInGame>(connections[i]))
                    em.AddComponent<NetworkStreamInGame>(connections[i]);
            }
        }

        static void ClearNetworkStreamInGame(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            using var connections = em.CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkStreamInGame))
                .ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; i++)
                em.RemoveComponent<NetworkStreamInGame>(connections[i]);
        }

        static bool IsServerWorldListening(World world)
        {
            if (world == null || !world.IsCreated) return false;
            using var query = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            if (!query.TryGetSingleton<NetworkStreamDriver>(out var driver)) return false;
            ref var store = ref driver.DriverStore;
            for (int i = store.FirstDriver; i < store.LastDriver; ++i)
            {
                if (store.GetDriverInstanceRO(i).driver.Listening)
                    return true;
            }
            return false;
        }

        static void ListenServer(World world, ushort port)
        {
            var driver = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingletonRW<NetworkStreamDriver>();
            if (TitanOrbitRelayState.TryGetServerRelay(out var relay))
            {
                if (!TitanOrbitRelayUtility.IsRelayEndpointValid(relay))
                {
                    Debug.LogError("[TitanOrbitSessionManager] Cannot listen: Relay endpoint invalid.");
                    DedicatedServerFileLog.Append("netcode", "Relay listen skipped — endpoint invalid.");
                    return;
                }

                // UTP Relay host: bind AnyIpv4 (relay params are on the driver), not relay.Endpoint.
                // See com.unity.transport RelayPing PingServerBehaviour.
                bool listenOk = driver.ValueRW.Listen(NetworkEndpoint.AnyIpv4);
                LogServerRelayListenDiagnostics(world, relay, listenOk);
                if (!listenOk)
                {
                    Debug.LogError("[TitanOrbitSessionManager] Relay Listen(AnyIpv4) failed. relayEndpoint=" + relay.Endpoint);
                    DedicatedServerFileLog.Append("netcode", "Relay Listen(AnyIpv4) failed relayEndpoint=" + relay.Endpoint);
                }
            }
            else if (!IsServerWorldListening(world))
            {
                driver.ValueRW.Listen(NetworkEndpoint.AnyIpv4.WithPort(port));
            }

            TickServerWorld(world);
        }

        static void LogServerRelayListenDiagnostics(World world, RelayServerData relay, bool listenOk)
        {
            if (world == null || !world.IsCreated)
                return;

            using var query = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            if (!query.TryGetSingleton<NetworkStreamDriver>(out var driver))
                return;

            ref var store = ref driver.DriverStore;
            int listeningDrivers = 0;
            for (int i = store.FirstDriver; i < store.LastDriver; ++i)
            {
                if (store.GetDriverInstanceRO(i).driver.Listening)
                    listeningDrivers++;
            }

            string line = "Relay listen bind=AnyIpv4 ok=" + listenOk +
                          " relayEndpoint=" + relay.Endpoint +
                          " drivers=" + (store.LastDriver - store.FirstDriver) +
                          " listeningDrivers=" + listeningDrivers;
            DedicatedServerFileLog.Append("netcode", line);
            Debug.Log("[TitanOrbitSessionManager] " + line);
        }

        static void ConnectLocalClient(ushort port)
        {
            var clientWorld = ClientServerBootstrap.ClientWorld;
            if (clientWorld == null || !clientWorld.IsCreated) return;
            var em = clientWorld.EntityManager;
            if (em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0)
                return;
            var driver = em.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingletonRW<NetworkStreamDriver>();
            driver.ValueRW.Connect(em, NetworkEndpoint.LoopbackIpv4.WithPort(port));
        }

        static void ConnectRelayClient(World world)
        {
            if (!TitanOrbitRelayState.TryGetClientRelay(out var relay))
                return;
            var em = world.EntityManager;
            if (em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0)
                return;
            var driver = em.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingletonRW<NetworkStreamDriver>();
            driver.ValueRW.Connect(world.EntityManager, relay.Endpoint);
        }

        static void ResetServerDriverIfNeeded()
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated) return;
            var driverEntity = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            if (!driverEntity.TryGetSingletonEntity<NetworkStreamDriver>(out var entity)) return;
            var driver = world.EntityManager.GetComponentData<NetworkStreamDriver>(entity);
            var store = new NetworkDriverStore();
            var netDebug = world.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
            new TitanOrbitRelayDriverConstructor().CreateServerDriver(world, ref store, netDebug);
            driver.ResetDriverStore(world.Unmanaged, ref store);
        }

        static void ResetClientDriverIfNeeded()
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated) return;
            var driverEntity = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            if (!driverEntity.TryGetSingletonEntity<NetworkStreamDriver>(out var entity)) return;
            var driver = world.EntityManager.GetComponentData<NetworkStreamDriver>(entity);
            var store = new NetworkDriverStore();
            var netDebug = world.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
            new TitanOrbitRelayDriverConstructor().CreateClientDriver(world, ref store, netDebug);
            driver.ResetDriverStore(world.Unmanaged, ref store);
        }

        public void RequestResumeExistingShip()
        {
            if (!SendRejoinShipRpc<ResumeExistingShipCommand>())
                LastStatusMessage = "Could not resume your ship.";
        }

        public void RequestAbandonShipForRejoin()
        {
            if (!SendRejoinShipRpc<AbandonShipForRejoinCommand>())
                LastStatusMessage = "Could not abandon your saved ship.";
        }

        bool SendRejoinShipRpc<T>() where T : unmanaged, IRpcCommand
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogError("[TitanOrbitSessionManager] Rejoin RPC failed: ClientWorld missing.");
                return false;
            }

            if (!IsClientGameplayReady(world))
            {
                Debug.LogError("[TitanOrbitSessionManager] Rejoin RPC failed: client not in-game.");
                return false;
            }

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, default(T));
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            Debug.Log("[TitanOrbitSessionManager] Sent rejoin RPC " + typeof(T).Name + ".");
            return true;
        }

        public void RequestTeam(TitanOrbit.Core.TeamId team)
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogError("[TitanOrbitSessionManager] RequestTeam failed: ClientWorld is missing.");
                return;
            }

            if (!IsClientGameplayReady(world))
            {
                Debug.LogError("[TitanOrbitSessionManager] RequestTeam failed: client is not in-game yet. Wait for 'Client in-game' in the console.");
                return;
            }

            if (!HasClientConnection(world))
            {
                Debug.LogError("[TitanOrbitSessionManager] RequestTeam failed: no network connection on ClientWorld.");
                return;
            }

            var em = world.EntityManager;
            int networkId = GetLocalNetworkId(world);
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new RequestTeamCommand
            {
                NetworkId = networkId,
                RequestedTeam = (byte)team,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            Debug.Log($"[TitanOrbitSessionManager] RequestTeam {team} (networkId={networkId}).");
        }

        static int GetLocalNetworkId(World world)
        {
            var em = world.EntityManager;
            using var inGame = em.CreateEntityQuery(
                    typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            if (inGame.Length > 0)
                return inGame[0].Value;

            using var ids = em.CreateEntityQuery(typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : 0;
        }
    }
}
