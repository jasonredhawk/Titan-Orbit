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
using Unity.Transforms;
using Unity.Scenes;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Session orchestration MonoBehaviour — replaces legacy NGO NetworkGameManager and
    /// DedicatedMatchServerBootstrap. Owns client/server world lifecycle: local LAN play, MPPM
    /// multi-editor testing, Unity Relay + Lobby dedicated join, headless dedicated boot, and
    /// gameplay RPC helpers (team pick, rejoin). DontDestroyOnLoad singleton accessed via Instance.
    /// Paired with TitanOrbitBootstrap, TitanOrbitDedicatedServerAutoBoot, and lobby services.
    /// </summary>
    public class TitanOrbitSessionManager : MonoBehaviour
    {
        /// <summary>[STANDARD] Global singleton for UI and bootstrap to reach session APIs.</summary>
        public static TitanOrbitSessionManager Instance { get; private set; }

        /// <summary>[NETCODE] Set by external boot when a non-editor build should auto-start LAN host.</summary>
        public static bool PendingLanHost { get; set; }

        const ushort DefaultServerPort = 7777;

        /// <summary>[TITAN-ORBIT] Max players advertised for lobby and server capacity.</summary>
        [SerializeField] int maxPlayers = 60;

        /// <summary>[NETCODE] UDP port for local LAN listen and MPPM host.</summary>
        [SerializeField] ushort serverPort = DefaultServerPort;

        /// <summary>[NETCODE] Active Unity Lobby id after dedicated join (empty when local only).</summary>
        string _activeLobbyId;

        /// <summary>[NETCODE] Last Relay join code attempted — compare with Docker "Dedicated server live. Relay=" log.</summary>
        string _lastRelayJoinCodeAttempt;

        /// <summary>[UNITY] Coroutine watching client connect to dedicated Relay host.</summary>
        Coroutine _connectWatch;

        /// <summary>[UNITY] MPPM additional editor instance LAN connect coroutine.</summary>
        Coroutine _mppmLanConnectCoroutine;

        /// <summary>[NETCODE] Parsed dedicated server command-line config when headless.</summary>
        TitanOrbitServerCommandLine _serverConfig;

        /// <summary>[TITAN-ORBIT] Guard against overlapping RecreateDedicatedMatchAsync calls.</summary>
        bool _recreateDedicatedMatchInProgress;

        /// <summary>[NETCODE] Consecutive UGS heartbeat failures; triggers lobby recreate when empty.</summary>
        int _consecutiveHeartbeatFailures;

        const int HeartbeatFailureRecreateThreshold = 3;

        /// <summary>[NETCODE] True after client reaches NetworkStreamInGame (playable).</summary>
        public bool IsInGame { get; private set; }

        /// <summary>[HYBRID] Last user-facing status string for lobby/menu UI.</summary>
        public string LastStatusMessage { get; private set; }

        /// <summary>[NETCODE] Active lobby id for leave/refresh operations.</summary>
        public string CurrentLobbyId => _activeLobbyId;

        /// <summary>[NETCODE] True while editor/client is connected (or connecting) to remote dedicated host via Relay.</summary>
        public static bool IsDedicatedOnlineClient { get; private set; }

        /// <summary>[NETCODE] Dedicated Relay join started but NetCode has not reached in-game yet.</summary>
        public static bool IsDedicatedJoinConnecting =>
            IsDedicatedOnlineClient && Instance != null && !Instance.IsInGame;

        /// <summary>
        /// Dedicated Relay join, or Local Host boot coroutine still running.
        /// Keeps the loading overlay up before NetworkId / recipe exist.
        /// </summary>
        public static bool IsJoinConnecting =>
            IsDedicatedJoinConnecting ||
            (Instance != null && Instance._localBootRunning);

        /// <summary>[UNITY] Editor-only: local ServerWorld sim suspended while joining dedicated online.</summary>
        static bool s_EditorLocalServerSuspendedForOnline;

        /// <summary>
        /// [UNITY] Registers singleton, DontDestroyOnLoad. Destroys duplicate instances.
        /// </summary>
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

        /// <summary>
        /// [UNITY] Entry point: headless dedicated boot, MPPM player idle, or editor client ready.
        /// Suspends local ServerWorld on editor menu to avoid sim finishing before Local play.
        /// </summary>
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
            // Keep ServerWorld idle on the menu so map/match sim does not run (and finish) before Local play/host.
            SuspendEditorLocalServerUntilLocalPlay();
#endif
        }

#if UNITY_SERVER
        /// <summary>
        /// Intentionally empty — do <b>not</b> manually tick ServerWorld here.
        /// </summary>
        /// <remarks>
        /// [NETCODE] <c>ClientServerBootstrap.CreateServerWorld</c> calls
        /// <c>ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop</c>, so the player loop
        /// already runs <c>ServerWorld.Update</c> every frame (Editor and headless).
        /// <para>
        /// basics35 (GCE Relay): this method used to call <see cref="TickServerWorld"/> whenever
        /// no ClientWorld existed. That <b>double-updated</b> sim (~2× server ticks vs wall clock).
        /// Both Relay clients stuck at <c>cmdAge≈18–21</c> / hard snaps even with client MaxSteps=8.
        /// Boot coroutines may still call <see cref="TickServerWorld"/> (frame-gated).
        /// </para>
        /// </remarks>
        void Update()
        {
            // Player loop owns ServerWorld — no TickServerWorld() here.
        }
#else
        /// <summary>
        /// Client: keep dedicated Join→GCE on VSync after scene platform defaults.
        /// VSync locks present to the monitor refresh and prevents tearing / “vertical strip”
        /// artifacts while the camera pans over asteroids (same panel can look fine in Starblast
        /// because the browser compositor typically presents synced).
        /// </summary>
        void Update()
        {
            // --- Dedicated online frame pace ---
            // [UNITY] vSyncCount = 1 → wait for vertical blank before presenting. When VSync is
            // on, Application.targetFrameRate is ignored; the panel Hz becomes the present rate.
            // [TITAN-ORBIT] We used to force vSync off + 60 FPS for join catch-up (basics55). That
            // caused visible tear bands during flight on high-Hz monitors. Re-assert VSync every
            // frame so a later Quality/UI change cannot leave tearing on.
            if (!IsDedicatedOnlineClient)
                return;
            if (QualitySettings.vSyncCount != 1)
                QualitySettings.vSyncCount = 1;
            // Fallback soft cap only if something clears VSync — harmless while vSyncCount > 0.
            if (Application.targetFrameRate != 60)
                Application.targetFrameRate = 60;

            WatchDedicatedClientDrop();
        }
#endif

        /// <summary>
        /// Dedicated client lost Relay or gameplay-ready. Reset the driver and map latch so Join
        /// works on the same page. A browser refresh used to be the only way past the stuck socket.
        /// </summary>
        void WatchDedicatedClientDrop()
        {
            bool relayDead = TitanOrbitRelayAllocationSignal.ConsumeClientInvalid();
            if (_returningToMenu || _unexpectedDisconnectResetRunning)
                return;

            if (relayDead && IsDedicatedOnlineClient)
            {
                BeginUnexpectedDedicatedClientReset(
                    "Connection to the match server dropped. Join again.");
                return;
            }

            // _connectWatch stays non-null after the coroutine ends, so it is not a "still joining" flag.
            if (!IsDedicatedOnlineClient || !IsInGame)
            {
                _dedicatedWasGameplayReady = false;
                _dedicatedMissingReadyFrames = 0;
                return;
            }

            var client = ClientServerBootstrap.ClientWorld;
            bool ready = IsClientGameplayReady(client);
            if (ready)
            {
                _dedicatedWasGameplayReady = true;
                _dedicatedMissingReadyFrames = 0;
                return;
            }

            if (!_dedicatedWasGameplayReady || !IsInGame)
                return;

            _dedicatedMissingReadyFrames++;
            if (_dedicatedMissingReadyFrames < 90)
                return;

            BeginUnexpectedDedicatedClientReset(
                "Connection lost. Join the match again.");
        }

        void BeginUnexpectedDedicatedClientReset(string reason)
        {
            if (_connectWatch != null)
            {
                StopCoroutine(_connectWatch);
                _connectWatch = null;
            }

            _dedicatedWasGameplayReady = false;
            _dedicatedMissingReadyFrames = 0;
            _unexpectedDisconnectResetRunning = true;
            LastStatusMessage = reason;
            Debug.LogWarning("[TitanOrbitSessionManager] " + reason);
            StartCoroutine(ResetAfterUnexpectedDisconnect());
        }

        IEnumerator ResetAfterUnexpectedDisconnect()
        {
            Task reset = ResetDedicatedClientSessionAsync(LastStatusMessage);
            while (!reset.IsCompleted)
                yield return null;
            _unexpectedDisconnectResetRunning = false;
        }

        /// <summary>
        /// Clears client map latches after a drop or before a new dedicated Join.
        /// Presentation applies this on the next UI tick via <c>EcsGameBridge.ConsumeSessionLeave</c>.
        /// </summary>
        static void NotifyClientMatchSessionEnded()
        {
#if !UNITY_SERVER
            MapSessionMetaCache.Clear();
            ClientMapHydrateCache.NotifySessionLeave();
#endif
        }

        /// <summary>Stops the editor's local ServerWorld sim until local play/host/client is started.</summary>
        public static void SuspendEditorLocalServerUntilLocalPlay()
        {
#if UNITY_EDITOR
            if (ShouldRunHeadlessServerBoot())
                return;

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            // Already fully parked (menu idle after a prior suspend).
            if (server.QuitUpdate)
            {
                s_EditorLocalServerSuspendedForOnline = true;
                return;
            }

            var simulation = server.GetExistingSystemManaged<SimulationSystemGroup>();
            if (simulation != null && simulation.Enabled)
                simulation.Enabled = false;

            // --- Park the whole ServerWorld, not only SimulationSystemGroup ---
            // [NETCODE] Leaving Init running while sim is off floods
            // "Expected server to always update once per frame when in sleep mode" (NetcodeServerRateManager
            // still ticks in InitializationSystemGroup). QuitUpdate stops all groups until Local Host.
            server.QuitUpdate = true;
            s_EditorLocalServerSuspendedForOnline = true;
            Debug.Log("[TitanOrbitSessionManager] Suspended local ServerWorld (QuitUpdate) until Local play/host/client.");
#endif
        }

        /// <summary>
        /// Disposes the Editor's idle local ServerWorld so Join→GCE is truly client-only.
        /// </summary>
        /// <remarks>
        /// basics38: suspending <see cref="SimulationSystemGroup"/> left
        /// <c>ClientServerBootstrap.HasServerWorld == true</c> during Relay play. Aggregates were
        /// all <c>hasServer=true,relay=true</c>, with repeated catch-up storms
        /// (<c>cmdAge</c> 50–212, <c>maxDelta</c> 15–21, <c>fps</c> 1–4) then rubber-band snaps.
        /// Disposing removes the dual-world player-loop cost and makes HasServerWorld false.
        /// </remarks>
        static void DisposeEditorServerWorldForDedicatedJoin()
        {
#if UNITY_EDITOR
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            DisposeEditorLocalServerWorld("dedicated Relay join (client-only)");
#endif
        }

        /// <summary>
        /// Disposes the Editor ServerWorld. The next Local play creates a new one, so map
        /// generation runs again. Used for dedicated-join client-only and for a finished match.
        /// </summary>
        /// <param name="reason">Logged reason.</param>
        static void DisposeEditorLocalServerWorld(string reason)
        {
#if UNITY_EDITOR
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            Debug.Log("[TitanOrbitSessionManager] Disposing local ServerWorld (" + reason + ").");
            // Next play is a new map. Drop match-only ship saves with the world.
            MatchPlayerShipStore.Clear();
            server.Dispose();
            s_EditorLocalServerSuspendedForOnline = false;
            // The finished-match latch belongs to this world. A new ServerWorld starts clean.
            MatchEndServerSignal.Clear();
#endif
        }

        /// <summary>
        /// Re-enables or recreates ServerWorld for LAN host/play after menu suspend or dedicated-join dispose.
        /// </summary>
        static void ResumeEditorLocalServerForLocalPlay()
        {
#if UNITY_EDITOR
            // MPPM Player 2+ joins the main Editor host. Recreating ServerWorld here generates a
            // second map in this process; RequestTeam / visuals then talk to the wrong world.
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
            {
                Debug.Log("[TitanOrbitSessionManager] MPPM clone — skipped ServerWorld recreate (join the main Editor host).");
                return;
            }

            // basics41: a Local Host recreate ran while Relay join was active (Recreated → Disposed
            // ~4s later, ServerWorld wall-clock probe during dedicated play). Never rebuild server
            // while this process is a dedicated online / Relay client.
            if (IsDedicatedOnlineClient || TitanOrbitRelayState.HasClientRelay)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] Skipped ServerWorld recreate — dedicated Relay client active.");
                return;
            }

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
            {
                // [NETCODE] Dedicated Join disposed ServerWorld — Local Host needs it again.
                // WARNING: late CreateServerWorld does NOT auto-stream scene SubScenes. Prefer
                // TitanOrbitBootstrap creating ServerWorld at Play enter. If we must recreate,
                // map gen waits for GamePrefabs; BootLanHost waits for them before Listen.
                ClientServerBootstrap.CreateServerWorld("ServerWorld");
                TryLoadGameplaySubScenesIntoWorld(ClientServerBootstrap.ServerWorld);
                s_EditorLocalServerSuspendedForOnline = false;
                Debug.Log("[TitanOrbitSessionManager] Recreated local ServerWorld for Local Host play.");
                return;
            }

            // --- Unpark world + re-enable sim (menu used QuitUpdate while idle) ---
            // [NETCODE] QuitUpdate was set in SuspendEditorLocalServerUntilLocalPlay to silence
            // sleep-mode rate-manager spam; Local Host must clear it before Listen / map gen.
            if (server.QuitUpdate)
                server.QuitUpdate = false;

            var simulation = server.GetExistingSystemManaged<SimulationSystemGroup>();
            if (simulation != null && !simulation.Enabled)
            {
                simulation.Enabled = true;
                Debug.Log("[TitanOrbitSessionManager] Resumed local ServerWorld simulation for local play.");
            }

            s_EditorLocalServerSuspendedForOnline = false;
