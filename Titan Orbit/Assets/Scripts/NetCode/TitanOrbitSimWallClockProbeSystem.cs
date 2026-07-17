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

            // Advance baseline only — disk I/O muted (basics30: MPPM + Editor file contention).
            lastRealtime = realtime;
            lastElapsed = elapsed;
            lastTick = tick;
            _ = dt;
            _ = simBatch;
            _ = maxSteps;
            _ = simHz;
            _ = worldName;
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
