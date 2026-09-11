using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Load-flight VFX no longer receives per-tick Active poses (egress).
    /// Clients magnet toward the live ship ghost; Consumed / Destroyed / Returned
    /// still go through <see cref="PeopleTransportNetNotify.SendPose"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    public partial struct PeopleTransportPoseSyncSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) { }
    }
}
