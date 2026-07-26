using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    /// Keeps dedicated NetCode matches available: heartbeats, rotation, empty-lobby recreate, match requests,
    /// and self-heal when no joinable latest lobby exists in UGS.
    /// Started by <see cref="TitanOrbitSessionManager"/> after the first Relay lobby is live.
    ///
    /// Lifecycle policy (do not break):
    /// - While any NetCode player is connected, this match must keep running and stay joinable
    ///   (IsOpen=1). Age rotation may spawn a successor and demote IsLatest, but must not close
    ///   or wipe an occupied conquest map.
    /// - Idle teardown / in-process recreate starts only when player count hits zero; the empty
    ///   countdown resets at that moment (last player left). Until then, keep the sim alive
    ///   until empty-idle timeout or a real game-end condition (e.g. team wins).
    /// - When the last player leaves, orphan ship ghosts are wiped immediately so a new joiner
    ///   cannot be offered a previous player's ship via NetworkId reuse.
    /// - After N successful 30‑minute idle recreates only (default 6 ≈ 3h empty), exit so
    ///   systemd/Edgegap starts a fresh binary. Stale/self-heal/heartbeat/match-request must
    ///   NOT exit — that made Join Game empty more often (2026-07-25 regression).
    /// - Empty process also exits on sustained STRUGGLING netdiag or RSS over budget (memory
    ///   reclaim for IL2CPP — in-process lobby swap does not free the map ServerWorld).
    /// - Hang watchdog hard-exits if Update stops ticking; paused during Relay/lobby recreate.
    /// - Periodic memory/entity logs correlate RSS with recreate count vs load spikes.
    /// </summary>
    public class TitanOrbitDedicatedServerHost : MonoBehaviour
    {
        const int SuccessorWaitSecondsPerSpawnAttempt = 120;
        const int MaxSpawnAttemptsPerHandoff = 5;
        const float SpawnRetryDelaySeconds = 10f;
        const float SuccessorPollIntervalSeconds = 3f;
        const float SelfHealPollIntervalSeconds = 30f;
        /// <summary>How often we evaluate RSS / struggling empty-recycle (seconds).</summary>
        const float MemoryHealthPollSeconds = 15f;
        /// <summary>After a full handoff fails, wait before starting another (avoids spawn spam every 3s).</summary>
        const float HandoffFailureCooldownSeconds = 300f;

        /// <summary>
        /// [TITAN-ORBIT] How often the background hang thread samples the main-thread stamp.
        /// Shorter than <see cref="TitanOrbitServerCommandLine.MainThreadHangQuitSeconds"/>.
        /// </summary>
        const int HangWatchdogPollSeconds = 15;

        /// <summary>
        /// [TITAN-ORBIT] Ignore hang checks until the main thread has stamped at least once and
        /// this many seconds have passed since hosting started (boot / Relay allocate is slow).
        /// </summary>
        const int HangWatchdogBootGraceSeconds = 120;

        static TitanOrbitDedicatedServerHost s_Instance;

        readonly HashSet<string> _processedMatchRequestLobbyIds = new HashSet<string>(StringComparer.Ordinal);

        TitanOrbitServerCommandLine _config;
        string _activeLobbyId;
        long _createdAtEpochSeconds;
        bool _matchIsLatest;
        bool _spawnedFromAge;
        bool _spawnedFromFull;
        bool _handoffInProgress;
        DateTime? _rotationHandoffRetryAfterUtc;
        DateTime? _emptySinceUtc;
        DateTime? _localLobbyUnjoinableSinceUtc;
        Coroutine _rotationCoroutine;
        Coroutine _presenceCoroutine;
        Coroutine _matchRequestCoroutine;
        Coroutine _netcodeHealthCoroutine;
        Coroutine _selfHealCoroutine;
        Coroutine _handoffCoroutine;
        Coroutine _memoryHealthCoroutine;

        /// <summary>Unix seconds of last periodic memory log (throttles MemoryLogIntervalSeconds).</summary>
        int _lastMemoryLogUnixSeconds;

        /// <summary>
        /// Successful empty in-process recreates this process lifetime. When it reaches
        /// <see cref="TitanOrbitServerCommandLine.MaxInProcessEmptyRecreates"/>, the next empty
        /// idle triggers process exit instead of another Relay/lobby swap.
        /// </summary>
        int _successfulEmptyInProcessRecreates;

        /// <summary>
        /// [STANDARD] Unix seconds of last Unity main-thread Update. Written from Update;
        /// read from the hang watchdog background thread. 0 = not stamped yet.
        /// </summary>
        int _mainThreadHeartbeatUnixSeconds;

        /// <summary>Unix seconds when <see cref="StartHosting"/> ran (hang boot grace).</summary>
        int _hostingStartedUnixSeconds;

        /// <summary>Background hang-watchdog thread (null when disabled).</summary>
        Thread _hangWatchdogThread;

        /// <summary>Set when we intentionally exit so the hang thread does not race another Exit.</summary>
        volatile bool _processExitRequested;

        /// <summary>
        /// [TITAN-ORBIT] 1 while Relay/lobby recreate is in flight — hang watchdog must not kill
        /// a healthy process blocked on UGS/Relay awaits (false "no game" exits).
        /// </summary>
        int _hangWatchdogPausedFlag;

        /// <summary>Called from <see cref="TitanOrbitSessionManager"/> after heartbeat-driven recreate.</summary>
        public static void NotifyLobbyReplacedFromSession(string newLobbyId, long createdAtEpochSeconds, bool isLatest)
        {
            if (s_Instance != null)
                s_Instance.NotifyLobbyReplaced(newLobbyId, createdAtEpochSeconds, isLatest);
        }

        /// <summary>
        /// UTC unix seconds when the current empty-idle recreate will fire, for UGS lobby heartbeat /
        /// Join Game countdown. Returns false while players are connected or hosting has not started.
        /// </summary>
        /// <param name="killAtEpochSeconds">Deadline epoch when true; otherwise 0.</param>
        /// <returns>True when the match is empty and an idle kill deadline is known.</returns>
        public static bool TryGetEmptyIdleKillAtEpochSeconds(out long killAtEpochSeconds)
        {
            // --- TryGetEmptyIdleKillAtEpochSeconds ---
            killAtEpochSeconds = 0;
            if (s_Instance == null || s_Instance._config == null || !s_Instance._emptySinceUtc.HasValue)
                return false;

            // [TITAN-ORBIT] _emptySinceUtc is cleared whenever playerCount > 0 (TrackEmptyMatchTime).
            DateTime emptyUtc = DateTime.SpecifyKind(s_Instance._emptySinceUtc.Value, DateTimeKind.Utc);
            long emptySinceEpoch = new DateTimeOffset(emptyUtc).ToUnixTimeSeconds();
            int idleSeconds = Mathf.Max(60, s_Instance._config.EmptyMatchRecreateSeconds);
            killAtEpochSeconds = emptySinceEpoch + idleSeconds;
            return killAtEpochSeconds > 0;
        }

        public static void Begin(TitanOrbitServerCommandLine config, string lobbyId, long createdAtEpochSeconds, bool isLatest)
        {
            // --- Begin ---
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
            // --- Unity lifecycle ---
            _config = config ?? TitanOrbitServerCommandLine.Parse();
            _activeLobbyId = lobbyId;
            _createdAtEpochSeconds = createdAtEpochSeconds;
            _matchIsLatest = isLatest;
            _emptySinceUtc = DateTime.UtcNow;
            _localLobbyUnjoinableSinceUtc = null;
            _successfulEmptyInProcessRecreates = 0;
            _processExitRequested = false;
            _hostingStartedUnixSeconds = CurrentUnixSeconds();
            StampMainThreadHeartbeat();

            if (_rotationCoroutine != null) StopCoroutine(_rotationCoroutine);
            if (_presenceCoroutine != null) StopCoroutine(_presenceCoroutine);
            if (_matchRequestCoroutine != null) StopCoroutine(_matchRequestCoroutine);
            if (_netcodeHealthCoroutine != null) StopCoroutine(_netcodeHealthCoroutine);
            if (_selfHealCoroutine != null) StopCoroutine(_selfHealCoroutine);
            if (_memoryHealthCoroutine != null) StopCoroutine(_memoryHealthCoroutine);

            _rotationCoroutine = StartCoroutine(RotationLoop());
            _presenceCoroutine = StartCoroutine(LobbyPresenceWatchdogLoop());
            _matchRequestCoroutine = StartCoroutine(MatchRequestWatchdogLoop());
            _netcodeHealthCoroutine = StartCoroutine(NetcodeHealthLoop());
            _selfHealCoroutine = StartCoroutine(JoinableLobbySelfHealLoop());
            _memoryHealthCoroutine = StartCoroutine(MemoryHealthLoop());
            EnsureHangWatchdogStarted();

            DedicatedServerFileLog.Append(
                "lobby",
                "Dedicated host loops started lobbyId=" + lobbyId +
                " isLatest=" + isLatest +
                " maxInProcessEmptyRecreates=" + _config.MaxInProcessEmptyRecreates +
                " mainThreadHangQuitSeconds=" + _config.MainThreadHangQuitSeconds +
                " rssRecycleMb=" + _config.RssRecycleMb +
                " strugglingSamplesBeforeRecycle=" + _config.StrugglingSamplesBeforeRecycle +
                " memoryLogIntervalSeconds=" + _config.MemoryLogIntervalSeconds);
            Debug.Log("[TitanOrbitDedicatedServerHost] Hosting loops started for lobby " + lobbyId);

            // [TITAN-ORBIT] Boot baseline — compare later logs for recreate-linked RSS climb.
            DedicatedServerMemoryTelemetry.LogSnapshot(
                "host_start",
                emptyInProcessRecreates: 0,
                playerCount: 0);
            _lastMemoryLogUnixSeconds = CurrentUnixSeconds();
        }

        /// <summary>
        /// [UNITY] Main-thread tick stamp for the hang watchdog. Must stay cheap — no ECS queries.
        /// </summary>
        void Update()
        {
            StampMainThreadHeartbeat();
        }

        public void NotifyLobbyReplaced(string newLobbyId, long createdAtEpochSeconds, bool isLatest)
        {
            // --- NotifyLobbyReplaced ---
            _activeLobbyId = newLobbyId;
            _createdAtEpochSeconds = createdAtEpochSeconds;
            _matchIsLatest = isLatest;
            _spawnedFromAge = false;
            _spawnedFromFull = false;
            _emptySinceUtc = DateTime.UtcNow;
            _localLobbyUnjoinableSinceUtc = null;
            DedicatedServerFileLog.Append(
                "lobby",
                "Lobby replaced id=" + newLobbyId + " isLatest=" + isLatest +
                " emptyInProcessRecreates=" + _successfulEmptyInProcessRecreates);
        }

        /// <summary>
        /// Pauses or resumes the main-thread hang watchdog. Session manager sets this around
        /// <c>RecreateDedicatedMatchAsync</c> so long UGS/Relay awaits are not treated as a hang.
        /// </summary>
        /// <param name="paused">True while recreate is in progress.</param>
        public static void SetHangWatchdogPaused(bool paused)
        {
            if (s_Instance == null)
                return;
            Volatile.Write(ref s_Instance._hangWatchdogPausedFlag, paused ? 1 : 0);
            if (!paused)
                s_Instance.StampMainThreadHeartbeat();
        }

        IEnumerator RotationLoop()
        {
            // --- RotationLoop ---
            var wait = new WaitForSeconds(3f);
            while (true)
            {
                bool pendingEmptyRecreate = false;
                bool pendingStaleRecreate = false;
                try
                {
                    if (!_handoffInProgress)
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

                        // [TITAN-ORBIT] Fast path when our lobby was closed or heartbeat-stale (tracked by self-heal).
                        if (!IsRecreateInProgress() && playerCount == 0 && _localLobbyUnjoinableSinceUtc.HasValue)
                        {
                            double unjoinableSeconds =
                                (DateTime.UtcNow - _localLobbyUnjoinableSinceUtc.Value).TotalSeconds;
                            if (unjoinableSeconds >= _config.StaleLobbyRecreateSeconds)
                                pendingStaleRecreate = true;
                        }

                        // [TITAN-ORBIT] Age rotation — spawn a fresh IsLatest successor for new joiners.
                        // Occupied maps stay open (demoted only); see RunRotationHandoff.
                        if (_matchIsLatest && !_spawnedFromAge && playerCount > 0 &&
                            ageSeconds >= _config.AgeThresholdSeconds && !isFull)
                        {
                            Debug.Log("[TitanOrbitDedicatedServerHost] Age rotation: starting handoff for " + lobbyId);
                            BeginRotationHandoff(lobbyId, "age_rotation", nextIsLatest: true);
                        }
                        else if (!_spawnedFromFull && isFull)
                        {
                            // Full = no room for more players; close listing and spawn capacity elsewhere.
                            bool nextIsLatest = _matchIsLatest;
                            Debug.Log("[TitanOrbitDedicatedServerHost] Full rotation: starting handoff for " + lobbyId);
                            BeginRotationHandoff(lobbyId, "full_rotation", nextIsLatest);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TitanOrbitDedicatedServerHost] Rotation error: " + ex.Message);
                }

                // In-process recreate — skip while a process handoff is in flight (Relay churn).
                // [TITAN-ORBIT] Only when playerCount==0 (gated above). Process recycle counts ONLY
                // empty_match_recreate (30‑min idle) — never stale/self-heal (those must repair
                // without exiting, or Join Game goes empty more often).
                if ((pendingEmptyRecreate || pendingStaleRecreate) && !_handoffInProgress &&
                    TitanOrbitSessionManager.Instance != null)
                {
                    string reason = pendingStaleRecreate ? "stale_lobby_recreate" : "empty_match_recreate";
                    bool isIdleEmptyRecreate = reason == "empty_match_recreate";

                    // [TITAN-ORBIT] Prefer process recycle over another Relay swap when already thrashing
                    // or over RSS budget (2026-07-26: recreate during STRUGGLING did not save the VM).
                    if (isIdleEmptyRecreate && TryGetEmptyPressureRecycleReason(out string pressureReason))
                    {
                        DedicatedServerMemoryTelemetry.LogSnapshot(
                            "before_recycle_" + pressureReason,
                            _successfulEmptyInProcessRecreates,
                            playerCount: 0);
                        yield return ExitForProcessRecycleCoroutine(pressureReason);
                        yield break;
                    }

                    if (isIdleEmptyRecreate && ShouldRecycleProcessInsteadOfInProcessEmptyRecreate())
                    {
                        DedicatedServerMemoryTelemetry.LogSnapshot(
                            "before_recycle_idle_count",
                            _successfulEmptyInProcessRecreates,
                            playerCount: 0);
                        yield return ExitForProcessRecycleCoroutine(reason + "_process_recycle");
                        yield break;
                    }

                    yield return RunInProcessRecreateCoroutine(
                        reason,
                        forceIsLatest: true,
                        countAsEmptyRecycle: isIdleEmptyRecreate);
                }

                yield return wait;
            }
        }

        /// <summary>
        /// [TITAN-ORBIT] Ensures at least one joinable IsLatest dedicated lobby exists; recreates when this server is idle.
        /// </summary>
        IEnumerator JoinableLobbySelfHealLoop()
        {
            // --- JoinableLobbySelfHealLoop ---
            var wait = new WaitForSeconds(SelfHealPollIntervalSeconds);
            while (true)
            {
                yield return wait;

                if (IsRecreateInProgress() || _handoffInProgress)
                    continue;

                Task selfHealTask = EvaluateJoinableLobbySelfHealAsync();
                while (!selfHealTask.IsCompleted)
                    yield return null;
            }
        }

        async Task EvaluateJoinableLobbySelfHealAsync()
        {
            // --- EvaluateJoinableLobbySelfHealAsync ---
            try
            {
                if (TitanOrbitSessionManager.Instance == null)
                    return;

                int playerCount = TitanOrbitSessionManager.Instance.GetServerConnectedPlayerCount();
                if (playerCount > 0)
                {
                    _localLobbyUnjoinableSinceUtc = null;
                    return;
                }

                bool ourLobbyJoinable = await TitanOrbitLobbyService.TryIsLobbyJoinableByIdAsync(_activeLobbyId);
                if (ourLobbyJoinable)
                {
                    _localLobbyUnjoinableSinceUtc = null;
                    return;
                }

                bool anyJoinableLatest = await TitanOrbitLobbyService.QueryAnyJoinableLatestDedicatedLobbyExistsAsync();
                if (anyJoinableLatest)
                {
                    // Another process already publishes a fresh IsLatest lobby (e.g. after handoff).
                    _localLobbyUnjoinableSinceUtc = null;
                    return;
                }

                if (!_localLobbyUnjoinableSinceUtc.HasValue)
                    _localLobbyUnjoinableSinceUtc = DateTime.UtcNow;

                // [TITAN-ORBIT] Self-heal must ALWAYS try in-process recreate — never exit here.
                // Exiting when there is already no joinable lobby made Join Game empty more often.
                Debug.LogWarning("[TitanOrbitDedicatedServerHost] Self-heal: no joinable latest lobby in UGS; recreating.");
                DedicatedServerFileLog.Append("self_heal", "self_heal_no_joinable_latest lobby=" + _activeLobbyId);

                var result = await TitanOrbitSessionManager.Instance.RecreateDedicatedMatchAsync(_config, forceIsLatest: true);
                if (result != null)
                    NotifyLobbyReplaced(result.LobbyId, result.CreatedAtEpochSeconds, result.IsLatest);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitDedicatedServerHost] Self-heal error: " + e.Message);
            }
        }

        /// <summary>Starts a single handoff coroutine; keeps <c>_matchIsLatest</c> until successor is confirmed.</summary>
        void BeginRotationHandoff(string closingLobbyId, string reason, bool nextIsLatest)
        {
            // --- BeginRotationHandoff ---
            if (_handoffInProgress || string.IsNullOrWhiteSpace(closingLobbyId))
                return;

            if (_rotationHandoffRetryAfterUtc.HasValue && DateTime.UtcNow < _rotationHandoffRetryAfterUtc.Value)
                return;

            if (_handoffCoroutine != null)
                StopCoroutine(_handoffCoroutine);

            _handoffCoroutine = StartCoroutine(RunRotationHandoff(closingLobbyId, reason, nextIsLatest));
        }

        /// <summary>
        /// Spawn successor with retries, wait for its UGS lobby, then hand off listing state.
        /// Occupied non-full matches are demoted from IsLatest but stay IsOpen so conquest maps
        /// remain joinable. Full matches (and empty edge cases) close for new joiners.
        /// On failure the old lobby stays open and heartbeating so browse never goes empty.
        /// </summary>
        IEnumerator RunRotationHandoff(string closingLobbyId, string reason, bool nextIsLatest)
        {
            // --- RunRotationHandoff ---
            _handoffInProgress = true;
            DedicatedServerFileLog.Append("rotation", "Handoff started reason=" + reason + " closing=" + closingLobbyId +
                                                  " nextIsLatest=" + nextIsLatest);

            bool handoffComplete = false;
            for (int attempt = 1; attempt <= MaxSpawnAttemptsPerHandoff && !handoffComplete; attempt++)
            {
                if (!TrySpawnNextMatch(nextIsLatest))
                {
                    Debug.LogWarning("[TitanOrbitDedicatedServerHost] SpawnNextMatch failed (attempt " + attempt + "/" +
                                     MaxSpawnAttemptsPerHandoff + ") for " + reason + ".");
                    DedicatedServerFileLog.Append("rotation", "SpawnNextMatch failed attempt=" + attempt + " " + reason);
                    yield return new WaitForSeconds(SpawnRetryDelaySeconds);
                    continue;
                }

                Task<bool> waitTask = WaitForSuccessorLobbyAsync(
                    closingLobbyId,
                    requireLatest: nextIsLatest,
                    TimeSpan.FromSeconds(SuccessorWaitSecondsPerSpawnAttempt));
                while (!waitTask.IsCompleted)
                    yield return null;

                bool successorReady = !waitTask.IsFaulted && waitTask.Result;
                if (!successorReady)
                {
                    Debug.LogWarning("[TitanOrbitDedicatedServerHost] Successor not detected (attempt " + attempt + "/" +
                                     MaxSpawnAttemptsPerHandoff + ") for " + reason + "; will retry spawn.");
                    DedicatedServerFileLog.Append("rotation", "Successor wait failed attempt=" + attempt + " " + reason);
                    yield return new WaitForSeconds(SpawnRetryDelaySeconds);
                    continue;
                }

                // --- Listing handoff (after successor is live) ---
                // [TITAN-ORBIT] Age rotation used to CloseLobby (IsOpen=0) while players were still
                // in the match — the map vanished from Join Game mid-conquest. Occupied maps must
                // stay open; only full lobbies need a hard close (no free slots).
                int playersStillConnected = TitanOrbitSessionManager.Instance != null
                    ? TitanOrbitSessionManager.Instance.GetServerConnectedPlayerCount()
                    : 0;
                bool hardCloseListing =
                    string.Equals(reason, "full_rotation", StringComparison.Ordinal) ||
                    playersStillConnected <= 0;

                Task listingTask = hardCloseListing
                    ? TitanOrbitSessionManager.Instance.CloseLobbyForNewJoinersAsync(closingLobbyId, reason)
                    : TitanOrbitSessionManager.Instance.DemoteFromLatestKeepOpenAsync(closingLobbyId, reason);
                while (!listingTask.IsCompleted)
                    yield return null;

                if (string.Equals(reason, "age_rotation", StringComparison.Ordinal))
                    _spawnedFromAge = true;
                else if (string.Equals(reason, "full_rotation", StringComparison.Ordinal))
                    _spawnedFromFull = true;

                _matchIsLatest = false;
                _rotationHandoffRetryAfterUtc = null;
                handoffComplete = true;
                string listingAction = hardCloseListing ? "closed" : "demoted_keep_open";
                DedicatedServerFileLog.Append("rotation", "Handoff complete reason=" + reason + " " + listingAction +
                                                      "=" + closingLobbyId + " players=" + playersStillConnected);
                Debug.Log("[TitanOrbitDedicatedServerHost] Handoff complete (" + reason + "); " + listingAction +
                          " lobby " + closingLobbyId + " players=" + playersStillConnected);
            }

            if (!handoffComplete)
            {
                _rotationHandoffRetryAfterUtc = DateTime.UtcNow.AddSeconds(HandoffFailureCooldownSeconds);
                Debug.LogError("[TitanOrbitDedicatedServerHost] Handoff failed after " + MaxSpawnAttemptsPerHandoff +
                               " attempts; keeping lobby open so browse stays populated. reason=" + reason +
                               " lobby=" + closingLobbyId + " nextRetryAfter=" +
                               HandoffFailureCooldownSeconds + "s");
                DedicatedServerFileLog.Append("rotation", "Handoff aborted — lobby kept open reason=" + reason +
                                                      " lobby=" + closingLobbyId);

                int playerCount = TitanOrbitSessionManager.Instance != null
                    ? TitanOrbitSessionManager.Instance.GetServerConnectedPlayerCount()
                    : 0;
                if (playerCount == 0)
                    yield return RunInProcessRecreateCoroutine("handoff_spawn_fallback", forceIsLatest: true);
            }

            _handoffInProgress = false;
            _handoffCoroutine = null;
        }

        /// <summary>
        /// Runs <see cref="TitanOrbitSessionManager.RecreateDedicatedMatchAsync"/> on the main thread
        /// (coroutine). Caller must already have verified the match is empty when
        /// <paramref name="countAsEmptyRecycle"/> is true.
        /// </summary>
        /// <param name="reason">Logged recreate reason.</param>
        /// <param name="forceIsLatest">Force new lobby IsLatest=1.</param>
        /// <param name="countAsEmptyRecycle">
        /// When true, increments the empty-recreate counter used for process recycle.
        /// </param>
        IEnumerator RunInProcessRecreateCoroutine(string reason, bool forceIsLatest, bool countAsEmptyRecycle = false)
        {
            // --- RunInProcessRecreateCoroutine ---
            if (IsRecreateInProgress() || TitanOrbitSessionManager.Instance == null)
                yield break;

            DedicatedServerFileLog.Append(
                "self_heal",
                "In-process recreate reason=" + reason +
                " forceLatest=" + forceIsLatest +
                " emptyRecreateCount=" + _successfulEmptyInProcessRecreates);
            Task<TitanOrbitSessionManager.DedicatedMatchRecreateResult> recreateTask =
                TitanOrbitSessionManager.Instance.RecreateDedicatedMatchAsync(_config, forceIsLatest);
            while (!recreateTask.IsCompleted)
                yield return null;

            if (recreateTask.IsFaulted)
            {
                DedicatedServerFileLog.Append(
                    "self_heal",
                    "In-process recreate FAULT reason=" + reason,
                    recreateTask.Exception);
                yield break;
            }

            if (recreateTask.Result != null)
            {
                var result = recreateTask.Result;
                NotifyLobbyReplaced(result.LobbyId, result.CreatedAtEpochSeconds, result.IsLatest);
                _spawnedFromAge = false;
                if (countAsEmptyRecycle)
                    NoteSuccessfulEmptyInProcessRecreate(reason);
            }
            else
            {
                DedicatedServerFileLog.Append("self_heal", "In-process recreate returned null reason=" + reason);
            }
        }

        /// <summary>
        /// Polls UGS that our lobby still exists. Only quits the process when the lobby is
        /// unreachable AND the match is empty — never kill an occupied conquest map on a UGS blip.
        /// </summary>
        IEnumerator LobbyPresenceWatchdogLoop()
        {
            // --- LobbyPresenceWatchdogLoop ---
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
                    if (consecutiveFailures < threshold)
                        continue;

                    // [TITAN-ORBIT] Players still in sim → keep process alive; UGS can recover on next heartbeat.
                    int playerCount = TitanOrbitSessionManager.Instance != null
                        ? TitanOrbitSessionManager.Instance.GetServerConnectedPlayerCount()
                        : 0;
                    if (playerCount > 0)
                    {
                        DedicatedServerFileLog.Append("watchdog",
                            "Lobby unreachable but players=" + playerCount + "; keeping process alive.");
                        Debug.LogWarning("[TitanOrbitDedicatedServerHost] Lobby unreachable with " +
                                         playerCount + " player(s) connected — not exiting.");
                        consecutiveFailures = 0;
                        continue;
                    }

                    DedicatedServerFileLog.Append("watchdog", "Lobby unreachable; exiting empty process.");
                    Application.Quit(1);
                    yield break;
                }
            }
        }

        IEnumerator MatchRequestWatchdogLoop()
        {
            // --- MatchRequestWatchdogLoop ---
            var wait = new WaitForSeconds(20f);
            while (true)
            {
                yield return wait;
                if (IsRecreateInProgress())
                    continue;

                bool shouldProcess = _matchIsLatest;
                if (!shouldProcess)
                {
                    Task<bool> existsTask = TitanOrbitLobbyService.QueryAnyJoinableLatestDedicatedLobbyExistsAsync();
                    while (!existsTask.IsCompleted)
                        yield return null;
                    shouldProcess = !existsTask.IsFaulted && !existsTask.Result;
                }

                if (!shouldProcess)
                    continue;

                Task checkTask = ProcessMatchRequestsAsync();
                while (!checkTask.IsCompleted)
                    yield return null;
            }
        }

        async Task ProcessMatchRequestsAsync()
        {
            // --- ProcessMatchRequestsAsync ---
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

                // [TITAN-ORBIT] Match request must recreate in-process — never exit (would empty Join Game).
                var result = await TitanOrbitSessionManager.Instance.RecreateDedicatedMatchAsync(_config, forceIsLatest: true);
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
            // --- NetcodeHealthLoop ---
            var wait = new WaitForSeconds(30f);
            while (true)
            {
                yield return wait;
                if (IsRecreateInProgress() || _handoffInProgress)
                    continue;

                if (!TitanOrbitSessionManager.Instance.IsServerListening())
                {
                    Debug.LogError("[TitanOrbitDedicatedServerHost] Server stopped listening; exiting.");
                    _ = CloseLobbyAndExitAsync(_activeLobbyId, "netcode_not_listening");
                    yield break;
                }
            }
        }

        /// <summary>
        /// Periodic RSS/entity telemetry plus empty-process recycle on RSS budget or sustained
        /// STRUGGLING. Never exits while players are connected.
        /// </summary>
        IEnumerator MemoryHealthLoop()
        {
            // --- MemoryHealthLoop ---
            var wait = new WaitForSeconds(MemoryHealthPollSeconds);
            while (true)
            {
                yield return wait;
                if (_processExitRequested || IsRecreateInProgress() || _handoffInProgress)
                    continue;
                if (TitanOrbitSessionManager.Instance == null || _config == null)
                    continue;

                int playerCount = TitanOrbitSessionManager.Instance.GetServerConnectedPlayerCount();

                // --- Periodic telemetry (occupied or empty) ---
                int logInterval = _config.MemoryLogIntervalSeconds;
                int now = CurrentUnixSeconds();
                if (logInterval > 0 && now - _lastMemoryLogUnixSeconds >= logInterval)
                {
                    DedicatedServerMemoryTelemetry.LogSnapshot(
                        "periodic",
                        _successfulEmptyInProcessRecreates,
                        playerCount);
                    _lastMemoryLogUnixSeconds = now;
                }

                // --- Empty-only pressure recycle ---
                if (playerCount > 0)
                    continue;

                if (TryGetEmptyPressureRecycleReason(out string reason))
                {
                    DedicatedServerMemoryTelemetry.LogSnapshot(
                        "before_recycle_" + reason,
                        _successfulEmptyInProcessRecreates,
                        playerCount: 0);
                    yield return ExitForProcessRecycleCoroutine(reason);
                    yield break;
                }
            }
        }

        /// <summary>
        /// Evaluates RSS / struggling empty-recycle (not idle-recreate count — that path is in RotationLoop).
        /// </summary>
        /// <param name="reason">Exit reason for the file log when true.</param>
        /// <returns>True when this empty process should exit now.</returns>
        bool TryGetEmptyPressureRecycleReason(out string reason)
        {
            // --- TryGetEmptyPressureRecycleReason ---
            reason = null;
            if (_config == null)
                return false;

            if (DedicatedServerMemoryTelemetry.ShouldRecycleEmptyDueToRss(_config.RssRecycleMb))
            {
                DedicatedServerMemoryTelemetry.TryReadProcessMemoryMb(out int rssMb, out _);
                reason = "rss_recycle_" + rssMb + "mb_ge_" + _config.RssRecycleMb;
                return true;
            }

            if (DedicatedServerMemoryTelemetry.ShouldRecycleEmptyDueToStruggling(
                    _config.StrugglingSamplesBeforeRecycle))
            {
                reason = "struggling_recycle_streak_" +
                         DedicatedServerMemoryTelemetry.ConsecutiveStrugglingSamples;
                return true;
            }

            return false;
        }

        static bool IsRecreateInProgress()
        {
            return TitanOrbitSessionManager.Instance != null &&
                   TitanOrbitSessionManager.Instance.IsRecreateDedicatedMatchInProgress;
        }

        /// <summary>
        /// Tracks when the match became empty. Countdown for idle recreate starts (or resets) at the
        /// moment the last player leaves — never while anyone is still connected.
        /// </summary>
        /// <param name="playerCount">Live NetCode <c>NetworkStreamConnection</c> count on ServerWorld.</param>
        void TrackEmptyMatchTime(int playerCount)
        {
            // --- TrackEmptyMatchTime ---
            // Anyone still playing → clear idle clock (occupied matches must not age into teardown).
            if (playerCount > 0)
            {
                _emptySinceUtc = null;
                return;
            }

            // First sample at zero players: start EmptyMatchRecreateSeconds countdown from now.
            // [TITAN-ORBIT] Also wipe orphan ships immediately so a mid-idle joiner is not offered
            // the previous player's hull via NetworkId reuse. Map planets stay.
            if (!_emptySinceUtc.HasValue)
            {
                _emptySinceUtc = DateTime.UtcNow;
                DedicatedServerFileLog.Append("idle",
                    "Empty match countdown started (last player left) lobby=" + _activeLobbyId +
                    " recreateAfterSeconds=" + (_config != null ? _config.EmptyMatchRecreateSeconds : -1));
                Debug.Log("[TitanOrbitDedicatedServerHost] Last player left — empty-idle countdown started (" +
                          (_config != null ? _config.EmptyMatchRecreateSeconds : -1) + "s).");
                if (TitanOrbitSessionManager.Instance != null)
                    TitanOrbitSessionManager.Instance.WipeOrphanPlayerShipsAndResetRosters();
            }
        }

        /// <summary>
        /// Launches a sibling headless process for rotation. Returns false when the executable cannot be resolved
        /// or <see cref="Process.Start"/> throws (logged for GCE diagnosis).
        /// </summary>
        bool TrySpawnNextMatch(bool nextIsLatest)
        {
            // --- Attempt resolution ---
            try
            {
                if (!TryResolveServerExecutable(out string exePath))
                {
                    Debug.LogError("[TitanOrbitDedicatedServerHost] Cannot determine server executable path.");
                    DedicatedServerFileLog.Append("rotation", "SpawnNextMatch failed: executable not resolved");
                    return false;
                }

                int pid = Process.GetCurrentProcess().Id;
                int derivedPort = _config.ServerPort + (pid % 2000) + UnityEngine.Random.Range(0, 2000);
                if (derivedPort > 65000)
                    derivedPort = 65000;

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
                    $"--ageThresholdSeconds={_config.AgeThresholdSeconds} " +
                    $"--staleLobbyRecreateSeconds={_config.StaleLobbyRecreateSeconds} " +
                    $"--maxInProcessEmptyRecreates={_config.MaxInProcessEmptyRecreates} " +
                    $"--mainThreadHangQuitSeconds={_config.MainThreadHangQuitSeconds} " +
                    $"--rssRecycleMb={_config.RssRecycleMb} " +
                    $"--strugglingSamplesBeforeRecycle={_config.StrugglingSamplesBeforeRecycle} " +
                    $"--memoryLogIntervalSeconds={_config.MemoryLogIntervalSeconds} " +
                    $"--waitNetworkManagerSeconds={_config.WaitNetworkManagerSeconds} " +
                    $"--isLatest={(nextIsLatest ? 1 : 0)}";

                if (!string.IsNullOrWhiteSpace(_config.ServerExecutablePath))
                    args += $" --serverExecutablePath={_config.ServerExecutablePath}";

                string logLine = "SpawnNextMatch exe=\"" + exePath + "\" args=\"" + args + "\"";
                DedicatedServerFileLog.Append("rotation", logLine);
                Debug.Log("[TitanOrbitDedicatedServerHost] " + logLine);

                Process.Start(new ProcessStartInfo(exePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory
                });

                Debug.Log("[TitanOrbitDedicatedServerHost] SpawnNextMatch started isLatest=" + nextIsLatest +
                          " port=" + derivedPort);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitDedicatedServerHost] SpawnNextMatch failed: " + e.Message);
                DedicatedServerFileLog.Append("rotation", "SpawnNextMatch exception", e);
                return false;
            }
        }

        /// <summary>
        /// Resolves the player binary for rotation spawns. On Linux GCE, <c>MainModule</c> can be empty;
        /// fall back to siblings of <c>Application.dataPath</c> (deploy root) or <see cref="TitanOrbitServerCommandLine.ServerExecutablePath"/>.
        /// </summary>
        bool TryResolveServerExecutable(out string exePath)
        {
            // --- CLI override ---
            if (!string.IsNullOrWhiteSpace(_config?.ServerExecutablePath))
            {
                exePath = _config.ServerExecutablePath.Trim();
                if (File.Exists(exePath))
                    return true;

                Debug.LogWarning("[TitanOrbitDedicatedServerHost] serverExecutablePath not found: " + exePath);
            }

            // --- MainModule ---
            exePath = null;
            try
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                    return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitDedicatedServerHost] MainModule lookup failed: " + e.Message);
            }

            string baseDir = Application.dataPath != null ? Path.GetDirectoryName(Application.dataPath) : null;
            if (string.IsNullOrEmpty(baseDir))
                return false;

            string[] candidates = { "TitanOrbitServer.x86_64", "TitanOrbitServer" };
            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = Path.Combine(baseDir, candidates[i]);
                if (File.Exists(candidate))
                {
                    exePath = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Polls UGS until a joinable dedicated successor lobby appears (optionally <c>IsLatest=1</c>).
        /// </summary>
        static async Task<bool> WaitForSuccessorLobbyAsync(string excludeLobbyId, bool requireLatest, TimeSpan timeout)
        {
            // --- WaitForSuccessorLobbyAsync ---
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var filters = new List<QueryFilter>
                    {
                        new QueryFilter(QueryFilter.FieldOptions.S1, TitanOrbitLobbyService.LobbyGameNameValue,
                            QueryFilter.OpOptions.EQ),
                        new QueryFilter(QueryFilter.FieldOptions.N1, "1", QueryFilter.OpOptions.EQ),
                    };
                    if (requireLatest)
                    {
                        filters.Add(new QueryFilter(QueryFilter.FieldOptions.N2, "1", QueryFilter.OpOptions.EQ));
                    }

                    QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                    {
                        Count = 10,
                        Filters = filters
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

                await Task.Delay(TimeSpan.FromSeconds(SuccessorPollIntervalSeconds));
            }

            return false;
        }

        /// <summary>
        /// True when this empty host has already done enough <c>empty_match_recreate</c> cycles
        /// and should exit for a fresh binary. Only consulted on the 30‑minute idle path.
        /// </summary>
        bool ShouldRecycleProcessInsteadOfInProcessEmptyRecreate()
        {
            // --- ShouldRecycleProcessInsteadOfInProcessEmptyRecreate ---
            // [TITAN-ORBIT] 0 = unlimited in-process (legacy / debug).
            if (_config == null || _config.MaxInProcessEmptyRecreates <= 0)
                return false;
            return _successfulEmptyInProcessRecreates >= _config.MaxInProcessEmptyRecreates;
        }

        /// <summary>
        /// Increments the idle-empty recreate counter (only call for <c>empty_match_recreate</c>).
        /// </summary>
        /// <param name="reason">Why this recreate ran (should be empty_match_recreate).</param>
        void NoteSuccessfulEmptyInProcessRecreate(string reason)
        {
            // --- NoteSuccessfulEmptyInProcessRecreate ---
            // [TITAN-ORBIT] Guard: never let stale/self-heal reasons inflate the recycle counter.
            if (!string.Equals(reason, "empty_match_recreate", StringComparison.Ordinal))
            {
                DedicatedServerFileLog.Append(
                    "self_heal",
                    "Ignoring non-idle recreate for process-recycle counter reason=" + reason);
                return;
            }

            _successfulEmptyInProcessRecreates++;
            int max = _config != null ? _config.MaxInProcessEmptyRecreates : 0;
            DedicatedServerFileLog.Append(
                "self_heal",
                "Idle empty_match_recreate ok count=" + _successfulEmptyInProcessRecreates +
                (max > 0 ? ("/" + max + " then process recycle") : " (unlimited)"));

            // [TITAN-ORBIT] Correlate RSS with recreate count (climb per recreate vs sudden spike).
            DedicatedServerMemoryTelemetry.LogSnapshot(
                "after_idle_recreate",
                _successfulEmptyInProcessRecreates,
                playerCount: 0);
            _lastMemoryLogUnixSeconds = CurrentUnixSeconds();
        }

        /// <summary>
        /// Closes the current lobby listing and exits so systemd/Edgegap can restart a clean process.
        /// Only called from empty-server paths.
        /// </summary>
        /// <param name="reason">Watchdog / recycle reason string for the file log.</param>
        IEnumerator ExitForProcessRecycleCoroutine(string reason)
        {
            // --- ExitForProcessRecycleCoroutine ---
            Task exitTask = ExitForProcessRecycleAsync(reason);
            while (!exitTask.IsCompleted)
                yield return null;
        }

        /// <summary>
        /// Async variant of process recycle exit (self-heal / match-request paths).
        /// </summary>
        /// <param name="reason">Logged exit reason.</param>
        async Task ExitForProcessRecycleAsync(string reason)
        {
            // --- ExitForProcessRecycleAsync ---
            DedicatedServerFileLog.Append(
                "watchdog",
                "Process recycle after empty recreates count=" + _successfulEmptyInProcessRecreates +
                " max=" + (_config != null ? _config.MaxInProcessEmptyRecreates : -1) +
                " reason=" + reason);
            Debug.LogWarning("[TitanOrbitDedicatedServerHost] " + reason +
                             " — exiting empty process for orchestrator restart.");
            await CloseLobbyAndExitAsync(_activeLobbyId, reason);
        }

        /// <summary>
        /// Starts a background thread that hard-exits if Unity's main thread stops ticking.
        /// Coroutines cannot detect a deadlocked main thread; this can.
        /// </summary>
        void EnsureHangWatchdogStarted()
        {
            // --- EnsureHangWatchdogStarted ---
            if (_config == null || _config.MainThreadHangQuitSeconds <= 0)
                return;
            if (_hangWatchdogThread != null && _hangWatchdogThread.IsAlive)
                return;

            int hangSeconds = _config.MainThreadHangQuitSeconds;
            int bootGrace = HangWatchdogBootGraceSeconds;
            int pollSeconds = HangWatchdogPollSeconds;

            _hangWatchdogThread = new Thread(() => HangWatchdogThreadMain(hangSeconds, bootGrace, pollSeconds))
            {
                IsBackground = true,
                Name = "TitanOrbitMainThreadHangWatchdog"
            };
            _hangWatchdogThread.Start();
            DedicatedServerFileLog.Append(
                "watchdog",
                "Main-thread hang watchdog started hangQuitSeconds=" + hangSeconds +
                " bootGraceSeconds=" + bootGrace);
        }

        /// <summary>
        /// Background loop: if main-thread Update stamps go stale, <see cref="Environment.Exit"/>.
        /// </summary>
        void HangWatchdogThreadMain(int hangSeconds, int bootGraceSeconds, int pollSeconds)
        {
            // --- HangWatchdogThreadMain ---
            try
            {
                Thread.Sleep(Math.Max(1000, bootGraceSeconds * 1000));
                while (!_processExitRequested)
                {
                    Thread.Sleep(Math.Max(1000, pollSeconds * 1000));
                    if (_processExitRequested)
                        break;

                    // [TITAN-ORBIT] Recreate / UGS awaits — do not treat as hang.
                    if (Volatile.Read(ref _hangWatchdogPausedFlag) != 0)
                        continue;

                    int started = _hostingStartedUnixSeconds;
                    int last = Volatile.Read(ref _mainThreadHeartbeatUnixSeconds);
                    int now = CurrentUnixSeconds();
                    if (started <= 0 || last <= 0)
                        continue;
                    if (now - started < bootGraceSeconds)
                        continue;

                    int age = now - last;
                    if (age < hangSeconds)
                        continue;

                    // [TITAN-ORBIT] Main thread wedged — Application.Quit may never run. Hard exit.
                    try
                    {
                        DedicatedServerFileLog.Append(
                            "watchdog",
                            "Main-thread hang detected ageSeconds=" + age +
                            " limit=" + hangSeconds + "; Environment.Exit(1)");
                    }
                    catch
                    {
                        // [STANDARD] Best-effort log — exit even if file IO fails.
                    }

                    _processExitRequested = true;
                    Environment.Exit(1);
                }
            }
            catch (ThreadAbortException)
            {
                // [STANDARD] Process teardown.
            }
            catch (Exception e)
            {
                try
                {
                    DedicatedServerFileLog.Append("watchdog", "Hang watchdog thread error: " + e.Message);
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>Stamps unix seconds for the hang watchdog (main thread only).</summary>
        void StampMainThreadHeartbeat()
        {
            Volatile.Write(ref _mainThreadHeartbeatUnixSeconds, CurrentUnixSeconds());
        }

        /// <summary>UTC unix seconds as int (watchdog-friendly, no DateTime on hot path).</summary>
        static int CurrentUnixSeconds()
        {
            return (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        static async Task CloseLobbyAndExitAsync(string lobbyId, string reason)
        {
            // --- CloseLobbyAndExitAsync ---
            if (s_Instance != null)
                s_Instance._processExitRequested = true;

            if (TitanOrbitSessionManager.Instance != null)
                await TitanOrbitSessionManager.Instance.CloseLobbyForNewJoinersAsync(lobbyId, reason);
            DedicatedServerFileLog.Append("watchdog", reason + "; exiting process.");
            Application.Quit(1);
        }

        void OnApplicationQuit()
        {
            _processExitRequested = true;
            if (!string.IsNullOrWhiteSpace(_activeLobbyId) && TitanOrbitSessionManager.Instance != null)
                _ = TitanOrbitSessionManager.Instance.CloseLobbyForNewJoinersAsync(_activeLobbyId, "process_exit");
        }
    }
}
