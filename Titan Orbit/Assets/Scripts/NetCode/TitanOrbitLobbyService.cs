using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TitanOrbit.Services;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>UGS Lobby query/join helpers for dedicated NetCode matches.</summary>
    public static class TitanOrbitLobbyService
    {
        public const string LobbyRelayCodeKey = "RelayJoinCode";
        /// <summary>Public IPv4 clients connect to (replaces Unity Relay join codes).</summary>
        public const string LobbyHostAddressKey = "HostAddress";
        /// <summary>Public UDP/WSS port clients connect to.</summary>
        public const string LobbyHostPortKey = "HostPort";
        public const string LobbyGameNameKey = "GameName";
        public const string LobbyGameNameValue = "TitanOrbit";
        public const string LobbyIsOpenKey = "IsOpen";
        public const string LobbyIsLatestKey = "IsLatest";
        public const string LobbyCreatedAtEpochKey = "CreatedAtEpoch";
        public const string LobbyServerAliveEpochKey = "ServerAliveAt";
        public const string LobbyRelayProtocolKey = "RelayProtocol";
        public const string LobbyServerListenAddressKey = "ServerListenAddress";
        public const string LobbyActivePlayersKey = "ActivePlayers";
        /// <summary>
        /// [TITAN-ORBIT] Unix epoch (UTC seconds) when an empty match will idle-recreate/kill.
        /// Published as <c>0</c> while any players are connected — Join Game hides the countdown then.
        /// </summary>
        public const string LobbyIdleKillAtEpochKey = "IdleKillAt";
        /// <summary>[TITAN-ORBIT] Authoritative map spawn step count (loading denominator).</summary>
        public const string LobbyMapLoadingStepsKey = "MapSteps";
        /// <summary>[TITAN-ORBIT] Team / home planet count for this match.</summary>
        public const string LobbyMapTeamCountKey = "MapTeams";
        /// <summary>[TITAN-ORBIT] Neutral planet count.</summary>
        public const string LobbyMapNeutralCountKey = "MapNeutrals";
        /// <summary>[TITAN-ORBIT] Asteroid count.</summary>
        public const string LobbyMapAsteroidCountKey = "MapAsteroids";
        /// <summary>[TITAN-ORBIT] Per-team owned planet counts as CSV (TeamA,TeamB,…), e.g. "1,2,1".</summary>
        public const string LobbyMapTeamPlanetsKey = "MapTeamPlanets";
        /// <summary>[TITAN-ORBIT] Per-team roster sizes as CSV (TeamA,TeamB,…), e.g. "2,0,1".</summary>
        public const string LobbyMapTeamPlayersKey = "MapTeamPlayers";
        /// <summary>[TITAN-ORBIT] Max players allowed on each team (from TeamStateSingleton).</summary>
        public const string LobbyMapMaxPlayersPerTeamKey = "MapMaxPerTeam";
        /// <summary>[TITAN-ORBIT] Rolled toroidal map width in world units (Join Game / browse).</summary>
        public const string LobbyMapWidthKey = "MapWidth";
        /// <summary>[TITAN-ORBIT] Rolled toroidal map height in world units (Join Game / browse).</summary>
        public const string LobbyMapHeightKey = "MapHeight";
        public const string LobbyMatchRequestGameName = "TitanOrbitMatchRequest";
        public const string LobbyMatchRequestEpochKey = "RequestedAt";
        public const int DedicatedLobbyStaleSeconds = 45;
        public const int DedicatedLobbyJoinMaxHeartbeatAgeSeconds = 45;

        static readonly SemaphoreSlim LobbyApiGate = new SemaphoreSlim(1, 1);
        static readonly SemaphoreSlim OpenLobbyRefreshGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Result of the most recent open-lobby query.
        /// Join Game / main-menu UI reads this when the returned list is empty to decide which status string to show.
        /// </summary>
        public enum OpenLobbyQueryResultKind
        {
            /// <summary>Query completed; list may still be empty if no dedicated matches are listed.</summary>
            Ok,
            /// <summary>UGS guest session or LobbyService.Instance was not ready yet.</summary>
            UnityServicesNotReady,
            /// <summary>UGS threw or returned an unusable response; see <see cref="LastOpenLobbyQueryErrorDetail"/>.</summary>
            Error
        }

        /// <summary>[TITAN-ORBIT] Kind of the last <see cref="QueryOpenLobbiesInternalAsync"/> outcome.</summary>
        public static OpenLobbyQueryResultKind LastOpenLobbyQueryKind { get; private set; }
        /// <summary>[TITAN-ORBIT] Exception / failure detail for the last Error result (null when Ok / not ready).</summary>
        public static string LastOpenLobbyQueryErrorDetail { get; private set; }

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
            public bool IsDedicatedServer;
            public long ServerAliveAtEpochSeconds;
            public int ActivePlayers = -1;

            /// <summary>
            /// [TITAN-ORBIT] UTC unix seconds when idle recreate will kill this empty match.
            /// 0 / unset when players are present or the server has not published the field yet.
            /// </summary>
            public long IdleKillAtEpochSeconds;

            /// <summary>[TITAN-ORBIT] Map spawn steps from lobby Data; -1 if server has not published yet.</summary>
            public int MapLoadingSteps = -1;
            /// <summary>[TITAN-ORBIT] Teams / homes; -1 if unknown.</summary>
            public int MapTeamCount = -1;
            /// <summary>[TITAN-ORBIT] Neutral planets; -1 if unknown.</summary>
            public int MapNeutralPlanetCount = -1;
            /// <summary>[TITAN-ORBIT] Asteroids; -1 if unknown.</summary>
            public int MapAsteroidCount = -1;
            /// <summary>
            /// [TITAN-ORBIT] Owned planet count per team in TeamA.. order (e.g. 1,1,2).
            /// Null when the server has not published <see cref="LobbyMapTeamPlanetsKey"/> yet.
            /// </summary>
            public int[] MapTeamPlanetCounts;

            /// <summary>
            /// [TITAN-ORBIT] Current player count per team in TeamA.. order (e.g. 2,0,1).
            /// Null when the server has not published <see cref="LobbyMapTeamPlayersKey"/> yet.
            /// </summary>
            public int[] MapTeamPlayerCounts;

            /// <summary>
            /// [TITAN-ORBIT] Cap per team from map/bootstrap meta; -1 if unknown.
            /// Join Game match capacity = <see cref="MapTeamCount"/> × this value when both are set.
            /// </summary>
            public int MapMaxPlayersPerTeam = -1;

            /// <summary>
            /// [TITAN-ORBIT] Rolled map width (world units), rounded; -1 if server has not published yet.
            /// </summary>
            public int MapWidth = -1;

            /// <summary>
            /// [TITAN-ORBIT] Rolled map height (world units), rounded; -1 if server has not published yet.
            /// </summary>
            public int MapHeight = -1;
        }

        public static async Task<List<LobbySummary>> QueryJoinableDedicatedLobbiesAsync(
            int count = 40,
            bool skipEmptyStabilization = false)
        {
            int stabilizationCap = skipEmptyStabilization ? 0 : -1;
            var raw = await QueryOpenLobbiesAsync(
                latestOnly: false,
                count: count,
                emptyStabilizationAttempt: 0,
                maxEmptyStabilizationAttemptsOverride: stabilizationCap);
            var joinable = FilterToJoinableDedicatedLobbies(raw);
            LogQueryDiagnostics("joinable", raw.Count, joinable.Count);
            return joinable;
        }

        /// <summary>All live dedicated lobbies for the browser (includes non-latest; sorted with latest first).</summary>
        public static async Task<List<LobbySummary>> QueryBrowsableDedicatedLobbiesAsync(
            int count = 40,
            bool skipEmptyStabilization = false)
        {
            const int browsableStabilizationAttempts = 3;
            int stabilizationCap = skipEmptyStabilization ? 0 : browsableStabilizationAttempts;
            var raw = await QueryOpenLobbiesAsync(
                latestOnly: false,
                count: count,
                emptyStabilizationAttempt: 0,
                maxEmptyStabilizationAttemptsOverride: stabilizationCap);
            var browsable = FilterBrowsableDedicatedLobbies(raw);
            LogQueryDiagnostics("browsable", raw.Count, browsable.Count);
            return browsable;
        }

        public static List<LobbySummary> FilterBrowsableDedicatedLobbies(List<LobbySummary> lobbies)
        {
            // --- FilterBrowsableDedicatedLobbies ---
            if (lobbies == null || lobbies.Count == 0)
                return new List<LobbySummary>();

            var list = new List<LobbySummary>();
            for (int i = 0; i < lobbies.Count; i++)
            {
                LobbySummary l = lobbies[i];
                if (TryAcceptBrowsableDedicatedLobby(l, out _))
                    list.Add(l);
            }

            if (lobbies.Count > 0 && list.Count == 0)
                LogBrowsableFilterRejections(lobbies);

            list.Sort((a, b) =>
            {
                if (a.IsLatest != b.IsLatest)
                    return b.IsLatest.CompareTo(a.IsLatest);
                return b.CreatedAtEpochSeconds.CompareTo(a.CreatedAtEpochSeconds);
            });

            // [TITAN-ORBIT] When any "Latest" dedicated lobby exists, hide older listings — they often
            // share a stale Relay allocation and cause connections=1 withNetworkId=0 on the client.
            var latestOnly = list.FindAll(l => l.IsLatest);
            if (latestOnly.Count > 0)
                list = latestOnly;

            return list;
        }

        static bool TryAcceptBrowsableDedicatedLobby(LobbySummary l, out string rejectReason)
        {
            // --- Attempt resolution ---
            rejectReason = null;
            if (l == null)
            {
                rejectReason = "null summary";
                return false;
            }

            if (!l.IsDedicatedServer)
            {
                rejectReason = "not dedicated";
                return false;
            }

            if (!l.IsOpen)
            {
                rejectReason = "closed";
                return false;
            }

            if (IsDedicatedLobbySummaryStale(l))
            {
                long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - l.ServerAliveAtEpochSeconds;
                rejectReason = "stale heartbeat (" + age + "s old, limit " + DedicatedLobbyStaleSeconds + "s)";
                return false;
            }

            return true;
        }

        static void LogBrowsableFilterRejections(List<LobbySummary> lobbies)
        {
            // --- LogBrowsableFilterRejections ---
            for (int i = 0; i < lobbies.Count; i++)
            {
                LobbySummary l = lobbies[i];
                if (l == null)
                    continue;
                if (TryAcceptBrowsableDedicatedLobby(l, out string reason))
                    continue;

                Debug.Log("[TitanOrbitLobbyService] Browsable filter rejected \"" + (l.Name ?? l.LobbyId) +
                          "\": " + (reason ?? "unknown"));
            }
        }

        static void LogQueryDiagnostics(string label, int rawCount, int filteredCount)
        {
            // --- LogQueryDiagnostics ---
            string detail = LastOpenLobbyQueryKind.ToString();
            if (!string.IsNullOrEmpty(LastOpenLobbyQueryErrorDetail))
                detail += ": " + LastOpenLobbyQueryErrorDetail;
            string hint = string.Empty;
            if (rawCount == 0 && LastOpenLobbyQueryKind == OpenLobbyQueryResultKind.Ok)
                hint = " — no UGS lobbies matched GameName=TitanOrbit IsOpen=1 (dedicated server may not have published yet)";
            else if (rawCount > 0 && filteredCount == 0)
                hint = " — lobbies exist but were filtered (see Browsable filter rejected lines)";
            Debug.Log("[TitanOrbitLobbyService] Query " + label + " raw=" + rawCount + " filtered=" + filteredCount +
                      " kind=" + detail + " project=" + (Application.cloudProjectId ?? "(none)") + hint);
        }

        public static async Task<List<LobbySummary>> QueryOpenLobbiesAsync(
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

        public static List<LobbySummary> FilterToJoinableDedicatedLobbies(List<LobbySummary> lobbies)
        {
            // --- FilterToJoinableDedicatedLobbies ---
            if (lobbies == null || lobbies.Count == 0)
                return lobbies ?? new List<LobbySummary>();

            var joinable = new List<LobbySummary>();
            for (int i = 0; i < lobbies.Count; i++)
            {
                LobbySummary l = lobbies[i];
                if (l == null || !l.IsDedicatedServer || !l.IsOpen || !l.IsLatest || IsDedicatedLobbySummaryStale(l))
                    continue;
                joinable.Add(l);
            }

            return joinable;
        }

        /// <summary>
        /// [NETCODE] True when UGS lists at least one open, latest, fresh dedicated lobby clients can join.
        /// Used by dedicated server self-heal and match-request watchdog.
        /// </summary>
        public static async Task<bool> QueryAnyJoinableLatestDedicatedLobbyExistsAsync()
        {
            // --- QueryAnyJoinableLatestDedicatedLobbyExistsAsync ---
            var summaries = await QueryOpenLobbiesAsync(
                latestOnly: true,
                count: 20,
                emptyStabilizationAttempt: 0,
                maxEmptyStabilizationAttemptsOverride: 0);
            return FilterToJoinableDedicatedLobbies(summaries).Count > 0;
        }

        /// <summary>
        /// [NETCODE] Fetches a lobby by id and returns whether <see cref="IsDedicatedLobbyJoinable"/> accepts it.
        /// </summary>
        public static async Task<bool> TryIsLobbyJoinableByIdAsync(string lobbyId)
        {
            // --- TryIsLobbyJoinableByIdAsync ---
            if (string.IsNullOrWhiteSpace(lobbyId))
                return false;

            try
            {
                await AcquireLobbyApiGateAsync();
                try
                {
                    Lobby lobby = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.GetLobbyAsync(lobbyId.Trim()),
                        TimeSpan.FromSeconds(20),
                        "LobbyService.GetLobbyAsync(self_heal)");
                    return lobby != null && IsDedicatedLobbyJoinable(lobby, out _);
                }
                finally
                {
                    ReleaseLobbyApiGate();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitLobbyService] TryIsLobbyJoinableByIdAsync failed: " + e.Message);
                return false;
            }
        }

        public static bool IsDedicatedLobbyJoinable(Lobby lobby, out string rejectReason)
        {
            // --- IsDedicatedLobbyJoinable ---
            rejectReason = null;
            if (lobby?.Data == null)
            {
                rejectReason = "lobby has no data";
                return false;
            }

            if (!lobby.Data.ContainsKey(LobbyServerListenAddressKey))
            {
                rejectReason = "not a dedicated server lobby";
                return false;
            }

            if (!TryGetHostEndpoint(lobby, out _, out _))
            {
                rejectReason = "lobby has no host address (server needs a rebuild without Unity Relay)";
                return false;
            }

            if (lobby.Data.TryGetValue(LobbyIsOpenKey, out DataObject io) && io != null &&
                !string.Equals(io.Value, "1", StringComparison.Ordinal))
            {
                rejectReason = "lobby is closed";
                return false;
            }

            if (IsDedicatedLobbyStale(lobby))
            {
                rejectReason = "server heartbeat is stale";
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

        /// <summary>Reads the advertised dedicated host IP:port from lobby data.</summary>
        public static bool TryGetHostEndpoint(Lobby lobby, out string address, out ushort port)
        {
            address = null;
            port = 0;
            if (lobby?.Data == null)
                return false;

            if (!lobby.Data.TryGetValue(LobbyHostAddressKey, out DataObject hostObj) ||
                string.IsNullOrWhiteSpace(hostObj?.Value))
                return false;

            address = hostObj.Value.Trim();
            port = TitanOrbitServerCommandLine.DefaultServerPort;
            if (lobby.Data.TryGetValue(LobbyHostPortKey, out DataObject portObj) &&
                ushort.TryParse(portObj?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort parsed))
                port = parsed;

            return address.Length > 0 && port > 0;
        }

        public static async Task<bool> RequestDedicatedMatchCreationAsync()
        {
            // --- RequestDedicatedMatchCreationAsync ---
            try
            {
                if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                {
                    Debug.LogWarning("[TitanOrbitLobbyService] RequestDedicatedMatchCreationAsync: UGS not ready.");
                    return false;
                }

                await AcquireLobbyApiGateAsync();
                try
                {
                    long requestedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string requestName = "DedicatedMatchRequest-" + requestedAtEpoch.ToString(CultureInfo.InvariantCulture);
                    Lobby created = await WithLobbyApiTimeoutAsync(
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
                                            LobbyMatchRequestGameName,
                                            DataObject.IndexOptions.S1)
                                    },
                                    {
                                        LobbyMatchRequestEpochKey,
                                        new DataObject(
                                            DataObject.VisibilityOptions.Public,
                                            requestedAtEpoch.ToString(CultureInfo.InvariantCulture),
                                            DataObject.IndexOptions.N1)
                                    }
                                }
                            }),
                        TimeSpan.FromSeconds(30),
                        "LobbyService.CreateLobbyAsync(match_request)");
                    Debug.Log("[TitanOrbitLobbyService] Dedicated match request published lobbyId=" +
                              (created?.Id ?? "(null)") + " project=" + (Application.cloudProjectId ?? "(none)"));
                    return created != null;
                }
                finally
                {
                    LobbyApiGate.Release();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitLobbyService] RequestDedicatedMatchCreationAsync failed: " + e.Message);
                return false;
            }
        }

        public static async Task<Lobby> QuickJoinLatestDedicatedLobbyAsync()
        {
            // --- QuickJoinLatestDedicatedLobbyAsync ---
            if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                return null;

            await AcquireLobbyApiGateAsync();
            try
            {
                foreach (bool latestOnly in new[] { true, false })
                {
                    try
                    {
                        var quickOptions = new QuickJoinLobbyOptions
                        {
                            Filter = BuildDedicatedLobbyQueryFilters(latestOnly),
                        };
                        string playerId = AuthenticationService.Instance.PlayerId;
                        if (!string.IsNullOrEmpty(playerId))
                            quickOptions.Player = new Player(id: playerId);

                        Lobby joined = await WithLobbyApiTimeoutAsync(
                            LobbyService.Instance.QuickJoinLobbyAsync(quickOptions),
                            TimeSpan.FromSeconds(30),
                            "LobbyService.QuickJoinLobbyAsync");
                        if (joined != null && IsDedicatedLobbyJoinable(joined, out _))
                            return joined;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[TitanOrbitLobbyService] QuickJoin (latestOnly=" + latestOnly + "): " + ex.Message);
                    }
                }
            }
            finally
            {
                ReleaseLobbyApiGate();
            }

            // Fallback must not run under LobbyApiGate — Query/Join also acquire it (would deadlock).
            foreach (bool latestOnly in new[] { true, false })
            {
                var summaries = await QueryOpenLobbiesAsync(latestOnly, 15, 0, 0);
                foreach (LobbySummary summary in FilterToJoinableDedicatedLobbies(summaries))
                {
                    if (string.IsNullOrWhiteSpace(summary.LobbyId))
                        continue;
                    try
                    {
                        Lobby joined = await JoinDedicatedLobbyByIdAsync(summary.LobbyId);
                        if (joined != null)
                        {
                            Debug.Log("[TitanOrbitLobbyService] QuickJoin fallback joined lobby " + summary.LobbyId +
                                      " (\"" + (summary.Name ?? "") + "\").");
                            return joined;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[TitanOrbitLobbyService] Join fallback " + summary.LobbyId + ": " + ex.Message);
                    }
                }
            }

            return null;
        }

        /// <summary>Join a dedicated lobby by id; recovers when the player is already a member.</summary>
        public static async Task<Lobby> JoinDedicatedLobbyByIdAsync(string lobbyId, string leavePreviousLobbyId = null)
        {
            // --- JoinDedicatedLobbyByIdAsync ---
            if (string.IsNullOrWhiteSpace(lobbyId))
                return null;

            if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                return null;

            await AcquireLobbyApiGateAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(leavePreviousLobbyId) &&
                    !string.Equals(leavePreviousLobbyId, lobbyId, StringComparison.Ordinal))
                {
                    await TryRemovePlayerFromLobbyAsync(leavePreviousLobbyId, "before_join");
                }

                string id = lobbyId.Trim();
                Lobby joined;
                try
                {
                    joined = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.JoinLobbyByIdAsync(id, BuildJoinLobbyByIdOptions()),
                        TimeSpan.FromSeconds(30),
                        "LobbyService.JoinLobbyByIdAsync");
                }
                catch (LobbyServiceException e)
                {
                    if (!IsLobbyJoinAlreadyMemberFailure(e))
                        throw;

                    Debug.Log("[TitanOrbitLobbyService] Already a lobby member; using GetLobbyAsync for host endpoint.");
                    joined = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.GetLobbyAsync(id),
                        TimeSpan.FromSeconds(20),
                        "LobbyService.GetLobbyAsync");
                }

                // HostAddress may be omitted from query snapshots until GetLobby.
                if (joined == null || joined.Data == null || !joined.Data.ContainsKey(LobbyHostAddressKey))
                {
                    joined = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.GetLobbyAsync(id),
                        TimeSpan.FromSeconds(20),
                        "LobbyService.GetLobbyAsync(host_address)");
                }

                return joined;
            }
            finally
            {
                LobbyApiGate.Release();
            }
        }

        /// <summary>Leaves every UGS lobby the guest is in (avoids stale relay codes from old memberships).</summary>
        public static async Task TryLeaveAllJoinedLobbiesAsync(string reason)
        {
            // --- Attempt resolution ---
            if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                return;
            if (!AuthenticationService.Instance.IsSignedIn)
                return;

            List<string> joinedIds;
            await AcquireLobbyApiGateAsync();
            try
            {
                joinedIds = await LobbyService.Instance.GetJoinedLobbiesAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitLobbyService] TryLeaveAllJoinedLobbiesAsync list failed: " + e.Message);
                return;
            }
            finally
            {
                ReleaseLobbyApiGate();
            }

            if (joinedIds == null || joinedIds.Count == 0)
                return;

            for (int i = 0; i < joinedIds.Count; i++)
                await TryRemovePlayerFromLobbyAsync(joinedIds[i], reason);
        }

        public static async Task TryRemovePlayerFromLobbyAsync(string lobbyId, string reason)
        {
            // --- Attempt resolution ---
            if (string.IsNullOrWhiteSpace(lobbyId))
                return;
            if (UnityServices.State != ServicesInitializationState.Initialized)
                return;
            if (!AuthenticationService.Instance.IsSignedIn || !AuthenticationService.Instance.IsAuthorized)
                return;

            string playerId = AuthenticationService.Instance.PlayerId;
            if (string.IsNullOrWhiteSpace(playerId))
                return;

            await AcquireLobbyApiGateAsync();
            try
            {
                await LobbyService.Instance.RemovePlayerAsync(lobbyId, playerId);
            }
            catch (LobbyServiceException e)
            {
                if (e.Reason != LobbyExceptionReason.PlayerNotFound &&
                    e.Reason != LobbyExceptionReason.LobbyNotFound &&
                    e.Reason != LobbyExceptionReason.Forbidden)
                {
                    Debug.LogWarning("[TitanOrbitLobbyService] TryRemovePlayerFromLobbyAsync (" + reason + "): " + e.Message);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitLobbyService] TryRemovePlayerFromLobbyAsync (" + reason + "): " + e.Message);
            }
            finally
            {
                LobbyApiGate.Release();
            }
        }

        static JoinLobbyByIdOptions BuildJoinLobbyByIdOptions()
        {
            // --- Build data ---
            var options = new JoinLobbyByIdOptions();
            string playerId = AuthenticationService.Instance.PlayerId;
            if (!string.IsNullOrEmpty(playerId))
                options.Player = new Player(id: playerId);
            return options;
        }

        static bool IsLobbyJoinAlreadyMemberFailure(LobbyServiceException e)
        {
            // --- IsLobbyJoinAlreadyMemberFailure ---
            if (e == null)
                return false;
            if (e.Reason == LobbyExceptionReason.LobbyConflict || e.Reason == LobbyExceptionReason.Conflict)
                return true;
            string m = e.Message ?? string.Empty;
            return m.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   m.IndexOf("member", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static async Task AcquireLobbyApiGateAsync()
        {
            // --- AcquireLobbyApiGateAsync ---
#if UNITY_WEBGL && !UNITY_EDITOR
            while (!LobbyApiGate.Wait(0))
                await Task.Yield();
#else
            await LobbyApiGate.WaitAsync();
#endif
        }

        internal static void ReleaseLobbyApiGate() => LobbyApiGate.Release();

        /// <summary>Registers the player's Relay join allocation with UGS Lobby (required for Relay routing).</summary>
        public static async Task TryUpdatePlayerRelayAllocationAsync(string lobbyId, string allocationId)
        {
            // --- Attempt resolution ---
            if (string.IsNullOrWhiteSpace(lobbyId) || string.IsNullOrWhiteSpace(allocationId))
                return;

            string playerId = AuthenticationService.Instance.PlayerId;
            if (string.IsNullOrEmpty(playerId))
                return;

            await AcquireLobbyApiGateAsync();
            try
            {
                await WithLobbyApiTimeoutAsync(
                    LobbyService.Instance.UpdatePlayerAsync(lobbyId, playerId, new UpdatePlayerOptions
                    {
                        AllocationId = allocationId
                    }),
                    TimeSpan.FromSeconds(15),
                    "LobbyService.UpdatePlayerAsync(relay_allocation)");
                Debug.Log("[TitanOrbitLobbyService] Registered player Relay allocation on lobby=" + lobbyId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TitanOrbitLobbyService] UpdatePlayer relay allocation failed: " + ex.Message);
            }
            finally
            {
                ReleaseLobbyApiGate();
            }
        }

        static async Task<List<LobbySummary>> QueryOpenLobbiesInternalAsync(
            bool latestOnly,
            int count,
            int emptyStabilizationAttempt,
            int maxEmptyStabilizationAttemptsOverride)
        {
            // --- Reset last-query status for this attempt ---
            // [TITAN-ORBIT] UI (Join Game / main menu) reads these when the list comes back empty.
            var results = new List<LobbySummary>();
            LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.Ok;
            LastOpenLobbyQueryErrorDetail = null;

            if (emptyStabilizationAttempt == 0)
            {
                Debug.Log("[TitanOrbitLobbyService] Querying UGS lobbies latestOnly=" + latestOnly +
                          " project=" + (Application.cloudProjectId ?? "(none)"));
            }

            try
            {
                // --- Ensure UGS guest session + Lobby API ---
                if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                {
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.UnityServicesNotReady;
                    return results;
                }

                if (LobbyService.Instance == null)
                {
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.UnityServicesNotReady;
                    return results;
                }

                // --- One UGS QueryLobbies call (serialized by LobbyApiGate) ---
                // [STANDARD] Semaphore prevents overlapping Lobby API calls from menu + Join Game.
                // Intentional: no client-side rate-limit lockout — Refresh may retry immediately.
                await AcquireLobbyApiGateAsync();
                QueryResponse response;
                try
                {
                    response = await QueryLobbiesAsyncUnguarded(new QueryLobbiesOptions
                    {
                        Count = count,
                        Filters = BuildDedicatedLobbyQueryFilters(latestOnly),
                        Order = new List<QueryOrder>
                        {
                            new QueryOrder(asc: false, field: QueryOrder.FieldOptions.Created)
                        }
                    });
                }
                finally
                {
                    LobbyApiGate.Release();
                }

                if (response?.Results == null)
                {
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.Error;
                    LastOpenLobbyQueryErrorDetail = "Lobby query returned no result set.";
                    return results;
                }

                foreach (Lobby lobby in response.Results)
                {
                    if (lobby != null)
                        results.Add(ToLobbySummary(lobby));
                }
            }
            catch (Exception e)
            {
                // --- Surface failure for UI; do not block subsequent Refresh ---
                LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.Error;
                LastOpenLobbyQueryErrorDetail = e.Message ?? e.GetType().Name;
                Debug.LogWarning("[TitanOrbitLobbyService] QueryOpenLobbiesAsync failed: " + e.Message);
            }

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

            return results;
        }

        static async Task<QueryResponse> QueryLobbiesAsyncUnguarded(QueryLobbiesOptions options)
        {
            // --- QueryLobbiesAsyncUnguarded ---
            try
            {
                return await LobbyService.Instance.QueryLobbiesAsync(options);
            }
            catch (NullReferenceException)
            {
                return new QueryResponse(new List<Lobby>(), null);
            }
        }

        static List<QueryFilter> BuildDedicatedLobbyQueryFilters(bool latestOnly)
        {
            // --- Build data ---
            var filters = new List<QueryFilter>
            {
                new QueryFilter(QueryFilter.FieldOptions.S1, LobbyGameNameValue, QueryFilter.OpOptions.EQ),
                new QueryFilter(QueryFilter.FieldOptions.N1, "1", QueryFilter.OpOptions.EQ),
            };
            if (latestOnly)
                filters.Add(new QueryFilter(QueryFilter.FieldOptions.N2, "1", QueryFilter.OpOptions.EQ));
            return filters;
        }

        static LobbySummary ToLobbySummary(Lobby lobby)
        {
            // --- ToLobbySummary ---
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
                int.TryParse(activePlayersObj.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                activePlayersFromServer = Mathf.Max(0, parsed);
            }

            if (isDedicatedServerLobby && activePlayersFromServer >= 0)
                normalizedPlayerCount = activePlayersFromServer;

            var summary = new LobbySummary
            {
                LobbyId = lobby.Id,
                Name = string.IsNullOrWhiteSpace(lobby.Name) ? "Unnamed Room" : lobby.Name,
                CurrentPlayers = normalizedPlayerCount,
                MaxPlayers = maxPlayerCapacity,
                IsOpen = true,
                IsLatest = false,
                IsDedicatedServer = isDedicatedServerLobby,
                CreatedAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ActivePlayers = activePlayersFromServer,
                MapLoadingSteps = -1,
                MapTeamCount = -1,
                MapNeutralPlanetCount = -1,
                MapAsteroidCount = -1,
                MapTeamPlanetCounts = null,
                MapTeamPlayerCounts = null,
                MapMaxPlayersPerTeam = -1,
                MapWidth = -1,
                MapHeight = -1
            };

            if (lobby.Data == null)
                return summary;

            // --- Map session metadata (public lobby Data from dedicated server heartbeat) ---
            summary.MapLoadingSteps = TryParseLobbyInt(lobby.Data, LobbyMapLoadingStepsKey, -1);
            summary.MapTeamCount = TryParseLobbyInt(lobby.Data, LobbyMapTeamCountKey, -1);
            summary.MapNeutralPlanetCount = TryParseLobbyInt(lobby.Data, LobbyMapNeutralCountKey, -1);
            summary.MapAsteroidCount = TryParseLobbyInt(lobby.Data, LobbyMapAsteroidCountKey, -1);
            summary.MapTeamPlanetCounts = TryParseLobbyIntCsv(lobby.Data, LobbyMapTeamPlanetsKey);
            summary.MapTeamPlayerCounts = TryParseLobbyIntCsv(lobby.Data, LobbyMapTeamPlayersKey);
            summary.MapMaxPlayersPerTeam = TryParseLobbyInt(lobby.Data, LobbyMapMaxPlayersPerTeamKey, -1);
            // [TITAN-ORBIT] Width/height are published as whole numbers (rounded world units).
            summary.MapWidth = TryParseLobbyInt(lobby.Data, LobbyMapWidthKey, -1);
            summary.MapHeight = TryParseLobbyInt(lobby.Data, LobbyMapHeightKey, -1);

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

            // --- Empty-idle kill deadline (Join Game countdown when CurrentPlayers == 0) ---
            summary.IdleKillAtEpochSeconds = 0;
            if (lobby.Data.TryGetValue(LobbyIdleKillAtEpochKey, out DataObject idleKillObj) &&
                long.TryParse(idleKillObj?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long idleKillAt) &&
                idleKillAt > 0)
            {
                summary.IdleKillAtEpochSeconds = idleKillAt;
            }

            return summary;
        }

        static bool IsDedicatedLobbyStale(Lobby lobby)
        {
            // --- IsDedicatedLobbyStale ---
            if (lobby?.Data == null || !lobby.Data.ContainsKey(LobbyServerListenAddressKey))
                return false;
            return TryGetDedicatedLobbyHeartbeatAgeSeconds(lobby, out long ageSeconds) &&
                   ageSeconds > DedicatedLobbyStaleSeconds;
        }

        public static bool TryGetDedicatedLobbyHeartbeatAgeSeconds(Lobby lobby, out long ageSeconds)
        {
            // --- Attempt resolution ---
            ageSeconds = 0;
            if (lobby?.Data == null)
                return false;
            if (!lobby.Data.TryGetValue(LobbyServerAliveEpochKey, out DataObject aliveObj) || aliveObj == null)
                return false;
            if (!long.TryParse(aliveObj.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long aliveEpoch) ||
                aliveEpoch <= 0)
                return false;

            ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - aliveEpoch;
            return true;
        }

        public static bool IsDedicatedLobbyHeartbeatTooOld(Lobby lobby, int maxAgeSeconds, out long ageSeconds)
        {
            // --- IsDedicatedLobbyHeartbeatTooOld ---
            if (!TryGetDedicatedLobbyHeartbeatAgeSeconds(lobby, out ageSeconds))
                return true;
            return ageSeconds > maxAgeSeconds;
        }

        static bool IsDedicatedLobbySummaryStale(LobbySummary summary)
        {
            // --- IsDedicatedLobbySummaryStale ---
            if (summary == null || !summary.IsDedicatedServer)
                return false;

            // Align with join validation: missing heartbeat means the listing is not joinable.
            if (summary.ServerAliveAtEpochSeconds <= 0)
                return true;

            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return nowEpoch - summary.ServerAliveAtEpochSeconds > DedicatedLobbyStaleSeconds;
        }

        /// <summary>
        /// Parses a public lobby Data integer key. Returns <paramref name="fallback"/> when missing or invalid.
        /// </summary>
        static int TryParseLobbyInt(Dictionary<string, DataObject> data, string key, int fallback)
        {
            // --- Parse lobby Data int ---
            // [STANDARD] UGS stores all lobby Data values as strings; we convert to int for UI.
            if (data == null || string.IsNullOrEmpty(key))
                return fallback;
            if (!data.TryGetValue(key, out DataObject obj) || obj == null)
                return fallback;
            if (!int.TryParse(obj.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return fallback;
            return parsed;
        }

        /// <summary>
        /// Parses a comma-separated list of ints from lobby Data (e.g. MapTeamPlanets "1,2,1").
        /// Returns null when the key is missing or empty.
        /// </summary>
        static int[] TryParseLobbyIntCsv(Dictionary<string, DataObject> data, string key)
        {
            // --- Parse lobby Data CSV ints ---
            // [TITAN-ORBIT] Used for MapTeamPlanets and MapTeamPlayers on the Join Game browser.
            if (data == null || string.IsNullOrEmpty(key))
                return null;
            if (!data.TryGetValue(key, out DataObject obj) || obj == null || string.IsNullOrWhiteSpace(obj.Value))
                return null;

            string[] parts = obj.Value.Split(',');
            if (parts.Length == 0)
                return null;

            var values = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    return null;
                values[i] = Mathf.Max(0, parsed);
            }

            return values;
        }

        static async Task<T> WithLobbyApiTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string operationName)
        {
            Task delay = Task.Delay(timeout);
            Task finished = await Task.WhenAny(task, delay);
            if (finished == delay)
                throw new TimeoutException(operationName);
            return await task;
        }
    }
}
