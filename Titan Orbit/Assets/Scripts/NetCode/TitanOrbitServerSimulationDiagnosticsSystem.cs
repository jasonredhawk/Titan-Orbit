using TitanOrbit.Diagnostics;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
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

        /// <summary>ElapsedTime when the current measurement window started (sim time — can lie).</summary>
        double _periodStartElapsed;

        /// <summary>Wall clock when the current measurement window started.</summary>
        float _periodStartRealtime;

        /// <summary>ServerTick at window start — used to compute effective Hz.</summary>
        NetworkTick _periodStartTick;

        /// <summary>Number of simulation OnUpdate calls this window (sim steps executed).</summary>
        int _simStepsThisPeriod;

        /// <summary>Ticks where <see cref="NetworkTime.IsCatchUpTick"/> was true (server recovering from long frame).</summary>
        int _catchUpTicksThisPeriod;

        /// <summary>True after the first sample so we do not log before we have a baseline tick.</summary>
        bool _hasBaseline;

        /// <summary>Wall ms spent inside SimulationSystemGroup this window (from tick-cost start system).</summary>
        float _sumTickMs;

        /// <summary>Slowest sim tick this window.</summary>
        float _maxTickMs;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // --- Per-step counters ---
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            _simStepsThisPeriod++;

            if (networkTime.IsCatchUpTick)
                _catchUpTicksThisPeriod++;

            float tickMs = TitanOrbitServerTickCost.ConsumeTickMs();
            _sumTickMs += tickMs;
            if (tickMs > _maxTickMs)
                _maxTickMs = tickMs;

            double elapsed = SystemAPI.Time.ElapsedTime;
            float realtime = Time.realtimeSinceStartup;
            if (!_hasBaseline)
            {
                _periodStartElapsed = elapsed;
                _periodStartRealtime = realtime;
                _periodStartTick = networkTime.ServerTick;
                _hasBaseline = true;
                return;
            }

            if (realtime - _periodStartRealtime < LogIntervalSeconds)
                return;

            // --- Compute effective sim rate vs wall clock ---
            // SystemAPI.Time.ElapsedTime is sim time: Sleep + MaxSteps can report 60 Hz
            // while wall-clock play is slow-mo. Wall Hz is what players feel.
            double simSeconds = elapsed - _periodStartElapsed;
            double wallSeconds = realtime - _periodStartRealtime;
            int tickDelta = networkTime.ServerTick.TicksSince(_periodStartTick);
            float simHz = simSeconds > 0.001 ? (float)(tickDelta / simSeconds) : 0f;
            float wallHz = wallSeconds > 0.001 ? (float)(tickDelta / wallSeconds) : 0f;
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
                out float maxRealtimeMs);

            string verdict = InterpretServerVerdict(simHz, wallHz, expectedHz, catchUpPercent, slowFrames, unityFrames);

            float avgTickMs = _simStepsThisPeriod > 0 ? _sumTickMs / _simStepsThisPeriod : 0f;
            float ticksPerUnityFrame = unityFrames > 0 ? (float)_simStepsThisPeriod / unityFrames : 0f;
            int liveShips = 0;
            int liveBullets = 0;
            foreach (var _ in SystemAPI.Query<RefRO<ShipTag>>())
                liveShips++;
            if (SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity) &&
                state.EntityManager.HasBuffer<BulletElement>(bulletEntity))
                liveBullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity).Length;

            string line =
                "[NetDiagnostics/Server] wallSim=" + wallHz.ToString("F1") + "Hz simClock=" + simHz.ToString("F1") +
                "Hz (target " + expectedHz + ")" +
                " simSteps=" + _simStepsThisPeriod +
                " catchUpTicks=" + _catchUpTicksThisPeriod + " (" + catchUpPercent.ToString("F0") + "%)" +
                " unityFrames=" + unityFrames +
                " ticksPerFrame=" + ticksPerUnityFrame.ToString("F1") +
                " tickMs avg=" + avgTickMs.ToString("F1") + " max=" + _maxTickMs.ToString("F1") +
                " ships=" + liveShips +
                " bullets=" + liveBullets +
                " unityDt=" + Time.deltaTime.ToString("F3") +
                " maxDelta=" + Time.maximumDeltaTime.ToString("F2") +
                " targetFps=" + Application.targetFrameRate +
                " deltaMs avg=" + avgFrameMs.ToString("F1") + " max=" + maxFrameMs.ToString("F1") +
                " wallFrameMs avg=" + avgRealtimeMs.ToString("F1") + " max=" + maxRealtimeMs.ToString("F1") +
                " slowFrames(>20ms)=" + slowFrames +
                " | Verdict: " + verdict;

            Debug.Log(line);
            DedicatedServerFileLog.Append("netdiag-server", line);

            // [TITAN-ORBIT] Feed empty-process recycle (RSS/struggling) — host exits only when 0 players.
            DedicatedServerMemoryTelemetry.ReportSimHealthSample(catchUpPercent, avgFrameMs, verdict, wallHz);

            // --- Reset window ---
            _periodStartElapsed = elapsed;
            _periodStartRealtime = realtime;
            _periodStartTick = networkTime.ServerTick;
            _simStepsThisPeriod = 0;
            _catchUpTicksThisPeriod = 0;
            _sumTickMs = 0f;
            _maxTickMs = 0f;
        }

        /// <summary>Plain-language server health from tick rate and frame spikes.</summary>
        static string InterpretServerVerdict(
            float simHz,
            float wallHz,
            float expectedHz,
            float catchUpPercent,
            int slowFrames,
            int unityFrames)
        {
            float minHealthyHz = expectedHz * 0.92f; // ~55 Hz at 60 target

            if (wallHz < minHealthyHz && simHz >= minHealthyHz)
                return "SLOW-MO — sim clock at target but wall-clock ticks are behind (deltaTime clamp / present rate)";

            if (wallHz < minHealthyHz)
                return "SLOW — simulation below target (CPU overload or process throttled)";

            if (catchUpPercent > 15f)
                return "STRUGGLING — frequent catch-up ticks after frame hitches";

            if (unityFrames > 0 && slowFrames > unityFrames * 0.2f)
                return "FRAME SPIKES — Unity frames often exceed 20 ms (check VM CPU / GC)";

            return "healthy";
        }
    }

    /// <summary>
    /// Wall-clock length of the current Server SimulationSystemGroup tick.
    /// <see cref="TitanOrbitServerTickCostStartSystem"/> stamps the start;
    /// diagnostics consume the ms at OrderLast.
    /// </summary>
    static class TitanOrbitServerTickCost
    {
        static float s_TickStartRealtime;

        /// <summary>Call from OrderFirst — wall time before the rest of the sim tick.</summary>
        public static void MarkTickStart()
        {
            s_TickStartRealtime = Time.realtimeSinceStartup;
        }

        /// <summary>Milliseconds since <see cref="MarkTickStart"/> (0 if start was never stamped).</summary>
        public static float ConsumeTickMs()
        {
            if (s_TickStartRealtime <= 0f)
                return 0f;
            float ms = (Time.realtimeSinceStartup - s_TickStartRealtime) * 1000f;
            s_TickStartRealtime = 0f;
            return ms;
        }
    }

    /// <summary>
    /// Stamps wall time at the start of each server sim tick so diagnostics can report
    /// tickMs (SimulationSystemGroup work) vs wallFrameMs (whole Unity present).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitServerTickCostStartSystem : ISystem
    {
        /// <summary>Record wall time before other server systems run this tick.</summary>
        public void OnUpdate(ref SystemState state)
        {
            TitanOrbitServerTickCost.MarkTickStart();
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
        float _sumRealtimeMs;
        float _maxRealtimeMs;
        float _lastRealtime = -1f;
        int _frameCount;
        int _slowFrameCount;

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

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            float dtMs = Time.deltaTime * 1000f;
            _sumDeltaMs += dtMs;
            _maxDeltaMs = Mathf.Max(_maxDeltaMs, dtMs);
            if (_lastRealtime >= 0f)
            {
                float realMs = (now - _lastRealtime) * 1000f;
                _sumRealtimeMs += realMs;
                _maxRealtimeMs = Mathf.Max(_maxRealtimeMs, realMs);
            }

            _lastRealtime = now;
            _frameCount++;
            if (dtMs > 20f)
                _slowFrameCount++;
        }

        /// <summary>Reads and clears accumulated frame stats since the last server diagnostics log.</summary>
        public static void ConsumeAndReset(
            out float avgFrameMs,
            out float maxFrameMs,
            out int slowFrames,
            out int frames)
        {
            ConsumeAndReset(out avgFrameMs, out maxFrameMs, out slowFrames, out frames, out _, out _);
        }

        /// <summary>Same as <see cref="ConsumeAndReset(out float, out float, out int, out int)"/> plus wall-clock frame ms.</summary>
        public static void ConsumeAndReset(
            out float avgFrameMs,
            out float maxFrameMs,
            out int slowFrames,
            out int frames,
            out float avgRealtimeMs,
            out float maxRealtimeMs)
        {
            if (s_instance == null)
            {
                avgFrameMs = 0f;
                maxFrameMs = 0f;
                slowFrames = 0;
                frames = 0;
                avgRealtimeMs = 0f;
                maxRealtimeMs = 0f;
                return;
            }

            frames = s_instance._frameCount;
            slowFrames = s_instance._slowFrameCount;
            maxFrameMs = s_instance._maxDeltaMs;
            avgFrameMs = frames > 0 ? s_instance._sumDeltaMs / frames : 0f;
            int realSamples = frames > 0 ? frames - 1 : 0;
            avgRealtimeMs = realSamples > 0 ? s_instance._sumRealtimeMs / realSamples : 0f;
            maxRealtimeMs = s_instance._maxRealtimeMs;

            s_instance._sumDeltaMs = 0f;
            s_instance._maxDeltaMs = 0f;
            s_instance._sumRealtimeMs = 0f;
            s_instance._maxRealtimeMs = 0f;
            s_instance._frameCount = 0;
            s_instance._slowFrameCount = 0;
        }
    }
}
