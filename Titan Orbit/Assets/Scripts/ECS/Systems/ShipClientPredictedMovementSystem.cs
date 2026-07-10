using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    // Order: ShipInputApplySystem → ShipClientPredictedMovementSystem → PhysicsSystemGroup → …
    /// <summary>
    /// Client-side prediction for the local owner's ship. Schedules the same <see cref="ShipMovementJob"/>
    /// as the server for entities tagged <see cref="Simulate"/>, before Unity Physics integrates position.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipClientPredictedMovementSystem : ISystem
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
            // Simulate — NetCode tag for entities in the owner prediction loop.
            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency = planets.Dispose(state.Dependency);
        }
    }
}
