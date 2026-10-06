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
    /// Topology uses <b>planet centers</b> (not gem moons) so sticky non-crossing links stay stable.
    /// One global planar map: <b>no line may cross any other</b> (friend or foe). First-created sticky
    /// edges win; blocked teams simply cannot add the crossing chord / triangle side.
    /// Publishes topology to ECS buffers on <see cref="PlanetConnectionGraphTag"/> and to the
    /// <b>server</b> side of <see cref="PlanetConnectionGraphCache"/> (never the client lists —
    /// host would race and wipe TerritoryTeam / flicker triangles).
    /// Applies stacked corner pop/growth bonuses onto <see cref="PlanetGrowthState"/> from
    /// <b>triangles only</b> (lone edges are visual-only). A capture repacks every corner,
    /// and a world that was already full moves its crew to the new CAP + LINK total.
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    [UpdateBefore(typeof(PlanetPopulationGrowthSystem))]
    public partial struct PlanetConnectionGraphSystem : ISystem
    {
        /// <summary>[TITAN-ORBIT] Soft fallback when only ghost-level fields drift without an ownership RPC.</summary>
        const float RecomputeIntervalSeconds = 1f;

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
                NextEdgeSequence = 1,
            });
        }

        /// <summary>
        /// Detects capture/level dirty or soft timer, then rebuilds the sticky non-crossing graph
        /// and triangle-only planet bonuses. Captures also fire <see cref="PlanetOwnershipChangedRpc"/>
        /// so clients do not wait on rate-limited planet ghosts.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            float now = (float)SystemAPI.Time.ElapsedTime;
            var graphState = SystemAPI.GetSingletonRW<PlanetConnectionGraphState>();

            // Missing map period → skip graph rebuild (never invent 1000).
            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) &&
                ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                preferredW = map.MapWidth;
                preferredH = map.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;

            // --- Collect planet-center inputs + fingerprint (server may query freely) ---
            // [TITAN-ORBIT] Centers (not moons) — topology must not churn as moons orbit.
            var planetInputs = new NativeList<PlanetConnectionGraphLogic.PlanetInput>(32, Allocator.Temp);
            foreach (var (planet, transform) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>())
            {
                planetInputs.Add(MakePlanetCenterInput(planet.ValueRO, transform.ValueRO, mapW, mapH));
            }

            uint fingerprint = ComputeFingerprint(planetInputs.AsArray());
            bool dirty = fingerprint != graphState.ValueRO.OwnershipFingerprint;
            bool timed = (now - graphState.ValueRO.LastRebuildElapsed) >= RecomputeIntervalSeconds;
            if (!dirty && !timed)
            {
                planetInputs.Dispose();
                return;
            }

            // --- Sticky edges, except on capture / level change ---
            // A timed refresh keeps first-created lines so borders do not flicker.
            // Ownership or level dirty repacks from scratch. Sticky blockers were
            // leaving some corners on the previous triangle set, so their link
            // crew total never moved when a nearby planet was captured.
            var edgeBuf = SystemAPI.GetSingletonBuffer<PlanetConnectionEdgeElement>();
            var previous = new NativeList<PlanetConnectionGraphLogic.Edge>(
                dirty ? 0 : math.max(1, edgeBuf.Length), Allocator.Temp);
            if (!dirty)
            {
                for (int i = 0; i < edgeBuf.Length; i++)
                {
                    var e = edgeBuf[i];
                    previous.Add(new PlanetConnectionGraphLogic.Edge
                    {
                        PlanetIdA = e.PlanetIdA,
                        PlanetIdB = e.PlanetIdB,
                        Team = e.Team,
                        CreationSequence = e.CreationSequence,
                    });
                }
            }

            // --- Rebuild topology (sticky + non-crossing + clique triangles) ---
            var edges = new NativeList<PlanetConnectionGraphLogic.Edge>(64, Allocator.Temp);
            var triangles = new NativeList<PlanetConnectionGraphLogic.Triangle>(32, Allocator.Temp);
            uint nextSequence = graphState.ValueRO.NextEdgeSequence;
            if (nextSequence == 0)
                nextSequence = 1;
            PlanetConnectionGraphLogic.RebuildFullGraph(
                planetInputs.AsArray(),
                mapW,
                mapH,
                previous,
                ref nextSequence,
                ref edges,
                ref triangles);

            var homeLevels = new NativeArray<int>(6, Allocator.Temp);
            PlanetConnectionGraphLogic.FillHomeLevels(planetInputs.AsArray(), ref homeLevels);

            // --- Write ECS buffers ---
            // Re-fetch edge buffer after other singleton access so the DynamicBuffer stays valid.
            edgeBuf = SystemAPI.GetSingletonBuffer<PlanetConnectionEdgeElement>();
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
                    CreationSequence = e.CreationSequence,
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

            // --- Publish server-side cache only (bake runtime verts from planetInputs) ---
            // [TITAN-ORBIT] Host runs ClientSimulation too — a shared list would race with
            // PlanetConnectionGraphClientSystem and wipe TerritoryTeam / flicker triangles.
            // Baking verts here means motor PIT matches drawn fills without waiting on Collect.
            PlanetConnectionGraphCache.PublishServer(
                edges, triangles, homeLevels, nextSequence, planetInputs.AsArray());

            // --- Every planet, from the triangles just built ---
            // One pass. A zero-then-fill used to leave corners that were not the
            // captured world on the previous bonus, so crew stayed at the old cap
            // while the label already showed CAP + the new LINK.
            var crewNotes = new NativeList<LinkCrewNote>(8, Allocator.Temp);
            foreach (var (planetState, growthState, transform) in SystemAPI
                         .Query<RefRW<PlanetState>, RefRW<PlanetGrowthState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>())
            {
                ref var planet = ref planetState.ValueRW;
                ref var growth = ref growthState.ValueRW;
                float newBonus = SumCornerBonus(planet.PlanetId, triangles);
                float oldBonus = growth.ConnectionBonusFraction;
                if (math.abs(newBonus - oldBonus) <= 0.0001f)
                    continue;

                float planetSize = math.max(0.5f, transform.ValueRO.Scale);
                int oldMax = PlanetPopulationMath.GetEffectiveMaxPopulation(
                    planetSize, planet.PlanetLevel, oldBonus);
                int newMax = PlanetPopulationMath.GetEffectiveMaxPopulation(
                    planetSize, planet.PlanetLevel, newBonus);
                int oldPop = planet.Population;

                growth.ConnectionBonusFraction = newBonus;

                // Full world: crew is the cap. A link change retargets that total
                // immediately (162 CAP + 122 LINK → 284), including corners that
                // were not the planet just captured. A drained world keeps its
                // people and grows toward the new cap.
                if (oldPop >= oldMax || oldPop > newMax)
                {
                    planet.Population = newMax;
                    growth.FractionalPopulation = newMax;
                }
                else if (growth.FractionalPopulation > newMax)
                    growth.FractionalPopulation = newMax;

                // Only neighbors whose crew number actually moved need an RPC.
                // A world that was not full keeps its people; its LINK line comes
                // from the same repacked graph the client rebuilds locally.
                if (planet.PlanetId != 0 && planet.Population != oldPop)
                {
                    crewNotes.Add(new LinkCrewNote
                    {
                        PlanetId = planet.PlanetId,
                        Population = planet.Population,
                        BonusFraction = newBonus,
                    });
                }
            }

            // Commit the fingerprint before any RPC entity create — structural
            // changes invalidate the singleton ref held at the top of this update.
            graphState.ValueRW.LastRebuildElapsed = now;
            graphState.ValueRW.OwnershipFingerprint = fingerprint;
            graphState.ValueRW.RebuildInProgress = false;
            graphState.ValueRW.NextEdgeSequence = nextSequence;

            var em = state.EntityManager;
            for (int i = 0; i < crewNotes.Length; i++)
            {
                var note = crewNotes[i];
                PlanetLinkCrewNetNotify.Broadcast(
                    em, note.PlanetId, note.Population, note.BonusFraction);
            }

            crewNotes.Dispose();

            previous.Dispose();
            edges.Dispose();
            triangles.Dispose();
            homeLevels.Dispose();
            planetInputs.Dispose();
        }

        /// <summary>
        /// Cheap dirty hash of ownership + level per planet id.
        /// Sorts by PlanetId first so query / HashSet order never masks a real ownership change.
        /// </summary>
        static uint ComputeFingerprint(NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets)
        {
            // --- Sort copy by PlanetId (stable dirty detection) ---
            int n = planets.Length;
            var order = new NativeArray<int>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
                order[i] = i;
            for (int i = 1; i < n; i++)
            {
                int key = order[i];
                int keyId = planets[key].PlanetId;
                int j = i - 1;
                while (j >= 0 && planets[order[j]].PlanetId > keyId)
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = key;
            }

            uint h = 2166136261u;
            for (int oi = 0; oi < n; oi++)
            {
                var p = planets[order[oi]];
                h ^= (uint)p.PlanetId;
                h *= 16777619u;
                h ^= (uint)p.Team;
                h *= 16777619u;
                h ^= (uint)p.PlanetLevel;
                h *= 16777619u;
            }

            order.Dispose();
            return h;
        }

        /// <summary>
        /// Builds a graph input whose <see cref="PlanetConnectionGraphLogic.PlanetInput.Position"/> is the
        /// planet core wrapped to canonical toroidal XZ (not the gem moon).
        /// </summary>
        static PlanetConnectionGraphLogic.PlanetInput MakePlanetCenterInput(
            in PlanetState planet,
            in LocalTransform transform,
            float mapW,
            float mapH)
        {
            float3 center = transform.Position;
            center.y = 0f;
            center = ToroidalMapEcs.Wrap(center, mapW, mapH);
            return new PlanetConnectionGraphLogic.PlanetInput
            {
                PlanetId = planet.PlanetId,
                Team = planet.Ownership,
                PlanetLevel = math.max(1, planet.PlanetLevel),
                Position = center,
                IsHomePlanet = planet.IsHomePlanet,
            };
        }

        /// <summary>
        /// Stacked triangle corner bonus for one planet — same sum the world label uses.
        /// Zero when this planet is not a corner of any filled triangle.
        /// </summary>
        static float SumCornerBonus(
            int planetId,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles)
        {
            float bonus = 0f;
            if (planetId == 0 || !triangles.IsCreated)
                return bonus;

            for (int i = 0; i < triangles.Length; i++)
            {
                var t = triangles[i];
                if (t.PlanetIdA != planetId && t.PlanetIdB != planetId && t.PlanetIdC != planetId)
                    continue;
                bonus += PlanetConnectionGraphLogic.GetCornerBonusStrength(t.AverageLevel);
            }

            return bonus;
        }
    }

    /// <summary>
    /// One planet whose link bonus changed this rebuild. Queued so the RPC create
    /// happens after the planet query (entity creates invalidate query refs).
    /// </summary>
    struct LinkCrewNote
    {
        public int PlanetId;
        public int Population;
        public float BonusFraction;
    }

        /// <summary>
        /// Client: rebuilds the non-crossing topology from Instantiated planet snapshots
        /// and publishes to <see cref="PlanetConnectionGraphCache"/> for predicted territory speed +
        /// Shapes drawing (triangles and lone edges). Capture and level changes repack from
        /// scratch — same as the server — so every corner's LINK matches the crew cap.
        /// Never uses planet archetype gathers under TransformQuarantine. Ownership flips arrive
        /// via <see cref="PlanetOwnershipChangedRpc"/> so lines/minimap do not wait on rate-limited
        /// planet ghosts. World: ClientSimulation.
        /// </summary>
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        [UpdateInGroup(typeof(SimulationSystemGroup))]
        public partial struct PlanetConnectionGraphClientSystem : ISystem
        {
            float _lastRebuildElapsed;
            uint _lastFingerprint;

            /// <summary>Resets timers when the client system is created.</summary>
            public void OnCreate(ref SystemState state)
            {
                _lastRebuildElapsed = -999f;
                _lastFingerprint = 0;
            }

            /// <summary>
            /// Rebuilds graph from registry Collect when ownership dirty or forced by capture RPC.
            /// Seeds sticky edges from the client cache. Skips during join Settling /
            /// GhostSpawnBacklog so Instantiates windows stay quiet.
            /// </summary>
            public void OnUpdate(ref SystemState state)
            {
                // --- Join safety: skip while planets/ships are still Instantiating ---
                // [TITAN-ORBIT] Registry Collect is quarantine-safe; still avoid empty thrash during settle.
                // [TITAN-ORBIT] Use ShouldSkipShipEntityQueries — includes post–TeamChoice hold that
                // GhostSpawnBacklog alone used to miss before it was folded into ComputeGhostSpawnBacklog.
                if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                    return;

                float now = (float)SystemAPI.Time.ElapsedTime;
                // Missing map period → skip client graph rebuild (never invent 1000).
            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) &&
                ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                preferredW = map.MapWidth;
                preferredH = map.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;

            // --- Quarantine-safe planet snapshots (centers, not moons) ---
            NativeList<PlanetMotorSnapshot> snaps =
                ClientJoinSettleCache.TransformQuarantine
                    ? PlanetMotorSnapshotCollection.CollectFromClientRegistry(ref state, Allocator.Temp)
                    : PlanetMotorSnapshotCollection.Collect(ref state, Allocator.Temp);

            var inputs = new NativeList<PlanetConnectionGraphLogic.PlanetInput>(snaps.Length, Allocator.Temp);
            for (int i = 0; i < snaps.Length; i++)
            {
                var s = snaps[i];
                float3 center = s.Transform.Position;
                center.y = 0f;
                center = ToroidalMapEcs.Wrap(center, mapW, mapH);

                // [TITAN-ORBIT] Read override before Resolve (Resolve clears it when ghost catches up).
                bool hasOverride = PlanetConnectionGraphCache.TryGetClientOwnershipOverride(
                    s.Planet.PlanetId, out _, out _, out int ovLevel);
                TeamId team = PlanetConnectionGraphCache.ResolveClientOwnership(
                    s.Planet.PlanetId, s.Planet.Ownership);
                int level = math.max(1, s.Planet.PlanetLevel);
                if (hasOverride && team != s.Planet.Ownership)
                    level = ovLevel;

                inputs.Add(new PlanetConnectionGraphLogic.PlanetInput
                {
                    PlanetId = s.Planet.PlanetId,
                    Team = team,
                    PlanetLevel = level,
                    Position = center,
                    IsHomePlanet = s.Planet.IsHomePlanet,
                });
            }

            uint fingerprint = ComputeClientFingerprint(inputs.AsArray());
            bool forced = PlanetConnectionGraphCache.ConsumeClientRebuildRequest();
            bool dirty = fingerprint != _lastFingerprint;
            // --- Skip identical topology republish ---
            // [TITAN-ORBIT] Timed rebuild used to PublishClient every 1s even when fingerprint
            // matched. That bumped ClientPublishRevision → EcsWorldVisualizer re-ran PIT for
            // every asteroid (~2 ms hitch, debug 94b4b4). Soft timer is unnecessary when dirty
            // + ownership RPC already cover real changes.
            if (!dirty && !forced)
            {
                inputs.Dispose();
                snaps.Dispose();
                return;
            }

            // --- Repack from scratch on capture / level change ---
            // The server does the same when the ownership fingerprint is dirty.
            // Seeding this client's sticky edges kept a different blocker set, so
            // the LINK line could show a new total the server crew cap never used.
            var previous = new NativeList<PlanetConnectionGraphLogic.Edge>(0, Allocator.Temp);

            var edges = new NativeList<PlanetConnectionGraphLogic.Edge>(64, Allocator.Temp);
            var triangles = new NativeList<PlanetConnectionGraphLogic.Triangle>(32, Allocator.Temp);
            uint nextSequence = PlanetConnectionGraphCache.ClientNextEdgeSequence;
            if (nextSequence == 0)
                nextSequence = 1;
            PlanetConnectionGraphLogic.RebuildFullGraph(
                inputs.AsArray(),
                mapW,
                mapH,
                previous,
                ref nextSequence,
                ref edges,
                ref triangles);

            var homeLevels = new NativeArray<int>(6, Allocator.Temp);
            PlanetConnectionGraphLogic.FillHomeLevels(inputs.AsArray(), ref homeLevels);
            // Client presentation / predicted motor — bake verts from the same inputs used for topology.
            // Never overwrite the server-side lists (host dual-world race).
            PlanetConnectionGraphCache.PublishClient(
                edges, triangles, homeLevels, nextSequence, inputs.AsArray());

            _lastFingerprint = fingerprint;
            _lastRebuildElapsed = now;

            previous.Dispose();
            edges.Dispose();
            triangles.Dispose();
            homeLevels.Dispose();
            inputs.Dispose();
            snaps.Dispose();
        }

        /// <summary>Order-stable FNV of (PlanetId, Team, Level) — same as server.</summary>
        static uint ComputeClientFingerprint(NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets)
        {
            int n = planets.Length;
            var order = new NativeArray<int>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
                order[i] = i;
            for (int i = 1; i < n; i++)
            {
                int key = order[i];
                int keyId = planets[key].PlanetId;
                int j = i - 1;
                while (j >= 0 && planets[order[j]].PlanetId > keyId)
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = key;
            }

            uint h = 2166136261u;
            for (int oi = 0; oi < n; oi++)
            {
                var p = planets[order[oi]];
                h ^= (uint)p.PlanetId;
                h *= 16777619u;
                h ^= (uint)p.Team;
                h *= 16777619u;
                h ^= (uint)p.PlanetLevel;
                h *= 16777619u;
            }

            order.Dispose();
            return h;
        }
    }

    /// <summary>
    /// Server → clients: a planet's link bonus changed, so its crew total may have
    /// jumped to the new cap. Planet ghosts are rate-limited, and only the captured
    /// world gets an ownership RPC — neighboring corners were staying on the old crew.
    /// </summary>
    public static class PlanetLinkCrewNetNotify
    {
        static readonly System.Collections.Generic.List<Entity> s_RegistryScratch =
            new System.Collections.Generic.List<Entity>(64);

        /// <summary>
        /// Mirrors onto the host client world and queues <see cref="PlanetLinkCrewRpc"/>
        /// for every connection. Call after the planet query, not inside it.
        /// </summary>
        public static void Broadcast(EntityManager serverEm, int planetId, int population, float bonusFraction)
        {
            if (planetId == 0)
                return;

            int pop = population < 0 ? 0 : population;
            float bonus = math.max(0f, bonusFraction);

            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
            {
                ApplyToClientWorld(
                    ClientServerBootstrap.ClientWorld.EntityManager,
                    planetId,
                    pop,
                    bonus);
            }

            Entity rpcEntity = serverEm.CreateEntity();
            serverEm.AddComponentData(rpcEntity, new PlanetLinkCrewRpc
            {
                PlanetId = planetId,
                Population = pop,
                BonusFraction = bonus,
            });
            serverEm.AddComponentData(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Latches crew + link bonus for labels. Does not write <see cref="PlanetState.Population"/>
        /// — that write would look like the ghost had already caught up, and the next
        /// rate-limited snapshot would paint the old total back.
        /// </summary>
        public static void ApplyToClientWorld(EntityManager em, int planetId, int population, float bonusFraction)
        {
            if (planetId == 0)
                return;

            int pop = population < 0 ? 0 : population;
            float bonus = math.max(0f, bonusFraction);
            int ghostPop = pop;
            PlanetClientEntityRegistry.CopyLive(s_RegistryScratch);
            for (int i = 0; i < s_RegistryScratch.Count; i++)
            {
                Entity entity = s_RegistryScratch[i];
                if (entity == Entity.Null ||
                    !em.Exists(entity) ||
                    !em.HasComponent<PlanetState>(entity))
                    continue;

                var state = em.GetComponentData<PlanetState>(entity);
                if (state.PlanetId != planetId)
                    continue;

                ghostPop = state.Population;
                break;
            }

            PlanetConnectionGraphCache.SetLinkCrewOverride(planetId, pop, bonus, ghostPop);
        }
    }

    /// <summary>
    /// Client: applies <see cref="PlanetLinkCrewRpc"/> so neighboring worlds update
    /// crew and LINK without waiting on the planet ghost. World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PlanetConnectionGraphClientSystem))]
    public partial struct PlanetLinkCrewRpcClientSystem : ISystem
    {
        /// <summary>Requires the incoming RPC queue.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ReceiveRpcCommandRequest>();
        }

        /// <summary>Applies each link-crew RPC, then destroys it so it cannot replay.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
            var em = state.EntityManager;

            foreach (var (rpc, rpcEntity) in SystemAPI
                         .Query<RefRO<PlanetLinkCrewRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var cmd = rpc.ValueRO;
                if (cmd.PlanetId != 0)
                {
                    PlanetLinkCrewNetNotify.ApplyToClientWorld(
                        em, cmd.PlanetId, cmd.Population, cmd.BonusFraction);
                }

                ecb.DestroyEntity(rpcEntity);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }
    }
}
