using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    // [TITAN-ORBIT] Pipeline order: ShipInputApplySystem → ShipClientPredictedMovementSystem → PhysicsSystemGroup → …
    /// <summary>
    /// Client-side prediction for the local owner's ship. NetCode marks predicted ghosts with
    /// the <see cref="Simulate"/> tag so this system runs the same <see cref="ShipMovementJob"/>
    /// as the server before Unity Physics integrates position. Input feels instant; server remains
    /// authoritative and can roll back mispredictions via NetCode's built-in smoothing.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipClientPredictedMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // [STANDARD] Skip OnUpdate until at least one ship with motor config exists.
            state.RequireForUpdate<ShipMotorConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // --- Same job as server — deterministic motor is the key to prediction ---
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
            // [NETCODE] ShipMovementJob queries WithAll<Simulate> — only predicted entities run here.
            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency = planets.Dispose(state.Dependency);
        }
    }
}
