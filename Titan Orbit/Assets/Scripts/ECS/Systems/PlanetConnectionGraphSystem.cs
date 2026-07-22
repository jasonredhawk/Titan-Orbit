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
    /// Server: rebuilds same-team planet connection edges/triangles when ownership or levels change.
    /// Publishes topology to ECS buffers on <see cref="PlanetConnectionGraphTag"/> and to the
    /// <b>server</b> side of <see cref="PlanetConnectionGraphCache"/> (never the client lists —
    /// host would race and wipe TerritoryTeam / flicker triangles).
    /// Applies stacked corner pop/growth bonuses onto <see cref="PlanetGrowthState"/>.
    /// World: ServerSimulation. Poll interval matches NGO (~3s) plus immediate dirty rebuilds.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    public partial struct PlanetConnectionGraphSystem : ISystem
    {
        /// <summary>[TITAN-ORBIT] NGO recomputeInterval — also refreshes AverageLevel after upgrades.</summary>
        const float RecomputeIntervalSeconds = 3f;

        /// <summary>Creates the graph singleton + buffers once on server boot.</summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Ensure singleton ---
            if (SystemAPI.HasSingleton<PlanetConnectionGraphTag>())
                return;

            var e = state.EntityManager.CreateEntity(
                typeof(PlanetConnectionGraphTag),
                typeof(PlanetConnectionGraphState));
            state.EntityManager.AddBuffer<PlanetConnectionEdgeElement>(e);
            state.EntityManager.AddBuffer<PlanetConnectionTriangleElement>(e);
            state.EntityManager.SetComponentData(e, new PlanetConnectionGraphState
            {
                LastRebuildElapsed = -999f,
                OwnershipFingerprint = 0,
                RebuildInProgress = false,
            });
        }

        /// <summary>
        /// Detects capture/level dirty or 3s timer, then rebuilds the full graph and planet bonuses.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            float now = (float)SystemAPI.Time.ElapsedTime;
            var graphState = SystemAPI.GetSingletonRW<PlanetConnectionGraphState>();

            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }

            // --- Moon clock for gem-moon vertex positions (nearest-neighbor = moons, not cores) ---
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            // --- Collect planet inputs + fingerprint (server may query freely) ---
            var planetInputs = new NativeList<PlanetConnectionGraphLogic.PlanetInput>(32, Allocator.Temp);
            foreach (var (planet, transform) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>())
            {
                planetInputs.Add(MakeMoonVertexInput(planet.ValueRO, transform.ValueRO, moonElapsed, mapW, mapH));
            }

            uint fingerprint = ComputeFingerprint(planetInputs.AsArray());
            bool dirty = fingerprint != graphState.ValueRO.OwnershipFingerprint;
            bool timed = (now - graphState.ValueRO.LastRebuildElapsed) >= RecomputeIntervalSeconds;
            if (!dirty && !timed)
            {
                planetInputs.Dispose();
                return;
            }

            // --- Rebuild topology ---
            var edges = new NativeList<PlanetConnectionGraphLogic.Edge>(64, Allocator.Temp);
            var triangles = new NativeList<PlanetConnectionGraphLogic.Triangle>(32, Allocator.Temp);
            PlanetConnectionGraphLogic.RebuildFullGraph(
                planetInputs.AsArray(), mapW, mapH, ref edges, ref triangles);

            var homeLevels = new NativeArray<int>(6, Allocator.Temp);
            PlanetConnectionGraphLogic.FillHomeLevels(planetInputs.AsArray(), ref homeLevels);

            // --- Write ECS buffers ---
            var edgeBuf = SystemAPI.GetSingletonBuffer<PlanetConnectionEdgeElement>();
            var triBuf = SystemAPI.GetSingletonBuffer<PlanetConnectionTriangleElement>();
            edgeBuf.Clear();
            triBuf.Clear();
            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                edgeBuf.Add(new PlanetConnectionEdgeElement
                {
                    PlanetIdA = e.PlanetIdA,
                    PlanetIdB = e.PlanetIdB,
                    Team = e.Team,
                });
            }

            for (int i = 0; i < triangles.Length; i++)
            {
                var t = triangles[i];
                triBuf.Add(new PlanetConnectionTriangleElement
                {
                    PlanetIdA = t.PlanetIdA,
                    PlanetIdB = t.PlanetIdB,
                    PlanetIdC = t.PlanetIdC,
                    Team = t.Team,
                    AverageLevel = t.AverageLevel,
                    GemBonusMultiplier = t.GemBonusMultiplier,
                });
            }

            // --- Publish server-side cache only ---
            // [TITAN-ORBIT] Host runs ClientSimulation too — a shared list would race with
            // PlanetConnectionGraphClientSystem and wipe TerritoryTeam / flicker triangles.
            PlanetConnectionGraphCache.PublishServer(edges, triangles, homeLevels);

            // --- Reset then stack corner pop/growth bonuses ---
            foreach (var growth in SystemAPI.Query<RefRW<PlanetGrowthState>>().WithAll<PlanetTag>())
                growth.ValueRW.ConnectionBonusFraction = 0f;

            if (triangles.Length > 0)
            {
                foreach (var (planet, growth) in SystemAPI
                             .Query<RefRO<PlanetState>, RefRW<PlanetGrowthState>>()
                             .WithAll<PlanetTag>())
                {
                    float bonus = 0f;
                    int id = planet.ValueRO.PlanetId;
                    for (int i = 0; i < triangles.Length; i++)
                    {
                        var t = triangles[i];
                        if (t.PlanetIdA != id && t.PlanetIdB != id && t.PlanetIdC != id)
                            continue;
                        bonus += PlanetConnectionGraphLogic.GetCornerBonusStrength(t.AverageLevel);
                    }

                    growth.ValueRW.ConnectionBonusFraction = bonus;
                }
            }

            graphState.ValueRW.LastRebuildElapsed = now;
            graphState.ValueRW.OwnershipFingerprint = fingerprint;
            graphState.ValueRW.RebuildInProgress = false;

            edges.Dispose();
            triangles.Dispose();
            homeLevels.Dispose();
            planetInputs.Dispose();
        }

        /// <summary>Cheap dirty hash of ownership + level per planet id.</summary>
        static uint ComputeFingerprint(NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets)
        {
            uint h = 2166136261u;
            for (int i = 0; i < planets.Length; i++)
            {
                var p = planets[i];
                h ^= (uint)p.PlanetId;
                h *= 16777619u;
                h ^= (uint)p.Team;
                h *= 16777619u;
                h ^= (uint)p.PlanetLevel;
                h *= 16777619u;
            }

            return h;
        }

        /// <summary>
        /// Builds a graph input whose <see cref="PlanetConnectionGraphLogic.PlanetInput.Position"/> is the
        /// gem-moon vertex wrapped to canonical toroidal XZ (not the planet core).
        /// </summary>
        static PlanetConnectionGraphLogic.PlanetInput MakeMoonVertexInput(
            in PlanetState planet,
            in LocalTransform transform,
            double moonElapsed,
            float mapW,
            float mapH)
        {
            float3 moon = PlanetOrbitMath.GetMoonWorldPosition(
                transform.Position,
                math.max(0.25f, transform.Scale),
                math.max(1, planet.PlanetLevel),
                planet.PlanetId,
                moonElapsed,
                planet.IsHomePlanet);
            moon.y = 0f;
            moon = ToroidalMapEcs.Wrap(moon, mapW, mapH);
            return new PlanetConnectionGraphLogic.PlanetInput
            {
                PlanetId = planet.PlanetId,
                Team = planet.Ownership,
                PlanetLevel = math.max(1, planet.PlanetLevel),
                Position = moon,
                IsHomePlanet = planet.IsHomePlanet,
            };
        }
    }

    /// <summary>
    /// Client: rebuilds the same connection topology from Instantiated planet snapshots and publishes
    /// to <see cref="PlanetConnectionGraphCache"/> for predicted territory speed + Shapes drawing.
    /// Never uses planet archetype gathers under TransformQuarantine.
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PlanetConnectionGraphClientSystem : ISystem
    {
        const float RecomputeIntervalSeconds = 3f;
        float _lastRebuildElapsed;
        uint _lastFingerprint;

        /// <summary>Resets timers when the client system is created.</summary>
        public void OnCreate(ref SystemState state)
        {
            _lastRebuildElapsed = -999f;
            _lastFingerprint = 0;
        }

        /// <summary>
        /// Rebuilds graph from registry Collect when ownership dirty or every 3s.
        /// Skips during join Settling / GhostSpawnBacklog so Instantiates windows stay quiet.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join safety: skip while planets/ships are still Instantiating ---
            // [TITAN-ORBIT] Registry Collect is quarantine-safe; still avoid empty thrash during settle.
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
                return;

            float now = (float)SystemAPI.Time.ElapsedTime;
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }

            // --- Moon clock (match server / world drawer) ---
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            // --- Quarantine-safe planet snapshots ---
            NativeList<PlanetMotorSnapshot> snaps =
                ClientJoinSettleCache.TransformQuarantine
                    ? PlanetMotorSnapshotCollection.CollectFromClientRegistry(ref state, Allocator.Temp)
                    : PlanetMotorSnapshotCollection.Collect(ref state, Allocator.Temp);

            var inputs = new NativeList<PlanetConnectionGraphLogic.PlanetInput>(snaps.Length, Allocator.Temp);
            for (int i = 0; i < snaps.Length; i++)
            {
                var s = snaps[i];
                // Same moon-vertex helper as server (MakeMoonVertexInput is on the server struct —
                // duplicate the wrap here so client stays in TitanOrbit.ECS without cross-calls).
                float3 moon = PlanetOrbitMath.GetMoonWorldPosition(
                    s.Transform.Position,
                    math.max(0.25f, s.Transform.Scale),
                    math.max(1, s.Planet.PlanetLevel),
                    s.Planet.PlanetId,
                    moonElapsed,
                    s.Planet.IsHomePlanet);
                moon.y = 0f;
                moon = ToroidalMapEcs.Wrap(moon, mapW, mapH);
                inputs.Add(new PlanetConnectionGraphLogic.PlanetInput
                {
                    PlanetId = s.Planet.PlanetId,
                    Team = s.Planet.Ownership,
                    PlanetLevel = math.max(1, s.Planet.PlanetLevel),
                    Position = moon,
                    IsHomePlanet = s.Planet.IsHomePlanet,
                });
            }

            uint fingerprint = 2166136261u;
            for (int i = 0; i < inputs.Length; i++)
            {
                var p = inputs[i];
                fingerprint ^= (uint)p.PlanetId;
                fingerprint *= 16777619u;
                fingerprint ^= (uint)p.Team;
                fingerprint *= 16777619u;
                fingerprint ^= (uint)p.PlanetLevel;
                fingerprint *= 16777619u;
            }

            bool dirty = fingerprint != _lastFingerprint;
            bool timed = (now - _lastRebuildElapsed) >= RecomputeIntervalSeconds;
            if (!dirty && !timed)
            {
                inputs.Dispose();
                snaps.Dispose();
                return;
            }

            var edges = new NativeList<PlanetConnectionGraphLogic.Edge>(64, Allocator.Temp);
            var triangles = new NativeList<PlanetConnectionGraphLogic.Triangle>(32, Allocator.Temp);
            PlanetConnectionGraphLogic.RebuildFullGraph(
                inputs.AsArray(), mapW, mapH, ref edges, ref triangles);

            var homeLevels = new NativeArray<int>(6, Allocator.Temp);
            PlanetConnectionGraphLogic.FillHomeLevels(inputs.AsArray(), ref homeLevels);
            // Client presentation / predicted motor — never overwrite the server-side lists.
            PlanetConnectionGraphCache.PublishClient(edges, triangles, homeLevels);

            _lastFingerprint = fingerprint;
            _lastRebuildElapsed = now;

            edges.Dispose();
            triangles.Dispose();
            homeLevels.Dispose();
            inputs.Dispose();
            snaps.Dispose();
        }
    }
}
