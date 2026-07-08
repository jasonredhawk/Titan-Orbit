using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Placeholder for future client timing tweaks. Baseline Interpolated mode needs none.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitClientTickRateSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) { }
    }
}
