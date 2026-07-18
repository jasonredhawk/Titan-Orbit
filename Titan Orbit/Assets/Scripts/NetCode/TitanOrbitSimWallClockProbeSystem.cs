using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Debug probe (session 6b87b4 / H29): compares ECS ElapsedTime to Unity wall clock on the
    /// server world. basics17 confirmed ratio≈2.0; basics18 verifies MaxSteps=2 restores ~1.0.
    /// World: ServerSimulation. Group: SimulationSystemGroup (last).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitSimWallClockProbeServerSystem : ISystem
    {
        double _nextRealtimeLog;
        double _lastRealtime;
        double _lastElapsed;
        uint _lastTick;
        bool _hasBaseline;

        /// <summary>Once per wall-clock second logs simDt/wallDt on ServerWorld.</summary>
        public void OnUpdate(ref SystemState state)
        {
            double realtime = Time.realtimeSinceStartupAsDouble;
            double elapsed = SystemAPI.Time.ElapsedTime;
            uint tick = 0;
            float dt = SystemAPI.Time.DeltaTime;
            int simBatch = 0;
            int maxSteps = 0;
            int simHz = 0;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var nt) && nt.ServerTick.IsValid)
            {
                tick = nt.ServerTick.TickIndexForValidTick;
                simBatch = nt.SimulationStepBatchSize;
            }

            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var csr))
            {
                maxSteps = csr.MaxSimulationStepsPerFrame;
                simHz = csr.SimulationTickRate;
            }

            EmitIfDue(
                ref _nextRealtimeLog, ref _lastRealtime, ref _lastElapsed, ref _lastTick, ref _hasBaseline,
                realtime, elapsed, tick, dt, simBatch, maxSteps, simHz, "ServerWorld");
        }

        // #region agent log
        /// <summary>Shared emit helper for server + client wall-clock probes.</summary>
        internal static void EmitIfDue(
            ref double nextRealtimeLog,
            ref double lastRealtime,
            ref double lastElapsed,
            ref uint lastTick,
            ref bool hasBaseline,
            double realtime,
            double elapsed,
            uint tick,
            float dt,
            int simBatch,
            int maxSteps,
            int simHz,
            string worldName)
        {
            if (!hasBaseline)
            {
                hasBaseline = true;
                lastRealtime = realtime;
                lastElapsed = elapsed;
                lastTick = tick;
                nextRealtimeLog = realtime + 1.0;
                return;
            }

            if (realtime < nextRealtimeLog)
                return;
            nextRealtimeLog = realtime + 1.0;

            double wallDt = realtime - lastRealtime;
            double simDt = elapsed - lastElapsed;
            int tickDelta = tick >= lastTick ? (int)(tick - lastTick) : 0;
            float ratio = wallDt > 1e-6 ? (float)(simDt / wallDt) : 0f;
            float tickHz = wallDt > 1e-6 ? (float)(tickDelta / wallDt) : 0f;

            lastRealtime = realtime;
            lastElapsed = elapsed;
            lastTick = tick;

            // --- Server-only: prove dedicated sim rate vs wall (basics36 double-tick fix) ---
            // Client probe stays quiet (basics30 multi-process log contention).
            if (worldName != "ServerWorld")
            {
                _ = dt;
                return;
            }

            try
            {
                string data =
                    "{\"world\":\"ServerWorld\",\"wallDt\":" +
                    wallDt.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"simDt\":" + simDt.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"ratio\":" + ratio.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"tickDelta\":" + tickDelta +
                    ",\"tickHz\":" + tickHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"maxSteps\":" + maxSteps +
                    ",\"simHz\":" + simHz + "}";
                TitanOrbit.Diagnostics.ShipFlightSmoothDebugLog.Write(
                    "H40",
                    "TitanOrbitSimWallClockProbe.ServerWorld",
                    "sim vs wall clock",
                    data);
#if UNITY_SERVER
                TitanOrbit.Diagnostics.DedicatedServerFileLog.Append(
                    "sim-wall",
                    "ratio=" + ratio.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    " tickHz=" + tickHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    " maxSteps=" + maxSteps);
#endif
            }
            catch { /* debug I/O only */ }

            _ = dt;
            _ = simBatch;
        }
        // #endregion
    }

    /// <summary>
    /// Same wall-clock probe for ClientWorld. World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitSimWallClockProbeClientSystem : ISystem
    {
        double _nextRealtimeLog;
        double _lastRealtime;
        double _lastElapsed;
        uint _lastTick;
        bool _hasBaseline;

        /// <summary>Once per wall-clock second logs simDt/wallDt on ClientWorld.</summary>
        public void OnUpdate(ref SystemState state)
        {
            double realtime = Time.realtimeSinceStartupAsDouble;
            double elapsed = SystemAPI.Time.ElapsedTime;
            uint tick = 0;
            float dt = SystemAPI.Time.DeltaTime;
            int simBatch = 0;
            int maxSteps = 0;
            int simHz = 0;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var nt) && nt.ServerTick.IsValid)
            {
                tick = nt.ServerTick.TickIndexForValidTick;
                simBatch = nt.SimulationStepBatchSize;
            }

            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var csr))
            {
                maxSteps = csr.MaxSimulationStepsPerFrame;
                simHz = csr.SimulationTickRate;
            }

            TitanOrbitSimWallClockProbeServerSystem.EmitIfDue(
                ref _nextRealtimeLog, ref _lastRealtime, ref _lastElapsed, ref _lastTick, ref _hasBaseline,
                realtime, elapsed, tick, dt, simBatch, maxSteps, simHz, "ClientWorld");
        }
    }
}
