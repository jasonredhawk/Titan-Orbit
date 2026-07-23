using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: assigns asteroid territory ownership from point-in-triangle tests against the
    /// current planet-connection graph (planet-center vertices). Port of NGO
    /// <c>AsteroidTerritoryHighlighter</c>, extended for multi-team overlap.
    /// <para>
    /// Writes ghosted <see cref="AsteroidState.TerritoryTeamsMask"/> (all owning teams) and
    /// <see cref="AsteroidState.TerritoryTeam"/> (strongest triangle — fallback tint). Clients
    /// prefer the local team colour when their bit is set; mining / destroy yellow gems require
    /// the interacting ship's team bit. World: ServerSimulation only — no client asteroid gather.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetConnectionGraphSystem))]
    public partial struct AsteroidTerritorySystem : ISystem
    {
        /// <summary>[TITAN-ORBIT] NGO highlighter interval — avoid rewriting every asteroid every frame.</summary>
        const float RefreshIntervalSeconds = 1f;

        float _lastRefreshElapsed;

        /// <summary>Requires the connection graph singleton before tinting asteroids.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PlanetConnectionGraphTag>();
            _lastRefreshElapsed = -999f;
        }

        /// <summary>
        /// Every second: build runtime moon-vertex triangles, then set each asteroid's
        /// territory mask + primary team.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            float now = (float)SystemAPI.Time.ElapsedTime;
            if (now - _lastRefreshElapsed < RefreshIntervalSeconds)
                return;
            _lastRefreshElapsed = now;

            // --- Empty graph → clear all territory ---
            // [TITAN-ORBIT] Must read ServerTriangles — CurrentTriangles is the client side.
            if (PlanetConnectionGraphCache.ServerTriangles.Count == 0)
            {
                foreach (var asteroid in SystemAPI.Query<RefRW<AsteroidState>>().WithAll<AsteroidTag>())
                {
                    if (asteroid.ValueRO.TerritoryTeam != TeamId.None ||
                        asteroid.ValueRO.TerritoryTeamsMask != 0)
                    {
                        asteroid.ValueRW.TerritoryTeam = TeamId.None;
                        asteroid.ValueRW.TerritoryTeamsMask = 0;
                    }
                }

                return;
            }

            // --- Map size (SystemAPI only valid in OnUpdate, not static helpers) ---
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }

            // --- Planet snapshots for moon vertex positions ---
            using var planets = PlanetMotorSnapshotCollection.Collect(ref state, Allocator.Temp);
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            using var runtime = PlanetConnectionGraphCache.BuildRuntimeTriangles(
                PlanetConnectionGraphSide.Server,
                planets.AsArray(),
                moonElapsed,
                Allocator.Temp);

            // --- Point-in-triangle ownership per asteroid ---
            foreach (var (asteroid, transform) in SystemAPI
                         .Query<RefRW<AsteroidState>, RefRO<LocalTransform>>()
                         .WithAll<AsteroidTag>())
            {
                if (asteroid.ValueRO.IsDestroyed)
                    continue;

                // Wrap asteroid into canonical space — same space as moon vertices.
                float3 asteroidPos = ToroidalMapEcs.Wrap(transform.ValueRO.Position, mapW, mapH);

                // [TITAN-ORBIT] Mask = every overlapping team; primary = strongest gem mult.
                PlanetConnectionGraphLogic.GetTerritoryOwnershipAtPosition(
                    asteroidPos,
                    runtime.AsArray(),
                    mapW,
                    mapH,
                    out byte mask,
                    out TeamId primary);

                if (asteroid.ValueRO.TerritoryTeam != primary ||
                    asteroid.ValueRO.TerritoryTeamsMask != mask)
                {
                    asteroid.ValueRW.TerritoryTeam = primary;
                    asteroid.ValueRW.TerritoryTeamsMask = mask;
                }
            }
        }
    }
}