#endif
        }

        /// <summary>
        /// Streams each scene SubScene into a late-created world (AutoLoad only hits worlds that
        /// existed when the parent scene loaded).
        /// </summary>
        static void TryLoadGameplaySubScenesIntoWorld(World world)
        {
#if UNITY_EDITOR
            if (world == null || !world.IsCreated)
                return;

            var subScenes = UnityEngine.Object.FindObjectsByType<SubScene>(FindObjectsSortMode.None);
            if (subScenes == null || subScenes.Length == 0)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] No SubScene in loaded scenes — ServerWorld may lack GamePrefabs.");
                return;
            }

            for (int i = 0; i < subScenes.Length; i++)
            {
                var sub = subScenes[i];
                if (sub == null || !sub.SceneGUID.IsValid)
                    continue;

                SceneSystem.LoadSceneAsync(
                    world.Unmanaged,
                    sub.SceneGUID,
                    new SceneSystem.LoadParameters
                    {
                        Flags = SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn,
                        AutoLoad = true,
                    });
                Debug.Log("[TitanOrbitSessionManager] Loading SubScene '" + sub.gameObject.name +
                          "' into " + world.Name + " for Local Host map/prefabs.");
            }
#endif
        }

        /// <summary>Clear dedicated-session leftovers before loopback LAN connect.</summary>
        void BeginLocalLanSession(bool resetTeamFlow)
        {
            IsDedicatedOnlineClient = false;
            IsInGame = false;
            if (resetTeamFlow)
                ClientTeamFlowState.Reset();

            if (_connectWatch != null)
            {
                StopCoroutine(_connectWatch);
                _connectWatch = null;
            }

            ResumeEditorLocalServerForLocalPlay();
            TitanOrbitRelayState.Clear();
        }

        /// <summary>
        /// [NETCODE] Clears in-game flags and connections on client/server worlds before LAN reconnect.
        /// </summary>
        IEnumerator PrepareWorldsForLocalLanConnect(bool resetTeamFlow, bool resetNetworkDrivers)
        {
            BeginLocalLanSession(resetTeamFlow);

            var client = ClientServerBootstrap.ClientWorld;
            var server = ClientServerBootstrap.ServerWorld;

            if (client != null && client.IsCreated)
            {
                ClearNetworkStreamInGame(client);
                yield return ClearNetworkConnections(client);
            }

            // MPPM clones must stay client-only. A leftover ServerWorld from a previous Local
            // client click would generate a second map and steal RequestTeam from the host.
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
            {
                DisposeMppmCloneServerWorldIfPresent();
                if (resetNetworkDrivers)
                    ResetClientDriverIfNeeded();
                yield break;
            }

            if (server != null && server.IsCreated)
            {
                ClearNetworkStreamInGame(server);
                yield return ClearNetworkConnections(server);
            }

            if (resetNetworkDrivers)
            {
                ResetClientDriverIfNeeded();
                ResetServerDriverIfNeeded();
            }
        }

        /// <summary>Drops a clone-local ServerWorld so Player 2 cannot host a second LAN map.</summary>
        static void DisposeMppmCloneServerWorldIfPresent()
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            Debug.Log("[TitanOrbitSessionManager] MPPM clone — disposing leftover ServerWorld (this window is client-only).");
            server.Dispose();
        }

        /// <summary>[UNITY_SERVER] True when this process should run headless dedicated boot (no client UI).</summary>
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

        /// <summary>CLI --dedicated or equivalent flag present.</summary>
        static bool HasExplicitDedicatedServerArg() => TitanOrbitServerCommandLine.HasDedicatedFlag();

        /// <summary>Whether headless build should auto-create Relay allocation on boot.</summary>
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

        /// <summary>[NETCODE] MPPM server virtual player — listen on bootstrap port and mark in-game.</summary>
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

        /// <summary>
        /// True while <see cref="ReturnToMainMenuAsync"/> is disconnecting.
        /// Blocks a second leave and a overlapping Local play boot.
        /// </summary>
        bool _returningToMenu;

        /// <summary>True while an unexpected Relay drop is resetting the client driver.</summary>
        bool _unexpectedDisconnectResetRunning;

        /// <summary>Saw a live dedicated connection this session — used to notice a later drop.</summary>
        bool _dedicatedWasGameplayReady;

        /// <summary>Frames without gameplay-ready after it had been true.</summary>
        int _dedicatedMissingReadyFrames;

        /// <summary>True while a dead Relay allocation is being replaced without wiping the match.</summary>
        bool _relayRebindInProgress;

        /// <summary>
        /// Bumped when a new dedicated Join starts. A leave that began earlier must not
        /// clear Relay state after that — it would pull the socket down again.
        /// </summary>
        int _clientSessionEpoch;

        /// <summary>Polls client world until a NetworkId exists — LAN host/client bootstrap.</summary>
        IEnumerator MaintainClientSession()
        {
            float deadline = Time.realtimeSinceStartup + 45f;
            while (Time.realtimeSinceStartup < deadline)
            {
                World client = ClientServerBootstrap.ClientWorld;
                if (client != null && client.IsCreated)
                {
                    if (!HasClientConnection(client))
                        ConnectLocalClient(serverPort);
                }

                if (HasClientNetworkId(client))
                {
                    IsInGame = true;
                    LastStatusMessage = TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance()
                        ? "Connected to host — building map..."
                        : "Connected — building map...";
                    Debug.Log("[TitanOrbitSessionManager] Client connected (NetworkId). Seed hydrate runs before InGame.");
                    yield break;
                }

                yield return null;
            }

            World timedOutClient = ClientServerBootstrap.ClientWorld;
            World timedOutServer = ClientServerBootstrap.ServerWorld;
            Debug.LogWarning("[TitanOrbitSessionManager] Client never received NetworkId. client=" +
                             (timedOutClient != null && timedOutClient.IsCreated ? timedOutClient.Name : "missing") +
                             " server=" + (timedOutServer != null && timedOutServer.IsCreated ? timedOutServer.Name : "missing") +
                             ". Press Play on the main Editor Game view, or disable the MPPM Server virtual player.");
        }

        /// <summary>
        /// [HYBRID] Menu "Local play" — boots LAN host + local client in one coroutine (editor/MPPM).
        /// </summary>
        public void StartLocalPlay()
        {
            LastStatusMessage = "Starting local play...";
            // Block a Play click that races an Escape leave (worlds are mid-disconnect).
            if (_returningToMenu || _localBootRunning || HasClientInGame() || IsInGame)
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
            if (_returningToMenu || _localBootRunning)
                return false;

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
            {
                LastStatusMessage = "ServerWorld missing. Run Titan Orbit > Configure Multiplayer For Local Play.";
                Debug.LogError("[TitanOrbitSessionManager] Local host requires Client+Server PlayMode (ServerWorld missing).");
                return false;
            }

            StartCoroutine(BootLanHostOnly());
            return true;
        }

        IEnumerator BootLanHostOnly()
        {
            _localBootRunning = true;
            try
            {
                yield return PrepareWorldsForLocalLanConnect(resetTeamFlow: false, resetNetworkDrivers: true);

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

                ListenLocalLanServer(server, serverPort);

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
            if (_returningToMenu || _localBootRunning || HasClientInGame() || IsInGame)
                return false;
            StartCoroutine(BootLanClient(address));
            return true;
        }

        IEnumerator BootLanClient(string address)
        {
            _localBootRunning = true;
            try
            {
                yield return PrepareWorldsForLocalLanConnect(resetTeamFlow: true, resetNetworkDrivers: true);

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

                float deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (HasClientNetworkId(client))
                    {
                        IsInGame = true;
                        LastStatusMessage = "Connected — building map...";
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

        /// <summary>
        /// [NETCODE] Quick-join latest browsable dedicated lobby via Unity Lobby + Relay.
        /// </summary>
        /// <returns>True if join coroutine started successfully.</returns>
        public async Task<bool> QuickJoinDedicatedAsync()
        {
            LastStatusMessage = "Finding a dedicated match...";
            try
            {
                Lobby lobby = await TitanOrbitLobbyService.QuickJoinLatestDedicatedLobbyAsync();
                if (lobby == null)
                {
                    var listed = await TitanOrbitLobbyService.QueryBrowsableDedicatedLobbiesAsync(15, skipEmptyStabilization: true);
                    LastStatusMessage = listed.Count > 0
                        ? "No joinable dedicated match — open Join game, Refresh, then select a live match."
                        : "No dedicated match found.";
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
            yield return PrepareWorldsForLocalLanConnect(resetTeamFlow: true, resetNetworkDrivers: true);

            LastStatusMessage = "Waiting for NetCode worlds + map prefabs...";
            float readyDeadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < readyDeadline)
            {
                var clientWorld = ClientServerBootstrap.ClientWorld;
                var serverWorld = ClientServerBootstrap.ServerWorld;
                // [NETCODE] Drivers + GamePrefabs (SubScene) before Listen — else map gen never starts.
                bool clientReady = clientWorld != null && clientWorld.IsCreated &&
                    clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
                bool serverReady = serverWorld != null && serverWorld.IsCreated &&
                    serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
                bool prefabsReady = serverWorld != null && serverWorld.IsCreated &&
                    serverWorld.EntityManager.CreateEntityQuery(typeof(GamePrefabs)).CalculateEntityCount() > 0;
                if (clientReady && serverReady && prefabsReady)
                    break;
                yield return null;
            }

            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
            {
                LastStatusMessage = "ClientWorld missing. Use the main Editor Game view.";
                Debug.LogError("[TitanOrbitSessionManager] ClientWorld required to connect.");
                yield break;
            }

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
            {
                LastStatusMessage = "ServerWorld failed to create for Local Host.";
                Debug.LogError("[TitanOrbitSessionManager] BootLanHost: ServerWorld missing after ResumeEditorLocalServerForLocalPlay.");
                yield break;
            }

            if (server.EntityManager.CreateEntityQuery(typeof(GamePrefabs)).CalculateEntityCount() == 0)
            {
                LastStatusMessage = "Server GamePrefabs missing (SubScene not loaded into ServerWorld).";
                Debug.LogError("[TitanOrbitSessionManager] BootLanHost: GamePrefabs missing on ServerWorld — map cannot generate. " +
                               "Stop Play, ensure GameplaySubScene AutoLoad, then Play again (bootstrap creates ServerWorld at enter).");
                yield break;
            }

            LastStatusMessage = "Starting local host...";
            // --- Display rate vs sim rate ---
            // [UNITY] Ask for 60 FPS (matched to sim Hz). basics14 (uncapped + vSync off) did not
            // raise Editor Local Host FPS (~26) and worsened spikes — CPU-bound dual-world load.
            if (Application.targetFrameRate != TitanOrbitServerTickRateSystem.SimulationHz)
                Application.targetFrameRate = TitanOrbitServerTickRateSystem.SimulationHz;

            // --- Listen, then rebuild client drivers for IPC, then Connect ---
            // [NETCODE] RegisterClientDriver prefers IPC only when ServerWorld exists. Rebuild after
            // Listen so the client driver matches the in-process server (not stale UDP from earlier).
            // Connect must use server GetLocalEndPoint(IPC) — see ConnectLocalClient.
            ListenLocalLanServer(server, serverPort);
            ResetClientDriverIfNeeded();
            ConnectLocalClient(serverPort);

            float deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (HasClientNetworkId(client))
                {
                    RequestGoInGame(server);
                    IsInGame = true;
                    LastStatusMessage = "Connected — building map...";
                    Debug.Log("[TitanOrbitSessionManager] Local Client+Server connected (NetworkId). Hydrate before InGame.");
                    yield break;
                }

                if (HasLocalConnection(server, client))
                    RequestGoInGame(server);

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

        /// <summary>
        /// True when the client connection has a <see cref="NetworkId"/> (handshake finished).
        /// Does not require <see cref="NetworkStreamInGame"/> — that waits for seed hydrate.
        /// </summary>
        static bool HasClientNetworkId(World client)
        {
            if (client == null || !client.IsCreated)
                return false;
            return client.EntityManager
                .CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkId))
                .CalculateEntityCount() > 0;
        }

        static bool HasClientInGame()
        {
            return IsClientGameplayReady(ClientServerBootstrap.ClientWorld);
        }

        static World s_readyQueryWorld;
        static EntityQuery s_readyQuery;

        public static bool IsClientConnectionReady(World world)
        {
            if (world == null || !world.IsCreated)
                return false;

            // One query for the life of the world. A new CreateEntityQuery every call
            // (this runs several times a frame while flying) was never disposed, so the
            // WebGL heap climbed a few megabytes every second until Chrome aborted.
            if (s_readyQueryWorld != world)
            {
                if (s_readyQueryWorld != null && s_readyQueryWorld.IsCreated)
                    s_readyQuery.Dispose();
                s_readyQuery = world.EntityManager.CreateEntityQuery(
                    typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId));
                s_readyQueryWorld = world;
            }

            return s_readyQuery.CalculateEntityCount() > 0;
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

        public bool IsRecreateDedicatedMatchInProgress =>
            _recreateDedicatedMatchInProgress || _relayRebindInProgress;

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

        static bool s_LoggedWebGlPlayerLoopTick;

        /// <summary>
        /// Ticks ClientWorld on desktop. On WebGL the ClientWorld stays on the player loop, so
        /// this does not call <c>World.Update</c> (that would double-tick and was the old
        /// SafeUpdate bypass).
        /// </summary>
        static void TickClientWorld(World world)
        {
            if (world == null || !world.IsCreated)
                return;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!s_LoggedWebGlPlayerLoopTick)
            {
                s_LoggedWebGlPlayerLoopTick = true;
                Debug.Log("[WebGLClient] Player loop owns ClientWorld.Update.");
            }
#else
            world.Update();
#endif
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

        /// <summary>
        /// Tears down the current UGS lobby + Relay listen and publishes a fresh empty match on the
        /// same ServerWorld. Hard-gated: refuses when any NetCode player is still connected so an
        /// in-progress conquest map cannot be wiped by idle/self-heal/heartbeat paths.
        /// </summary>
        /// <param name="config">Dedicated CLI config (ports, idle seconds, IsLatest).</param>
        /// <param name="forceIsLatest">When true, the new lobby is published as IsLatest=1.</param>
        /// <returns>New lobby ids/timestamps, or null when skipped/failed.</returns>
        public async Task<DedicatedMatchRecreateResult> RecreateDedicatedMatchAsync(
            TitanOrbitServerCommandLine config,
            bool forceIsLatest = false)
        {
            // --- RecreateDedicatedMatchAsync ---
            if (_recreateDedicatedMatchInProgress)
                return null;

            // [TITAN-ORBIT] A won match must not be republished. The host spawns a new
            // process and exits; recreating here would put the finished map back in Join Game.
            if (MatchEndServerSignal.IsMatchWon)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] Recreate skipped — match already won.");
                DedicatedServerFileLog.Append("match", "Recreate skipped; match already won");
                return null;
            }

            // [TITAN-ORBIT] Occupied match — never wipe ships/map or delete the live lobby.
            int connectedPlayers = GetServerConnectedPlayerCount();
            if (connectedPlayers > 0)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] Recreate skipped — " + connectedPlayers +
                                 " player(s) still connected.");
                DedicatedServerFileLog.Append("self_heal",
                    "Recreate skipped; players still connected count=" + connectedPlayers);
                return null;
            }

            _recreateDedicatedMatchInProgress = true;
            // [TITAN-ORBIT] Pause hang watchdog — Relay/UGS awaits can stall Update stamps.
            TitanOrbitDedicatedServerHost.SetHangWatchdogPaused(true);
            string oldLobbyId = _activeLobbyId;
            bool publishAsLatest = forceIsLatest || config.IsLatest;
            try
            {
                var prep = await PrepareDedicatedRelayAsync(config);
                if (prep == null)
                {
                    DedicatedServerFileLog.Append("lobby", "Match recreate FAILED: PrepareDedicatedRelay returned null");
                    return null;
                }

                prep.IsLatest = publishAsLatest;

                var serverWorld = ClientServerBootstrap.ServerWorld;
                if (serverWorld == null || !serverWorld.IsCreated)
                {
                    DedicatedServerFileLog.Append("lobby", "Match recreate FAILED: ServerWorld missing");
                    return null;
                }

                TitanOrbitRelayState.SetServerRelay(prep.Relay);
                await ClearNetworkConnectionsAsync(serverWorld);
                // [TITAN-ORBIT] New lobby on the same ServerWorld must not keep orphan ships —
                // NetCode reuses low NetworkIds, which falsely offered "rescue my ship" to new joiners.
                // clearSessionShips drops match-only ship saves. This recreate is a new game.
                WipeOrphanPlayerShipsAndResetRosters(serverWorld, clearSessionShips: true);
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
                    publishAsLatest,
                    prep.HostAllocationId);
                if (prep.Lobby == null)
                {
                    // [TITAN-ORBIT] Old lobby still open — do not close it after a failed create.
                    DedicatedServerFileLog.Append(
                        "lobby",
                        "Match recreate FAILED: CreateDedicatedLobby returned null oldLobby=" + oldLobbyId);
                    return null;
                }

                _activeLobbyId = prep.Lobby.Id;
                _consecutiveHeartbeatFailures = 0;

                // [TITAN-ORBIT] Publish order matters for Join Game: create+assign new lobby BEFORE
                // closing the old one so browse never sees a moment with zero open listings.
                DedicatedServerFileLog.Append(
                    "lobby",
                    "Match recreate published NEW lobby first id=" + prep.Lobby.Id +
                    " — closing old next id=" + oldLobbyId);

                await CloseLobbyForNewJoinersAsync(oldLobbyId, "empty_match_recreate");
                try
                {
                    await LobbyService.Instance.DeleteLobbyAsync(oldLobbyId);
                }
                catch (Exception deleteEx)
                {
                    Debug.LogWarning("[TitanOrbitSessionManager] Could not delete old lobby: " + deleteEx.Message);
                }

                // [TITAN-ORBIT] Recreate does not re-run boot's "Dedicated server live" line — log the
                // new lobby id here so overnight gaps are visible in TitanOrbitDedicatedServer.log.
                DedicatedServerFileLog.Append(
                    "lobby",
                    "Match recreated lobby=" + prep.Lobby.Id +
                    " name=" + (prep.Lobby.Name ?? "") +
                    " isLatest=" + publishAsLatest +
                    " closedOld=" + oldLobbyId +
                    " relay=" + prep.JoinCode);
                Debug.Log("[TitanOrbitSessionManager] Match recreated: " + prep.Lobby.Id + " isLatest=" + publishAsLatest);
                return new DedicatedMatchRecreateResult
                {
                    LobbyId = prep.Lobby.Id,
                    CreatedAtEpochSeconds = prep.CreatedAtEpochSeconds,
                    IsLatest = publishAsLatest,
                };
            }
            finally
            {
                _recreateDedicatedMatchInProgress = false;
                TitanOrbitDedicatedServerHost.SetHangWatchdogPaused(false);
            }
        }

        /// <summary>
        /// Relay told this process the allocation is dead. Publish a new join code on the same
        /// lobby and listen again. The conquest map and ships stay; players who were dropped can
        /// Join the same game. Does not wait out the 30-minute empty recycle.
        /// </summary>
        public async Task<bool> RebindDedicatedRelayKeepMatchAsync()
        {
            if (_relayRebindInProgress || _recreateDedicatedMatchInProgress)
                return false;
            if (MatchEndServerSignal.IsMatchWon)
                return false;
            if (string.IsNullOrEmpty(_activeLobbyId) || _serverConfig == null)
                return false;

            _relayRebindInProgress = true;
            TitanOrbitDedicatedServerHost.SetHangWatchdogPaused(true);
            string lobbyId = _activeLobbyId;
            try
            {
                var prep = await PrepareDedicatedRelayAsync(_serverConfig);
                if (prep == null)
                {
                    DedicatedServerFileLog.Append("lobby", "Relay rebind FAILED: PrepareDedicatedRelay returned null");
                    return false;
                }

                var serverWorld = ClientServerBootstrap.ServerWorld;
                if (serverWorld == null || !serverWorld.IsCreated)
                {
                    DedicatedServerFileLog.Append("lobby", "Relay rebind FAILED: ServerWorld missing");
                    return false;
                }

                TitanOrbitRelayState.SetServerRelay(prep.Relay);
                await ClearNetworkConnectionsAsync(serverWorld);
                ResetServerDriverIfNeeded();
                ListenServer(serverWorld, _serverConfig.ServerPort);
                for (int i = 0; i < 90; i++)
                {
                    TickServerWorld(serverWorld);
                    if (IsServerWorldListening(serverWorld))
                        break;
                    await Task.Delay(16);
                }

                if (!IsServerWorldListening(serverWorld))
                {
                    DedicatedServerFileLog.Append("lobby", "Relay rebind FAILED: listen not confirmed");
                    return false;
                }

                RequestGoInGame(serverWorld);
                await UpdateDedicatedLobbyRelayCodeAsync(lobbyId, prep.JoinCode, prep.RelayProtocol);
                await TitanOrbitLobbyService.TryUpdatePlayerRelayAllocationAsync(lobbyId, prep.HostAllocationId);
                DedicatedServerFileLog.Append(
                    "lobby",
                    "Relay rebind kept match lobby=" + lobbyId + " relay=" + prep.JoinCode);
                Debug.Log("[TitanOrbitSessionManager] Relay rebind kept match lobby=" + lobbyId +
                          " relay=" + prep.JoinCode);
                return true;
            }
            catch (Exception ex)
            {
                DedicatedServerFileLog.Append("lobby", "Relay rebind exception", ex);
                Debug.LogError("[TitanOrbitSessionManager] Relay rebind failed: " + ex.Message);
                return false;
            }
            finally
            {
                _relayRebindInProgress = false;
                TitanOrbitDedicatedServerHost.SetHangWatchdogPaused(false);
                TitanOrbitRelayAllocationSignal.ClearServerInvalid();
            }
        }

        /// <summary>Writes a fresh Relay join code onto the live lobby without closing it.</summary>
        async Task UpdateDedicatedLobbyRelayCodeAsync(string lobbyId, string joinCode, string protocol)
        {
            if (string.IsNullOrWhiteSpace(lobbyId) || string.IsNullOrWhiteSpace(joinCode))
                return;

            await TitanOrbitLobbyService.AcquireLobbyApiGateAsync();
            try
            {
                await LobbyService.Instance.UpdateLobbyAsync(lobbyId, new UpdateLobbyOptions
                {
                    Data = new Dictionary<string, DataObject>
                    {
                        {
                            TitanOrbitLobbyService.LobbyRelayCodeKey,
                            new DataObject(DataObject.VisibilityOptions.Member, joinCode)
                        },
                        {
                            TitanOrbitLobbyService.LobbyRelayProtocolKey,
                            new DataObject(DataObject.VisibilityOptions.Public, protocol)
                        }
                    }
                });
            }
            finally
            {
                TitanOrbitLobbyService.ReleaseLobbyApiGate();
            }
        }

        /// <summary>
        /// Marks a dedicated lobby closed for browse/join: IsOpen=0, IsLatest=0, locked.
        /// Use when the match is empty (idle recreate), full (no slots), or the process is exiting.
        /// Do not use this for age rotation while players are still connected — use
        /// <see cref="DemoteFromLatestKeepOpenAsync"/> so conquest maps stay joinable.
        /// </summary>
        /// <param name="lobbyId">UGS lobby id to close.</param>
        /// <param name="reason">Logged reason (e.g. empty_match_recreate, full_rotation).</param>
        public async Task CloseLobbyForNewJoinersAsync(string lobbyId, string reason)
        {
            // --- CloseLobbyForNewJoinersAsync ---
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

        /// <summary>
        /// Age / supersede handoff for an occupied match: clear IsLatest so a successor can be the
        /// new "fresh game" target, but keep IsOpen=1 and unlocked so players mid-conquest (and
        /// friends rejoining) still see and join this lobby in Join Game.
        /// </summary>
        /// <param name="lobbyId">UGS lobby id to demote.</param>
        /// <param name="reason">Logged reason (e.g. age_rotation, superseded_occupied).</param>
        public async Task DemoteFromLatestKeepOpenAsync(string lobbyId, string reason)
        {
            // --- DemoteFromLatestKeepOpenAsync ---
            if (string.IsNullOrWhiteSpace(lobbyId))
                return;

            try
            {
                await TitanOrbitLobbyService.AcquireLobbyApiGateAsync();
                try
                {
                    // [TITAN-ORBIT] Only touch IsLatest. Leaving IsOpen=1 + unlocked is intentional —
                    // closing mid-match was removing live games from the browser after ~AgeThreshold.
                    await LobbyService.Instance.UpdateLobbyAsync(lobbyId, new UpdateLobbyOptions
                    {
                        Data = new Dictionary<string, DataObject>
                        {
                            {
                                TitanOrbitLobbyService.LobbyIsLatestKey,
                                new DataObject(DataObject.VisibilityOptions.Public, "0", DataObject.IndexOptions.N2)
                            },
                            {
                                TitanOrbitLobbyService.LobbyIsOpenKey,
                                new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N1)
                            }
                        },
                        IsLocked = false
                    });
                }
                finally
                {
                    TitanOrbitLobbyService.ReleaseLobbyApiGate();
                }

                DedicatedServerFileLog.Append("lobby",
                    "Demoted IsLatest keep open (" + reason + ") id=" + lobbyId);
                Debug.Log("[TitanOrbitSessionManager] Demoted lobby from IsLatest (kept open): " + lobbyId +
                          " reason=" + reason);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] DemoteFromLatestKeepOpen failed: " + e.Message);
            }
        }

        /// <summary>
        /// Copies MapStateSingleton totals (counts + rolled map size) into UGS lobby Data when the
        /// server map is ready. Join Game browser reads these keys without connecting to NetCode.
        /// </summary>
        /// <param name="data">Lobby Data dictionary being created or heartbeat-updated.</param>
        static void AppendMapSessionMetaLobbyData(Dictionary<string, DataObject> data)
        {
            // --- Resolve authoritative map totals from ServerWorld ---
            // [TITAN-ORBIT] Same numbers clients get via MapSessionMetaRpc after GoInGame
            // (teams, neutrals, asteroids, and MapWidth/MapHeight for the browse footer).
            if (data == null)
                return;

            var server = ClientServerBootstrap.ServerWorld;
            if (!MapSessionMetaCache.TryReadFromServerWorld(server, out MapSessionMetaRpc meta))
                return;

            data[TitanOrbitLobbyService.LobbyMapLoadingStepsKey] = new DataObject(
                DataObject.VisibilityOptions.Public,
                meta.LoadingTotalSteps.ToString(CultureInfo.InvariantCulture));
            data[TitanOrbitLobbyService.LobbyMapTeamCountKey] = new DataObject(
                DataObject.VisibilityOptions.Public,
                meta.TeamCount.ToString(CultureInfo.InvariantCulture));
            data[TitanOrbitLobbyService.LobbyMapNeutralCountKey] = new DataObject(
                DataObject.VisibilityOptions.Public,
                meta.NeutralPlanetCount.ToString(CultureInfo.InvariantCulture));
            data[TitanOrbitLobbyService.LobbyMapAsteroidCountKey] = new DataObject(
                DataObject.VisibilityOptions.Public,
                meta.AsteroidCount.ToString(CultureInfo.InvariantCulture));

            // --- Rolled toroidal size (Join Game shows "333×444") ---
            // [TITAN-ORBIT] Same MapWidth/Height clients get via MapSessionMetaRpc; round for lobby strings.
            if (meta.MapWidth >= 100f && meta.MapHeight >= 100f)
            {
                int mapW = Mathf.RoundToInt(meta.MapWidth);
                int mapH = Mathf.RoundToInt(meta.MapHeight);
                data[TitanOrbitLobbyService.LobbyMapWidthKey] = new DataObject(
                    DataObject.VisibilityOptions.Public,
                    mapW.ToString(CultureInfo.InvariantCulture));
                data[TitanOrbitLobbyService.LobbyMapHeightKey] = new DataObject(
                    DataObject.VisibilityOptions.Public,
                    mapH.ToString(CultureInfo.InvariantCulture));
            }

            // --- Per-team owned planet counts (live; updates each heartbeat as captures happen) ---
            // [TITAN-ORBIT] Join Game team cards show worlds from MapTeamPlanets CSV.
            if (MapSessionMetaCache.TryBuildTeamPlanetCountsCsv(server, meta.TeamCount, out string teamPlanetsCsv) &&
                !string.IsNullOrEmpty(teamPlanetsCsv))
            {
                data[TitanOrbitLobbyService.LobbyMapTeamPlanetsKey] = new DataObject(
                    DataObject.VisibilityOptions.Public,
                    teamPlanetsCsv);
            }

            // --- Per-team roster sizes + per-team cap (Join Game player lines) ---
            // [TITAN-ORBIT] Match capacity for the browser is teamCount × maxPerTeam, not the
            // UGS lobby MaxPlayers ceiling (often 60). Publish both so the UI can show e.g. 1/20.
            if (MapSessionMetaCache.TryBuildTeamPlayerCountsCsv(server, meta.TeamCount, out string teamPlayersCsv) &&
                !string.IsNullOrEmpty(teamPlayersCsv))
            {
                data[TitanOrbitLobbyService.LobbyMapTeamPlayersKey] = new DataObject(
                    DataObject.VisibilityOptions.Public,
                    teamPlayersCsv);
            }

            if (MapSessionMetaCache.TryReadMaxPlayersPerTeam(server, out int maxPerTeam))
            {
                data[TitanOrbitLobbyService.LobbyMapMaxPlayersPerTeamKey] = new DataObject(
                    DataObject.VisibilityOptions.Public,
                    maxPerTeam.ToString(CultureInfo.InvariantCulture));
            }
        }

        public int GetServerConnectedPlayerCount()
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return 0;
            return server.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount();
        }

        /// <summary>
        /// True when every joinable seat is taken. Uses teams × max-per-team once the map
        /// has rolled; otherwise the dedicated <c>--maxPlayers</c> ceiling. A second game is
        /// opened only in this state.
        /// </summary>
        /// <param name="connectedPlayers">Live NetCode connection count.</param>
        public bool IsServerMatchRosterFull(int connectedPlayers)
        {
            // --- IsServerMatchRosterFull ---
            int cap = _serverConfig != null ? _serverConfig.MaxPlayers : TitanOrbitServerCommandLine.DefaultMaxPlayers;
            var server = ClientServerBootstrap.ServerWorld;
            if (MapSessionMetaCache.TryReadFromServerWorld(server, out MapSessionMetaRpc meta) &&
                meta.TeamCount > 0 &&
                MapSessionMetaCache.TryReadMaxPlayersPerTeam(server, out int perTeam) &&
                perTeam > 0)
            {
                int rosterCap = meta.TeamCount * perTeam;
                if (rosterCap > 0)
                    cap = Math.Min(cap, rosterCap);
            }

            return connectedPlayers >= Math.Max(1, cap);
        }

        /// <summary>
        /// Destroys every player ship ghost on the server world and zeroes team roster counts.
        /// Call when the match is empty or when an in-process lobby recreate publishes a "new game"
        /// on the same ServerWorld. Without this, NetCode reassigns NetworkId 1 to the next joiner
        /// and the client shows the previous player's orphan ship as "rescue."
        /// Map planets stay; only ships and roster slots are cleared.
        /// </summary>
        public void WipeOrphanPlayerShipsAndResetRosters()
        {
            // Empty match keeps saved ships so the same players can return before idle recreate.
            WipeOrphanPlayerShipsAndResetRosters(ClientServerBootstrap.ServerWorld, clearSessionShips: false);
        }

        /// <summary>
        /// See <see cref="WipeOrphanPlayerShipsAndResetRosters()"/> — world overload for recreate path.
        /// </summary>
        /// <param name="serverWorld">Dedicated or host ServerWorld; no-op if null/destroyed.</param>
        /// <param name="clearSessionShips">
        /// True on a new game (lobby recreate). False when the same match is only briefly empty.
        /// </param>
        static void WipeOrphanPlayerShipsAndResetRosters(World serverWorld, bool clearSessionShips)
        {
            // --- Guard: no server world yet ---
            if (serverWorld == null || !serverWorld.IsCreated)
                return;

            var em = serverWorld.EntityManager;
            if (clearSessionShips)
                MatchPlayerShipStore.Clear();
            else
                MatchPlayerShipStore.CaptureRemainingShips(em);

            // --- Destroy all ship ghosts (orphan reconnect targets) ---
            // [NETCODE] Disconnect does not despawn owned ghosts. OrphanPlayerShipCleanupSystem
            // removes a hull once its owner connection is gone; this wipe is the empty-match
            // backstop so a reused NetworkId cannot still point at a leftover ship.
            using (var ships = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner)))
            using (var entities = ships.ToEntityArray(Allocator.Temp))
            {
                int destroyed = entities.Length;
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity ship = entities[i];
                    if (!em.Exists(ship))
                        continue;

                    // Free the titan bay before the hull is gone so a later resume can reclaim it
                    // when nobody else bought that slot.
                    int ownerId = em.HasComponent<GhostOwner>(ship)
                        ? em.GetComponentData<GhostOwner>(ship).NetworkId
                        : 0;
                    if (em.HasComponent<MegaShipState>(ship))
                        MegaShipStatApplyLogic.ReleaseMegaOccupancy(em, ship);
                    if (ownerId > 0)
                        MegaShipPlanetLogic.FreeSlotsOccupiedBy(em, ownerId);
                    em.DestroyEntity(ship);
                }

                if (destroyed > 0)
                {
                    Debug.Log("[TitanOrbitSessionManager] Wiped " + destroyed +
                              " orphan player ship(s) for empty/recreate match.");
                    DedicatedServerFileLog.Append("match", "Wiped orphan ships count=" + destroyed);
                }
            }

            if (!clearSessionShips)
                MatchPlayerShipStore.ClearBindings();

            // --- Reset roster counts (ActiveTeamCount stays — map still has those teams) ---
            using var teamQuery = em.CreateEntityQuery(typeof(TeamStateSingleton));
            if (teamQuery.CalculateEntityCount() == 1)
            {
                var teamEntity = teamQuery.GetSingletonEntity();
                var team = em.GetComponentData<TeamStateSingleton>(teamEntity);
                team.TeamACount = 0;
                team.TeamBCount = 0;
                team.TeamCCount = 0;
                team.TeamDCount = 0;
                team.TeamECount = 0;
                em.SetComponentData(teamEntity, team);
            }
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
                if (world == ClientServerBootstrap.ClientWorld)
                    TickClientWorld(world);
                else
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

        /// <summary>
        /// [NETCODE] Join a specific dedicated lobby by id: validate heartbeat, fetch Relay join code,
        /// reset client driver, connect via Relay, start ClientConnectWatch coroutine.
        /// </summary>
        /// <param name="lobbyId">Unity Lobby id from browse UI.</param>
        public async Task<bool> JoinDedicatedLobbyAsync(string lobbyId)
        {
            try
            {
                // --- Validate lobby id and guest auth ---
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
                _lastRelayJoinCodeAttempt = joinCode;
                string hostProtocol = lobby.Data.TryGetValue(TitanOrbitLobbyService.LobbyRelayProtocolKey, out var proto)
                    ? TitanOrbitRelayUtility.SanitizeRelayProtocolForRelaySdk(proto.Value)
                    : TitanOrbitRelayUtility.ClientConnectionTypeForPlatform();
                // Same allocation as the dedicated host. WebGL dials wss; the Editor dials dtls.
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

                Debug.Log("[TitanOrbitSessionManager] Relay endpoint ready clientProtocol=" + clientProtocol +
                          " endpointValid=True");

                await TitanOrbitLobbyService.TryUpdatePlayerRelayAllocationAsync(
                    lobby.Id, joinAllocation.AllocationId.ToString());

                TitanOrbitRelayState.SetClientRelay(clientRelay);
                await EnsureClientReadyForRelayDriverResetAsync();

                var clientWorld = ClientServerBootstrap.ClientWorld;
                if (clientWorld == null || !clientWorld.IsCreated)
                {
                    Debug.LogError("[TitanOrbitSessionManager] Client world missing.");
                    LastStatusMessage = "Client world missing.";
                    return false;
                }

#if UNITY_WEBGL && !UNITY_EDITOR
                // Player loop owns ClientWorld. The init-group system resets the driver and
                // connects before NetworkStreamReceiveSystem polls the WebSocket.
                TitanOrbitWebGlRelayConnect.Request();
#else
                // A leftover IPC connection from the Editor's local ServerWorld makes
                // ResetDriverStore refuse, and Connect then dials Relay on the wrong driver.
                int released = ForceReleaseClientConnectionEntities(clientWorld.EntityManager);
                if (released > 0)
                    Debug.Log("[TitanOrbitSessionManager] Released " + released +
                              " stale connection(s) before the Editor Relay driver rebuild.");
                if (!ResetClientDriverIfNeeded())
                {
                    Debug.LogError("[TitanOrbitSessionManager] Editor Relay driver was not rebuilt.");
                    LastStatusMessage = "Could not open the online connection. Stop Play, then Join game again.";
                    return false;
                }

                Entity connection = ConnectRelayClient(clientWorld);
                if (connection == Entity.Null)
                {
                    Debug.LogError("[TitanOrbitSessionManager] Editor Relay connect was skipped.");
                    LastStatusMessage = "Online connect did not start. Tap Refresh, then join again.";
                    return false;
                }

                TitanOrbitRelayState.TryGetClientRelay(out var dial);
                Debug.Log("[TitanOrbitSessionManager] Editor Relay connect issued endpoint=" + dial.Endpoint +
                          " wss=" + dial.IsWebSocket + " secure=" + dial.IsSecure +
                          " connection=" + connection.Index);
#endif
                for (int i = 0; i < 30; i++)
                {
                    TickClientWorld(clientWorld);
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

        /// <summary>
        /// True when the authoritative server has already declared a winner.
        /// </summary>
        public bool IsServerMatchWon()
        {
            if (MatchEndServerSignal.IsMatchWon)
                return true;

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            using var query = server.EntityManager.CreateEntityQuery(typeof(MatchStateSingleton));
            if (!query.TryGetSingleton<MatchStateSingleton>(out var match))
                return false;
            return match.WinningTeam != TeamId.None;
        }

        /// <summary>
        /// Clears a latched win on the client world so the congrats card does not
        /// reopen on the menu or on the next match.
        /// </summary>
        static void ClearClientMatchWinLatch()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
                return;

            var em = client.EntityManager;
            using var query = em.CreateEntityQuery(typeof(MatchStateSingleton));
            if (query.CalculateEntityCount() != 1)
                return;

            Entity entity = query.GetSingletonEntity();
            var match = em.GetComponentData<MatchStateSingleton>(entity);
            if (match.WinningTeam == TeamId.None && match.GameState == 0)
                return;

            match.WinningTeam = TeamId.None;
            match.GameState = 0;
            em.SetComponentData(entity, match);
        }

        /// <summary>
        /// Disconnects this client and returns UI to the Main Menu.
        /// Called from the Escape command overlay. Dedicated Relay clients leave the lobby
        /// and reset the driver; a local host also parks ServerWorld so the leftover match
        /// does not keep simulating on the menu. Does not run on a headless dedicated server.
        /// </summary>
        /// <remarks>
        /// [NETCODE] Removing <see cref="NetworkStreamInGame"/> is the leave-session signal.
        /// After a short debounce the client tears down hybrid proxies and the game-flow
        /// controller shows MainMenuPanel because gameplay-ready is false.
        /// </remarks>
        public async Task ReturnToMainMenuAsync()
        {
            if (_returningToMenu)
                return;

            _returningToMenu = true;
            LastStatusMessage = "Leaving match...";
            Debug.Log("[TitanOrbitSessionManager] Returning to main menu.");

            try
            {
                // --- Cancel an in-flight dedicated connect watch ---
                if (_connectWatch != null)
                {
                    StopCoroutine(_connectWatch);
                    _connectWatch = null;
                }

                bool dedicated = IsDedicatedOnlineClient;
                var client = ClientServerBootstrap.ClientWorld;

                // Destroy the hull before the connection times out. Local host parks ServerWorld
                // on the next lines, so a later sim tick never gets a chance to reap the ship.
                // A titan bay is freed the same way death releases it.
                bool sentLeaveRpc = ReleaseShipOnExit(client, dedicated);
                if (sentLeaveRpc)
                    await FlushClientSoLeaveRpcSends();
                // Drop client copies now so the minimap cannot keep the hull through the disconnect.
                ClearClientShipPresentation(client);

                // Read before we tear worlds down. A won local host must not resume this map.
                bool matchWon = MatchEndServerSignal.IsMatchWon || IsServerMatchWon();
                IsInGame = false;
                ClientTeamFlowState.Reset();
                // Drop the client win latch and planet-count inference so the menu
                // (and the next match) do not reopen the congrats card.
                MapSessionMetaCache.Clear();
                ClearClientMatchWinLatch();

                if (dedicated)
                {
                    // ResetDedicatedClientSessionAsync clears Relay, NetworkStreamInGame, and connections.
                    await ResetDedicatedClientSessionAsync("Returned to main menu.");
                    ClearClientShipPresentation(client);
                    _activeLobbyId = null;
                    await TitanOrbitLobbyService.TryLeaveAllJoinedLobbiesAsync("return_to_menu");
                    LastStatusMessage = "Returned to main menu.";
                    return;
                }

                // --- Local host / local LAN client ---
                // [NETCODE] Drop GoInGame on the client first so HUD / flow see "not in game".
                if (client != null && client.IsCreated)
                {
                    ClearNetworkStreamInGame(client);
                    await ClearNetworkConnectionsAsync(client);
                    ClearClientShipPresentation(client);
                }

                ResetClientDriverIfNeeded();
                TitanOrbitRelayState.Clear();
                IsDedicatedOnlineClient = false;
                _activeLobbyId = null;

                // MPPM additional editors are clients of the main Editor host — do not park
                // their unused ServerWorld or disconnect the host's listeners.
                bool isMppmClient = TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance();
                var server = ClientServerBootstrap.ServerWorld;
                if (!isMppmClient && server != null && server.IsCreated)
                {
                    ClearNetworkStreamInGame(server);
                    await ClearNetworkConnectionsAsync(server);
                    ResetServerDriverIfNeeded();
                    if (matchWon)
                    {
                        // Next Local play creates a new ServerWorld and rolls a new map.
                        DisposeEditorLocalServerWorld("match finished — next play is a new game");
                    }
                    else
                    {
                        // [TITAN-ORBIT] Same park as boot-to-menu: QuitUpdate so map/match sim stops.
                        SuspendEditorLocalServerUntilLocalPlay();
                    }
                }

                LastStatusMessage = "Returned to main menu.";
                Debug.Log("[TitanOrbitSessionManager] Returned to main menu.");
            }
            finally
            {
                _returningToMenu = false;
            }
        }

        /// <summary>
        /// Destroys the leaving player's hull now. In-process host: every parked hull goes,
        /// because this process is about to stop simulating. A client of a remote server
        /// sends a leave RPC so that host frees the titan bay on its next tick.
        /// </summary>
        /// <returns>True when a leave RPC was queued and still needs a few client ticks to send.</returns>
        static bool ReleaseShipOnExit(World client, bool dedicated)
        {
            bool mppmClient = TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance();
            var server = ClientServerBootstrap.ServerWorld;
            bool serverAlive = !dedicated && !mppmClient && server != null && server.IsCreated;
            bool localHost = IsLocalHostWorldsReady();
            if (serverAlive && (localHost || ServerHasPlayerShips(server)))
            {
                int removed = PlayerShipExit.DespawnAllPlayerShips(server.EntityManager);
                Debug.Log("[TitanOrbitSessionManager] Exit destroyed " + removed +
                          " server ship(s) before parking the match.");
            }

            // This process is the match. The hulls are already gone; do not wait on an RPC.
            if (localHost)
                return false;

            if (client == null || !client.IsCreated)
                return false;

            var em = client.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new LeaveMatchDespawnShipCommand());
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            Debug.Log("[TitanOrbitSessionManager] Sent leave-despawn RPC.");
            return true;
        }

        static bool ServerHasPlayerShips(World server)
        {
            using var query = server.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ShipTag>());
            return !query.IsEmptyIgnoreFilter;
        }

        /// <summary>Gives the reliable leave RPC a few client ticks to leave the machine.</summary>
        async Task FlushClientSoLeaveRpcSends()
        {
            var client = ClientServerBootstrap.ClientWorld;
            for (int i = 0; i < 20; i++)
            {
                if (client != null && client.IsCreated)
                    TickClientWorld(client);
                await Task.Yield();
            }
        }

        /// <summary>
        /// Drops client ship ghosts and the local-ship seed so the minimap cannot keep the hull
        /// from the match you just left.
        /// </summary>
        static void ClearClientShipPresentation(World client)
        {
            if (client != null && client.IsCreated)
                PlayerShipExit.DestroyClientShipGhosts(client.EntityManager);
            LocalShipEntitySeed.Clear();
        }

        public async Task ResetDedicatedClientSessionAsync(string reason = null)
        {
            // Captured so a Join that starts while this await is in flight is not undone.
            int epoch = _clientSessionEpoch;
            IsDedicatedOnlineClient = false;
            IsInGame = false;
            ClientTeamFlowState.Reset();
            NotifyClientMatchSessionEnded();
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

            if (epoch != _clientSessionEpoch)
            {
                Debug.Log("[TitanOrbitSessionManager] Skipped stale client reset — a newer Join already started.");
                return;
            }

            TitanOrbitRelayState.Clear();
            // WebGL: the player loop is already inside ClientWorld. Resetting the socket
            // from this async method races NetworkStreamReceiveSystem and can throw while
            // a dead connection entity still exists — the next Join then never connects
            // until the browser page is refreshed. The init-group system resets instead.
#if UNITY_WEBGL && !UNITY_EDITOR
            TitanOrbitWebGlRelayConnect.RequestIdleReset();
#else
            ResetClientDriverIfNeeded();
#endif

            if (!string.IsNullOrEmpty(reason))
            {
                LastStatusMessage = reason;
                Debug.LogWarning("[TitanOrbitSessionManager] Dedicated client session reset: " + reason);
            }
        }

        async Task PrepareClientForDedicatedRelayJoinAsync()
        {
            _clientSessionEpoch++;
            IsDedicatedOnlineClient = true;
            IsInGame = false;
            ClientTeamFlowState.Reset();
            // Previous WebGL session can still say the map finished loading. Clear that before connect.
            NotifyClientMatchSessionEnded();
            StopMppmLanAutoConnect();

            // [UNITY] VSync on at join — sync presents to the monitor and avoid tear strips while
            // flying. targetFrameRate is ignored while vSyncCount > 0; kept as a soft fallback.
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 1;

            await SuspendLocalServerForDedicatedClientAsync();
            await EnsureClientReadyForRelayDriverResetAsync();
            TitanOrbitRelayState.Clear();
        }

        /// <summary>Stops MPPM LAN auto-connect coroutine when user switches to dedicated join.</summary>
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
                TickClientWorld(clientWorld);
                await Task.Yield();
            }

            int remaining = clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount();
            if (remaining > 0)
                Debug.LogWarning("[TitanOrbitSessionManager] " + remaining +
                                 " NetworkStreamConnection(s) still present before Relay driver reset.");
        }

        /// <summary>
        /// Clears local connections, then disposes Editor ServerWorld so Relay join is client-only.
        /// </summary>
        static async Task SuspendLocalServerForDedicatedClientAsync()
        {
            var server = ClientServerBootstrap.ServerWorld;
            var client = ClientServerBootstrap.ClientWorld;

            if (server != null && server.IsCreated)
            {
                // Stop sim first so disconnect flush does not advance local match state.
                SuspendEditorLocalServerUntilLocalPlay();
                ClearNetworkStreamInGame(server);
                RequestDisconnectAllConnections(server);
                ResetServerDriverIfNeeded();

                // Short flush only — basics38 used 60 ticks here and worsened join hitch.
                for (int i = 0; i < 5; i++)
                {
                    if (server.IsCreated)
                        TickServerWorld(server);
                    await Task.Yield();
                }

                DisposeEditorServerWorldForDedicatedJoin();
            }

            if (client != null && client.IsCreated)
            {
                ClearNetworkStreamInGame(client);
                RequestDisconnectAllConnections(client);
            }

            for (int i = 0; i < 10; i++)
            {
                if (client != null && client.IsCreated)
                    TickClientWorld(client);
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
                var lobbyData = new Dictionary<string, DataObject>
                {
                    { TitanOrbitLobbyService.LobbyRelayCodeKey, new DataObject(DataObject.VisibilityOptions.Member, joinCode) },
                    { TitanOrbitLobbyService.LobbyGameNameKey, new DataObject(DataObject.VisibilityOptions.Public, TitanOrbitLobbyService.LobbyGameNameValue, DataObject.IndexOptions.S1) },
                    { TitanOrbitLobbyService.LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N1) },
                    { TitanOrbitLobbyService.LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, isLatest ? "1" : "0", DataObject.IndexOptions.N2) },
                    { TitanOrbitLobbyService.LobbyCreatedAtEpochKey, new DataObject(DataObject.VisibilityOptions.Public, createdAt.ToString(CultureInfo.InvariantCulture), DataObject.IndexOptions.N3) },
                    { TitanOrbitLobbyService.LobbyServerAliveEpochKey, new DataObject(DataObject.VisibilityOptions.Public, createdAt.ToString(CultureInfo.InvariantCulture), DataObject.IndexOptions.N5) },
                    { TitanOrbitLobbyService.LobbyRelayProtocolKey, new DataObject(DataObject.VisibilityOptions.Public, protocol) },
                    { TitanOrbitLobbyService.LobbyServerListenAddressKey, new DataObject(DataObject.VisibilityOptions.Public, serverListenAddress) },
                    { TitanOrbitLobbyService.LobbyActivePlayersKey, new DataObject(DataObject.VisibilityOptions.Public, "0", DataObject.IndexOptions.N4) },
                };

                // [TITAN-ORBIT] Brand-new lobby is empty — publish idle-kill deadline for Join Game countdown.
                int idleSeconds = _serverConfig != null
                    ? _serverConfig.EmptyMatchRecreateSeconds
                    : TitanOrbitServerCommandLine.DefaultEmptyMatchRecreateSeconds;
                long idleKillAt = createdAt + Mathf.Max(60, idleSeconds);
                lobbyData[TitanOrbitLobbyService.LobbyIdleKillAtEpochKey] = new DataObject(
                    DataObject.VisibilityOptions.Public,
                    idleKillAt.ToString(CultureInfo.InvariantCulture));

                // [TITAN-ORBIT] Publish map totals when generation already finished (often still rolling at create).
                AppendMapSessionMetaLobbyData(lobbyData);

                var createOptions = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Data = lobbyData
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

        /// <summary>[NETCODE] Periodic Unity Lobby heartbeat while dedicated server hosts a match.</summary>
        IEnumerator LobbyHeartbeatLoop()
        {
            if (!string.IsNullOrEmpty(_activeLobbyId))
            {
                Task<bool> first = SendHeartbeatAsync();
                while (!first.IsCompleted)
                    yield return null;
                if (!first.IsFaulted && first.Result)
                    _consecutiveHeartbeatFailures = 0;
            }

            var wait = new WaitForSeconds(15f);
            while (true)
            {
                if (!string.IsNullOrEmpty(_activeLobbyId))
                {
                    Task<bool> heartbeat = SendHeartbeatAsync();
                    while (!heartbeat.IsCompleted)
                        yield return null;

                    bool heartbeatOk = !heartbeat.IsFaulted && heartbeat.Result;
                    if (heartbeatOk)
                    {
                        _consecutiveHeartbeatFailures = 0;
                    }
                    else
                    {
                        _consecutiveHeartbeatFailures++;
                        if (_consecutiveHeartbeatFailures >= HeartbeatFailureRecreateThreshold &&
                            GetServerConnectedPlayerCount() == 0 &&
                            !_recreateDedicatedMatchInProgress &&
                            _serverConfig != null)
                        {
                            // [TITAN-ORBIT] Empty-only. Always in-process recreate — do not exit on
                            // heartbeat failure (that closed the only lobby and worsened Join Game empty).
                            Debug.LogWarning("[TitanOrbitSessionManager] Heartbeat failed " +
                                             _consecutiveHeartbeatFailures + " times; recreating lobby.");
                            DedicatedServerFileLog.Append("heartbeat",
                                "Recreate after " + _consecutiveHeartbeatFailures + " consecutive failures.");
                            Task<TitanOrbitSessionManager.DedicatedMatchRecreateResult> recreateTask =
                                RecreateDedicatedMatchAsync(_serverConfig, forceIsLatest: true);
                            while (!recreateTask.IsCompleted)
                                yield return null;
                            if (!recreateTask.IsFaulted && recreateTask.Result != null)
                            {
                                var result = recreateTask.Result;
                                TitanOrbitDedicatedServerHost.NotifyLobbyReplacedFromSession(
                                    result.LobbyId, result.CreatedAtEpochSeconds, result.IsLatest);
                            }
                        }
                    }
                }

                yield return wait;
            }
        }

        /// <summary>
        /// [NETCODE] UGS heartbeat + public lobby Data refresh (alive epoch, ActivePlayers, map meta,
        /// and IdleKillAt for empty-match Join Game countdown).
        /// </summary>
        /// <returns>True when ping + UpdateLobby succeeded.</returns>
        async Task<bool> SendHeartbeatAsync()
        {
            // --- SendHeartbeatAsync ---
            try
            {
                await TitanOrbitLobbyService.AcquireLobbyApiGateAsync();
                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    int activePlayers = GetServerConnectedPlayerCount();

                    // Idle-kill deadline only while empty; publish 0 so Join Game hides the countdown
                    // as soon as anyone is connected (does not wait for a full list refresh).
                    long idleKillAtEpoch = 0;
                    if (activePlayers <= 0 &&
                        TitanOrbitDedicatedServerHost.TryGetEmptyIdleKillAtEpochSeconds(out long killAt))
                    {
                        idleKillAtEpoch = killAt;
                    }

                    var heartbeatData = new Dictionary<string, DataObject>
                    {
                        {
                            TitanOrbitLobbyService.LobbyServerAliveEpochKey,
                            new DataObject(DataObject.VisibilityOptions.Public, now.ToString(CultureInfo.InvariantCulture), DataObject.IndexOptions.N5)
                        },
                        {
                            TitanOrbitLobbyService.LobbyActivePlayersKey,
                            new DataObject(DataObject.VisibilityOptions.Public, activePlayers.ToString(CultureInfo.InvariantCulture))
                        },
                        {
                            TitanOrbitLobbyService.LobbyIdleKillAtEpochKey,
                            new DataObject(
                                DataObject.VisibilityOptions.Public,
                                idleKillAtEpoch.ToString(CultureInfo.InvariantCulture))
                        }
                    };

                    // [TITAN-ORBIT] Keep Join Game browser map stats in sync once the map is ready.
                    AppendMapSessionMetaLobbyData(heartbeatData);

                    await LobbyService.Instance.SendHeartbeatPingAsync(_activeLobbyId);
                    await LobbyService.Instance.UpdateLobbyAsync(_activeLobbyId, new UpdateLobbyOptions
                    {
                        Data = heartbeatData
                    });
                }
                finally
                {
                    TitanOrbitLobbyService.ReleaseLobbyApiGate();
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] Heartbeat failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// After this process publishes a new IsLatest lobby, clear IsLatest on older "latest"
        /// lobbies. Occupied matches are demoted but kept open; only empty ones are hard-closed
        /// so a mid-conquest map is never removed from Join Game by a sibling boot.
        /// </summary>
        /// <param name="keepLobbyId">This process's new lobby id — never closed/demoted here.</param>
        IEnumerator CloseSupersededDedicatedLobbies(string keepLobbyId)
        {
            // --- CloseSupersededDedicatedLobbies ---
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

                // [TITAN-ORBIT] Heartbeat publishes ActivePlayers; CurrentPlayers prefers that for dedicated.
                // If either says occupied, demote only — never IsOpen=0 on a live conquest map.
                bool occupied = summary.CurrentPlayers > 0 || summary.ActivePlayers > 0;
                Task listingTask = occupied
                    ? DemoteFromLatestKeepOpenAsync(summary.LobbyId, "superseded_occupied")
                    : CloseLobbyForNewJoinersAsync(summary.LobbyId, "superseded_by_new_boot");
                while (!listingTask.IsCompleted)
                    yield return null;
            }
        }

        /// <summary>Headless server: marks NetworkStreamConnection entities in-game after Relay listen.</summary>
        IEnumerator MaintainDedicatedServerGoInGame()
        {
            int lastConnectionCount = -1;
            float lastPeriodicLog = 0f;
            while (true)
            {
                var server = ClientServerBootstrap.ServerWorld;
                if (server != null && server.IsCreated)
                {
                    // Server world is ticked by the Entities player loop (CreateServerWorld appends it).
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

        /// <summary>Polls client until in-game or timeout — dedicated Relay join watchdog.</summary>
        IEnumerator ClientConnectWatch(float timeoutSeconds, bool dedicatedJoin = false)
        {
            float started = Time.realtimeSinceStartup;
            float deadline = started + timeoutSeconds;
            // First diagnostic runs on the opening frame, then every 5s.
            float lastDiag = started - 5f;
            const float zombieFailSeconds = 20f;
            var client = ClientServerBootstrap.ClientWorld;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (client != null && client.IsCreated)
                {
                    TickClientWorld(client);

                    // [TITAN-ORBIT] Never force client NetworkStreamInGame here.
                    // TitanOrbitGoInGameClientSystem does that after seed hydrate.

                    if (dedicatedJoin && Time.realtimeSinceStartup - lastDiag >= 5f)
                    {
                        lastDiag = Time.realtimeSinceStartup;
                        LogClientConnectDiagnostics(client);
#if UNITY_WEBGL && !UNITY_EDITOR
                        // A failed WebSocket removes the connection entity. Ask for another
                        // connect on the next initialization tick instead of waiting out the minute.
                        if (!HasClientConnection(client) && TitanOrbitRelayState.TryGetClientRelay(out _))
                            TitanOrbitWebGlRelayConnect.Request();
#endif
                    }

                    if (dedicatedJoin && Time.realtimeSinceStartup - started >= zombieFailSeconds &&
                        HasZombieRelayConnection(client))
                    {
                        LastStatusMessage =
                            "Could not reach dedicated server — tap Refresh, join the Latest lobby only, " +
                            "and confirm Docker logs show the same Relay code.";
                        Debug.LogError("[TitanOrbitSessionManager] Client stuck on pending Relay connection (no NetworkId). " +
                                       "Relay join code=" + (_lastRelayJoinCodeAttempt ?? "(none)") +
                                       " lobby=" + (_activeLobbyId ?? "(none)") +
                                       ". Compare Relay= in Docker logs; stop stale containers/GCE servers.");
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
            using var connectionsQuery = em.CreateEntityQuery(typeof(NetworkStreamConnection));
            using var idQuery = em.CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkId));
            using var inGameQuery = em.CreateEntityQuery(typeof(NetworkStreamInGame));
            using var protocolQuery = em.CreateEntityQuery(typeof(NetworkProtocolVersion));
            int connections = connectionsQuery.CalculateEntityCount();
            int withNetworkId = idQuery.CalculateEntityCount();
            int inGame = inGameQuery.CalculateEntityCount();
            int spawnBuf = CountGhostSpawnBuffer(em);
            string connState = "(none)";
            // Desktop ClientConnectWatch calls World.Update, which leaves RpcSystem:RpcExecJob
            // writing NetworkStreamConnection. EntityQuery.TryGetSingleton does not complete that
            // job (unlike EntityManager.GetComponentData). WebGL skips this tick — the player
            // loop has already finished the jobs — so only the Editor/standalone path threw and
            // aborted the connect coroutine.
            em.CompleteDependencyBeforeRO<NetworkStreamConnection>();
            if (connections == 1 && connectionsQuery.TryGetSingleton<NetworkStreamConnection>(out var conn))
                connState = conn.CurrentState.ToString();
            var transform = client.GetExistingSystemManaged<TransformSystemGroup>();
            bool transformOn = transform != null && transform.Enabled;
            var predicted = client.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            bool predictedOn = predicted != null && predicted.Enabled;
            bool relayReady = TitanOrbitRelayState.TryGetClientRelay(out var relay);
            Debug.Log("[TitanOrbitSessionManager] Client connect diag: connections=" + connections +
                      " state=" + connState +
                      " withNetworkId=" + withNetworkId + " inGame=" + inGame +
                      " spawnBuf=" + spawnBuf +
                      " protocolReady=" + (protocolQuery.CalculateEntityCount() > 0) +
                      " transformOn=" + transformOn +
                      " predictedOn=" + predictedOn +
                      " clientProtocol=" + TitanOrbitRelayUtility.ClientConnectionTypeForPlatform() +
                      " relay=" + relayReady +
                      (relayReady ? " endpoint=" + relay.Endpoint + " wss=" + relay.IsWebSocket + " secure=" + relay.IsSecure : ""));
        }

        /// <summary>Ghost spawn queue length, or -1 when GhostSpawn has not created the queue.</summary>
        static int CountGhostSpawnBuffer(EntityManager em)
        {
            using var spawnQueue = em.CreateEntityQuery(typeof(GhostSpawnQueue));
            if (spawnQueue.CalculateEntityCount() != 1)
                return -1;
            Entity queue = spawnQueue.GetSingletonEntity();
            if (!em.HasBuffer<GhostSpawnBuffer>(queue))
                return -1;
            return em.GetBuffer<GhostSpawnBuffer>(queue).Length;
        }

        /// <summary>Resets client worlds and UI after dedicated connect timeout.</summary>
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

        /// <summary>
        /// Marks <see cref="NetworkStreamInGame"/> on SERVER connection entities only.
        /// Client InGame is owned by <c>TitanOrbitGoInGameClientSystem</c> after seed hydrate —
        /// forcing it here made the loading bar move while zero asteroids spawned.
        /// </summary>
        static void RequestGoInGame(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            if (world == ClientServerBootstrap.ClientWorld)
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

        /// <summary>Loopback LAN bind on <paramref name="port"/> — resets stale Relay/dedicated listen state first.</summary>
        static void ListenLocalLanServer(World world, ushort port)
        {
            if (world == null || !world.IsCreated)
                return;

            TitanOrbitRelayState.Clear();
            if (IsServerWorldListening(world))
                ResetServerDriverIfNeeded();

            var driver = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingletonRW<NetworkStreamDriver>();
            bool listenOk = driver.ValueRW.Listen(NetworkEndpoint.AnyIpv4.WithPort(port));
            if (!listenOk)
                Debug.LogError("[TitanOrbitSessionManager] LAN Listen failed on port " + port + ".");

            TickServerWorld(world);
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

        /// <summary>
        /// Connects the in-process client to the local server. Prefers the server's IPC
        /// <see cref="NetworkStreamDriver.GetLocalEndPoint"/> so Client+Server Local Host uses
        /// NetCode's zero-latency IPC path (not UDP loopback). UDP Loopback:port is the fallback
        /// when no IPC driver is listening (e.g. remote-only server layout).
        /// </summary>
        static void ConnectLocalClient(ushort port)
        {
            var clientWorld = ClientServerBootstrap.ClientWorld;
            if (clientWorld == null || !clientWorld.IsCreated) return;
            var em = clientWorld.EntityManager;
            if (em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0)
                return;

            // --- Prefer IPC endpoint from the in-process server (Client+Server Local Host) ---
            // [NETCODE] IPC: NetworkTimeSystem uses TargetCommandSlack=0 and 1-tick RTT. UDP loopback
            // was leaving ServerCommandAge ≈ +24 and metronomic 12-tick prediction snaps.
            // Prefer IPC when an in-process ServerWorld is listening (Local Host).
            // MPPM Player 2+ must use UDP loopback — IPC in that process is the clone's own
            // leftover ServerWorld, not the main Editor host.
            NetworkEndpoint endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(port);
            bool mppmClone = TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance();
            var server = ClientServerBootstrap.ServerWorld;
            if (!mppmClone && server != null && server.IsCreated)
            {
                using var serverQ = server.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
                if (serverQ.TryGetSingleton(out NetworkStreamDriver serverDriver))
                {
                    ref var store = ref serverDriver.DriverStore;
                    for (int i = store.FirstDriver; i < store.LastDriver; ++i)
                    {
                        if (store.GetDriverType(i) != TransportType.IPC)
                            continue;
                        NetworkEndpoint ipcEp = serverDriver.GetLocalEndPoint(i);
                        if (ipcEp.IsValid)
                            endpoint = ipcEp;
                        break;
                    }
                }
            }

            Debug.Log("[TitanOrbitSessionManager] ConnectLocalClient " + endpoint +
                      (mppmClone ? " (MPPM clone — UDP to host)" : " (Local Host IPC if available)"));

            var driver = em.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingletonRW<NetworkStreamDriver>();
            driver.ValueRW.Connect(em, endpoint);
        }

        /// <summary>
        /// Opens the Relay connection on an already-reset client driver.
        /// Returns the connection entity, or <see cref="Entity.Null"/> when connect was skipped.
        /// </summary>
        internal static Entity ConnectRelayClient(World world)
        {
            if (!TitanOrbitRelayState.TryGetClientRelay(out var relay))
                return Entity.Null;
            var em = world.EntityManager;
            using var connections = em.CreateEntityQuery(typeof(NetworkStreamConnection));
            if (connections.CalculateEntityCount() > 0)
                return Entity.Null;
            using var driverQuery = em.CreateEntityQuery(typeof(NetworkStreamDriver));
            var driver = driverQuery.GetSingletonRW<NetworkStreamDriver>();
            return driver.ValueRW.Connect(em, relay.Endpoint);
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

        /// <summary>
        /// Removes leftover client connection entities so the WebSocket driver can be rebuilt.
        /// </summary>
        /// <param name="em">Client world entity manager. Call this from the init-group system, before receive runs.</param>
        /// <returns>How many connection entities were released. Zero means the driver was already clear.</returns>
        /// <remarks>
        /// [NETCODE] <see cref="NetworkStreamConnection"/> is cleanup data. <c>DestroyEntity</c> does
        /// not remove it, so the entity stays in the query. <c>ResetDriverStore</c> then throws
        /// ("connections still present") before it disposes the old socket. On WebGL that throw
        /// aborts the reconnect, and the browser keeps the dead WebSocket until a full page refresh.
        /// Stripping the cleanup components lets the entity actually die. The caller then disposes
        /// the driver, which closes the JavaScript socket.
        /// </remarks>
        internal static int ForceReleaseClientConnectionEntities(EntityManager em)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            if (query.IsEmptyIgnoreFilter)
                return 0;

            using var entities = query.ToEntityArray(Allocator.Temp);
            int released = entities.Length;
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                if (!em.Exists(entity))
                    continue;

                // --- Strip cleanup, then destroy ---
                // A normal disconnect removes these from NetworkStreamReceiveSystem. A dropped
                // WebSocket can leave Value uncreated, and that system then returns without
                // cleaning up. The entity would block every later Join.
                using (var types = em.GetComponentTypes(entity, Allocator.Temp))
                {
                    for (int t = 0; t < types.Length; t++)
                    {
                        if (!em.Exists(entity))
                            break;
                        if (!types[t].IsCleanupComponent)
                            continue;
                        em.RemoveComponent(entity, types[t]);
                    }
                }

                if (em.Exists(entity))
                    em.DestroyEntity(entity);
            }

            return released;
        }

        /// <summary>
        /// Disposes the client transport and builds a new one from the current Relay allocation.
        /// </summary>
        /// <returns>False when there is no client world, no driver, or a connection entity is still alive.</returns>
        /// <remarks>
        /// [NETCODE] Call this only when no <see cref="NetworkStreamConnection"/> exists.
        /// <c>ResetDriverStore</c> throws in that case, and it throws before disposing the new
        /// store, which leaks a WebSocket. On WebGL the browser will not recover that socket
        /// until the page reloads. The WebGL init system releases stuck connections first.
        /// </remarks>
        internal static bool ResetClientDriverIfNeeded()
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var driverQuery = em.CreateEntityQuery(typeof(NetworkStreamDriver));
            if (!driverQuery.TryGetSingletonEntity<NetworkStreamDriver>(out var entity))
                return false;

            // --- Refuse while a connection entity is still registered ---
            // Building the replacement store and then throwing leaks that store. Skip so the
            // caller can drop the entities and try again on a later initialization tick.
            using var connections = em.CreateEntityQuery(typeof(NetworkStreamConnection));
            if (!connections.IsEmptyIgnoreFilter)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] Client driver reset skipped — " +
                                 connections.CalculateEntityCount() +
                                 " NetworkStreamConnection(s) still present.");
                return false;
            }

            var driver = em.GetComponentData<NetworkStreamDriver>(entity);
            var store = new NetworkDriverStore();
            using var debugQuery = em.CreateEntityQuery(typeof(NetDebug));
            var netDebug = debugQuery.GetSingleton<NetDebug>();
            new TitanOrbitRelayDriverConstructor().CreateClientDriver(world, ref store, netDebug);
            // DriverStore is a pointer inside the singleton, so this writes the live driver
            // even though GetComponentData returned a copy.
            driver.ResetDriverStore(world.Unmanaged, ref store);
            return true;
        }

        /// <summary>
        /// [NETCODE] Client rejoin flow: resume control of persisted ship from prior session.
        /// Sends <see cref="ResumeExistingShipCommand"/> RPC to server.
        /// </summary>
        public void RequestResumeExistingShip()
        {
            if (!SendRejoinShipRpc<ResumeExistingShipCommand>())
                LastStatusMessage = "Could not resume your ship.";
        }

        /// <summary>
        /// [NETCODE] Client rejoin flow: destroy persisted ship and return to team picker.
        /// Sends <see cref="AbandonShipForRejoinCommand"/> RPC to server.
        /// </summary>
        public void RequestAbandonShipForRejoin()
        {
            if (!SendRejoinShipRpc<AbandonShipForRejoinCommand>())
                LastStatusMessage = "Could not abandon your saved ship.";
        }

        /// <summary>[NETCODE] Sends rejoin resume/abandon RPC from local connection entity.</summary>
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

            if (IsLocalHostWorldsReady() &&
                TryReadLocalHostNetworkId(world, out int networkId) &&
                TryEnqueueLocalHostRejoinRpc<T>(networkId))
            {
                Debug.Log("[TitanOrbitSessionManager] Enqueued rejoin RPC " + typeof(T).Name +
                          " on ServerWorld (Local Host).");
                return true;
            }

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, default(T));
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            Debug.Log("[TitanOrbitSessionManager] Sent rejoin RPC " + typeof(T).Name + ".");
            return true;
        }

        /// <summary>
        /// [NETCODE] Team picker UI calls this to request a team assignment and ship spawn.
        /// Requires client in-game with active network connection.
        /// <para>
        /// [TITAN-ORBIT] Local Host queues the command directly on <c>ServerWorld</c> with
        /// <see cref="ReceiveRpcCommandRequest"/> so <see cref="TeamManagementSystem"/> runs without
        /// IPC. Under join Instantiates load, client→server <see cref="SendRpcCommandRequest"/> can
        /// occasionally never arrive — Console shows RequestTeam, no TeamManagement spawn, UI stuck
        /// on "Spawning your ship..." (Editor.log 2026-07-30). Dedicated clients still SendRpc.
        /// </para>
        /// </summary>
        /// <param name="team">Requested team assignment.</param>
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

            // --- Optimistic UI latch ---
            // Block late-arriving ship ghosts from opening the rejoin screen after a normal team pick.
            ClientTeamFlowState.NotifyTeamPickRequested(team);

            // --- Drop stale local-ship seed from a prior Play / failed TeamChoice ---
            // [TITAN-ORBIT] Domain Reload disabled: SeededShip can stay non-null while destroyed.
            // Watchdog retries call RequestTeam again — do not Clear a live hull that already
            // Instantiated via GhostReceive (late Join Team click: seed exists, Result still in flight).
            bool keepLiveHull = LocalShipEntitySeed.HasLiveOwnedShipSeed(world.EntityManager) &&
                                ClientTeamFlowState.LastRequestedTeam == team;
            if (!keepLiveHull)
            {
                LocalShipEntitySeed.Clear();
                ClientPredictedShipSpawnRequest.ResetForTeamPick();
            }

            // --- Pre-arm ship Instantiates hold before the server can reply ---
            // [TITAN-ORBIT] Player.log 2026-07-31: TeamChoiceResult → Crash!!! the same frame.
            // Server spawn + TeamChoiceResultRpc often share one snapshot; GhostSpawn can
            // Instantiates the hull *before* TeamChoiceResultClientSystem runs Arm. Predicted
            // Burst drive then ScheduleParallel on a mid-Instantiates archetype → native Crash!!!.
            // Arming here (Join Team click) closes that RTT/same-frame race. TeamChoiceResult
            // re-Arms to keep Deferred Confirm suppressed for the full hold window.
            ClientJoinSettleCache.ArmPostTeamChoiceHold();

            int networkId = GetLocalNetworkId(world);

            // --- Local Host: inject onto ServerWorld (no IPC) ---
            // [TITAN-ORBIT] Same idea as MoonOrbitRpcClient Local Host bypass — SendRpc under
            // Instantiates pressure can drop; TeamManagement never sees ReceiveRpcCommandRequest.
            // Inline Local Host check (avoid NetCode→Game asmdef cycle via EcsGameBridge).
            if (IsLocalHostWorldsReady() && TryEnqueueLocalHostTeamRequest(networkId, team))
            {
                Debug.Log(
                    $"[TitanOrbitSessionManager] RequestTeam {team} (networkId={networkId}) " +
                    "enqueued on ServerWorld (Local Host — skip IPC).");
                return;
            }

            // --- Dedicated / remote / Local Host fallback: ClientWorld SendRpc ---
            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new RequestTeamCommand
            {
                NetworkId = networkId,
                RequestedTeam = (byte)team,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            Debug.Log($"[TitanOrbitSessionManager] RequestTeam {team} (networkId={networkId}).");
        }

        /// <summary>
        /// [TITAN-ORBIT] Creates a <see cref="RequestTeamCommand"/> already tagged with
        /// <see cref="ReceiveRpcCommandRequest"/> on the server world so
        /// <see cref="TeamManagementSystem"/> processes it on the next server tick without
        /// waiting for transport delivery.
        /// </summary>
        /// <param name="networkId">Local player's NetCode network id.</param>
        /// <param name="team">Requested team.</param>
        /// <returns>True when the server connection was found and the RPC entity was created.</returns>
        static bool TryEnqueueLocalHostTeamRequest(int networkId, TitanOrbit.Core.TeamId team)
        {
            // --- Resolve ServerWorld ---
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;
            if (networkId <= 0)
                return false;

            var em = server.EntityManager;

            // --- Find this player's connection entity on the server ---
            // [NETCODE] TeamManagementSystem needs SourceConnection for CommandTarget + result RPC.
            Entity connection = Entity.Null;
            using (var query = em.CreateEntityQuery(
                       ComponentType.ReadOnly<NetworkId>(),
                       ComponentType.ReadOnly<NetworkStreamInGame>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            using (var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp))
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i].Value != networkId)
                        continue;
                    connection = entities[i];
                    break;
                }
            }

            if (connection == Entity.Null)
            {
                Debug.LogWarning(
                    "[TitanOrbitSessionManager] Local Host team enqueue: no server NetworkStreamInGame " +
                    $"for networkId={networkId} — falling back to ClientWorld SendRpc.");
                return false;
            }

            // --- Mimic a delivered RPC (ReceiveRpcCommandRequest already present) ---
            var rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, new RequestTeamCommand
            {
                NetworkId = networkId,
                RequestedTeam = (byte)team,
            });
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        /// <summary>
        /// Local Host: deliver resume / abandon on ServerWorld without IPC.
        /// </summary>
        static bool TryEnqueueLocalHostRejoinRpc<T>(int networkId) where T : unmanaged, IRpcCommand
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated || networkId <= 0)
                return false;

            var em = server.EntityManager;
            Entity connection = Entity.Null;
            using (var query = em.CreateEntityQuery(
                       ComponentType.ReadOnly<NetworkId>(),
                       ComponentType.ReadOnly<NetworkStreamInGame>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            using (var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp))
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i].Value != networkId)
                        continue;
                    connection = entities[i];
                    break;
                }
            }

            if (connection == Entity.Null)
                return false;

            Entity rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, default(T));
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        static bool TryReadLocalHostNetworkId(World client, out int networkId)
        {
            networkId = 0;
            if (client == null || !client.IsCreated)
                return false;

            var em = client.EntityManager;
            using var ids = em.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkStreamConnection>(),
                    ComponentType.ReadOnly<NetworkStreamInGame>(),
                    ComponentType.ReadOnly<NetworkId>())
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            if (ids.Length == 0 || ids[0].Value <= 0)
                return false;

            networkId = ids[0].Value;
            return true;
        }

        /// <summary>
        /// True when this process has both ClientWorld and ServerWorld ready for Local Host play.
        /// Mirrors <c>EcsGameBridge.IsLocalHost</c> without referencing the Game assembly.
        /// </summary>
        static bool IsLocalHostWorldsReady()
        {
            // Dedicated Relay clients never inject onto a local ServerWorld.
            if (IsDedicatedOnlineClient)
                return false;

            // MPPM Player 2+ must SendRpc to the main Editor host, even if a leftover ServerWorld exists.
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                return false;

            var client = ClientServerBootstrap.ClientWorld;
            var server = ClientServerBootstrap.ServerWorld;
            return client != null && client.IsCreated &&
                   server != null && server.IsCreated &&
                   IsClientGameplayReady(client) &&
                   IsClientConnectionReady(server);
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
