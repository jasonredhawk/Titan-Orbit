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
    /// Presentation (Shapes / minimap / thruster scale) always reads the <see cref="PlanetConnectionGraphSide.Client"/> side.
    /// <para>
    /// Runtime moon-vertex arrays are <see cref="Allocator.Persistent"/> and reused across drive ticks
    /// (no TempJob NativeList alloc every predicted step). Callers must <b>not</b> Dispose them.
    /// </para>
    /// </summary>
    public static class PlanetConnectionGraphCache
    {
        /// <summary>
        /// Reuse moon-vertex runtime triangles for this many moon-clock seconds unless topology changes.
        /// </summary>
        const double RuntimeMoonCacheSeconds = 0.2;

        /// <summary>
        /// After leaving a friendly triangle, keep the presentation thruster boost this many seconds
        /// so edge / resim flicker does not blink engine scale every frame.
        /// </summary>
        const float LocalOwnerTerritoryStickySeconds = 0.5f;

        /// <summary>One world's edges / triangles / home levels + throttled moon-vertex cache.</summary>
        sealed class Side
        {
            public readonly List<PlanetConnectionGraphLogic.Edge> Edges;
            public readonly List<PlanetConnectionGraphLogic.Triangle> Triangles;
            public readonly int[] HomeLevelByTeam;
            public readonly List<PlanetConnectionGraphLogic.RuntimeTriangle> RuntimeCache;
            public NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle> RuntimeNative;
            public double RuntimeCacheMoonElapsed = -999.0;

            public Side(int edgeCap, int triCap)
            {
                Edges = new List<PlanetConnectionGraphLogic.Edge>(edgeCap);
                Triangles = new List<PlanetConnectionGraphLogic.Triangle>(triCap);
                HomeLevelByTeam = new int[6];
                RuntimeCache = new List<PlanetConnectionGraphLogic.RuntimeTriangle>(triCap);
                RuntimeNative = default;
            }

            /// <summary>Clears topology + runtime moon vertices (disposes Persistent native).</summary>
            public void Clear()
            {
                Edges.Clear();
                Triangles.Clear();
                RuntimeCache.Clear();
                RuntimeCacheMoonElapsed = -999.0;
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

        /// <summary>Presentation triangles (client side). Empty until client graph system publishes.</summary>
        public static IReadOnlyList<PlanetConnectionGraphLogic.Triangle> CurrentTriangles => Client.Triangles;

        /// <summary>Presentation edges (client side).</summary>
        public static IReadOnlyList<PlanetConnectionGraphLogic.Edge> CurrentEdges => Client.Edges;

        /// <summary>Server-authoritative triangles for territory tint / mining.</summary>
        public static IReadOnlyList<PlanetConnectionGraphLogic.Triangle> ServerTriangles => Server.Triangles;

        /// <summary>Publishes into the server-side lists (ServerSimulation only).</summary>
        public static void PublishServer(
            in NativeList<PlanetConnectionGraphLogic.Edge> edges,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles,
            in NativeArray<int> homeLevelByTeamIndex) =>
            PublishInto(Server, edges, triangles, homeLevelByTeamIndex, isClient: false);

        /// <summary>Publishes into the client-side lists (ClientSimulation / presentation).</summary>
        public static void PublishClient(
            in NativeList<PlanetConnectionGraphLogic.Edge> edges,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles,
            in NativeArray<int> homeLevelByTeamIndex) =>
            PublishInto(Client, edges, triangles, homeLevelByTeamIndex, isClient: true);

        /// <summary>Legacy alias — treats as client publish (presentation).</summary>
        public static void Publish(
            in NativeList<PlanetConnectionGraphLogic.Edge> edges,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles,
            in NativeArray<int> homeLevelByTeamIndex) =>
            PublishClient(edges, triangles, homeLevelByTeamIndex);

        static void PublishInto(
            Side side,
            in NativeList<PlanetConnectionGraphLogic.Edge> edges,
            in NativeList<PlanetConnectionGraphLogic.Triangle> triangles,
            in NativeArray<int> homeLevelByTeamIndex,
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

            // Topology changed — invalidate moon-vertex cache so the next motor tick rebuilds.
            side.RuntimeCache.Clear();
            side.RuntimeCacheMoonElapsed = -999.0;
            side.DisposeRuntimeNative();

            if (isClient)
                ClientPublishRevision++;
            else
                ServerPublishRevision++;
        }

        /// <summary>Clears both sides (leave session / domain reload).</summary>
        public static void Clear()
        {
            Server.Clear();
            Client.Clear();
            LocalOwnerTerritoryMult = 1f;
            s_StickyTerritoryMult = 1f;
            s_StickyTerritoryUntilMoonElapsed = -1.0;
            ClientPublishRevision++;
            ServerPublishRevision++;
        }

        /// <summary>
        /// Updates the local-owner presentation thruster mult with enter/exit sticky hold.
        /// Call only from <c>NetworkTime.IsFirstTimeFullyPredictingTick</c> — resim must not write this.
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

            // --- Outside: keep latched boost briefly (edge / moon-vertex noise) ---
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
        /// Rebuilds moon vertices when topology or moon clock cache expires.
        /// </summary>
        public static NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle> GetRuntimeTrianglesNative(
            PlanetConnectionGraphSide sideKind,
            in NativeArray<PlanetMotorSnapshot> planets,
            double moonElapsedSeconds)
        {
            Side side = sideKind == PlanetConnectionGraphSide.Server ? Server : Client;

            if (side.Triangles.Count == 0)
            {
                side.RuntimeCache.Clear();
                side.RuntimeCacheMoonElapsed = moonElapsedSeconds;
                // Keep a single empty Persistent array — do not Dispose/realloc every tick.
                if (!side.RuntimeNative.IsCreated || side.RuntimeNative.Length != 0)
                {
                    side.DisposeRuntimeNative();
                    side.RuntimeNative = new NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle>(
                        0, Allocator.Persistent);
                }

                return side.RuntimeNative;
            }

            bool cacheFresh =
                side.RuntimeCache.Count > 0 &&
                side.RuntimeNative.IsCreated &&
                math.abs(moonElapsedSeconds - side.RuntimeCacheMoonElapsed) < RuntimeMoonCacheSeconds;

            if (!cacheFresh)
            {
                RebuildRuntimeCache(side, planets, moonElapsedSeconds);
                side.SyncRuntimeNativeFromCache();
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
            double moonElapsedSeconds,
            Allocator allocator)
        {
            var native = GetRuntimeTrianglesNative(sideKind, planets, moonElapsedSeconds);
            var list = new NativeList<PlanetConnectionGraphLogic.RuntimeTriangle>(native.Length, allocator);
            for (int i = 0; i < native.Length; i++)
                list.Add(native[i]);
            return list;
        }

        /// <summary>Legacy — builds from <b>client</b> side (predicted motor / presentation).</summary>
        public static NativeList<PlanetConnectionGraphLogic.RuntimeTriangle> BuildRuntimeTriangles(
            in NativeArray<PlanetMotorSnapshot> planets,
            double moonElapsedSeconds,
            Allocator allocator) =>
            BuildRuntimeTriangles(PlanetConnectionGraphSide.Client, planets, moonElapsedSeconds, allocator);

        /// <summary>Recomputes moon vertices into the side's managed runtime cache.</summary>
        static void RebuildRuntimeCache(
            Side side,
            in NativeArray<PlanetMotorSnapshot> planets,
            double moonElapsedSeconds)
        {
            side.RuntimeCache.Clear();
            for (int i = 0; i < side.Triangles.Count; i++)
            {
                var t = side.Triangles[i];
                if (!TryFindPlanet(planets, t.PlanetIdA, out var pa) ||
                    !TryFindPlanet(planets, t.PlanetIdB, out var pb) ||
                    !TryFindPlanet(planets, t.PlanetIdC, out var pc))
                    continue;

                float3 va = WrapMoonCanonical(PlanetOrbitMath.GetMoonWorldPosition(
                    pa.Transform.Position,
                    math.max(0.25f, pa.Transform.Scale),
                    pa.Planet.PlanetLevel,
                    pa.Planet.PlanetId,
                    moonElapsedSeconds,
                    pa.Planet.IsHomePlanet));
                float3 vb = WrapMoonCanonical(PlanetOrbitMath.GetMoonWorldPosition(
                    pb.Transform.Position,
                    math.max(0.25f, pb.Transform.Scale),
                    pb.Planet.PlanetLevel,
                    pb.Planet.PlanetId,
                    moonElapsedSeconds,
                    pb.Planet.IsHomePlanet));
                float3 vc = WrapMoonCanonical(PlanetOrbitMath.GetMoonWorldPosition(
                    pc.Transform.Position,
                    math.max(0.25f, pc.Transform.Scale),
                    pc.Planet.PlanetLevel,
                    pc.Planet.PlanetId,
                    moonElapsedSeconds,
                    pc.Planet.IsHomePlanet));

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

            side.RuntimeCacheMoonElapsed = moonElapsedSeconds;
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

        static float3 WrapMoonCanonical(float3 moonWorld)
        {
            moonWorld.y = 0f;
            return ToroidalMapEcs.Wrap(moonWorld);
        }
    }
}
