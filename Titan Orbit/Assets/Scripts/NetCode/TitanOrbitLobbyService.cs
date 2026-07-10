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
        public const string LobbyGameNameKey = "GameName";
        public const string LobbyGameNameValue = "TitanOrbit";
        public const string LobbyIsOpenKey = "IsOpen";
        public const string LobbyIsLatestKey = "IsLatest";
        public const string LobbyCreatedAtEpochKey = "CreatedAtEpoch";
        public const string LobbyServerAliveEpochKey = "ServerAliveAt";
        public const string LobbyRelayProtocolKey = "RelayProtocol";
        public const string LobbyServerListenAddressKey = "ServerListenAddress";
        public const string LobbyActivePlayersKey = "ActivePlayers";
        public const string LobbyMatchRequestGameName = "TitanOrbitMatchRequest";
        public const string LobbyMatchRequestEpochKey = "RequestedAt";
        public const int DedicatedLobbyStaleSeconds = 45;
        public const int DedicatedLobbyJoinMaxHeartbeatAgeSeconds = 45;

        static readonly SemaphoreSlim LobbyApiGate = new SemaphoreSlim(1, 1);
        static readonly SemaphoreSlim OpenLobbyRefreshGate = new SemaphoreSlim(1, 1);
        static DateTime s_NextLobbyQueryAllowedUtc = DateTime.MinValue;

        public enum OpenLobbyQueryResultKind
        {
            Ok,
            RateLimitBackoff,
            UnityServicesNotReady,
            Error
        }

        public static OpenLobbyQueryResultKind LastOpenLobbyQueryKind { get; private set; }
        public static string LastOpenLobbyQueryErrorDetail { get; private set; }
        public static float LobbyRateLimitRemainingSeconds { get; private set; }

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
            return list;
        }

        static bool TryAcceptBrowsableDedicatedLobby(LobbySummary l, out string rejectReason)
        {
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

        public static bool IsDedicatedLobbyJoinable(Lobby lobby, out string rejectReason)
        {
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

        public static async Task<bool> RequestDedicatedMatchCreationAsync()
        {
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

                    Debug.Log("[TitanOrbitLobbyService] Already a lobby member; using GetLobbyAsync for relay details.");
                    joined = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.GetLobbyAsync(id),
                        TimeSpan.FromSeconds(20),
                        "LobbyService.GetLobbyAsync");
                }

                // RelayJoinCode is Member visibility; query responses may omit it until GetLobby.
                if (joined == null || joined.Data == null || !joined.Data.ContainsKey(LobbyRelayCodeKey))
                {
                    joined = await WithLobbyApiTimeoutAsync(
                        LobbyService.Instance.GetLobbyAsync(id),
                        TimeSpan.FromSeconds(20),
                        "LobbyService.GetLobbyAsync(relay_code)");
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
            var options = new JoinLobbyByIdOptions();
            string playerId = AuthenticationService.Instance.PlayerId;
            if (!string.IsNullOrEmpty(playerId))
                options.Player = new Player(id: playerId);
            return options;
        }

        static bool IsLobbyJoinAlreadyMemberFailure(LobbyServiceException e)
        {
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
            var results = new List<LobbySummary>();
            LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.Ok;
            LastOpenLobbyQueryErrorDetail = null;
            LobbyRateLimitRemainingSeconds = 0f;

            if (emptyStabilizationAttempt == 0)
            {
                Debug.Log("[TitanOrbitLobbyService] Querying UGS lobbies latestOnly=" + latestOnly +
                          " project=" + (Application.cloudProjectId ?? "(none)"));
            }

            if (DateTime.UtcNow < s_NextLobbyQueryAllowedUtc)
            {
                LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.RateLimitBackoff;
                LobbyRateLimitRemainingSeconds = (float)(s_NextLobbyQueryAllowedUtc - DateTime.UtcNow).TotalSeconds;
                return results;
            }

            try
            {
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

                s_NextLobbyQueryAllowedUtc = DateTime.MinValue;
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
                if (IsLikelyLobbyRateLimitException(e))
                {
                    s_NextLobbyQueryAllowedUtc = DateTime.UtcNow.AddSeconds(12);
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.RateLimitBackoff;
                    LobbyRateLimitRemainingSeconds = 12f;
                }
                else
                {
                    LastOpenLobbyQueryKind = OpenLobbyQueryResultKind.Error;
                    LastOpenLobbyQueryErrorDetail = e.Message ?? e.GetType().Name;
                }

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

        static bool IsDedicatedLobbyStale(Lobby lobby)
        {
            if (lobby?.Data == null || !lobby.Data.ContainsKey(LobbyServerListenAddressKey))
                return false;
            return TryGetDedicatedLobbyHeartbeatAgeSeconds(lobby, out long ageSeconds) &&
                   ageSeconds > DedicatedLobbyStaleSeconds;
        }

        public static bool TryGetDedicatedLobbyHeartbeatAgeSeconds(Lobby lobby, out long ageSeconds)
        {
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
            if (!TryGetDedicatedLobbyHeartbeatAgeSeconds(lobby, out ageSeconds))
                return true;
            return ageSeconds > maxAgeSeconds;
        }

        static bool IsDedicatedLobbySummaryStale(LobbySummary summary)
        {
            if (summary == null || !summary.IsDedicatedServer)
                return false;

            // Align with join validation: missing heartbeat means the listing is not joinable.
            if (summary.ServerAliveAtEpochSeconds <= 0)
                return true;

            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return nowEpoch - summary.ServerAliveAtEpochSeconds > DedicatedLobbyStaleSeconds;
        }

        static bool IsLikelyLobbyRateLimitException(Exception e)
        {
            if (e == null) return false;
            if (e is LobbyServiceException lse && lse.Reason == LobbyExceptionReason.RateLimited)
                return true;
            string m = e.Message ?? string.Empty;
            return string.Equals(m, "Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
                   m.IndexOf("429", StringComparison.Ordinal) >= 0 ||
                   m.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0;
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
