using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Which simulation world owns a published planet-connection graph.
    /// Host runs both worlds in one process — they must not share one list or they overwrite
    /// each other (triangle flicker, TerritoryTeam cleared, bonuses drop).
    /// </summary>
    public enum PlanetConnectionGraphSide : byte
    {
        /// <summary>ServerSimulation — authority for tint / mining / pop bonuses / server motor.</summary>
        Server = 0,

        /// <summary>ClientSimulation — presentation, predicted motor, minimap / world draw.</summary>
        Client = 1,
    }

    /// <summary>
    /// Dual-sided managed snapshot of the planet-connection graph.
    /// Server and client each publish into their own lists so listen-server cannot race.
    /// Presentation (Shapes / minimap / thruster scale) prefers the client side, then
    /// falls back to the server lists on Local Host while the client publish is still empty.
    /// <para>
    /// Runtime planet-center triangle verts are baked at <see cref="PublishServer"/> /
    /// <see cref="PublishClient"/> from the same <see cref="PlanetConnectionGraphLogic.PlanetInput"/>
    /// list used to rebuild topology — so drawn fills and motor point-in-triangle share verts
    /// immediately (no wait on per-tick motor Collect). Persistent native arrays are reused across
    /// drive ticks; callers must <b>not</b> Dispose them.
    /// </para>
    /// </summary>
    public static class PlanetConnectionGraphCache
    {
        /// <summary>
        /// After leaving a friendly triangle, keep the presentation thruster boost this many seconds
        /// so edge / resim flicker does not blink engine scale every frame.
        /// Matches motor <see cref="ShipTerritoryBoostLatch"/> / GraphLogic sticky.
        /// </summary>
        const float LocalOwnerTerritoryStickySeconds =
            PlanetConnectionGraphLogic.TerritoryBoostStickySeconds;

        /// <summary>One world's edges / triangles / home levels + planet-center runtime cache.</summary>
        sealed class Side
        {
            public readonly List<PlanetConnectionGraphLogic.Edge> Edges;
            public readonly List<PlanetConnectionGraphLogic.Triangle> Triangles;
            public readonly int[] HomeLevelByTeam;
            public readonly List<PlanetConnectionGraphLogic.RuntimeTriangle> RuntimeCache;
            public NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle> RuntimeNative;
            public double RuntimeCacheStamp = -999.0;
            /// <summary>PlanetId → canonical center from the last publish (drawer fallback).</summary>
            public readonly Dictionary<int, float3> PlanetCenters;

            /// <summary>
            /// Next sticky <see cref="PlanetConnectionGraphLogic.Edge.CreationSequence"/> for this side.
            /// </summary>
            public uint NextEdgeSequence = 1;

            public Side(int edgeCap, int triCap)
            {
                Edges = new List<PlanetConnectionGraphLogic.Edge>(edgeCap);
                Triangles = new List<PlanetConnectionGraphLogic.Triangle>(triCap);
                HomeLevelByTeam = new int[6];
                RuntimeCache = new List<PlanetConnectionGraphLogic.RuntimeTriangle>(triCap);
                RuntimeNative = default;
                PlanetCenters = new Dictionary<int, float3>(32);
                NextEdgeSequence = 1;
            }

            /// <summary>Clears topology + runtime planet-center verts (disposes Persistent native).</summary>
            public void Clear()
            {
                Edges.Clear();
                Triangles.Clear();
                RuntimeCache.Clear();
                RuntimeCacheStamp = -999.0;
                PlanetCenters.Clear();
                NextEdgeSequence = 1;
                DisposeRuntimeNative();
                for (int i = 0; i < HomeLevelByTeam.Length; i++)
                    HomeLevelByTeam[i] = 0;
            }

            /// <summary>Frees Persistent runtime array if allocated.</summary>
            public void DisposeRuntimeNative()
            {
                if (RuntimeNative.IsCreated)
                    RuntimeNative.Dispose();
                RuntimeNative = default;
            }

            /// <summary>Copies managed RuntimeCache into Persistent native (job-readable, no per-tick alloc).</summary>
            public void SyncRuntimeNativeFromCache()
            {
                DisposeRuntimeNative();
                int n = RuntimeCache.Count;
                RuntimeNative = new NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle>(
                    n, Allocator.Persistent);
                for (int i = 0; i < n; i++)
                    RuntimeNative[i] = RuntimeCache[i];
            }
        }

        static readonly Side Server = new Side(64, 32);
        static readonly Side Client = new Side(64, 32);

        /// <summary>
        /// Optimistic Ownership overrides keyed by PlanetId — used while planet ghosts lag
        /// MaxSendRate / Importance under snapshot chunk caps. Cleared when the ghost catches up.
        /// </summary>
        static readonly Dictionary<int, OwnershipOverride> s_ClientOwnershipOverrides =
            new Dictionary<int, OwnershipOverride>(16);

        /// <summary>True when the client graph must rebuild this tick (capture RPC / host mirror).</summary>
        static bool s_ClientRebuildRequested;

        /// <summary>One optimistic ownership patch until the Instantiated ghost matches.</summary>
        struct OwnershipOverride
        {
            public TeamId Team;
            public int Population;
            public int PlanetLevel;
        }

        /// <summary>
        /// Sticky friendly-territory mult for the local predicted ship (client presentation).
        /// Written only on first-time predicting ticks — never from NetCode rollback/resim.
        /// </summary>
        public static float LocalOwnerTerritoryMult { get; private set; } = 1f;

        static float s_StickyTerritoryMult = 1f;
        static double s_StickyTerritoryUntilMoonElapsed = -1.0;

        /// <summary>Revision bumped on each Client publish — minimap rebuilds when this changes.</summary>
        public static int ClientPublishRevision { get; private set; }

        /// <summary>Revision bumped on each Server publish — territory rebuilds when this changes.</summary>
        public static int ServerPublishRevision { get; private set; }

        /// <summary>
        /// Drawers watch this. Local Host can show server topology before the client side
        /// publishes — <see cref="ClientPublishRevision"/> alone left those frames stuck empty.
        /// </summary>
        public static int PresentationRevision =>
            ClientPublishRevision + ServerPublishRevision;

        /// <summary>True when the client side has any published edges or triangles.</summary>
        static bool ClientHasPresentationTopology =>
            Client.Triangles.Count > 0 || Client.Edges.Count > 0;

        /// <summary>
        /// Presentation triangles. Prefer the client publish; Local Host falls back to the
        /// server list when the client side is still empty (drawers used to show nothing).
        /// </summary>
        public static IReadOnlyList<PlanetConnectionGraphLogic.Triangle> CurrentTriangles =>
            ClientHasPresentationTopology ? Client.Triangles : Server.Triangles;

        /// <summary>Presentation edges. Same client-then-server fallback as <see cref="CurrentTriangles"/>.</summary>
        public static IReadOnlyList<PlanetConnectionGraphLogic.Edge> CurrentEdges =>
            ClientHasPresentationTopology ? Client.Edges : Server.Edges;

        /// <summary>
        /// Next sticky edge creation sequence on the client side (seeded into rebuild, updated on publish).
        /// </summary>
        public static uint ClientNextEdgeSequence => Client.NextEdgeSequence;

        /// <summary>Server-authoritative triangles for territory tint / mining.</summary>
        public static IReadOnlyList<PlanetConnectionGraphLogic.Triangle> ServerTriangles => Server.Triangles;

        /// <summary>
        /// Stacked triangle corner bonus fraction for one planet — same math the server writes into
        /// <c>PlanetGrowthState.ConnectionBonusFraction</c>. Presentation reads the client graph;
        /// listen-server falls back to the server list when the client side is still empty.
        /// </summary>
        /// <param name="planetId">Stable <see cref="PlanetState.PlanetId"/> to sum corners for.</param>
        /// <returns>
        /// Sum of <see cref="PlanetConnectionGraphLogic.GetCornerBonusStrength"/> over every triangle
        /// that includes this planet. 0 when the planet is not a triangle corner (or graph is empty).
        /// </returns>
        public static float GetStackedConnectionBonusFraction(int planetId)
        {
            // --- Prefer client triangles (HUD / world labels) ---
            // [TITAN-ORBIT] ConnectionBonusFraction is server-only on PlanetGrowthState — clients
            // recompute from the published graph so planet labels can show base + bonus max.
            if (planetId == 0)
                return 0f;

            float bonus = SumCornerBonusFraction(Client.Triangles, planetId);

            // --- Host fallback: client publish can lag one frame behind server ---
            if (bonus <= 0f && Client.Triangles.Count == 0 && Server.Triangles.Count > 0)
                bonus = SumCornerBonusFraction(Server.Triangles, planetId);

            return bonus;
        }

        /// <summary>
        /// Sums <see cref="PlanetConnectionGraphLogic.GetCornerBonusStrength"/> for every triangle
        /// that lists <paramref name="planetId"/> as a corner.
        /// </summary>
        static float SumCornerBonusFraction(
            IReadOnlyList<PlanetConnectionGraphLogic.Triangle> triangles,
            int planetId)
        {
            if (triangles == null || triangles.Count == 0)
                return 0f;

            float bonus = 0f;
            for (int i = 0; i < triangles.Count; i++)
            {
                var t = triangles[i];
                if (t.PlanetIdA != planetId && t.PlanetIdB != planetId && t.PlanetIdC != planetId)
                    continue;
                bonus += PlanetConnectionGraphLogic.GetCornerBonusStrength(t.AverageLevel);
            }

            return bonus;
        }

        /// <summary>
        /// Records an optimistic Ownership flip for the client graph fingerprint / rebuild.
        /// Called from <see cref="PlanetOwnershipNetNotify"/> (RPC + host mirror).
        /// </summary>
        public static void SetClientOwnershipOverride(
            int planetId,
            TeamId team,
            int population,
            int planetLevel)
        {
            if (planetId == 0 || team == TeamId.None)
                return;

            s_ClientOwnershipOverrides[planetId] = new OwnershipOverride
            {
                Team = team,
                Population = population < 0 ? 0 : population,
                PlanetLevel = planetLevel < 1 ? 1 : planetLevel,
            };
            s_ClientRebuildRequested = true;
        }

        /// <summary>
        /// Resolves client Ownership for graph rebuild: override wins until the ghost matches,
        /// then the override is cleared.
        /// </summary>
        public static TeamId ResolveClientOwnership(int planetId, TeamId ghostTeam)
        {
            if (!s_ClientOwnershipOverrides.TryGetValue(planetId, out var ov))
                return ghostTeam;

            // Ghost caught up — drop the patch so later captures stay authoritative.
            if (ghostTeam == ov.Team)
            {
                s_ClientOwnershipOverrides.Remove(planetId);
                return ghostTeam;
            }

            return ov.Team;
        }

        /// <summary>
        /// Optional population / level from an active override (when ghost still lags).
        /// Returns false when no override is active for <paramref name="planetId"/>.
        /// </summary>
        public static bool TryGetClientOwnershipOverride(
            int planetId,
            out TeamId team,
            out int population,
            out int planetLevel)
        {
            if (s_ClientOwnershipOverrides.TryGetValue(planetId, out var ov))
            {
                team = ov.Team;
                population = ov.Population;
                planetLevel = ov.PlanetLevel;
                return true;
            }

            team = TeamId.None;
            population = 0;
            planetLevel = 1;
            return false;
        }

        /// <summary>
        /// Consumes the one-shot client rebuild request from an ownership RPC / host mirror.
        /// </summary>
        public static bool ConsumeClientRebuildRequest()
        {
            bool requested = s_ClientRebuildRequested;
            s_ClientRebuildRequested = false;
            return requested;
        }

        /// <summary>
        /// Publishes into the server-side lists (ServerSimulation only) and bakes runtime triangle
        /// verts from <paramref name="planets"/> so motor PIT matches topology immediately.
        /// </summary>
        public static void PublishServer(
            in NativeList<PlanetConnectionGraphLogic.Edge> edges,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles,
            in NativeArray<int> homeLevelByTeamIndex,
            uint nextEdgeSequence,
            in NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets) =>
            PublishInto(
                Server, edges, triangles, homeLevelByTeamIndex, nextEdgeSequence, planets, isClient: false);

        /// <summary>
        /// Publishes into the client-side lists (ClientSimulation / presentation) and bakes runtime
        /// triangle verts from <paramref name="planets"/>.
        /// </summary>
        public static void PublishClient(
            in NativeList<PlanetConnectionGraphLogic.Edge> edges,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles,
            in NativeArray<int> homeLevelByTeamIndex,
            uint nextEdgeSequence,
            in NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets) =>
            PublishInto(
                Client, edges, triangles, homeLevelByTeamIndex, nextEdgeSequence, planets, isClient: true);

        /// <summary>Legacy alias — treats as client publish without baking verts (fallback rebuild).</summary>
        public static void Publish(
            in NativeList<PlanetConnectionGraphLogic.Edge> edges,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles,
            in NativeArray<int> homeLevelByTeamIndex) =>
            PublishInto(
                Client,
                edges,
                triangles,
                homeLevelByTeamIndex,
                Client.NextEdgeSequence,
                default,
                isClient: true);

        /// <summary>
        /// Copies topology + home levels, then bakes planet-center <see cref="RuntimeTriangle"/> verts
        /// from the same PlanetInput list the graph rebuild used (planets do not drift).
        /// </summary>
        static void PublishInto(
            Side side,
            in NativeList<PlanetConnectionGraphLogic.Edge> edges,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles,
            in NativeArray<int> homeLevelByTeamIndex,
            uint nextEdgeSequence,
            in NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets,
            bool isClient)
        {
            side.Edges.Clear();
            side.Triangles.Clear();
            if (edges.IsCreated)
            {
                for (int i = 0; i < edges.Length; i++)
                    side.Edges.Add(edges[i]);
            }

            if (triangles.IsCreated)
            {
                for (int i = 0; i < triangles.Length; i++)
                    side.Triangles.Add(triangles[i]);
            }

            for (int i = 0; i < side.HomeLevelByTeam.Length; i++)
                side.HomeLevelByTeam[i] = 0;

            if (homeLevelByTeamIndex.IsCreated)
            {
                int n = math.min(side.HomeLevelByTeam.Length, homeLevelByTeamIndex.Length);
                for (int i = 0; i < n; i++)
                    side.HomeLevelByTeam[i] = homeLevelByTeamIndex[i];
            }

            // Sticky sequence persists across publishes so “first created wins” stays stable.
            side.NextEdgeSequence = nextEdgeSequence == 0 ? 1u : nextEdgeSequence;

            // --- Bake runtime verts from graph PlanetInput (primary path) ---
            // [TITAN-ORBIT] Do not wait for per-tick PlanetMotorSnapshot Collect — under client
            // TransformQuarantine a partial registry used to leave RuntimeCache short while hybrid
            // drawers still showed fills → FriendlyTerritoryMovementMultiplier always returned 1.
            BakeRuntimeCacheFromPlanetInputs(side, planets);
            StorePlanetCenters(side, planets);

            if (isClient)
                ClientPublishRevision++;
            else
                ServerPublishRevision++;
        }

        /// <summary>Caches PlanetId → center from the same inputs used to rebuild topology.</summary>
        static void StorePlanetCenters(
            Side side,
            in NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets)
        {
            side.PlanetCenters.Clear();
            if (!planets.IsCreated)
                return;

            for (int i = 0; i < planets.Length; i++)
            {
                var p = planets[i];
                if (p.PlanetId == 0)
                    continue;
                side.PlanetCenters[p.PlanetId] = p.Position;
            }
        }

        /// <summary>
        /// Canonical planet center from the last graph publish. Used when hybrid planet
        /// proxies are missing so world / minimap lines can still draw.
        /// </summary>
        public static bool TryGetPublishedPlanetCenter(int planetId, out float3 position)
        {
            position = default;
            if (planetId == 0)
                return false;
            if (Client.PlanetCenters.TryGetValue(planetId, out position))
                return true;
            return Server.PlanetCenters.TryGetValue(planetId, out position);
        }

        /// <summary>Forces the client graph system to rebuild on its next eligible tick.</summary>
        public static void RequestClientRebuild() => s_ClientRebuildRequested = true;

        /// <summary>
        /// Builds complete runtime triangles from PlanetInput centers already used for topology.
        /// Syncs Persistent native for the motor job. Incomplete only if a triangle planet id is
        /// missing from <paramref name="planets"/> (should not happen on a normal rebuild).
        /// </summary>
        static void BakeRuntimeCacheFromPlanetInputs(
            Side side,
            in NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets)
        {
            side.RuntimeCache.Clear();
            side.RuntimeCacheStamp = -999.0;

            if (side.Triangles.Count == 0)
            {
                side.DisposeRuntimeNative();
                side.RuntimeNative = new NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle>(
                    0, Allocator.Persistent);
                return;
            }

            for (int i = 0; i < side.Triangles.Count; i++)
            {
                var t = side.Triangles[i];
                if (!TryFindPlanetInput(planets, t.PlanetIdA, out var pa) ||
                    !TryFindPlanetInput(planets, t.PlanetIdB, out var pb) ||
                    !TryFindPlanetInput(planets, t.PlanetIdC, out var pc))
                    continue;

                // Positions are already canonical-wrapped by graph rebuild helpers.
                float3 va = pa.Position;
                float3 vb = pb.Position;
                float3 vc = pc.Position;
                va.y = 0f;
                vb.y = 0f;
                vc.y = 0f;

                side.RuntimeCache.Add(new PlanetConnectionGraphLogic.RuntimeTriangle
                {
                    VertexA = va,
                    VertexB = vb,
                    VertexC = vc,
                    Team = t.Team,
                    GemBonusMultiplier = t.GemBonusMultiplier,
                    AverageLevel = t.AverageLevel,
                    PlanetIdA = t.PlanetIdA,
                    PlanetIdB = t.PlanetIdB,
                    PlanetIdC = t.PlanetIdC,
                });
            }

            side.SyncRuntimeNativeFromCache();
        }

        /// <summary>Looks up a graph PlanetInput by PlanetId.</summary>
        static bool TryFindPlanetInput(
            in NativeArray<PlanetConnectionGraphLogic.PlanetInput> planets,
            int planetId,
            out PlanetConnectionGraphLogic.PlanetInput input)
        {
            input = default;
            if (!planets.IsCreated || planetId == 0)
                return false;

            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].PlanetId != planetId)
                    continue;
                input = planets[i];
                return true;
            }

            return false;
        }

        /// <summary>Clears both sides (leave session / domain reload).</summary>
        public static void Clear()
        {
            Server.Clear();
            Client.Clear();
            LocalOwnerTerritoryMult = 1f;
            s_StickyTerritoryMult = 1f;
            s_StickyTerritoryUntilMoonElapsed = -1.0;
            s_ClientOwnershipOverrides.Clear();
            s_ClientRebuildRequested = false;
            ClientPublishRevision++;
            ServerPublishRevision++;
        }

        /// <summary>
        /// Updates the local-owner presentation thruster mult with enter/exit sticky hold
        /// (<see cref="PlanetConnectionGraphLogic.TerritoryBoostStickySeconds"/> — same window as
        /// motor <see cref="ShipTerritoryBoostLatch"/>). Call only from
        /// <c>NetworkTime.IsFirstTimeFullyPredictingTick</c> — resim must not write this.
        /// </summary>
        /// <param name="rawMult">Instant point-in-triangle result (1 or 1+0.05×homeLevel).</param>
        /// <param name="moonElapsedSeconds">Shared moon orbit clock (sticky expiry).</param>
        public static void UpdateLocalOwnerTerritoryMult(float rawMult, double moonElapsedSeconds)
        {
            rawMult = math.max(1f, rawMult);

            // --- Inside friendly triangle: latch boost ---
            if (rawMult > 1.001f)
            {
                s_StickyTerritoryMult = rawMult;
                s_StickyTerritoryUntilMoonElapsed = moonElapsedSeconds + LocalOwnerTerritoryStickySeconds;
                LocalOwnerTerritoryMult = rawMult;
                return;
            }

            // --- Outside: keep latched boost briefly (edge flicker) ---
            if (moonElapsedSeconds < s_StickyTerritoryUntilMoonElapsed)
            {
                LocalOwnerTerritoryMult = s_StickyTerritoryMult;
                return;
            }

            s_StickyTerritoryMult = 1f;
            LocalOwnerTerritoryMult = 1f;
        }

        /// <summary>Legacy direct set — prefer <see cref="UpdateLocalOwnerTerritoryMult"/>.</summary>
        public static void SetLocalOwnerTerritoryMult(float mult) =>
            LocalOwnerTerritoryMult = math.max(1f, mult);

        /// <summary>Copies home levels for the given side into a native array sized ≥ 6.</summary>
        public static void CopyHomeLevels(PlanetConnectionGraphSide side, ref NativeArray<int> dst)
        {
            int[] src = side == PlanetConnectionGraphSide.Server ? Server.HomeLevelByTeam : Client.HomeLevelByTeam;
            for (int i = 0; i < dst.Length && i < src.Length; i++)
                dst[i] = src[i];
        }

        /// <summary>Legacy — copies <b>client</b> home levels (presentation / predicted motor).</summary>
        public static void CopyHomeLevels(ref NativeArray<int> dst) =>
            CopyHomeLevels(PlanetConnectionGraphSide.Client, ref dst);

        /// <summary>Home planet level for a team from the <b>server</b> side (mining / destroy bonuses).</summary>
        public static int GetHomePlanetLevel(TeamId team) =>
            GetHomePlanetLevel(PlanetConnectionGraphSide.Server, team);

        /// <summary>Home planet level for a team from the given side.</summary>
        public static int GetHomePlanetLevel(PlanetConnectionGraphSide side, TeamId team)
        {
            if (team == TeamId.None)
                return 1;
            int[] src = side == PlanetConnectionGraphSide.Server ? Server.HomeLevelByTeam : Client.HomeLevelByTeam;
            int idx = (int)team;
            if (idx < 0 || idx >= src.Length)
                return 1;
            int level = src[idx];
            return level > 0 ? level : 1;
        }

        /// <summary>
        /// Returns a job-readable Persistent runtime-triangle array (do <b>not</b> Dispose).
        /// Primary path: verts already baked at publish from graph PlanetInput.
        /// Fallback: rebuild from motor planet snapshots only when the bake was incomplete
        /// (RuntimeCache.Count != Triangles.Count) — e.g. legacy Publish without planets.
        /// </summary>
        public static NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle> GetRuntimeTrianglesNative(
            PlanetConnectionGraphSide sideKind,
            in NativeArray<PlanetMotorSnapshot> planets,
            double elapsedSeconds)
        {
            Side side = sideKind == PlanetConnectionGraphSide.Server ? Server : Client;

            if (side.Triangles.Count == 0)
            {
                side.RuntimeCache.Clear();
                side.RuntimeCacheStamp = elapsedSeconds;
                // Keep a single empty Persistent array — do not Dispose/realloc every tick.
                if (!side.RuntimeNative.IsCreated || side.RuntimeNative.Length != 0)
                {
                    side.DisposeRuntimeNative();
                    side.RuntimeNative = new NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle>(
                        0, Allocator.Persistent);
                }

                return side.RuntimeNative;
            }

            // --- Prefer publish bake; fallback Collect only when bake missed a vertex ---
            bool cacheComplete =
                side.RuntimeCache.Count == side.Triangles.Count &&
                side.RuntimeNative.IsCreated &&
                side.RuntimeNative.Length == side.RuntimeCache.Count;

            if (!cacheComplete)
            {
                int prevResolved = side.RuntimeCache.Count;
                RebuildRuntimeCache(side, planets);
                if (side.RuntimeCache.Count != prevResolved ||
                    !side.RuntimeNative.IsCreated ||
                    side.RuntimeNative.Length != side.RuntimeCache.Count)
                {
                    side.SyncRuntimeNativeFromCache();
                }

                side.RuntimeCacheStamp = elapsedSeconds;
            }

            return side.RuntimeNative;
        }

        /// <summary>
        /// Legacy TempJob list API — prefer <see cref="GetRuntimeTrianglesNative"/> (no alloc).
        /// Caller must Dispose the returned list.
        /// </summary>
        public static NativeList<PlanetConnectionGraphLogic.RuntimeTriangle> BuildRuntimeTriangles(
            PlanetConnectionGraphSide sideKind,
            in NativeArray<PlanetMotorSnapshot> planets,
            double elapsedSeconds,
            Allocator allocator)
        {
            var native = GetRuntimeTrianglesNative(sideKind, planets, elapsedSeconds);
            var list = new NativeList<PlanetConnectionGraphLogic.RuntimeTriangle>(native.Length, allocator);
            for (int i = 0; i < native.Length; i++)
                list.Add(native[i]);
            return list;
        }

        /// <summary>Legacy — builds from <b>client</b> side (predicted motor / presentation).</summary>
        public static NativeList<PlanetConnectionGraphLogic.RuntimeTriangle> BuildRuntimeTriangles(
            in NativeArray<PlanetMotorSnapshot> planets,
            double elapsedSeconds,
            Allocator allocator) =>
            BuildRuntimeTriangles(PlanetConnectionGraphSide.Client, planets, elapsedSeconds, allocator);

        /// <summary>Recomputes planet-center vertices into the side's managed runtime cache.</summary>
        static void RebuildRuntimeCache(
            Side side,
            in NativeArray<PlanetMotorSnapshot> planets)
        {
            side.RuntimeCache.Clear();
            for (int i = 0; i < side.Triangles.Count; i++)
            {
                var t = side.Triangles[i];
                if (!TryFindPlanet(planets, t.PlanetIdA, out var pa) ||
                    !TryFindPlanet(planets, t.PlanetIdB, out var pb) ||
                    !TryFindPlanet(planets, t.PlanetIdC, out var pc))
                    continue;

                float3 va = WrapPlanetCanonical(pa.Transform.Position);
                float3 vb = WrapPlanetCanonical(pb.Transform.Position);
                float3 vc = WrapPlanetCanonical(pc.Transform.Position);

                side.RuntimeCache.Add(new PlanetConnectionGraphLogic.RuntimeTriangle
                {
                    VertexA = va,
                    VertexB = vb,
                    VertexC = vc,
                    Team = t.Team,
                    GemBonusMultiplier = t.GemBonusMultiplier,
                    AverageLevel = t.AverageLevel,
                    PlanetIdA = t.PlanetIdA,
                    PlanetIdB = t.PlanetIdB,
                    PlanetIdC = t.PlanetIdC,
                });
            }
        }

        static bool TryFindPlanet(
            in NativeArray<PlanetMotorSnapshot> planets,
            int planetId,
            out PlanetMotorSnapshot snap)
        {
            snap = default;
            if (!planets.IsCreated || planetId == 0)
                return false;

            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].Planet.PlanetId != planetId)
                    continue;
                snap = planets[i];
                return true;
            }

            return false;
        }

        /// <summary>Planet core XZ wrapped into canonical toroidal space (Y forced to 0).</summary>
        static float3 WrapPlanetCanonical(float3 planetWorld)
        {
            planetWorld.y = 0f;
            return ToroidalMapEcs.Wrap(planetWorld);
        }
    }
}
