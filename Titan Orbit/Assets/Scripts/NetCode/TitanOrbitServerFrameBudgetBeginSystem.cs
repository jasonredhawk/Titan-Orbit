using System.Diagnostics;
using Unity.Entities;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Marks the start of each server Initialization+Simulation pass so diagnostics can split
    /// ECS time from the rest of the Unity frame (NullGfx present vs real sim).
    /// World: ServerSimulation. Group: InitializationSystemGroup (first).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitServerFrameBudgetBeginSystem : ISystem
    {
        public static long LastSimStartTimestamp;

        public void OnUpdate(ref SystemState state)
        {
            LastSimStartTimestamp = Stopwatch.GetTimestamp();
        }
    }
}
