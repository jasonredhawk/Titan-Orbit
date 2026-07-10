using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    // [TITAN-ORBIT] Pipeline order: ShipInputApplySystem → ShipMovementSystem → PhysicsSystemGroup → BulletSimulationSystem → …
    /// <summary>
    /// Authoritative ship motor on the dedicated server (and host server world). Schedules
    /// <see cref="ShipMovementJob"/> before <see cref="PhysicsSystemGroup"/> so the motor sets
    /// velocity and rotation, then Unity Physics integrates hull position and resolves collisions.
    /// Paired with <see cref="ShipClientPredictedMovementSystem"/> on the client — both call the
    /// same Burst job and <see cref="ShipMovementBurstLogic.Step"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // [STANDARD] Skip OnUpdate until at least one ship with motor config exists.
            state.RequireForUpdate<ShipMotorConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // --- Shared context for all ships this tick ---
            ShipMovementLogic.GetMapSize(ref state, out float mapW, out float mapH);
            // [ECS/DOTS] TempJob planet snapshot — disposed after the parallel job completes.
            var planets = PlanetMotorSnapshotCollection.Collect(ref state, Allocator.TempJob);

            var job = new ShipMovementJob
            {
                Dt = SystemAPI.Time.DeltaTime,
                Elapsed = SystemAPI.Time.ElapsedTime,
                MapW = mapW,
                MapH = mapH,
                Planets = planets.AsArray(),
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency = planets.Dispose(state.Dependency);
        }
    }
}
