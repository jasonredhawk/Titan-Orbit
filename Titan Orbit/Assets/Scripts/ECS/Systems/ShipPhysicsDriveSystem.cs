using Unity.Entities;
using Unity.NetCode;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative ship motor. Schedules shared <see cref="ShipPhysicsDriveJob"/> before
    /// <see cref="PhysicsSystemGroup"/> so thrust/turn write <see cref="Unity.Physics.PhysicsVelocity"/>,
    /// then Unity Physics integrates position and resolves hull collisions.
    /// Paired with <see cref="ShipClientPredictedPhysicsDriveSystem"/> (same job, client owner).
    /// Pipeline: Input → MassSync → Drive → Physics → Planar → KinematicsSync.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipPhysicsDriveSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipMotorConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // [NETCODE] Fixed-step dt from PredictedFixedStepSimulationSystemGroup — not frame delta.
            var job = new ShipPhysicsDriveJob
            {
                Dt = SystemAPI.Time.DeltaTime,
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }
    }
}
