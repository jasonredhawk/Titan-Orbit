using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    // Order: ShipInputApplySystem → ShipMovementSystem → PhysicsSystemGroup → BulletSimulationSystem → …
    /// <summary>
    /// Authoritative ship motor (server only). Schedules <see cref="ShipMovementJob"/> before
    /// Unity Physics integrates hull position. Paired with <see cref="ShipClientPredictedMovementSystem"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipMotorConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            ShipMovementLogic.GetMapSize(ref state, out float mapW, out float mapH);
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
