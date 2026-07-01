using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Planet capture resolves when hostile people unload drives population to zero (legacy Planet.AddPopulationFromServer).
    /// Proximity auto-capture is intentionally disabled until people transport is ported to ECS.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemDepositSystem))]
    public partial struct CaptureSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
        }
    }
}
