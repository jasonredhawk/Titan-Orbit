using TitanOrbit.Diagnostics;
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

        /// <summary>ElapsedTime when the current measurement window started.</summary>
        double _periodStartElapsed;

        /// <summary>ServerTick at window start — used to compute effective Hz.</summary>
        NetworkTick _periodStartTick;

        /// <summary>Number of simulation OnUpdate calls this window (sim steps executed).</summary>
        int _simStepsThisPeriod;

        /// <summary>Ticks where <see cref="NetworkTime.IsCatchUpTick"/> was true (server recovering from long frame).</summary>
        int _catchUpTicksThisPeriod;

        /// <summary>True after the first sample so we do not log before we have a baseline tick.</summary>
        bool _hasBaseline;

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

            double elapsed = SystemAPI.Time.ElapsedTime;
            if (!_hasBaseline)
            {
                _periodStartElapsed = elapsed;
                _periodStartTick = networkTime.ServerTick;
                _hasBaseline = true;
                return;
            }

            if (elapsed - _periodStartElapsed < LogIntervalSeconds)
                return;

            // --- Compute effective sim rate ---
            double wallSeconds = elapsed - _periodStartElapsed;
            int tickDelta = networkTime.ServerTick.TicksSince(_periodStartTick);
            float effectiveHz = wallSeconds > 0.001 ? (float)(tickDelta / wallSeconds) : 0f;
            float expectedHz = TitanOrbitServerTickRateSystem.SimulationHz;
            float catchUpPercent = _simStepsThisPeriod > 0
                ? 100f * _catchUpTicksThisPeriod / _simStepsThisPeriod
                : 0f;

            TitanOrbitServerFrameDiagnostics.ConsumeAndReset(
                out float avgFrameMs,
                out float maxFrameMs,
                out int slowFrames,
                out int unityFrames);

            string verdict = InterpretServerVerdict(effectiveHz, expectedHz, catchUpPercent, slowFrames, unityFrames);

            string line =
                "[NetDiagnostics/Server] effectiveSim=" + effectiveHz.ToString("F1") + "Hz (target " + expectedHz + ")" +
                " simSteps=" + _simStepsThisPeriod +
                " catchUpTicks=" + _catchUpTicksThisPeriod + " (" + catchUpPercent.ToString("F0") + "%)" +
                " unityFrames=" + unityFrames +
                " frameMs avg=" + avgFrameMs.ToString("F1") + " max=" + maxFrameMs.ToString("F1") +
                " slowFrames(>20ms)=" + slowFrames +
                " | Verdict: " + verdict;

            Debug.Log(line);
            DedicatedServerFileLog.Append("netdiag-server", line);

            // --- Reset window ---
            _periodStartElapsed = elapsed;
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
            float dtMs = Time.deltaTime * 1000f;
            _sumDeltaMs += dtMs;
            _maxDeltaMs = Mathf.Max(_maxDeltaMs, dtMs);
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
            if (s_instance == null)
            {
                avgFrameMs = 0f;
                maxFrameMs = 0f;
                slowFrames = 0;
                frames = 0;
                return;
            }

            frames = s_instance._frameCount;
            slowFrames = s_instance._slowFrameCount;
            maxFrameMs = s_instance._maxDeltaMs;
            avgFrameMs = frames > 0 ? s_instance._sumDeltaMs / frames : 0f;

            s_instance._sumDeltaMs = 0f;
            s_instance._maxDeltaMs = 0f;
            s_instance._frameCount = 0;
            s_instance._slowFrameCount = 0;
        }
    }
}
