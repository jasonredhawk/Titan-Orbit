using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client owner prediction — runs the same <see cref="ShipPhysicsDriveJob"/> as the server
    /// before physics so local input feels instant (Starblast pillar 1). Does not wait for RTT.
    /// [NETCODE] Only entities with <see cref="Simulate"/> participate; remotes interpolate.
    /// Input is already on the ghost from <see cref="ShipInputApplySystem"/> in GhostInputSystemGroup.
    /// Planet snapshots match the server so orbit ring membership and shield repel stay deterministic.
    /// </summary>
    // OrderFirst + after MassSync: runs before default-slot PhysicsSystemGroup without UpdateBefore
    // (ClientWorld often lacks PhysicsSystemGroup as a PredictedFixedStep sibling → sorter spam).
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(ShipPhysicsMassSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipClientPredictedPhysicsDriveSystem : ISystem
    {
        /// <summary>Require at least one ship motor config before scheduling the drive job.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipMotorConfig>();
        }

        /// <summary>
        /// Collects shared orbit context, then schedules the Burst drive job for predicted local ships.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Map size for toroidal orbit / shield math (same source as server) ---
            GetMapSize(ref state, out float mapW, out float mapH);

            // [ECS/DOTS] TempJob planet snapshot — disposed after the parallel job completes.
            var planets = PlanetMotorSnapshotCollection.Collect(ref state, Allocator.TempJob);

            // --- Moon orbit clock for predicted shield repel ---
            // [TITAN-ORBIT] Must match server / collider sync — World.ElapsedTime diverges on late-join.
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

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
