using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using TitanOrbit.Diagnostics;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Keeps dedicated NetCode matches available: heartbeats, rotation, empty-lobby recreate, match requests.
    /// Started by <see cref="TitanOrbitSessionManager"/> after the first Relay lobby is live.
    /// </summary>
    public class TitanOrbitDedicatedServerHost : MonoBehaviour
    {
        static TitanOrbitDedicatedServerHost s_Instance;

        readonly HashSet<string> _processedMatchRequestLobbyIds = new HashSet<string>(StringComparer.Ordinal);

        TitanOrbitServerCommandLine _config;
        string _activeLobbyId;
        long _createdAtEpochSeconds;
        bool _matchIsLatest;
        bool _spawnedFromAge;
        bool _spawnedFromFull;
        DateTime? _emptySinceUtc;
        Coroutine _rotationCoroutine;
        Coroutine _presenceCoroutine;
        Coroutine _matchRequestCoroutine;
        Coroutine _netcodeHealthCoroutine;

        public static void Begin(TitanOrbitServerCommandLine config, string lobbyId, long createdAtEpochSeconds, bool isLatest)
        {
            if (s_Instance == null)
            {
                var session = TitanOrbitSessionManager.Instance;
                if (session == null)
                {
                    Debug.LogError("[TitanOrbitDedicatedServerHost] Session manager missing.");
                    return;
                }

                s_Instance = session.gameObject.GetComponent<TitanOrbitDedicatedServerHost>();
                if (s_Instance == null)
                    s_Instance = session.gameObject.AddComponent<TitanOrbitDedicatedServerHost>();
            }

            s_Instance.StartHosting(config, lobbyId, createdAtEpochSeconds, isLatest);
        }

        void StartHosting(TitanOrbitServerCommandLine config, string lobbyId, long createdAtEpochSeconds, bool isLatest)
        {
            _config = config ?? TitanOrbitServerCommandLine.Parse();
            _activeLobbyId = lobbyId;
            _createdAtEpochSeconds = createdAtEpochSeconds;
            _matchIsLatest = isLatest;
            _emptySinceUtc = DateTime.UtcNow;

            if (_rotationCoroutine != null) StopCoroutine(_rotationCoroutine);
            if (_presenceCoroutine != null) StopCoroutine(_presenceCoroutine);
            if (_matchRequestCoroutine != null) StopCoroutine(_matchRequestCoroutine);
            if (_netcodeHealthCoroutine != null) StopCoroutine(_netcodeHealthCoroutine);

            _rotationCoroutine = StartCoroutine(RotationLoop());
            _presenceCoroutine = StartCoroutine(LobbyPresenceWatchdogLoop());
            _matchRequestCoroutine = StartCoroutine(MatchRequestWatchdogLoop());
            _netcodeHealthCoroutine = StartCoroutine(NetcodeHealthLoop());

            DedicatedServerFileLog.Append("lobby", "Dedicated host loops started lobbyId=" + lobbyId + " isLatest=" + isLatest);
            Debug.Log("[TitanOrbitDedicatedServerHost] Hosting loops started for lobby " + lobbyId);
        }

        public void NotifyLobbyReplaced(string newLobbyId, long createdAtEpochSeconds, bool isLatest)
        {
            _activeLobbyId = newLobbyId;
            _createdAtEpochSeconds = createdAtEpochSeconds;
            _matchIsLatest = isLatest;
            _spawnedFromAge = false;
            _spawnedFromFull = false;
            _emptySinceUtc = DateTime.UtcNow;
        }

        IEnumerator RotationLoop()
        {
            var wait = new WaitForSeconds(3f);
            while (true)
            {
                bool pendingEmptyRecreate = false;
                try
                {
                    string lobbyId = _activeLobbyId;
                    int playerCount = TitanOrbitSessionManager.Instance != null
                        ? TitanOrbitSessionManager.Instance.GetServerConnectedPlayerCount()
                        : 0;
                    TrackEmptyMatchTime(playerCount);

                    bool isFull = playerCount >= _config.MaxPlayers;
                    long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long ageSeconds = nowEpoch - _createdAtEpochSeconds;

                    if (!IsRecreateInProgress() && playerCount == 0 && _emptySinceUtc.HasValue &&
                        !string.IsNullOrWhiteSpace(lobbyId))
                    {
                        double emptySeconds = (DateTime.UtcNow - _emptySinceUtc.Value).TotalSeconds;
                        if (emptySeconds >= _config.EmptyMatchRecreateSeconds)
                            pendingEmptyRecreate = true;
                    }

                    if (_matchIsLatest && !_spawnedFromAge && playerCount > 0 &&
                        ageSeconds >= _config.AgeThresholdSeconds && !isFull)
                    {
                        _spawnedFromAge = true;
                        _matchIsLatest = false;
                        string closingLobbyId = lobbyId;
                        Debug.Log("[TitanOrbitDedicatedServerHost] Age rotation: spawning successor for " + closingLobbyId);
                        SpawnNextMatch(nextIsLatest: true);
                        _ = HandoffAndCloseLobbyAsync(closingLobbyId, "age_rotation");
                    }

                    if (!_spawnedFromFull && isFull)
                    {
                        _spawnedFromFull = true;
                        bool nextIsLatest = _matchIsLatest;
                        _matchIsLatest = false;
                        string closingLobbyId = lobbyId;
                        Debug.Log("[TitanOrbitDedicatedServerHost] Full rotation for " + closingLobbyId);
                        SpawnNextMatch(nextIsLatest);
                        _ = HandoffAndCloseLobbyAsync(closingLobbyId, "full_rotation");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TitanOrbitDedicatedServerHost] Rotation error: " + ex.Message);
                }

                if (pendingEmptyRecreate && TitanOrbitSessionManager.Instance != null)
                {
                    Task<TitanOrbitSessionManager.DedicatedMatchRecreateResult> recreateTask =
                        TitanOrbitSessionManager.Instance.RecreateDedicatedMatchAsync(_config);
                    while (!recreateTask.IsCompleted)
                        yield return null;
                    if (!recreateTask.IsFaulted && recreateTask.Result != null)
                    {
                        var result = recreateTask.Result;
                        NotifyLobbyReplaced(result.LobbyId, result.CreatedAtEpochSeconds, result.IsLatest);
                        _spawnedFromAge = false;
                    }
                }

                yield return wait;
            }
        }

        IEnumerator LobbyPresenceWatchdogLoop()
        {
            int consecutiveFailures = 0;
            const int threshold = 4;
            var wait = new WaitForSeconds(45f);
            while (true)
            {
                yield return wait;

                string lobbyId = _activeLobbyId;
                Task<Lobby> task = null;
                if (!string.IsNullOrWhiteSpace(lobbyId))
                    task = LobbyService.Instance.GetLobbyAsync(lobbyId);

                if (task != null)
                {
                    while (!task.IsCompleted)
                        yield return null;
                }

                try
                {
                    if (task == null)
                        continue;
                    if (task.Exception != null)
                        throw task.Exception;
                    consecutiveFailures = 0;
                }
                catch (Exception e)
                {
                    consecutiveFailures++;
                    Debug.LogWarning("[TitanOrbitDedicatedServerHost] Lobby presence check failed (" +
                                     consecutiveFailures + "/" + threshold + "): " + e.Message);
                    if (consecutiveFailures >= threshold)
                    {
                        DedicatedServerFileLog.Append("watchdog", "Lobby unreachable; exiting process.");
                        Application.Quit(1);
                        yield break;
                    }
                }
            }
        }

        IEnumerator MatchRequestWatchdogLoop()
        {
            var wait = new WaitForSeconds(20f);
            while (true)
            {
                yield return wait;
                if (IsRecreateInProgress() || !_matchIsLatest)
                    continue;

                Task checkTask = ProcessMatchRequestsAsync();
                while (!checkTask.IsCompleted)
                    yield return null;
            }
        }

        async Task ProcessMatchRequestsAsync()
        {
            try
            {
                QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                {
                    Count = 10,
                    Filters = new List<QueryFilter>
                    {
                        new QueryFilter(
                            QueryFilter.FieldOptions.S1,
                            TitanOrbitLobbyService.LobbyMatchRequestGameName,
                            QueryFilter.OpOptions.EQ),
                    },
                    Order = new List<QueryOrder>
                    {
                        new QueryOrder(asc: false, field: QueryOrder.FieldOptions.Created)
                    }
                });

                if (response?.Results == null || response.Results.Count == 0)
                    return;

                bool foundNew = false;
                foreach (Lobby requestLobby in response.Results)
                {
                    if (requestLobby == null || string.IsNullOrWhiteSpace(requestLobby.Id))
                        continue;
                    if (!_processedMatchRequestLobbyIds.Add(requestLobby.Id))
                        continue;
                    foundNew = true;
                }

                if (!foundNew)
                    return;

                int playerCount = TitanOrbitSessionManager.Instance.GetServerConnectedPlayerCount();
                if (playerCount > 0)
                    return;

                var result = await TitanOrbitSessionManager.Instance.RecreateDedicatedMatchAsync(_config);
                if (result != null)
                    NotifyLobbyReplaced(result.LobbyId, result.CreatedAtEpochSeconds, result.IsLatest);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitDedicatedServerHost] Match request watchdog: " + e.Message);
            }
        }

        IEnumerator NetcodeHealthLoop()
        {
            var wait = new WaitForSeconds(30f);
            while (true)
            {
                yield return wait;
                if (IsRecreateInProgress())
                    continue;

                if (!TitanOrbitSessionManager.Instance.IsServerListening())
                {
                    Debug.LogError("[TitanOrbitDedicatedServerHost] Server stopped listening; exiting.");
                    _ = CloseLobbyAndExitAsync(_activeLobbyId, "netcode_not_listening");
                    yield break;
                }
            }
        }

        static bool IsRecreateInProgress()
        {
            return TitanOrbitSessionManager.Instance != null &&
                   TitanOrbitSessionManager.Instance.IsRecreateDedicatedMatchInProgress;
        }

        void TrackEmptyMatchTime(int playerCount)
        {
            if (playerCount > 0)
            {
                _emptySinceUtc = null;
                return;
            }

            if (!_emptySinceUtc.HasValue)
                _emptySinceUtc = DateTime.UtcNow;
        }

        void SpawnNextMatch(bool nextIsLatest)
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                int derivedPort = _config.ServerPort + (pid % 2000) + UnityEngine.Random.Range(0, 2000);
                if (derivedPort > 65000)
                    derivedPort = 65000;

                string exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    Debug.LogError("[TitanOrbitDedicatedServerHost] Cannot determine server executable path.");
                    return;
                }

                string args =
