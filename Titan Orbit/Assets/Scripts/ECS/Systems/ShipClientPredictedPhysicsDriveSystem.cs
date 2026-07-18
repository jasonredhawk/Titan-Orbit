using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client owner prediction — runs the same <see cref="ShipPhysicsDriveJob"/> as the server
    /// before physics so local input feels instant (Starblast pillar 1). Does not wait for RTT.
    /// [NETCODE] Only entities with <see cref="Simulate"/> participate; remotes interpolate.
    /// Input is already on the ghost from <see cref="ShipInputApplySystem"/> in GhostInputSystemGroup.
    /// </summary>
    // OrderFirst + after MassSync: runs before default-slot PhysicsSystemGroup without UpdateBefore
    // (ClientWorld often lacks PhysicsSystemGroup as a PredictedFixedStep sibling → sorter spam).
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(ShipPhysicsMassSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipClientPredictedPhysicsDriveSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipMotorConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var job = new ShipPhysicsDriveJob
            {
                Dt = SystemAPI.Time.DeltaTime,
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }
    }
}
