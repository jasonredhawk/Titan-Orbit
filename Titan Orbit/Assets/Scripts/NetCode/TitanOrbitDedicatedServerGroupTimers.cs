using System.Diagnostics;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Stopwatch samples around predicted-fixed (physics + motors) and ghost sim.
    /// IL2CPP release strips <c>ProfilerRecorder</c> — these are the dedicated-server numbers.
    /// </summary>
    public static class TitanOrbitDedicatedServerGroupTimers
    {
        public static float LastPredictedFixedMs;
        public static float LastGhostSimMs;
        internal static long PredictedStart;
        internal static long GhostStart;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitDedicatedPredictedFixedBeginSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            TitanOrbitDedicatedServerGroupTimers.PredictedStart = Stopwatch.GetTimestamp();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitDedicatedPredictedFixedEndSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            long start = TitanOrbitDedicatedServerGroupTimers.PredictedStart;
            if (start == 0)
                return;
            TitanOrbitDedicatedServerGroupTimers.LastPredictedFixedMs =
                (float)(1000.0 * (Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitDedicatedGhostSimBeginSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            TitanOrbitDedicatedServerGroupTimers.GhostStart = Stopwatch.GetTimestamp();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitDedicatedGhostSimEndSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            long start = TitanOrbitDedicatedServerGroupTimers.GhostStart;
            if (start == 0)
                return;
            TitanOrbitDedicatedServerGroupTimers.LastGhostSimMs =
                (float)(1000.0 * (Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency);
        }
    }
}
