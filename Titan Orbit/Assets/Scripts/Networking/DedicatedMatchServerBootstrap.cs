using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using TitanOrbit.Services;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Multiplayer;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using TitanOrbit.Data;
using TitanOrbit.Diagnostics;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Dedicated server bootstrap (server-only): creates one Relay allocation + one UGS Lobby, then starts Netcode server.
    ///
    /// This script is intended to run in a dedicated server build (headless) and is started automatically on scene load.
    /// Match rotation is handled by extending this script in a later step.
    /// </summary>
    public static class DedicatedMatchServerBootstrap
    {
        private const string LobbyRelayCodeKey = "RelayJoinCode";
        private const string LobbyGameNameKey = "GameName";
        private const string LobbyGameNameValue = "TitanOrbit";
        private const string LobbyIsOpenKey = "IsOpen";
        private const string LobbyIsLatestKey = "IsLatest";
        private const string LobbyCreatedAtEpochKey = "CreatedAtEpoch";
        private const string LobbyRelayProtocolKey = "RelayProtocol";
        private const string LobbyServerListenAddressKey = "ServerListenAddress";
        private static int _dbgWatchdogFailCount;
        private static string _activeLobbyId;
        private static int _matchMaxPlayers;
        private static ushort _matchServerPort;
        private static string _matchRelayProtocol;
        private static bool _matchIsLatest;
        private static DateTime? _emptySinceUtc;
        private static bool _recreateEmptyMatchInProgress;
        /// <summary>After this many seconds with zero connected players, close the lobby and publish a new one (same process).</summary>
        private const int DefaultEmptyMatchRecreateSeconds = 30 * 60;

        /// <summary>Called from <see cref="Core.MatchManager"/> when a dedicated match ends so new players cannot join a finished game.</summary>
        public static void NotifyDedicatedMatchEnded()
        {
            if (string.IsNullOrWhiteSpace(_activeLobbyId))
                return;
            _ = CloseLobbyForNewJoinersAsync(_activeLobbyId, "match_ended");
        }

        /// <summary>
        /// Linux GCE builds use the Dedicated Server player subtarget (compile-time <c>UNITY_SERVER</c>);
        /// those processes are not guaranteed to set <see cref="Application.isBatchMode"/> unless <c>-batchmode</c> is passed.
        /// Without this gate, the dedicated bootstrap never runs and no UGS lobby is created.
        /// </summary>
        private static bool IsDedicatedMatchServerProcess()
        {
#if UNITY_EDITOR
            return false;
#else
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return false;
#if UNITY_SERVER
            return true;
#else
            if (Application.isBatchMode)
                return true;
            return HasTitanOrbitDedicatedCliFlag();
#endif
#endif
        }

        private static bool HasTitanOrbitDedicatedCliFlag()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg == null)
                    continue;
                if (string.Equals(arg, "--titanOrbitDedicated", StringComparison.OrdinalIgnoreCase))
                    return true;
                const string prefix = "--titanOrbitDedicated=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string v = arg.Substring(prefix.Length);
                    return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        /// <summary>
        /// Logs before the first scene loads so headless SSH sessions don't look "stuck" after engine init lines.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BootMarkerBeforeSceneLoad()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return;
            if (!IsDedicatedMatchServerProcess())
                return;

            string cmd = Environment.CommandLine ?? string.Empty;
            if (cmd.Length > 1800)
                cmd = cmd.Substring(0, 1797) + "...";
            DedicatedServerFileLog.Append(
                "boot",
                "BeforeSceneLoad pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                " batchMode=" + Application.isBatchMode +
#if UNITY_SERVER
                " build=UNITY_SERVER" +
#else
                " build=player" +
#endif
                " cmdline=" + cmd);
            Debug.Log("[DedicatedMatchServerBootstrap] BeforeSceneLoad: dedicated server bootstrap will run AfterSceneLoad.");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return;
            if (!IsDedicatedMatchServerProcess())
                return;

            DedicatedServerFileLog.Append("boot", "AfterSceneLoad Init scheduling BootAsync (Relay + UGS Lobby + Netcode).");
            Debug.Log("[DedicatedMatchServerBootstrap] AfterSceneLoad: scheduling BootAsync...");
            _ = BootAsyncWithRetriesAsync();
        }

        private static int GetArgInt(string name, int defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg != null && arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Substring(prefix.Length), out int parsed))
                        return parsed;
                }
            }
            return defaultValue;
        }

        private static string GetArgString(string name, string defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg != null && arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(prefix.Length);
            }
            return defaultValue;
        }

        /// <summary>Align with <see cref="NetworkGameManager"/>: legacy <c>udp</c> is stored/joined as <c>dtls</c> for MPS 2.0 Relay.</summary>
        private static string SanitizeRelayProtocolForSdk(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "dtls";
            string x = raw.Trim().ToLowerInvariant();
            if (x == "wss")
                return "wss";
            if (x == "udp" || x == "dtls")
                return "dtls";
            return "dtls";
        }

        private static bool GetArgBool(string name, bool defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == null || !arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string raw = arg.Substring(prefix.Length);
                if (bool.TryParse(raw, out bool parsedBool))
                    return parsedBool;

                if (int.TryParse(raw, out int parsedInt))
                    return parsedInt != 0;
            }

            return defaultValue;
        }

        /// <summary>Wraps an async UGS/network call so SSH foreground runs don't hang forever with no log line.</summary>
        private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string operationName)
        {
            Task delay = Task.Delay(timeout);
            Task finished = await Task.WhenAny(task, delay);
            if (finished == delay)
            {
                Debug.LogError($"[DedicatedMatchServerBootstrap] TIMEOUT after {timeout.TotalSeconds:0}s: {operationName}");
                throw new TimeoutException(operationName);
            }
            return await task;
        }

        private static async Task WithTimeoutAsync(Task task, TimeSpan timeout, string operationName)
        {
            Task delay = Task.Delay(timeout);
            Task finished = await Task.WhenAny(task, delay);
            if (finished == delay)
            {
                Debug.LogError($"[DedicatedMatchServerBootstrap] TIMEOUT after {timeout.TotalSeconds:0}s: {operationName}");
                throw new TimeoutException(operationName);
            }
            await task;
        }

        private static async Task<bool> EnsureUnityServicesInitializedAsync(TimeSpan initTimeout, TimeSpan signInTimeout)
        {
            try
            {
                if (UnityServices.State == ServicesInitializationState.Initialized &&
                    AuthenticationService.Instance.IsSignedIn &&
                    AuthenticationService.Instance.IsAuthorized)
                {
                    Debug.Log("[DedicatedMatchServerBootstrap] Unity Services already initialized and signed in.");
                    return true;
                }

                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    Debug.Log("[DedicatedMatchServerBootstrap] Initializing Unity Services...");
                    await WithTimeoutAsync(UnityGameServicesBootstrap.InitializeUnityServicesAsync(), initTimeout, "UnityServices.InitializeAsync");
                }

                if (!AuthenticationService.Instance.IsSignedIn || !AuthenticationService.Instance.IsAuthorized)
                {
                    Debug.Log("[DedicatedMatchServerBootstrap] Signing in anonymously...");
                    await WithTimeoutAsync(UnityGameServicesBootstrap.SignInGuestAsync(), signInTimeout, "AuthenticationService.SignInAnonymouslyAsync");
                }

                Debug.Log("[DedicatedMatchServerBootstrap] Unity Services ready.");
                return true;
            }
            catch (Exception e)
            {
                DedicatedServerFileLog.Append("ugs", "Unity Services init or sign-in failed.", e);
                Debug.LogWarning("[DedicatedMatchServerBootstrap] Unity Services failed. " + e.Message);
                return false;
            }
        }

        private static void EnsurePlayerPrefabSet()
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab != null)
                return;

            GameObject fallback = Resources.Load<GameObject>("Prefabs/Starship");
            if (fallback != null && fallback.GetComponent<NetworkObject>() != null)
            {
                NetworkManager.Singleton.NetworkConfig.PlayerPrefab = fallback;
                Debug.Log("[DedicatedMatchServerBootstrap] Player Prefab missing; assigned from Resources/Prefabs/Starship.");
            }
        }

        private static bool HasMissingScripts(GameObject prefab)
        {
            if (prefab == null)
                return true;

            var behaviours = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null)
                    return true;
            }

            return false;
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null)
                return null;

            var type = target.GetType();
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
                return prop.GetValue(target);

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null ? field.GetValue(target) : null;
        }

        private static bool TryExtractPrefab(object entry, out GameObject prefab)
        {
            prefab = null;
            if (entry == null)
                return false;

            if (entry is GameObject go)
            {
                prefab = go;
                return true;
            }

            var value = GetMemberValue(entry, "Prefab");
            if (value is GameObject prefabGo)
            {
                prefab = prefabGo;
                return true;
            }

            return false;
        }

        private static int SanitizePrefabList(IList list, string listName)
        {
            if (list == null)
                return 0;

            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var entry = list[i];
                if (!TryExtractPrefab(entry, out var prefab))
                    continue;

                if (prefab == null || HasMissingScripts(prefab))
                {
                    string prefabName = prefab != null ? prefab.name : "<null>";
                    Debug.LogWarning($"[DedicatedMatchServerBootstrap] Removing invalid network prefab from {listName}: {prefabName}");
                    list.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        private static void SanitizeNetworkPrefabs()
        {
            if (NetworkManager.Singleton == null)
                return;

            int removed = 0;
            var config = NetworkManager.Singleton.NetworkConfig;

            // NGO versions differ in where prefab lists are stored; sanitize both common shapes.
            if (GetMemberValue(config, "Prefabs") is IList prefabsList)
                removed += SanitizePrefabList(prefabsList, "NetworkConfig.Prefabs");

            if (GetMemberValue(config, "NetworkPrefabs") is IList networkPrefabsList)
                removed += SanitizePrefabList(networkPrefabsList, "NetworkConfig.NetworkPrefabs");

            var nestedLists = GetMemberValue(config, "NetworkPrefabsLists") as IList;
            if (nestedLists != null)
            {
                for (int i = 0; i < nestedLists.Count; i++)
                {
                    var nested = nestedLists[i];
                    if (nested == null)
                        continue;

                    if (GetMemberValue(nested, "List") is IList innerList)
                        removed += SanitizePrefabList(innerList, $"NetworkPrefabsLists[{i}].List");
                }
            }

            if (removed > 0)
                Debug.Log($"[DedicatedMatchServerBootstrap] Sanitized network prefabs. Removed invalid entries: {removed}");
        }

        /// <summary>
        /// After a cold VM start or systemd restart, <see cref="NetworkManager"/> may not exist yet on the first frame after scene load.
        /// </summary>
        private static async Task<bool> WaitForNetworkManagerAndPlayerPrefabAsync()
        {
            int waitSeconds = GetArgInt("waitNetworkManagerSeconds", 120);
            waitSeconds = Mathf.Max(10, waitSeconds);
            DateTime deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
            DedicatedServerFileLog.Append(
                "boot",
                "Waiting for NetworkManager + player prefab (up to " + waitSeconds + "s; increase with --waitNetworkManagerSeconds= if needed).");
            int iteration = 0;
            while (DateTime.UtcNow < deadline)
            {
                if (NetworkManager.Singleton != null)
                {
                    EnsurePlayerPrefabSet();
                    SanitizeNetworkPrefabs();
                    var cfg = NetworkManager.Singleton.NetworkConfig;
                    if (cfg != null && cfg.PlayerPrefab != null)
                    {
                        DedicatedServerFileLog.Append("boot", "NetworkManager and PlayerPrefab ready.");
                        return true;
                    }
                }

                if (iteration % 20 == 0)
                {
                    DedicatedServerFileLog.Append(
                        "boot",
                        "Still waiting… nmSingletonNull=" + (NetworkManager.Singleton == null ? "1" : "0"));
                }

                iteration++;
                await Task.Delay(250);
            }

            DedicatedServerFileLog.Append(
                "boot",
                "Timed out after " + waitSeconds + "s: NetworkManager or PlayerPrefab never became ready.");
            return false;
        }

        /// <summary>
        /// Retries initial match creation (UGS + Relay + Lobby + Netcode) so a VM that boots before networking is ready can still publish a lobby.
        /// </summary>
        private static async Task BootAsyncWithRetriesAsync()
        {
            if (!await WaitForNetworkManagerAndPlayerPrefabAsync())
            {
                Debug.LogError(
                    "[DedicatedMatchServerBootstrap] NetworkManager or Player Prefab did not become ready in time. " +
                    "See TitanOrbitDedicatedServer.log and increase --waitNetworkManagerSeconds= if the scene loads slowly.");
                Application.Quit(1);
                return;
            }

            EnsurePlayerPrefabSet();
            SanitizeNetworkPrefabs();
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                DedicatedServerFileLog.Append("boot", "Abort: NetworkManager or Player Prefab missing after wait.");
                Debug.LogError("[DedicatedMatchServerBootstrap] Player Prefab not set on NetworkManager.");
                Application.Quit(1);
                return;
            }

            int maxAttempts = GetArgInt("bootMaxAttempts", 15);
            int delaySeconds = GetArgInt("bootRetryDelaySeconds", 5);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Debug.Log($"[DedicatedMatchServerBootstrap] Boot attempt {attempt}/{maxAttempts}...");
                    await TryStartInitialMatchAsync();
                    return;
                }
                catch (Exception e)
                {
                    DedicatedServerFileLog.Append("boot", "Boot attempt " + attempt + "/" + maxAttempts + " failed.", e);
                    Debug.LogWarning(
                        "[DedicatedMatchServerBootstrap] Boot attempt " + attempt + "/" + maxAttempts + " failed: " +
                        e.GetType().Name + ": " + e.Message);
                    if (attempt >= maxAttempts)
                    {
                        Debug.LogException(e);
                        Application.Quit(1);
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(1, delaySeconds)));
                }
            }
        }

        /// <summary>
        /// One attempt: sign in, Relay allocation, UGS lobby, StartServer, background loops.
        /// Dedicated Linux/Windows/Mac hosts should use <c>udp</c> (or <c>dtls</c>) to Relay; WebGL clients join the same allocation with <c>wss</c>.
        /// </summary>
        private static async Task TryStartInitialMatchAsync()
        {
            int maxPlayers = GetArgInt("maxPlayers", 60);
            ushort serverPort = (ushort)GetArgInt("serverPort", 7777);
            string relayProtocol = SanitizeRelayProtocolForSdk(GetArgString("relayProtocol", "dtls"));
            bool isLatest = GetArgBool("isLatest", true);
            long ageThresholdSeconds = GetArgInt("ageThresholdSeconds", 30 * 60);
            int emptyMatchRecreateSeconds = GetArgInt("emptyMatchRecreateSeconds", DefaultEmptyMatchRecreateSeconds);
            emptyMatchRecreateSeconds = Mathf.Max(60, emptyMatchRecreateSeconds);
            int ugsInitTimeoutMs = GetArgInt("ugsInitTimeoutMs", 120000);
            int ugsSignInTimeoutMs = GetArgInt("ugsSignInTimeoutMs", 60000);
            int relayAllocTimeoutMs = GetArgInt("relayAllocTimeoutMs", 60000);
            int lobbyCreateTimeoutMs = GetArgInt("lobbyCreateTimeoutMs", 60000);
            string serverListenAddress = GetArgString("serverListenAddress", "0.0.0.0");

            Debug.Log(
                "[DedicatedMatchServerBootstrap] TryStartInitialMatchAsync. maxPlayers=" + maxPlayers +
                " serverPort=" + serverPort + " relayProtocol=" + relayProtocol + " serverListenAddress=" + serverListenAddress + " isLatest=" + isLatest);

            if (!await EnsureUnityServicesInitializedAsync(
                    TimeSpan.FromMilliseconds(ugsInitTimeoutMs),
                    TimeSpan.FromMilliseconds(ugsSignInTimeoutMs)))
            {
                throw new InvalidOperationException("Cannot initialize Unity Services on server.");
            }

            try
            {
                string authPid = AuthenticationService.Instance.PlayerId;
                string prefix = string.IsNullOrEmpty(authPid) ? "" : (authPid.Length <= 10 ? authPid : authPid.Substring(0, 10) + "…");
                DedicatedServerFileLog.Append(
                    "ugs",
                    "Unity Services ready. playerIdPrefix=" + prefix + " cloudProjectId=" + (Application.cloudProjectId ?? ""));
            }
            catch (Exception logEx)
            {
                DedicatedServerFileLog.Append("ugs", "Post-sign-in diagnostic log failed (non-fatal).", logEx);
            }

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport == null)
            {
                throw new InvalidOperationException("UnityTransport not found on NetworkManager.");
            }

            transport.SetConnectionData(transport.ConnectionData.Address, serverPort, serverListenAddress);

            int maxConnections = Mathf.Max(1, maxPlayers - 1);
            Debug.Log("[DedicatedMatchServerBootstrap] Creating Relay allocation (maxConnections=" + maxConnections + ")...");
            Allocation allocation = await WithTimeoutAsync(
                RelayService.Instance.CreateAllocationAsync(maxConnections),
                TimeSpan.FromMilliseconds(relayAllocTimeoutMs),
                "RelayService.CreateAllocationAsync");
            Debug.Log("[DedicatedMatchServerBootstrap] Requesting Relay join code...");
            string joinCode = await WithTimeoutAsync(
                RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId),
                TimeSpan.FromMilliseconds(relayAllocTimeoutMs),
                "RelayService.GetJoinCodeAsync");

            transport.UseWebSockets = string.Equals(relayProtocol, "wss", StringComparison.OrdinalIgnoreCase);
            RelayProtocol relayProto = relayProtocol == "wss" ? RelayProtocol.WSS : RelayProtocol.DTLS;
            transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, relayProto));
            // UTP heartbeats (HeartbeatTimeoutMS) keep the Relay allocation alive while waiting for joiners.
            // Do not swap allocations on a timer — SetRelayServerData breaks the running match even without Shutdown().
            NetworkGameManager.ApplyRelayFriendlyTransportSettings(transport);

            DeferredPlayerShipSpawn.Configure(NetworkManager.Singleton);

            // Start NGO before publishing the lobby so clients can never join a non-running server advertisement.
            bool started = NetworkManager.Singleton.StartServer();
            if (!started)
            {
                throw new InvalidOperationException("Netcode StartServer returned false.");
            }

            long createdAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Debug.Log("[DedicatedMatchServerBootstrap] Creating UGS Lobby...");
            Lobby createdLobby = null;
            try
            {
                createdLobby = await WithTimeoutAsync(
                    LobbyService.Instance.CreateLobbyAsync(
                        GameNames.GetRandomRoomName(),
                        maxPlayers,
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
                                        createdAtEpochSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        DataObject.IndexOptions.N3
                                    )
                                },
                                { LobbyRelayProtocolKey, new DataObject(DataObject.VisibilityOptions.Public, relayProtocol) },
                                { LobbyServerListenAddressKey, new DataObject(DataObject.VisibilityOptions.Public, serverListenAddress) }
                            }
                        }),
                    TimeSpan.FromMilliseconds(lobbyCreateTimeoutMs),
                    "LobbyService.CreateLobbyAsync");
            }
            catch
            {
                // If lobby publication fails, tear down NGO so retry can start cleanly.
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                    NetworkManager.Singleton.Shutdown();
                throw;
            }

            Debug.Log("[DedicatedMatchServerBootstrap] Starting server for lobby " + createdLobby.Id + " (isLatest=" + isLatest + ").");
            DedicatedServerFileLog.Append(
                "lobby",
                "UGS lobby published id=" + createdLobby.Id + " name=" + (createdLobby.Name ?? "") + " isLatest=" + isLatest +
                " maxPlayers=" + maxPlayers + " relayJoinCodeLen=" + (joinCode != null ? joinCode.Length : 0));

            _activeLobbyId = createdLobby.Id;
            _matchMaxPlayers = maxPlayers;
            _matchServerPort = serverPort;
            _matchRelayProtocol = relayProtocol;
            _matchIsLatest = isLatest;
            _emptySinceUtc = DateTime.UtcNow;
            _ = HeartbeatLoopAsync();
            _ = LobbyPresenceWatchdogAsync();
            _ = NetcodeHealthLoopAsync();
            _ = RotationLoopAsync(
                createdAtEpochSeconds,
                maxPlayers,
                serverPort,
                relayProtocol,
                isLatest,
                ageThresholdSeconds,
                emptyMatchRecreateSeconds
            );
        }

        private static async Task CloseLobbyForNewJoinersAsync(string lobbyId, string reason)
        {
            if (string.IsNullOrWhiteSpace(lobbyId))
                return;
            try
            {
                await UpdateLobbyFlagsAsync(lobbyId, isOpen: false, isLatest: false);
                DedicatedServerFileLog.Append("lobby", "Closed lobby for new joiners (" + reason + ") id=" + lobbyId);
                Debug.Log("[DedicatedMatchServerBootstrap] Lobby closed for new joiners (" + reason + "): " + lobbyId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DedicatedMatchServerBootstrap] CloseLobbyForNewJoiners failed: " + e.Message);
            }
        }

        private static async Task CloseLobbyAndExitAsync(string lobbyId, string reason)
        {
            await CloseLobbyForNewJoinersAsync(lobbyId, reason);
            DedicatedServerFileLog.Append("watchdog", reason + "; exiting process.");
            Application.Quit(1);
        }

        /// <summary>
        /// If the lobby disappears (expired, deleted, heartbeat loss), exit so systemd can restart and recreate a match.
        /// </summary>
        private static async Task LobbyPresenceWatchdogAsync()
        {
            int consecutiveFailures = 0;
            const int threshold = 4;
            var interval = TimeSpan.FromSeconds(45);

            while (true)
            {
                await Task.Delay(interval);
                try
                {
                    string lobbyId = _activeLobbyId;
                    if (string.IsNullOrWhiteSpace(lobbyId))
                        continue;
                    await LobbyService.Instance.GetLobbyAsync(lobbyId);
                    consecutiveFailures = 0;
                }
                catch (Exception e)
                {
                    consecutiveFailures++;
                    _dbgWatchdogFailCount++;
                    Debug.LogWarning(
                        "[DedicatedMatchServerBootstrap] LobbyPresenceWatchdog GetLobby failed (" + consecutiveFailures + "/" +
                        threshold + "): " + e.Message);
                    if (consecutiveFailures >= threshold)
                    {
                        Debug.LogError(
                            "[DedicatedMatchServerBootstrap] Lobby no longer reachable; exiting so the service can restart with a new lobby.");
                        DedicatedServerFileLog.Append("watchdog", "Lobby unreachable after " + consecutiveFailures + " failures; exiting process.");
                        Application.Quit(1);
                        return;
                    }
                }
            }
        }

        private static async Task HeartbeatLoopAsync()
        {
            // Lobbies require heartbeat pings to stay alive.
            // We match the existing NetworkGameManager heartbeat interval (15s).
            int ugsInitTimeoutMs = GetArgInt("ugsInitTimeoutMs", 120000);
            int ugsSignInTimeoutMs = GetArgInt("ugsSignInTimeoutMs", 60000);
            while (true)
            {
                try
                {
                    string lobbyId = _activeLobbyId;
                    if (!await EnsureUnityServicesInitializedAsync(
                            TimeSpan.FromMilliseconds(ugsInitTimeoutMs),
                            TimeSpan.FromMilliseconds(ugsSignInTimeoutMs)))
                    {
                        Debug.LogWarning("[DedicatedMatchServerBootstrap] Heartbeat skipped: Unity Services not ready.");
                    }
                    else if (!string.IsNullOrWhiteSpace(lobbyId))
                    {
                        await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[DedicatedMatchServerBootstrap] Heartbeat failed: " + e.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        }

        /// <summary>Exits when Netcode is no longer hosting so systemd can publish a fresh lobby + Relay allocation.</summary>
        private static async Task NetcodeHealthLoopAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                try
                {
                    if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
                    {
                        Debug.LogError(
                            "[DedicatedMatchServerBootstrap] Netcode server stopped listening; closing lobby and exiting.");
                        await CloseLobbyAndExitAsync(_activeLobbyId, "netcode_not_listening");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[DedicatedMatchServerBootstrap] NetcodeHealthLoop error: " + e.Message);
                }
            }
        }

        private static void TrackEmptyMatchTime(int playerCount)
        {
            if (playerCount > 0)
            {
                _emptySinceUtc = null;
                return;
            }

            if (!_emptySinceUtc.HasValue)
                _emptySinceUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// After a long idle period with no players, delete the old UGS lobby and publish a new one (same server process).
        /// </summary>
        private static async Task<bool> RecreateEmptyMatchInProcessAsync(
            int maxPlayers,
            string relayProtocol,
            bool isLatest,
            TimeSpan relayTimeout,
            int lobbyCreateTimeoutMs)
        {
            if (_recreateEmptyMatchInProgress)
                return false;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.ConnectedClients.Count > 0)
                return false;

            string oldLobbyId = _activeLobbyId;
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport == null || string.IsNullOrWhiteSpace(oldLobbyId))
                return false;

            _recreateEmptyMatchInProgress = true;
            try
            {
                await CloseLobbyForNewJoinersAsync(oldLobbyId, "empty_match_recreate");

                int maxConnections = Mathf.Max(1, maxPlayers - 1);
                Allocation allocation = await WithTimeoutAsync(
                    RelayService.Instance.CreateAllocationAsync(maxConnections),
                    relayTimeout,
                    "RelayService.CreateAllocationAsync(empty_recreate)");
                string joinCode = await WithTimeoutAsync(
                    RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId),
                    relayTimeout,
                    "RelayService.GetJoinCodeAsync(empty_recreate)");

                RelayProtocol relayProto = relayProtocol == "wss" ? RelayProtocol.WSS : RelayProtocol.DTLS;
                transport.UseWebSockets = string.Equals(relayProtocol, "wss", StringComparison.OrdinalIgnoreCase);
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, relayProto));
                NetworkGameManager.ApplyRelayFriendlyTransportSettings(transport);

                if (NetworkManager.Singleton.IsListening)
                    NetworkManager.Singleton.Shutdown();

                bool started = NetworkManager.Singleton.StartServer();
                if (!started)
                    throw new InvalidOperationException("Netcode StartServer returned false after empty match recreate.");

                long createdAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string serverListenAddress = GetArgString("serverListenAddress", "0.0.0.0");
                Lobby newLobby = await WithTimeoutAsync(
                    LobbyService.Instance.CreateLobbyAsync(
                        GameNames.GetRandomRoomName(),
                        maxPlayers,
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
                                        createdAtEpochSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        DataObject.IndexOptions.N3
                                    )
                                },
                                { LobbyRelayProtocolKey, new DataObject(DataObject.VisibilityOptions.Public, relayProtocol) },
                                { LobbyServerListenAddressKey, new DataObject(DataObject.VisibilityOptions.Public, serverListenAddress) }
                            }
                        }),
                    TimeSpan.FromMilliseconds(lobbyCreateTimeoutMs),
                    "LobbyService.CreateLobbyAsync(empty_recreate)");

                try
                {
                    await LobbyService.Instance.DeleteLobbyAsync(oldLobbyId);
                }
                catch (Exception deleteEx)
                {
                    Debug.LogWarning(
                        "[DedicatedMatchServerBootstrap] Could not delete old lobby after empty recreate: " + deleteEx.Message);
                }

                _activeLobbyId = newLobby.Id;
                _matchIsLatest = isLatest;
                _emptySinceUtc = DateTime.UtcNow;
                Debug.Log(
                    "[DedicatedMatchServerBootstrap] Empty match recreated: new lobby " + newLobby.Id +
                    " (replaced " + oldLobbyId + ").");
                DedicatedServerFileLog.Append(
                    "lobby",
                    "Empty match recreated newLobbyId=" + newLobby.Id + " oldLobbyId=" + oldLobbyId);
                return true;
            }
            finally
            {
                _recreateEmptyMatchInProgress = false;
            }
        }

        private static async Task RotationLoopAsync(
            long createdAtEpochSeconds,
            int maxPlayers,
            ushort serverPort,
            string relayProtocol,
            bool initialIsLatest,
            long ageThresholdSeconds,
            int emptyMatchRecreateSeconds)
        {
            bool isLatest = initialIsLatest;
            bool spawnedFromAge = false;
            bool spawnedFromFull = false;
            int relayAllocTimeoutMs = GetArgInt("relayAllocTimeoutMs", 60000);
            int lobbyCreateTimeoutMs = GetArgInt("lobbyCreateTimeoutMs", 60000);

            while (true)
            {
                try
                {
                    string lobbyId = _activeLobbyId;
                    int connectedClients = NetworkManager.Singleton != null ? NetworkManager.Singleton.ConnectedClients.Count : 0;
                    // Dedicated server: ConnectedClients are Netcode players only (no listen-host +1).
                    int playerCount = connectedClients;
                    TrackEmptyMatchTime(playerCount);

                    bool isFull = playerCount >= maxPlayers;
                    long nowEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long ageSeconds = nowEpochSeconds - createdAtEpochSeconds;

                    // Idle empty lobby: replace lobby + relay after a long period with no joiners (same process; no extra VM processes).
                    if (playerCount == 0 && _emptySinceUtc.HasValue && !string.IsNullOrWhiteSpace(lobbyId))
                    {
                        double emptySeconds = (DateTime.UtcNow - _emptySinceUtc.Value).TotalSeconds;
                        if (emptySeconds >= emptyMatchRecreateSeconds)
                        {
                            bool recreated = await RecreateEmptyMatchInProcessAsync(
                                maxPlayers,
                                relayProtocol,
                                isLatest,
                                TimeSpan.FromMilliseconds(relayAllocTimeoutMs),
                                lobbyCreateTimeoutMs);
                            if (recreated)
                            {
                                lobbyId = _activeLobbyId;
                                createdAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                spawnedFromAge = false;
                            }
                        }
                    }

                    // Age-based rotation when the lobby still has players but is not full (spawn a sibling process for the next "latest" slot).
                    if (isLatest && !spawnedFromAge && playerCount > 0 && ageSeconds >= ageThresholdSeconds && !isFull)
                    {
                        spawnedFromAge = true;
                        isLatest = false;

                        await UpdateLobbyFlagsAsync(lobbyId, isOpen: false, isLatest: false);
                        Debug.Log(
                            "[DedicatedMatchServerBootstrap] Age rotation: closed lobby " + lobbyId +
                            " and spawned next match.");
                        SpawnNextMatch(maxPlayers, serverPort, relayProtocol, nextIsLatest: true);
                    }

                    // Full-based rotation (when max players are reached).
                    if (!spawnedFromFull && isFull)
                    {
                        spawnedFromFull = true;

                        bool nextIsLatest = isLatest;
                        isLatest = false;

                        await UpdateLobbyFlagsAsync(lobbyId, isOpen: false, isLatest: false);
                        Debug.Log($"[DedicatedMatchServerBootstrap] Lobby full rotation: spawned next match (old lobby {lobbyId} closed). NextIsLatest={nextIsLatest}.");
                        SpawnNextMatch(maxPlayers, serverPort, relayProtocol, nextIsLatest: nextIsLatest);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[DedicatedMatchServerBootstrap] Rotation loop error: " + e.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        private static async Task UpdateLobbyFlagsAsync(string lobbyId, bool isOpen, bool isLatest)
        {
            await LobbyService.Instance.UpdateLobbyAsync(lobbyId, new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    { LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, isOpen ? "1" : "0", DataObject.IndexOptions.N1) },
                    { LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, isLatest ? "1" : "0", DataObject.IndexOptions.N2) }
                },
                IsLocked = !isOpen
            });
        }

        private static void SpawnNextMatch(int maxPlayers, ushort serverPort, string relayProtocol, bool nextIsLatest)
        {
            try
            {
                // Allow multiple concurrent matches on one host by choosing a derived port.
                // (The relay connection itself is independent of this, but UnityTransport still binds a local socket.)
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                int derivedPort = serverPort + (pid % 2000) + UnityEngine.Random.Range(0, 2000);
                if (derivedPort > 65000) derivedPort = 65000;
                ushort childServerPort = (ushort)derivedPort;

                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule != null
                    ? System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName
                    : null;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    Debug.LogError("[DedicatedMatchServerBootstrap] Cannot determine server executable path for spawn.");
                    return;
                }

                string args =
#if !UNITY_SERVER
                    "-batchmode -nographics " +
#endif
                    $"--maxPlayers={maxPlayers} " +
                    $"--serverPort={childServerPort} " +
                    $"--relayProtocol={relayProtocol} " +
                    $"--isLatest={(nextIsLatest ? 1 : 0)}";

                var psi = new System.Diagnostics.ProcessStartInfo(exePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory
                };

                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DedicatedMatchServerBootstrap] SpawnNextMatch failed: " + e.Message);
            }
        }
    }
}

