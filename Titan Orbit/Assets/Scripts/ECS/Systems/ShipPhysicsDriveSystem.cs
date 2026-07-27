using TitanOrbit.Generation;
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
    /// Collects planet snapshots + territory triangles + map size once per tick for toroidal orbit,
    /// shield repel, and friendly territory speed.
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
        /// Collects shared orbit/territory context, then schedules the Burst drive job for all simulated ships.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Map size for toroidal orbit / shield / territory math ---
            // [TITAN-ORBIT] Prefer ToroidalMapEcs (clients get size from MapSessionMetaRpc —
            // MapStateSingleton often never ghosts). Wrong period (hardcoded 1000) made
            // ShortestOffset / triangle PIT fail on duplicate tiles → stepped orbit + no boost.
            // No per-tick CreateEntityQuery — that alloc showed up as drive hitch / lag.
            float mapW = math.max(100f, ToroidalMapEcs.MapWidth);
            float mapH = math.max(100f, ToroidalMapEcs.MapHeight);
            if (SystemAPI.TryGetSingleton(out MapStateSingleton mapState) &&
                mapState.MapWidth >= 100f &&
                mapState.MapHeight >= 100f)
            {
                mapW = mapState.MapWidth;
                mapH = mapState.MapHeight;
            }

            // [ECS/DOTS] TempJob planet snapshot — disposed after the parallel job completes.
            var planets = PlanetMotorSnapshotCollection.Collect(ref state, Allocator.TempJob);

            // --- Moon orbit clock for shield repel + territory moon vertices ---
            // [TITAN-ORBIT] Same ServerTick seconds as PlanetGemMoonColliderSyncSystem (not World.ElapsedTime).
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            // --- Friendly territory triangles (Persistent native — do not Dispose) ---
            // [TITAN-ORBIT] Server side of dual cache — client must not overwrite these lists.
            var territory = PlanetConnectionGraphCache.GetRuntimeTrianglesNative(
                PlanetConnectionGraphSide.Server,
                planets.AsArray(),
                moonElapsed);
            var homeLevels = new NativeArray<int>(6, Allocator.TempJob);
            PlanetConnectionGraphCache.CopyHomeLevels(PlanetConnectionGraphSide.Server, ref homeLevels);

            // [NETCODE] Fixed-step dt from PredictedFixedStepSimulationSystemGroup — not frame delta.
            var job = new ShipPhysicsDriveJob
            {
                Dt = SystemAPI.Time.DeltaTime,
                Elapsed = moonElapsed,
                MapW = mapW,
                MapH = mapH,
                Planets = planets.AsArray(),
                TerritoryTriangles = territory,
                HomeLevelByTeam = homeLevels,
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency = planets.Dispose(state.Dependency);
            // territory is Persistent cache — never Dispose here.
            state.Dependency = homeLevels.Dispose(state.Dependency);
        }
    }
}
