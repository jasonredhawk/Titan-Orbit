using TitanOrbit.Diagnostics;
using Unity.Entities;
using Unity.NetCode;
using Unity.Profiling;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Server-world timing diagnostics for dedicated server and editor host. Every 10 seconds logs
    /// effective simulation Hz, catch-up tick count, and Unity frame-time spikes so you can tell if
    /// GCE/the host machine is falling behind 60 Hz. Written to TitanOrbitDedicatedServer.log and
    /// Debug.Log. Paired with <see cref="TitanOrbitClientNetDiagnostics"/> on clients.
    /// World: ServerSimulation. Group: SimulationSystemGroup (last).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitServerSimulationDiagnosticsSystem : ISystem
    {
        const double LogIntervalSeconds = 10.0;

        /// <summary>ElapsedTime when the current measurement window started.</summary>
        double _periodStartElapsed;

        /// <summary>realtimeSinceStartup at window start — wall-clock sim Hz (ElapsedTime can lie).</summary>
        float _periodStartRealtime;

        /// <summary>ServerTick at window start — used to compute effective Hz.</summary>
        NetworkTick _periodStartTick;

        /// <summary>Number of simulation OnUpdate calls this window (sim steps executed).</summary>
        int _simStepsThisPeriod;

        /// <summary>Ticks where <see cref="NetworkTime.IsCatchUpTick"/> was true (server recovering from long frame).</summary>
        int _catchUpTicksThisPeriod;

        /// <summary>True after the first sample so we do not log before we have a baseline tick.</summary>
        bool _hasBaseline;

        /// <summary>
        /// Last computed effective sim Hz (0 until the first 10 s window). Published on the UGS lobby
        /// so Join Game can show a stalled GCE process without SSH.
        /// </summary>
        public static int LastEffectiveSimHz { get; private set; }

        /// <summary>realtimeSinceStartup at the previous OnUpdate (wall ms per sim tick).</summary>
        float _lastTickRealtime;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // --- Per-step counters ---
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            _simStepsThisPeriod++;

            // #region agent log
            float nowRt = UnityEngine.Time.realtimeSinceStartup;
            if (_lastTickRealtime > 0f && _simStepsThisPeriod == 2)
            {
                DedicatedServerFileLog.Append(
                    "pace",
                    "tickWallMs=" + ((nowRt - _lastTickRealtime) * 1000f).ToString("F1") +
                    " simHz=" + TitanOrbitServerTickRateSystem.SimulationHz +
                    " maxSteps=" + TitanOrbitServerTickRateSystem.MaxStepsPerFrame);
            }
            _lastTickRealtime = nowRt;
            // #endregion

            if (networkTime.IsCatchUpTick)
                _catchUpTicksThisPeriod++;

            double elapsed = SystemAPI.Time.ElapsedTime;
            if (!_hasBaseline)
            {
                _periodStartElapsed = elapsed;
                _periodStartRealtime = UnityEngine.Time.realtimeSinceStartup;
                _periodStartTick = networkTime.ServerTick;
                _hasBaseline = true;
                return;
            }

            if (elapsed - _periodStartElapsed < LogIntervalSeconds)
                return;

            // --- Compute effective sim rate ---
            double elapsedSeconds = elapsed - _periodStartElapsed;
            float realtimeNow = UnityEngine.Time.realtimeSinceStartup;
            float wallSeconds = realtimeNow - _periodStartRealtime;
            int tickDelta = networkTime.ServerTick.TicksSince(_periodStartTick);
            float elapsedHz = elapsedSeconds > 0.001 ? (float)(tickDelta / elapsedSeconds) : 0f;
            float wallHz = wallSeconds > 0.001f ? tickDelta / wallSeconds : 0f;
            // Publish wall-clock Hz — ElapsedTime stays ~60 while Unity frames are 3 s (GCE 2026-08-30).
            LastEffectiveSimHz = Mathf.RoundToInt(wallHz);
            float effectiveHz = wallHz;
            float expectedHz = TitanOrbitServerTickRateSystem.SimulationHz;
            float catchUpPercent = _simStepsThisPeriod > 0
                ? 100f * _catchUpTicksThisPeriod / _simStepsThisPeriod
                : 0f;

            TitanOrbitServerFrameDiagnostics.ConsumeAndReset(
                out float avgFrameMs,
                out float maxFrameMs,
                out int slowFrames,
                out int unityFrames,
                out float avgRealtimeMs,
                out float waitFpsMs,
                out float physicsMs,
                out float ghostSendMs);

            string verdict = InterpretServerVerdict(effectiveHz, expectedHz, catchUpPercent, slowFrames, unityFrames);

            long simTicks = TitanOrbitServerFrameBudgetBeginSystem.LastSimStartTimestamp;
            float simPassMs = 0f;
            if (simTicks != 0)
            {
                simPassMs = (float)(1000.0 * (System.Diagnostics.Stopwatch.GetTimestamp() - simTicks) /
                                    System.Diagnostics.Stopwatch.Frequency);
            }

            string line =
                "[NetDiagnostics/Server] effectiveSim=" + elapsedHz.ToString("F1") + "Hz wallSim=" + wallHz.ToString("F1") +
                "Hz (target " + expectedHz + ")" +
                " simSteps=" + _simStepsThisPeriod +
                " catchUpTicks=" + _catchUpTicksThisPeriod + " (" + catchUpPercent.ToString("F0") + "%)" +
                " maxSteps=" + TitanOrbitServerTickRateSystem.MaxStepsPerFrame +
                " unityFrames=" + unityFrames +
                " frameMs avg=" + avgFrameMs.ToString("F1") + " max=" + maxFrameMs.ToString("F1") +
                " realtimeMs avg=" + avgRealtimeMs.ToString("F1") +
                " ecsPassMs=" + simPassMs.ToString("F1") +
                " presentMs=" + waitFpsMs.ToString("F1") +
                " waitFpsMs=" + physicsMs.ToString("F1") +
                " ghostSendMs=" + ghostSendMs.ToString("F1") +
                " slowFrames(>20ms)=" + slowFrames +
                " | Verdict: " + verdict;

            Debug.Log(line);
            DedicatedServerFileLog.Append("netdiag-server", line);

            // [TITAN-ORBIT] Feed empty-process recycle (RSS/struggling) — host exits only when 0 players.
            DedicatedServerMemoryTelemetry.ReportSimHealthSample(catchUpPercent, avgFrameMs, verdict);

            // --- Reset window ---
            _periodStartElapsed = elapsed;
            _periodStartRealtime = realtimeNow;
            _periodStartTick = networkTime.ServerTick;
            _simStepsThisPeriod = 0;
            _catchUpTicksThisPeriod = 0;
        }

        /// <summary>Plain-language server health from tick rate and frame spikes.</summary>
        static string InterpretServerVerdict(
            float effectiveHz,
            float expectedHz,
            float catchUpPercent,
            int slowFrames,
            int unityFrames)
        {
            float minHealthyHz = expectedHz * 0.92f; // ~55 Hz at 60 target

            if (effectiveHz < 12f)
                return "STALLED — wall sim below 12 Hz. Compare waitFpsMs / physicsMs / ghostSendMs in this line";

            if (effectiveHz < minHealthyHz)
                return "SLOW — simulation below target (CPU overload or process throttled)";

            if (catchUpPercent > 15f)
                return "STRUGGLING — frequent catch-up ticks after frame hitches";

            if (unityFrames > 0 && slowFrames > unityFrames * 0.2f)
                return "FRAME SPIKES — Unity frames often exceed 20 ms (check VM CPU / GC)";

            return "healthy";
        }
    }

    /// <summary>
    /// Tracks UnityEngine frame delta on processes that run a server world (dedicated or host).
    /// Consumed by <see cref="TitanOrbitServerSimulationDiagnosticsSystem"/> each log interval.
    /// </summary>
    public sealed class TitanOrbitServerFrameDiagnostics : MonoBehaviour
    {
        static TitanOrbitServerFrameDiagnostics s_instance;

        float _sumDeltaMs;
        float _maxDeltaMs;
        int _frameCount;
        int _slowFrameCount;
        float _lastRealtime;
        float _sumRealtimeMs;
        float _maxRealtimeMs;
        float _nextPaceLogRealtime;
        ProfilerRecorder _waitFps;
        ProfilerRecorder _physics;
        ProfilerRecorder _ghostSend;
        long _sumWaitFpsNs;
        long _sumPhysicsNs;
        long _sumGhostSendNs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (s_instance != null)
                return;

            var go = new GameObject(nameof(TitanOrbitServerFrameDiagnostics));
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<TitanOrbitServerFrameDiagnostics>();
        }

        void OnEnable()
        {
            _waitFps = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "PresentAndWait");
            _physics = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "WaitForTargetFPS");
            _ghostSend = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "GhostSendSystem");
        }

        void OnDisable()
        {
            if (_waitFps.Valid)
                _waitFps.Dispose();
            if (_physics.Valid)
                _physics.Dispose();
            if (_ghostSend.Valid)
                _ghostSend.Dispose();
        }

        void Update()
        {
            if (_waitFps.Valid)
                _sumWaitFpsNs += _waitFps.LastValue;
            if (_physics.Valid)
                _sumPhysicsNs += _physics.LastValue;
            if (_ghostSend.Valid)
                _sumGhostSendNs += _ghostSend.LastValue;

            float now = Time.realtimeSinceStartup;
            float realtimeMs = _lastRealtime > 0f ? (now - _lastRealtime) * 1000f : 0f;
            _lastRealtime = now;
            if (realtimeMs > 0f)
            {
                _sumRealtimeMs += realtimeMs;
                _maxRealtimeMs = Mathf.Max(_maxRealtimeMs, realtimeMs);
            }

            float dtMs = Time.deltaTime * 1000f;
            _sumDeltaMs += dtMs;
            _maxDeltaMs = Mathf.Max(_maxDeltaMs, dtMs);
            _frameCount++;
            if (dtMs > 20f)
                _slowFrameCount++;

            // #region agent log
            if (now >= _nextPaceLogRealtime)
            {
                _nextPaceLogRealtime = now + 2f;
                DedicatedServerFileLog.Append(
                    "pace",
                    "targetFps=" + Application.targetFrameRate +
                    " vSync=" + QualitySettings.vSyncCount +
                    " maxDt=" + Time.maximumDeltaTime.ToString("F3") +
                    " deltaMs=" + dtMs.ToString("F1") +
                    " realtimeMs=" + realtimeMs.ToString("F1") +
                    " timeScale=" + Time.timeScale.ToString("F2"));
            }
            // #endregion
        }

        /// <summary>Reads and clears accumulated frame stats since the last server diagnostics log.</summary>
        public static void ConsumeAndReset(
            out float avgFrameMs,
            out float maxFrameMs,
            out int slowFrames,
            out int frames,
            out float avgRealtimeMs,
            out float waitFpsMs,
            out float physicsMs,
            out float ghostSendMs)
        {
            if (s_instance == null)
            {
                avgFrameMs = 0f;
                maxFrameMs = 0f;
                slowFrames = 0;
                frames = 0;
                avgRealtimeMs = 0f;
                waitFpsMs = 0f;
                physicsMs = 0f;
                ghostSendMs = 0f;
                return;
            }

            frames = s_instance._frameCount;
            slowFrames = s_instance._slowFrameCount;
            maxFrameMs = s_instance._maxDeltaMs;
            avgFrameMs = frames > 0 ? s_instance._sumDeltaMs / frames : 0f;
            avgRealtimeMs = frames > 0 ? s_instance._sumRealtimeMs / frames : 0f;
            waitFpsMs = frames > 0 ? s_instance._sumWaitFpsNs / (float)frames / 1_000_000f : 0f;
            physicsMs = frames > 0 ? s_instance._sumPhysicsNs / (float)frames / 1_000_000f : 0f;
            ghostSendMs = frames > 0 ? s_instance._sumGhostSendNs / (float)frames / 1_000_000f : 0f;

            s_instance._sumDeltaMs = 0f;
            s_instance._maxDeltaMs = 0f;
            s_instance._frameCount = 0;
            s_instance._slowFrameCount = 0;
            s_instance._sumRealtimeMs = 0f;
            s_instance._maxRealtimeMs = 0f;
            s_instance._sumWaitFpsNs = 0;
            s_instance._sumPhysicsNs = 0;
            s_instance._sumGhostSendNs = 0;
        }
    }
}