#if !UNITY_SERVER
                    "-batchmode -nographics " +
#endif
                    "--titanOrbitDedicated=1 " +
                    $"--maxPlayers={_config.MaxPlayers} " +
                    $"--serverPort={derivedPort} " +
                    $"--relayProtocol={_config.RelayProtocol} " +
                    $"--serverListenAddress={_config.ServerListenAddress} " +
                    $"--emptyMatchRecreateSeconds={_config.EmptyMatchRecreateSeconds} " +
                    $"--isLatest={(nextIsLatest ? 1 : 0)}";

                Process.Start(new ProcessStartInfo(exePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitDedicatedServerHost] SpawnNextMatch failed: " + e.Message);
            }
        }

        static async Task HandoffAndCloseLobbyAsync(string lobbyId, string reason)
        {
            bool successorReady = await WaitForSuccessorLatestLobbyAsync(lobbyId, TimeSpan.FromSeconds(120));
            if (!successorReady)
            {
                Debug.LogWarning("[TitanOrbitDedicatedServerHost] Successor lobby not detected before closing " +
                                 lobbyId + " (" + reason + ").");
            }

            await TitanOrbitSessionManager.Instance.CloseLobbyForNewJoinersAsync(lobbyId, reason);
        }

        static async Task<bool> WaitForSuccessorLatestLobbyAsync(string excludeLobbyId, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                    {
                        Count = 10,
                        Filters = new List<QueryFilter>
                        {
                            new QueryFilter(QueryFilter.FieldOptions.S1, TitanOrbitLobbyService.LobbyGameNameValue,
                                QueryFilter.OpOptions.EQ),
                            new QueryFilter(QueryFilter.FieldOptions.N1, "1", QueryFilter.OpOptions.EQ),
                            new QueryFilter(QueryFilter.FieldOptions.N2, "1", QueryFilter.OpOptions.EQ),
                        }
                    });

                    if (response?.Results != null)
                    {
                        foreach (Lobby candidate in response.Results)
                        {
                            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id))
                                continue;
                            if (string.Equals(candidate.Id, excludeLobbyId, StringComparison.Ordinal))
                                continue;
                            if (candidate.Data != null &&
                                candidate.Data.ContainsKey(TitanOrbitLobbyService.LobbyServerListenAddressKey))
                                return true;
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[TitanOrbitDedicatedServerHost] WaitForSuccessor query failed: " + e.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }

            return false;
        }

        static async Task CloseLobbyAndExitAsync(string lobbyId, string reason)
        {
            if (TitanOrbitSessionManager.Instance != null)
                await TitanOrbitSessionManager.Instance.CloseLobbyForNewJoinersAsync(lobbyId, reason);
            DedicatedServerFileLog.Append("watchdog", reason + "; exiting process.");
            Application.Quit(1);
        }

        void OnApplicationQuit()
        {
            if (!string.IsNullOrWhiteSpace(_activeLobbyId) && TitanOrbitSessionManager.Instance != null)
                _ = TitanOrbitSessionManager.Instance.CloseLobbyForNewJoinersAsync(_activeLobbyId, "process_exit");
        }
    }
}
