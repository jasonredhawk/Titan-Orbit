using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative ship motor. Schedules shared <see cref="ShipPhysicsDriveJob"/> before
    /// <see cref="PhysicsSystemGroup"/> so thrust/turn/orbit write <see cref="Unity.Physics.PhysicsVelocity"/>,
    /// then Unity Physics integrates position and resolves hull collisions.
    /// Collects planet snapshots + map size once per tick for toroidal orbit detection and shield repel.
    /// Paired with <see cref="ShipClientPredictedPhysicsDriveSystem"/> (same job, client owner).
    /// Pipeline: Input → MassSync → Drive → Physics → Planar → KinematicsSync.
    /// </summary>
    // OrderFirst + after MassSync: thrust before PhysicsSystemGroup without UpdateBefore(Physics…).
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(ShipPhysicsMassSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipPhysicsDriveSystem : ISystem
    {
        /// <summary>Require at least one ship motor config before scheduling the drive job.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipMotorConfig>();
        }

        /// <summary>
        /// Collects shared orbit context, then schedules the Burst drive job for all simulated ships.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Map size for toroidal orbit / shield math ---
            GetMapSize(ref state, out float mapW, out float mapH);

            // [ECS/DOTS] TempJob planet snapshot — disposed after the parallel job completes.
            var planets = PlanetMotorSnapshotCollection.Collect(ref state, Allocator.TempJob);

            // --- Moon orbit clock for shield repel ---
            // [TITAN-ORBIT] Same ServerTick seconds as PlanetGemMoonColliderSyncSystem (not World.ElapsedTime).
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            // [NETCODE] Fixed-step dt from PredictedFixedStepSimulationSystemGroup — not frame delta.
            var job = new ShipPhysicsDriveJob
            {
                Dt = SystemAPI.Time.DeltaTime,
                Elapsed = moonElapsed,
                MapW = mapW,
                MapH = mapH,
                Planets = planets.AsArray(),
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency = planets.Dispose(state.Dependency);
        }

        /// <summary>
        /// Reads toroidal map dimensions from <see cref="MapStateSingleton"/>, or 1000×1000 fallback.
        /// </summary>
        static void GetMapSize(ref SystemState state, out float mapW, out float mapH)
        {
            mapW = 1000f;
            mapH = 1000f;
            using var query = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
            if (query.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }
        }
    }
}
