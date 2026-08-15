using TitanOrbit.Core;
using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Pure toroidal planet-connection graph math for same-team territory.
    /// <para>
    /// [TITAN-ORBIT] One global planar graph on <b>planet centers</b>: <b>no two lines ever cross</b>,
    /// whether friendly or enemy (proper intersections <b>and</b> collinear overlaps). Sticky history
    /// wins — whoever created an edge first keeps it (<see cref="Edge.CreationSequence"/>); a later
    /// team blocked by that segment cannot add the conflicting line. After sticky seed + resolve +
    /// greedy pack, a final strip pass guarantees planarity. Territory fills publish only for
    /// <b>short-embeddable facial</b> 3-cliques (all three sides are shortest geodesics in one chart,
    /// and no other same-team planet sits inside) so drawers never invent a long opposite-side chord
    /// and never stack overlapping fills when an interior capture subdivides a larger triangle.
    /// Lone edges are visual-only; bonuses need a filled embeddable face.
    /// </para>
    /// Point-in-triangle tests short-embed charts (same geodesic disk the fill draws), not a
    /// VertexA-only Euclidean unwrap that can disagree across seams.
    /// Shared by server authority (asteroid tint, mining, pop bonuses) and client prediction
    /// (friendly speed). Burst-safe — no managed allocations inside hot helpers.
    /// </summary>
    [BurstCompile]
    public static class PlanetConnectionGraphLogic
    {
        /// <summary>
        /// [TITAN-ORBIT] Original balance: +5% gem / speed per home planet level inside friendly territory.
        /// </summary>
        public const float PerLevelGemBonusFraction = 0.05f;

        /// <summary>
        /// After leaving a friendly triangle, keep the latched movement multiplier this many seconds
        /// (motor + presentation) so edge / brief PIT misses do not chop thrust every tick.
        /// [TITAN-ORBIT] Shared by <c>ShipTerritoryBoostLatch</c> and presentation sticky hold —
        /// ship MovementSpeed attributes are <b>not</b> required for this boost.
        /// </summary>
        public const float TerritoryBoostStickySeconds = 1.25f;

        /// <summary>
        /// [TITAN-ORBIT] Original balance: +5% max pop and growth per triangle average level, stacked
        /// onto each corner planet.
        /// </summary>
        public const float PerTrianglePlanetBonusFraction = 0.05f;

        /// <summary>One owned planet fed into <see cref="RebuildFullGraph"/>.</summary>
        public struct PlanetInput
        {
            /// <summary>Stable planet id from planet ghost state.</summary>
            public int PlanetId;

            /// <summary>Controlling team; <see cref="TeamId.None"/> planets are ignored.</summary>
            public TeamId Team;

            /// <summary>Upgrade level — averages into triangle strength.</summary>
            public int PlanetLevel;

            /// <summary>
            /// Planet-core XZ in canonical toroidal space (Y ignored) for nearest-neighbor edges.
            /// [TITAN-ORBIT] Uses planet centers — not gem moons — so sticky non-crossing topology
            /// stays stable as moons orbit (moons would constantly invalidate crossings).
            /// </summary>
            public float3 Position;

            /// <summary>True for team home worlds — used when resolving home level for bonuses.</summary>
            public bool IsHomePlanet;
        }

        /// <summary>
        /// Undirected same-team edge between two planet ids (part of the global non-crossing map).
        /// <see cref="CreationSequence"/> is sticky history: lower = created earlier; wins when any
        /// two edges (any teams) later intersect under current planet centers.
        /// </summary>
        public struct Edge
        {
            public int PlanetIdA;
            public int PlanetIdB;
            public TeamId Team;

            /// <summary>Monotonic create order for this world's graph (server or client cache).</summary>
            public uint CreationSequence;
        }

        /// <summary>Territory triangle — three same-team planets plus bonus metadata.</summary>
        public struct Triangle
        {
            public int PlanetIdA;
            public int PlanetIdB;
            public int PlanetIdC;
            public TeamId Team;

            /// <summary>Mean of the three planet levels at build time.</summary>
            public float AverageLevel;

            /// <summary>Gem multiplier for strongest-triangle picks: <c>1 + avg × 0.05</c>.</summary>
            public float GemBonusMultiplier;
        }

        /// <summary>
        /// Runtime triangle with planet-core vertices for point-in-triangle and drawing.
        /// Topology comes from <see cref="Triangle"/>; positions are stable (planets do not drift).
        /// </summary>
        public struct RuntimeTriangle
        {
            public float3 VertexA;
            public float3 VertexB;
            public float3 VertexC;
            public TeamId Team;
            public float GemBonusMultiplier;
            public float AverageLevel;
            public int PlanetIdA;
            public int PlanetIdB;
            public int PlanetIdC;
        }

        /// <summary>
        /// Clears and rebuilds the global non-crossing edge map, then same-team territory triangles.
        /// <para>
        /// Steps: (1) seed every sticky edge still owned by its team; (2) if any two edges conflict
        /// (proper cross or collinear overlap, friend or foe), drop the newer
        /// <see cref="Edge.CreationSequence"/>; (3) greedily add every shorter same-team pair that
        /// clears <b>any</b> existing edge; (4) strip again so planarity is guaranteed even if pack
        /// missed once; (5) publish only <b>short-embeddable facial</b> same-team 3-cliques as
        /// triangles (empty interior — outer shells drop when an inner planet subdivides them).
        /// </para>
        /// </summary>
        /// <param name="planets">All planets (neutral entries are skipped).</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="previousEdges">Edges from the last rebuild (may be empty on first run).</param>
        /// <param name="nextSequence">In/out monotonic sequence for newly created edges.</param>
        /// <param name="edges">Destination edge list (cleared).</param>
        /// <param name="triangles">Destination triangle list (cleared).</param>
        public static void RebuildFullGraph(
            in NativeArray<PlanetInput> planets,
            float mapW,
            float mapH,
            in NativeList<Edge> previousEdges,
            ref uint nextSequence,
            ref NativeList<Edge> edges,
            ref NativeList<Triangle> triangles)
        {
            edges.Clear();
            triangles.Clear();

            // --- Need at least two owned planets somewhere to form a lone edge ---
            if (!planets.IsCreated || planets.Length < 2)
                return;

            // --- Phase 1: seed all sticky edges still valid (any team) ---
            // [TITAN-ORBIT] Capture / team flip drops an edge when either endpoint leaves that team.
            if (previousEdges.IsCreated)
            {
                for (int i = 0; i < previousEdges.Length; i++)
                {
                    var e = previousEdges[i];
                    if (e.Team == TeamId.None)
                        continue;
                    if (!TeamOwnsPlanetId(planets, e.PlanetIdA, e.Team) ||
                        !TeamOwnsPlanetId(planets, e.PlanetIdB, e.Team))
                        continue;
                    TryAddEdgeExact(ref edges, e);
                }
            }

            // --- Phase 2: resolve geometric conflicts across the whole map — first-created wins ---
            // [TITAN-ORBIT] Friendly vs friendly, enemy vs enemy, and friend vs enemy all count.
            ResolveCrossingStickyEdges(planets, ref edges, mapW, mapH);

            // --- Phase 3: pack every shorter same-team edge that clears every existing line ---
            PackNonCrossingEdgesGreedy(planets, mapW, mapH, ref nextSequence, ref edges);

            // --- Phase 4: hard guarantee — strip any remaining conflicts (oracle / pack miss) ---
            StripAllCrossingEdges(planets, ref edges, mapW, mapH);
            ValidateNoCrossings(planets, edges, mapW, mapH);

            // --- Phase 5: publish only short-embeddable same-team 3-cliques as territory triangles ---
            PublishTrianglesForAllTeams(planets, mapW, mapH, ref edges, ref triangles);
        }

        /// <summary>
        /// True when segment AB properly intersects segment CD on the torus (XZ).
        /// Shared endpoints are not a cross (caller should skip same-planet pairs).
        /// <para>
        /// [TITAN-ORBIT] Each connection is a <b>shortest-path</b> chord (≤ half map per axis).
        /// A single A-anchored chart is not enough: two seam-crossing chords can miss each other
        /// in that chart (classic torus generators) and then the greedy pack would allow a real
        /// friend/foe cross after capture. We fix AB as its shortest lift from A, then test CD
        /// against all 3×3 periodic images of that short segment in the covering plane.
        /// </para>
        /// </summary>
        public static bool SegmentsCrossToroidalXZ(
            float3 a,
            float3 b,
            float3 c,
            float3 d,
            float mapW,
            float mapH)
        {
            // --- Fix AB as the shortest lift with A at the origin ---
            float2 a0 = float2.zero;
            float3 offB = ToroidalMapEcs.ShortestOffsetXZ(a, b, mapW, mapH);
            float2 b0 = new float2(offB.x, offB.z);

            // --- CD as a short segment; try every neighboring tile relative to A ---
            // [TITAN-ORBIT] cBase is one lift of C near A; dDelta is always the shortest C→D.
            // Shifting (cBase, cBase+dDelta) by (±mapW, ±mapH) covers every way CD can sit
            // next to AB on the torus without enumerating lifts of AB itself.
            float3 offC = ToroidalMapEcs.ShortestOffsetXZ(a, c, mapW, mapH);
            float3 offCD = ToroidalMapEcs.ShortestOffsetXZ(c, d, mapW, mapH);
            float2 cBase = new float2(offC.x, offC.z);
            float2 dDelta = new float2(offCD.x, offCD.z);

            for (int ox = -1; ox <= 1; ox++)
            {
                for (int oz = -1; oz <= 1; oz++)
                {
                    float2 c0 = cBase + new float2(ox * mapW, oz * mapH);
                    float2 d0 = c0 + dDelta;
                    // Proper cross OR collinear interior overlap — both are illegal on the map.
                    if (SegmentsConflict2D(a0, b0, c0, d0))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when three planet centers form a triangle whose three shortest geodesics embed as a
        /// simple Euclidean triangle in at least one corner-anchored chart.
        /// <para>
        /// [TITAN-ORBIT] A 3-clique of short edges on a torus is not always a disk: the opposite side
        /// in an A-anchored chart can be the <b>long</b> way around. Publishing those as fills made
        /// <c>TriangleBorder</c> draw a fake chord that crossed other lines. We only fill when
        /// chart(BC) ≈ Shortest(B,C) for some anchor.
        /// </para>
        /// </summary>
        public static bool IsShortEmbeddableTriangle(
            float3 a,
            float3 b,
            float3 c,
            float mapW,
            float mapH)
        {
            return TryShortEmbedFromAnchor(a, b, c, mapW, mapH) ||
                   TryShortEmbedFromAnchor(b, a, c, mapW, mapH) ||
                   TryShortEmbedFromAnchor(c, a, b, mapW, mapH);
        }

        /// <summary>
        /// True when <paramref name="worldPos"/> lies inside the same short-embed territory fill
        /// that <c>PlanetConnectionShapesVisual</c> draws (geodesic triangle on the XZ torus).
        /// <para>
        /// [TITAN-ORBIT] Do <b>not</b> always unwrap from <paramref name="vertexA"/>. The drawer
        /// picks a corner where chart(BC) ≈ Shortest(B,C); anchoring PIT at a different corner
        /// builds a Euclidean triangle that uses the <b>long</b> way for one side — boost then
        /// fires in the wrong region (often outside the tinted fill, or missing inside it).
        /// Ship positions may be unbounded; verts are canonical-wrapped — ShortestOffset handles both.
        /// Display retile / seam wrap-copies are presentation only; membership is this chart test.
        /// </para>
        /// </summary>
        /// <param name="worldPos">Ship or asteroid world pose (Y ignored; need not be wrapped).</param>
        /// <param name="vertexA">Canonical planet-center A.</param>
        /// <param name="vertexB">Canonical planet-center B.</param>
        /// <param name="vertexC">Canonical planet-center C.</param>
        /// <param name="mapW">Toroidal map width from MapStateSingleton / ToroidalMapEcs.</param>
        /// <param name="mapH">Toroidal map height.</param>
        public static bool PointInTriangleXZ(
            float3 worldPos,
            float3 vertexA,
            float3 vertexB,
            float3 vertexC,
            float mapW,
            float mapH)
        {
            // --- Every short-embed chart (matches drawn fill) ---
            // Published triangles are graph-gated as short-embeddable from at least one corner.
            // Testing all valid charts keeps membership identical to the geodesic disk the player sees.
            // No VertexA-only fallback: that chart can use a long opposite side and disagree with the tint.
            return PointInShortEmbedChart(worldPos, vertexA, vertexB, vertexC, mapW, mapH) ||
                   PointInShortEmbedChart(worldPos, vertexB, vertexA, vertexC, mapW, mapH) ||
                   PointInShortEmbedChart(worldPos, vertexC, vertexA, vertexB, mapW, mapH);
        }

        /// <summary>
        /// True when <paramref name="worldPos"/> is inside the Euclidean triangle formed by
        /// shortest offsets from <paramref name="anchor"/> to <paramref name="p"/> / <paramref name="q"/>,
        /// and that chart is a short embedding (opposite side ≈ toroidal Shortest).
        /// </summary>
        static bool PointInShortEmbedChart(
            float3 worldPos,
            float3 anchor,
            float3 p,
            float3 q,
            float mapW,
            float mapH)
        {
            if (!TryShortEmbedFromAnchor(anchor, p, q, mapW, mapH))
                return false;
            return PointInAnchorChart(worldPos, anchor, p, q, mapW, mapH);
        }

        /// <summary>
        /// Barycentric point-in-triangle in the A-anchored shortest-offset chart on the torus.
        /// Does not require the chart to be short-embeddable (caller decides).
        /// </summary>
        static bool PointInAnchorChart(
            float3 worldPos,
            float3 anchor,
            float3 vertexB,
            float3 vertexC,
            float mapW,
            float mapH)
        {
            // --- Unwrap into local XZ with anchor at origin (ShortestOffset — seam-safe) ---
            // [TITAN-ORBIT] Ship may be many map-widths away; verts stay in [-half, half).
            float2 a = float2.zero;
            float3 offB = ToroidalMapEcs.ShortestOffsetXZ(anchor, vertexB, mapW, mapH);
            float3 offC = ToroidalMapEcs.ShortestOffsetXZ(anchor, vertexC, mapW, mapH);
            float3 offP = ToroidalMapEcs.ShortestOffsetXZ(anchor, worldPos, mapW, mapH);
            float2 b = new float2(offB.x, offB.z);
            float2 c = new float2(offC.x, offC.z);
            float2 p = new float2(offP.x, offP.z);

            float area = Cross(b - a, c - a);
            if (math.abs(area) < 1e-8f)
                return false;

            float s = Cross(p - a, c - a) / area;
            float t = Cross(b - a, p - a) / area;
            float u = 1f - s - t;
            const float eps = -0.0001f;
            return s >= eps && t >= eps && u >= eps;
        }

        /// <summary>
        /// Returns the team owning the strongest (highest gem mult) triangle containing
        /// <paramref name="worldPos"/>, or <see cref="TeamId.None"/>.
        /// Use <see cref="GetTeamsMaskAtPosition"/> when multiple teams can share a point
        /// (overlap) — strongest-wins alone is wrong for tint / friendly bonuses.
        /// </summary>
        public static TeamId GetTeamAtPosition(
            float3 worldPos,
            in NativeArray<RuntimeTriangle> runtime,
            float mapW,
            float mapH)
        {
            // --- Strongest-triangle pick (primary / fallback tint) ---
            TeamId bestTeam = TeamId.None;
            float bestBonus = 0f;
            if (!runtime.IsCreated)
                return bestTeam;

            for (int i = 0; i < runtime.Length; i++)
            {
                var tri = runtime[i];
                if (!PointInTriangleXZ(worldPos, tri.VertexA, tri.VertexB, tri.VertexC, mapW, mapH))
                    continue;
                if (tri.GemBonusMultiplier > bestBonus)
                {
                    bestBonus = tri.GemBonusMultiplier;
                    bestTeam = tri.Team;
                }
            }

            return bestTeam;
        }

        /// <summary>
        /// Bitmask of every team whose triangle contains <paramref name="worldPos"/>
        /// (TeamA = bit 0 … TeamE = bit 4). Overlaps set multiple bits — the rock is
        /// “both teams” for friendly mining / destroy bonuses.
        /// </summary>
        /// <returns>0 when outside every triangle or <paramref name="runtime"/> is empty.</returns>
        public static byte GetTeamsMaskAtPosition(
            float3 worldPos,
            in NativeArray<RuntimeTriangle> runtime,
            float mapW,
            float mapH)
        {
            GetTerritoryOwnershipAtPosition(
                worldPos, runtime, mapW, mapH, out byte mask, out _);
            return mask;
        }

        /// <summary>
        /// Single point-in-triangle pass: all owning teams (mask) plus strongest primary team.
        /// Used by <c>AsteroidTerritorySystem</c> so each rock is not tested twice per refresh.
        /// </summary>
        /// <param name="worldPos">Canonical wrapped XZ position.</param>
        /// <param name="runtime">Live planet-center triangles.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="mask">OR of every team bit that contains the point.</param>
        /// <param name="primaryTeam">Highest <see cref="RuntimeTriangle.GemBonusMultiplier"/> team, or None.</param>
        public static void GetTerritoryOwnershipAtPosition(
            float3 worldPos,
            in NativeArray<RuntimeTriangle> runtime,
            float mapW,
            float mapH,
            out byte mask,
            out TeamId primaryTeam)
        {
            // --- Accumulate ownership bits + track strongest triangle ---
            mask = 0;
            primaryTeam = TeamId.None;
            float bestBonus = 0f;
            if (!runtime.IsCreated)
                return;

            for (int i = 0; i < runtime.Length; i++)
            {
                var tri = runtime[i];
                byte bit = TeamToMaskBit(tri.Team);
                if (bit == 0)
                    continue;

                // Still PIT even when bit already set — another triangle of same team may
                // be stronger for primary, and we need gem mult for primary pick.
                if (!PointInTriangleXZ(worldPos, tri.VertexA, tri.VertexB, tri.VertexC, mapW, mapH))
                    continue;

                mask |= bit;
                if (tri.GemBonusMultiplier > bestBonus)
                {
                    bestBonus = tri.GemBonusMultiplier;
                    primaryTeam = tri.Team;
                }
            }
        }

        /// <summary>
        /// True when <paramref name="team"/> owns at least one triangle containing the point.
        /// Unlike <see cref="GetTeamAtPosition"/>, a stronger enemy overlap does not hide you.
        /// </summary>
        public static bool IsTeamAtPosition(
            TeamId team,
            float3 worldPos,
            in NativeArray<RuntimeTriangle> runtime,
            float mapW,
            float mapH)
        {
            if (team == TeamId.None || !runtime.IsCreated)
                return false;

            byte bit = TeamToMaskBit(team);
            if (bit == 0)
                return false;

            // Fast path: full mask scan would also work; walk only matching-team tris.
            for (int i = 0; i < runtime.Length; i++)
            {
                var tri = runtime[i];
                if (tri.Team != team)
                    continue;
                if (PointInTriangleXZ(worldPos, tri.VertexA, tri.VertexB, tri.VertexC, mapW, mapH))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// [TITAN-ORBIT] Asteroid tint for a viewer: if their team is in the ownership mask,
        /// show their colour (overlap defaults to “own team”); else show <paramref name="primaryTeam"/>
        /// (strongest triangle). Empty mask → <see cref="TeamId.None"/> (no tint).
        /// </summary>
        /// <param name="territoryMask">Ghosted multi-team ownership bits.</param>
        /// <param name="primaryTeam">Strongest containing triangle (server fallback).</param>
        /// <param name="viewerTeam">Local player team, or None before Join Team.</param>
        public static TeamId ResolveAsteroidTintTeam(
            byte territoryMask,
            TeamId primaryTeam,
            TeamId viewerTeam)
        {
            // --- No ownership → clear tint ---
            if (territoryMask == 0)
                return TeamId.None;

            // --- Overlap: prefer the viewer's own team colour ---
            if (viewerTeam != TeamId.None && TeamMaskContains(territoryMask, viewerTeam))
                return viewerTeam;

            // --- Single / other-team ownership: strongest (primary) if still in mask ---
            if (primaryTeam != TeamId.None && TeamMaskContains(territoryMask, primaryTeam))
                return primaryTeam;

            // --- Primary stale: first set bit (A→E) ---
            for (byte t = (byte)TeamId.TeamA; t <= (byte)TeamId.TeamE; t++)
            {
                var candidate = (TeamId)t;
                if (TeamMaskContains(territoryMask, candidate))
                    return candidate;
            }

            return TeamId.None;
        }

        /// <summary>
        /// Friendly territory movement multiplier: <c>1 + 0.05 × homeLevel</c> when the ship sits
        /// inside a triangle owned by <paramref name="shipTeam"/>; otherwise 1.
        /// Overlaps with a stronger enemy triangle still count as friendly.
        /// <para>
        /// [TITAN-ORBIT] This is <b>not</b> a ship MovementSpeed attribute upgrade — chassis stats
        /// set base MaxSpeed; territory multiplies at drive time. Callers should sticky-latch via
        /// <c>ShipTerritoryBoostLatch</c> so edge flicker does not chop thrust.
        /// </para>
        /// </summary>
        public static float FriendlyTerritoryMovementMultiplier(
            float3 shipPos,
            TeamId shipTeam,
            in NativeArray<RuntimeTriangle> runtime,
            in NativeArray<int> homeLevelByTeamIndex,
            float mapW,
            float mapH)
        {
            if (shipTeam == TeamId.None)
                return 1f;

            // [TITAN-ORBIT] Must not use GetTeamAtPosition (strongest-wins) — enemy overlap
            // would incorrectly strip the friendly speed boost.
            if (!IsTeamAtPosition(shipTeam, shipPos, runtime, mapW, mapH))
                return 1f;

            int homeLevel = GetHomePlanetLevel(shipTeam, homeLevelByTeamIndex);
            return 1f + PerLevelGemBonusFraction * homeLevel;
        }

        /// <summary>
        /// Extra mining / destroy yield when the ship's team owns this asteroid (mask bit).
        /// Enemy-only rocks return 1 (no extra crystals). The extra gems Instantiates yellow
        /// so the bonus is visible; scoop is still free-for-all.
        /// </summary>
        public static float FriendlyTerritoryGemMultiplier(
            TeamId shipTeam,
            byte asteroidTerritoryMask,
            int homePlanetLevel)
        {
            if (shipTeam == TeamId.None ||
                asteroidTerritoryMask == 0 ||
                !TeamMaskContains(asteroidTerritoryMask, shipTeam))
                return 1f;

            int level = math.max(1, homePlanetLevel);
            return 1f + PerLevelGemBonusFraction * level;
        }

        /// <summary>
        /// Legacy single-team overload — treats <paramref name="asteroidTerritoryTeam"/> as the
        /// only owner. Prefer the mask overload when <c>TerritoryTeamsMask</c> is available.
        /// </summary>
        public static float FriendlyTerritoryGemMultiplier(
            TeamId shipTeam,
            TeamId asteroidTerritoryTeam,
            int homePlanetLevel) =>
            FriendlyTerritoryGemMultiplier(
                shipTeam,
                TeamToMaskBit(asteroidTerritoryTeam),
                homePlanetLevel);

        /// <summary>
        /// [STANDARD] TeamA = bit 0 … TeamE = bit 4 (same as <c>TeamIdExtensions.ToMaskBit</c>).
        /// Inlined here so Burst callers never touch managed extension methods.
        /// </summary>
        public static byte TeamToMaskBit(TeamId team)
        {
            if (team == TeamId.None)
                return 0;
            return (byte)(1 << ((int)team - 1));
        }

        /// <summary>True when <paramref name="mask"/> includes <paramref name="team"/>.</summary>
        public static bool TeamMaskContains(byte mask, TeamId team)
        {
            byte bit = TeamToMaskBit(team);
            return bit != 0 && (mask & bit) != 0;
        }

        /// <summary>
        /// Looks up home planet level for a team from a 6-slot array indexed by <see cref="TeamId"/> byte.
        /// Missing / zero entries fall back to level 1 (original NGO behavior).
        /// </summary>
        public static int GetHomePlanetLevel(TeamId team, in NativeArray<int> homeLevelByTeamIndex)
        {
            if (team == TeamId.None || !homeLevelByTeamIndex.IsCreated)
                return 1;

            int idx = (int)team;
            if (idx < 0 || idx >= homeLevelByTeamIndex.Length)
                return 1;

            int level = homeLevelByTeamIndex[idx];
            return level > 0 ? level : 1;
        }

        /// <summary>
        /// Fills a 6-slot home-level array (index = TeamId byte) from planet inputs.
        /// </summary>
        public static void FillHomeLevels(in NativeArray<PlanetInput> planets, ref NativeArray<int> homeLevelByTeamIndex)
        {
            for (int i = 0; i < homeLevelByTeamIndex.Length; i++)
                homeLevelByTeamIndex[i] = 0;

            if (!planets.IsCreated)
                return;

            for (int i = 0; i < planets.Length; i++)
            {
                var p = planets[i];
                if (!p.IsHomePlanet || p.Team == TeamId.None)
                    continue;
                int idx = (int)p.Team;
                if (idx >= 0 && idx < homeLevelByTeamIndex.Length)
                    homeLevelByTeamIndex[idx] = math.max(1, p.PlanetLevel);
            }
        }

        /// <summary>
        /// Stacks per-triangle planet bonuses: for each triangle corner, add
        /// <c>0.05 × AverageLevel</c>. Writes into <paramref name="bonusByPlanetId"/> keyed by planet id
        /// (caller supplies a map via parallel arrays).
        /// </summary>
        public static float GetCornerBonusStrength(float averageLevel) =>
            PerTrianglePlanetBonusFraction * averageLevel;

        /// <summary>
        /// True when the undirected edge (a,b) is a side of any triangle in <paramref name="triangles"/>
        /// for the same team. Kept for callers that still want to know; primary drawers now draw
        /// every graph edge as a shortest line (fills no longer invent opposite-side borders).
        /// </summary>
        public static bool EdgeIsTriangleSide(
            int planetIdA,
            int planetIdB,
            TeamId team,
            in NativeList<Triangle> triangles)
        {
            if (!triangles.IsCreated || planetIdA == planetIdB)
                return false;

            CanonicalEdgeIds(planetIdA, planetIdB, out int a, out int b);
            for (int i = 0; i < triangles.Length; i++)
            {
                var t = triangles[i];
                if (t.Team != team)
                    continue;
                if (EdgeMatchesTriangleSide(a, b, t.PlanetIdA, t.PlanetIdB) ||
                    EdgeMatchesTriangleSide(a, b, t.PlanetIdB, t.PlanetIdC) ||
                    EdgeMatchesTriangleSide(a, b, t.PlanetIdC, t.PlanetIdA))
                    return true;
            }

            return false;
        }

        /// <summary>Managed-list overload for presentation (Shapes / minimap).</summary>
        public static bool EdgeIsTriangleSide(
            int planetIdA,
            int planetIdB,
            TeamId team,
            System.Collections.Generic.IReadOnlyList<Triangle> triangles)
        {
            if (triangles == null || planetIdA == planetIdB)
                return false;

            CanonicalEdgeIds(planetIdA, planetIdB, out int a, out int b);
            for (int i = 0; i < triangles.Count; i++)
            {
                var t = triangles[i];
                if (t.Team != team)
                    continue;
                if (EdgeMatchesTriangleSide(a, b, t.PlanetIdA, t.PlanetIdB) ||
                    EdgeMatchesTriangleSide(a, b, t.PlanetIdB, t.PlanetIdC) ||
                    EdgeMatchesTriangleSide(a, b, t.PlanetIdC, t.PlanetIdA))
                    return true;
            }

            return false;
        }

        // -------------------------------------------------------------------------
        // Sticky rebuild helpers
        // -------------------------------------------------------------------------

        /// <summary>2D cross product (x*y components) for barycentric / orientation tests.</summary>
        static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        /// <summary>
        /// True when two segments conflict in R2: proper intersection <b>or</b> collinear interior
        /// overlap. Shared endpoints alone are not a conflict (T-junction / path).
        /// </summary>
        static bool SegmentsConflict2D(float2 a, float2 b, float2 c, float2 d)
        {
            if (SegmentsCrossProper2D(a, b, c, d))
                return true;
            return SegmentsOverlapCollinear2D(a, b, c, d);
        }

        /// <summary>
        /// Proper segment intersection in R2 (not merely touching at an endpoint).
        /// Collinear cases are handled separately by <see cref="SegmentsOverlapCollinear2D"/>.
        /// </summary>
        static bool SegmentsCrossProper2D(float2 a, float2 b, float2 c, float2 d)
        {
            // --- Shared / nearly-shared endpoints → not a proper cross ---
            const float eps = 1e-4f;
            if (math.distancesq(a, c) < eps * eps || math.distancesq(a, d) < eps * eps ||
                math.distancesq(b, c) < eps * eps || math.distancesq(b, d) < eps * eps)
                return false;

            float o1 = Orient(a, b, c);
            float o2 = Orient(a, b, d);
            float o3 = Orient(c, d, a);
            float o4 = Orient(c, d, b);

            // Strict opposite orientations on both segments.
            return (o1 * o2 < 0f) && (o3 * o4 < 0f);
        }

        /// <summary>
        /// True when AB and CD are (nearly) collinear and their interiors overlap on the line.
        /// Endpoint-only touch (T-junction / shared vertex) returns false.
        /// </summary>
        static bool SegmentsOverlapCollinear2D(float2 a, float2 b, float2 c, float2 d)
        {
            const float orientEps = 1e-3f;
            float o1 = Orient(a, b, c);
            float o2 = Orient(a, b, d);
            float o3 = Orient(c, d, a);
            float o4 = Orient(c, d, b);
            if (math.abs(o1) > orientEps || math.abs(o2) > orientEps ||
                math.abs(o3) > orientEps || math.abs(o4) > orientEps)
                return false;

            // --- Project onto the dominant axis of AB ---
            float2 ab = b - a;
            bool useX = math.abs(ab.x) >= math.abs(ab.y);
            float a0 = useX ? a.x : a.y;
            float b0 = useX ? b.x : b.y;
            float c0 = useX ? c.x : c.y;
            float d0 = useX ? d.x : d.y;
            if (a0 > b0)
            {
                float tmp = a0;
                a0 = b0;
                b0 = tmp;
            }

            if (c0 > d0)
            {
                float tmp = c0;
                c0 = d0;
                d0 = tmp;
            }

            // Strict interior overlap (touching only at an endpoint is allowed).
            const float overlapEps = 1e-3f;
            float left = math.max(a0, c0);
            float right = math.min(b0, d0);
            return right - left > overlapEps;
        }

        /// <summary>Signed area orientation of triangle (a,b,c) in XZ plane.</summary>
        static float Orient(float2 a, float2 b, float2 c) => Cross(b - a, c - a);

        /// <summary>
        /// True when shortest geodesics AB, AC, BC all match the A-anchored Euclidean triangle
        /// (chart BC ≈ Shortest(B,C)) and the triangle has non-zero area.
        /// </summary>
        static bool TryShortEmbedFromAnchor(
            float3 anchor,
            float3 p,
            float3 q,
            float mapW,
            float mapH)
        {
            float3 offP = ToroidalMapEcs.ShortestOffsetXZ(anchor, p, mapW, mapH);
            float3 offQ = ToroidalMapEcs.ShortestOffsetXZ(anchor, q, mapW, mapH);
            float2 P = new float2(offP.x, offP.z);
            float2 Q = new float2(offQ.x, offQ.z);

            // Degenerate (collinear / zero area) — not a fillable territory triangle.
            const float areaEps = 1e-3f;
            if (math.abs(Cross(P, Q)) < areaEps)
                return false;

            // Chart vector Q→P must equal the shortest geodesic Q→P on the torus.
            float3 shortQP = ToroidalMapEcs.ShortestOffsetXZ(q, p, mapW, mapH);
            float2 chartQP = P - Q;
            float2 geodesicQP = new float2(shortQP.x, shortQP.z);
            const float matchEps = 0.5f;
            return math.lengthsq(chartQP - geodesicQP) <= matchEps * matchEps;
        }

        /// <summary>
        /// Final planarity pass after greedy pack — same first-created wins rule as sticky resolve.
        /// [TITAN-ORBIT] Guarantees zero conflicts remain even if pack's oracle missed once.
        /// </summary>
        static void StripAllCrossingEdges(
            in NativeArray<PlanetInput> planets,
            ref NativeList<Edge> edges,
            float mapW,
            float mapH) =>
            ResolveCrossingStickyEdges(planets, ref edges, mapW, mapH);

        /// <summary>
        /// Debug assertion: after strip, no two edges without a shared endpoint may conflict.
        /// Call sites are stripped in non-Editor / non-development builds.
        /// </summary>
        /// <remarks>
        /// [STANDARD] No <c>in</c>/<c>ref</c> params so <see cref="System.Diagnostics.ConditionalAttribute"/>
        /// can strip call sites outside Editor / development builds.
        /// </remarks>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        static void ValidateNoCrossings(
            NativeArray<PlanetInput> planets,
            NativeList<Edge> edges,
            float mapW,
            float mapH)
        {
            for (int i = 0; i < edges.Length; i++)
            {
                for (int j = i + 1; j < edges.Length; j++)
                {
                    var e0 = edges[i];
                    var e1 = edges[j];
                    if (EdgesShareEndpoint(e0, e1))
                        continue;
                    if (!TryGetPlanetPosition(planets, e0.PlanetIdA, out float3 a) ||
                        !TryGetPlanetPosition(planets, e0.PlanetIdB, out float3 b) ||
                        !TryGetPlanetPosition(planets, e1.PlanetIdA, out float3 c) ||
                        !TryGetPlanetPosition(planets, e1.PlanetIdB, out float3 d))
                        continue;
                    if (!SegmentsCrossToroidalXZ(a, b, c, d, mapW, mapH))
                        continue;

                    Debug.LogError(
                        "[PlanetConnection] ValidateNoCrossings failed after strip — " +
                        $"edge ({e0.PlanetIdA}-{e0.PlanetIdB} seq={e0.CreationSequence}) conflicts with " +
                        $"({e1.PlanetIdA}-{e1.PlanetIdB} seq={e1.CreationSequence}).");
                    return;
                }
            }
        }

        /// <summary>Sorts teammate indices ascending by planet id (deterministic fill order).</summary>
        static void SortIndicesByPlanetId(in NativeArray<PlanetInput> planets, ref NativeList<int> indices)
        {
            // --- Insertion sort — n is small (owned planets per team) ---
            for (int i = 1; i < indices.Length; i++)
            {
                int key = indices[i];
                int keyId = planets[key].PlanetId;
                int j = i - 1;
                while (j >= 0 && planets[indices[j]].PlanetId > keyId)
                {
                    indices[j + 1] = indices[j];
                    j--;
                }

                indices[j + 1] = key;
            }
        }

        /// <summary>True when <paramref name="planetId"/> is currently owned by <paramref name="team"/>.</summary>
        static bool TeamOwnsPlanetId(in NativeArray<PlanetInput> planets, int planetId, TeamId team)
        {
            for (int i = 0; i < planets.Length; i++)
            {
                var p = planets[i];
                if (p.PlanetId == planetId && p.Team == team)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Drops newer sticky edges that geometrically conflict with older ones under current planet
        /// centers (proper cross or collinear interior overlap). Compares every pair — same team or
        /// enemy — so the map stays globally planar.
        /// </summary>
        static void ResolveCrossingStickyEdges(
            in NativeArray<PlanetInput> planets,
            ref NativeList<Edge> edges,
            float mapW,
            float mapH)
        {
            // --- Repeat until a full pass finds no crosses (n is small) ---
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < edges.Length; i++)
                {
                    for (int j = i + 1; j < edges.Length; j++)
                    {
                        var e0 = edges[i];
                        var e1 = edges[j];

                        // Shared planet id → T-junction / path, not a line cross.
                        // (Different teams rarely share an endpoint — ownership is exclusive.)
                        if (EdgesShareEndpoint(e0, e1))
                            continue;

                        if (!TryGetPlanetPosition(planets, e0.PlanetIdA, out float3 a) ||
                            !TryGetPlanetPosition(planets, e0.PlanetIdB, out float3 b) ||
                            !TryGetPlanetPosition(planets, e1.PlanetIdA, out float3 c) ||
                            !TryGetPlanetPosition(planets, e1.PlanetIdB, out float3 d))
                            continue;

                        if (!SegmentsCrossToroidalXZ(a, b, c, d, mapW, mapH))
                            continue;

                        // [TITAN-ORBIT] First-created sticky edge wins — drop the newer sequence.
                        int drop = e0.CreationSequence >= e1.CreationSequence ? i : j;
                        edges.RemoveAtSwapBack(drop);
                        changed = true;
                        break;
                    }

                    if (changed)
                        break;
                }
            }
        }

        /// <summary>
        /// Greedy planar packing across all teams: every same-team pair, shortest-first; add when
        /// missing and the segment does not properly cross <b>any</b> existing edge (any team).
        /// <para>
        /// [TITAN-ORBIT] No per-planet degree cap. An enemy sticky line can permanently block a
        /// teammate chord — that team simply cannot form triangles that need the blocked side.
        /// Shared endpoints never count as a cross.
        /// </para>
        /// </summary>
        static void PackNonCrossingEdgesGreedy(
            in NativeArray<PlanetInput> planets,
            float mapW,
            float mapH,
            ref uint nextSequence,
            ref NativeList<Edge> edges)
        {
            // --- Count owned planets (pairs only among same team) ---
            int owned = 0;
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].Team != TeamId.None)
                    owned++;
            }

            if (owned < 2)
                return;

            // Worst case: all owned planets on one team → n*(n-1)/2 pairs.
            int pairCap = owned * (owned - 1) / 2;
            var pairI = new NativeList<int>(pairCap, Allocator.Temp);
            var pairJ = new NativeList<int>(pairCap, Allocator.Temp);
            var pairTeam = new NativeList<TeamId>(pairCap, Allocator.Temp);
            var pairDist = new NativeList<float>(pairCap, Allocator.Temp);

            // --- Enumerate every unordered same-team pair ---
            for (int i = 0; i < planets.Length; i++)
            {
                var a = planets[i];
                if (a.Team == TeamId.None)
                    continue;
                for (int j = i + 1; j < planets.Length; j++)
                {
                    var b = planets[j];
                    if (b.Team != a.Team)
                        continue;

                    float d = ToroidalMapEcs.ToroidalDistance(a.Position, b.Position, mapW, mapH);
                    pairI.Add(i);
                    pairJ.Add(j);
                    pairTeam.Add(a.Team);
                    pairDist.Add(d);
                }
            }

            // --- Insertion-sort pairs by distance (nearest first) ---
            // Deterministic ties: equal length → lower min planet id, then max id.
            for (int i = 1; i < pairDist.Length; i++)
            {
                float keyD = pairDist[i];
                int keyI = pairI[i];
                int keyJ = pairJ[i];
                TeamId keyT = pairTeam[i];
                int keyLo = math.min(planets[keyI].PlanetId, planets[keyJ].PlanetId);
                int keyHi = math.max(planets[keyI].PlanetId, planets[keyJ].PlanetId);
                int k = i - 1;
                while (k >= 0)
                {
                    float dK = pairDist[k];
                    if (dK < keyD)
                        break;
                    if (math.abs(dK - keyD) <= 1e-6f)
                    {
                        int loK = math.min(planets[pairI[k]].PlanetId, planets[pairJ[k]].PlanetId);
                        int hiK = math.max(planets[pairI[k]].PlanetId, planets[pairJ[k]].PlanetId);
                        if (loK < keyLo || (loK == keyLo && hiK <= keyHi))
                            break;
                    }

                    pairDist[k + 1] = pairDist[k];
                    pairI[k + 1] = pairI[k];
                    pairJ[k + 1] = pairJ[k];
                    pairTeam[k + 1] = pairTeam[k];
                    k--;
                }

                pairDist[k + 1] = keyD;
                pairI[k + 1] = keyI;
                pairJ[k + 1] = keyJ;
                pairTeam[k + 1] = keyT;
            }

            // --- Add each edge that clears the global planar map ---
            for (int p = 0; p < pairDist.Length; p++)
            {
                int aId = planets[pairI[p]].PlanetId;
                int bId = planets[pairJ[p]].PlanetId;
                TeamId team = pairTeam[p];
                if (HasEdge(edges, aId, bId, team))
                    continue;
                if (EdgeWouldCrossAny(planets, edges, aId, bId, mapW, mapH))
                    continue;

                AddNewEdge(ref edges, aId, bId, team, ref nextSequence);
            }

            pairI.Dispose();
            pairJ.Dispose();
            pairTeam.Dispose();
            pairDist.Dispose();
        }

        /// <summary>
        /// Builds territory triangles for every team that has at least three planets.
        /// Only short-embeddable <b>facial</b> 3-cliques are published (edges still remain for
        /// non-fillable ones). Nested outer shells are culled after clique discovery.
        /// </summary>
        static void PublishTrianglesForAllTeams(
            in NativeArray<PlanetInput> planets,
            float mapW,
            float mapH,
            ref NativeList<Edge> edges,
            ref NativeList<Triangle> triangles)
        {
            // [TITAN-ORBIT] At most TeamA–TeamE (5). Fixed scratch avoids a HashSet.
            TeamId t0 = TeamId.None, t1 = TeamId.None, t2 = TeamId.None, t3 = TeamId.None, t4 = TeamId.None;
            int teamCount = 0;
            for (int i = 0; i < planets.Length; i++)
            {
                TeamId team = planets[i].Team;
                if (team == TeamId.None)
                    continue;
                if (team == t0 || team == t1 || team == t2 || team == t3 || team == t4)
                    continue;
                if (teamCount == 0) t0 = team;
                else if (teamCount == 1) t1 = team;
                else if (teamCount == 2) t2 = team;
                else if (teamCount == 3) t3 = team;
                else if (teamCount == 4) t4 = team;
                teamCount++;
                if (teamCount >= 5)
                    break;
            }

            for (int ti = 0; ti < teamCount; ti++)
            {
                TeamId team = ti == 0 ? t0 : ti == 1 ? t1 : ti == 2 ? t2 : ti == 3 ? t3 : t4;

                int n = 0;
                for (int i = 0; i < planets.Length; i++)
                {
                    if (planets[i].Team == team)
                        n++;
                }

                if (n < 3)
                    continue;

                var indices = new NativeList<int>(n, Allocator.Temp);
                for (int i = 0; i < planets.Length; i++)
                {
                    if (planets[i].Team == team)
                        indices.Add(i);
                }

                SortIndicesByPlanetId(planets, ref indices);
                BuildCliquesAsTriangles(planets, indices, team, mapW, mapH, ref edges, ref triangles);
                indices.Dispose();
            }
        }

        /// <summary>
        /// Forms territory fills for every short-embeddable triple of teammates that has all three
        /// edges, then drops any triangle that still has another same-team planet in its interior.
        /// <para>
        /// [TITAN-ORBIT] Without the empty-interior cull, capturing planet D inside triangle ABC
        /// while D connects to A/B/C publishes ABD + BCD + CAD <b>and</b> keeps ABC — same region
        /// drawn twice (more opaque fill) and corner pop bonuses stacked on the outer shell.
        /// Planar faces are only the small triangles; the outer 3-clique is not a face once D exists.
        /// Non-embeddable 3-cliques keep their edges (drawn as shortest lines) but get no fill/PIT.
        /// </para>
        /// </summary>
        /// <param name="planets">All planet inputs (used for positions of this team's members).</param>
        /// <param name="indices">Sorted indices into <paramref name="planets"/> for this team only.</param>
        /// <param name="team">Owning team for the edges / triangles being published.</param>
        /// <param name="mapW">Toroidal map width (point-in-triangle + short-embed checks).</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="edges">Global non-crossing edge list (read-only here).</param>
        /// <param name="triangles">Destination list; this team appends then may remove nested shells.</param>
        static void BuildCliquesAsTriangles(
            in NativeArray<PlanetInput> planets,
            in NativeList<int> indices,
            TeamId team,
            float mapW,
            float mapH,
            ref NativeList<Edge> edges,
            ref NativeList<Triangle> triangles)
        {
            if (indices.Length < 3)
                return;

            // --- Where this team's triangles start in the shared list ---
            // Cull only touches entries we add below so other teams' faces stay intact.
            int teamTriStart = triangles.Length;

            // --- Phase A: every short-embeddable 3-clique (graph may still include nested shells) ---
            for (int i = 0; i < indices.Length; i++)
            {
                for (int j = i + 1; j < indices.Length; j++)
                {
                    for (int k = j + 1; k < indices.Length; k++)
                    {
                        var a = planets[indices[i]];
                        var b = planets[indices[j]];
                        var c = planets[indices[k]];
                        if (!HasEdge(edges, a.PlanetId, b.PlanetId, team) ||
                            !HasEdge(edges, b.PlanetId, c.PlanetId, team) ||
                            !HasEdge(edges, a.PlanetId, c.PlanetId, team))
                            continue;

                        // [TITAN-ORBIT] Skip fills that would invent a long opposite-side chord.
                        if (!IsShortEmbeddableTriangle(a.Position, b.Position, c.Position, mapW, mapH))
                            continue;

                        TryAddTriangle(ref triangles, a, b, c, team);
                    }
                }
            }

            // --- Phase B: keep facial triangles only (empty same-team interior) ---
            CullTrianglesWithInteriorTeammates(planets, indices, mapW, mapH, teamTriStart, ref triangles);
        }

        /// <summary>
        /// Removes this team's triangles that contain another owned teammate inside the fill.
        /// Corners of the triangle itself are ignored. Enemy planets inside do <b>not</b> cull —
        /// multi-team overlap is intentional elsewhere.
        /// <para>
        /// [TITAN-ORBIT] Runs after clique discovery on each rebuild (including planet capture).
        /// Edges pack in the same <see cref="RebuildFullGraph"/> pass before this runs, so an
        /// interior capture that connects to the three corners already has ABD/BCD/CAD candidates
        /// when ABC is culled — no lasting hole, and no stacked opaque fill / corner bonus.
        /// Order of remaining triangles does not matter (swap-back removal).
        /// </para>
        /// </summary>
        /// <param name="planets">Full planet array (positions for PIT).</param>
        /// <param name="indices">This team's planet indices into <paramref name="planets"/>.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="teamTriStart">First index in <paramref name="triangles"/> owned by this pass.</param>
        /// <param name="triangles">Mutable triangle list; entries in <c>[teamTriStart, Length)</c> may drop.</param>
        static void CullTrianglesWithInteriorTeammates(
            in NativeArray<PlanetInput> planets,
            in NativeList<int> indices,
            float mapW,
            float mapH,
            int teamTriStart,
            ref NativeList<Triangle> triangles)
        {
            // Need at least one candidate triangle and a fourth teammate that could sit inside it.
            if (triangles.Length <= teamTriStart || indices.Length < 4)
                return;

            // --- Walk newest→oldest so RemoveAtSwapBack never skips an unchecked entry ---
            for (int t = triangles.Length - 1; t >= teamTriStart; t--)
            {
                var tri = triangles[t];

                // --- Resolve the three corner centers (canonical planet positions) ---
                if (!TryGetPlanetPosition(planets, tri.PlanetIdA, out float3 va) ||
                    !TryGetPlanetPosition(planets, tri.PlanetIdB, out float3 vb) ||
                    !TryGetPlanetPosition(planets, tri.PlanetIdC, out float3 vc))
                    continue;

                // --- Any other teammate inside this fill? Then this is a nested shell, not a face ---
                bool hasInteriorTeammate = false;
                for (int i = 0; i < indices.Length; i++)
                {
                    var p = planets[indices[i]];

                    // Skip the three corners — they lie on the boundary by definition.
                    if (p.PlanetId == tri.PlanetIdA ||
                        p.PlanetId == tri.PlanetIdB ||
                        p.PlanetId == tri.PlanetIdC)
                        continue;

                    // [TITAN-ORBIT] Same geodesic PIT the fill / ship boost use — seam-safe on the torus.
                    if (PointInTriangleXZ(p.Position, va, vb, vc, mapW, mapH))
                    {
                        hasInteriorTeammate = true;
                        break;
                    }
                }

                if (hasInteriorTeammate)
                    triangles.RemoveAtSwapBack(t);
            }
        }

        /// <summary>Looks up a planet center position by id.</summary>
        static bool TryGetPlanetPosition(in NativeArray<PlanetInput> planets, int planetId, out float3 pos)
        {
            pos = default;
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].PlanetId != planetId)
                    continue;
                pos = planets[i].Position;
                return true;
            }

            return false;
        }

        /// <summary>True when two undirected edges share a planet id endpoint.</summary>
        static bool EdgesShareEndpoint(in Edge a, in Edge b) =>
            a.PlanetIdA == b.PlanetIdA || a.PlanetIdA == b.PlanetIdB ||
            a.PlanetIdB == b.PlanetIdA || a.PlanetIdB == b.PlanetIdB;

        /// <summary>True when undirected edge exists for this team.</summary>
        static bool HasEdge(in NativeList<Edge> edges, int a, int b, TeamId team)
        {
            CanonicalEdgeIds(a, b, out int lo, out int hi);
            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                if (e.Team == team && e.PlanetIdA == lo && e.PlanetIdB == hi)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True if a new edge between a–b would conflict with <b>any</b> existing edge on the map
        /// (any team) — proper cross or collinear interior overlap. Shared endpoints are allowed.
        /// </summary>
        static bool EdgeWouldCrossAny(
            in NativeArray<PlanetInput> planets,
            in NativeList<Edge> edges,
            int aId,
            int bId,
            float mapW,
            float mapH)
        {
            if (!TryGetPlanetPosition(planets, aId, out float3 a) ||
                !TryGetPlanetPosition(planets, bId, out float3 b))
                return true;

            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                if (e.PlanetIdA == aId || e.PlanetIdA == bId ||
                    e.PlanetIdB == aId || e.PlanetIdB == bId)
                    continue;

                if (!TryGetPlanetPosition(planets, e.PlanetIdA, out float3 c) ||
                    !TryGetPlanetPosition(planets, e.PlanetIdB, out float3 d))
                    continue;

                if (SegmentsCrossToroidalXZ(a, b, c, d, mapW, mapH))
                    return true;
            }

            return false;
        }

        /// <summary>Adds a new edge with the next creation sequence (canonical id order).</summary>
        static void AddNewEdge(
            ref NativeList<Edge> edges,
            int a,
            int b,
            TeamId team,
            ref uint nextSequence)
        {
            if (a == b)
                return;
            CanonicalEdgeIds(a, b, out int lo, out int hi);
            if (HasEdge(edges, lo, hi, team))
                return;

            uint seq = nextSequence;
            nextSequence = seq + 1;
            edges.Add(new Edge
            {
                PlanetIdA = lo,
                PlanetIdB = hi,
                Team = team,
                CreationSequence = seq,
            });
        }

        /// <summary>Adds an edge with its exact sequence if missing (sticky seed / restore).</summary>
        static void TryAddEdgeExact(ref NativeList<Edge> edges, in Edge edge)
        {
            if (edge.PlanetIdA == edge.PlanetIdB)
                return;

            CanonicalEdgeIds(edge.PlanetIdA, edge.PlanetIdB, out int lo, out int hi);
            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                if (e.Team == edge.Team && e.PlanetIdA == lo && e.PlanetIdB == hi)
                    return;
            }

            edges.Add(new Edge
            {
                PlanetIdA = lo,
                PlanetIdB = hi,
                Team = edge.Team,
                CreationSequence = edge.CreationSequence,
            });
        }

        /// <summary>Adds a triangle if the three planet ids are not already present for this team.</summary>
        static void TryAddTriangle(
            ref NativeList<Triangle> triangles,
            in PlanetInput a,
            in PlanetInput b,
            in PlanetInput c,
            TeamId team)
        {
            // --- Canonical id order for duplicate checks ---
            int id0 = a.PlanetId;
            int id1 = b.PlanetId;
            int id2 = c.PlanetId;
            Sort3(ref id0, ref id1, ref id2);

            for (int i = 0; i < triangles.Length; i++)
            {
                var t = triangles[i];
                if (t.Team != team)
                    continue;
                int x = t.PlanetIdA, y = t.PlanetIdB, z = t.PlanetIdC;
                Sort3(ref x, ref y, ref z);
                if (x == id0 && y == id1 && z == id2)
                    return;
            }

            float avg = (math.max(1, a.PlanetLevel) + math.max(1, b.PlanetLevel) + math.max(1, c.PlanetLevel)) / 3f;
            triangles.Add(new Triangle
            {
                PlanetIdA = a.PlanetId,
                PlanetIdB = b.PlanetId,
                PlanetIdC = c.PlanetId,
                Team = team,
                AverageLevel = avg,
                GemBonusMultiplier = 1f + avg * PerLevelGemBonusFraction,
            });
        }

        /// <summary>Canonical undirected edge ids (lo ≤ hi).</summary>
        static void CanonicalEdgeIds(int a, int b, out int lo, out int hi)
        {
            if (a <= b)
            {
                lo = a;
                hi = b;
            }
            else
            {
                lo = b;
                hi = a;
            }
        }

        /// <summary>True when (a,b) matches undirected side (x,y).</summary>
        static bool EdgeMatchesTriangleSide(int a, int b, int x, int y)
        {
            CanonicalEdgeIds(x, y, out int lo, out int hi);
            return a == lo && b == hi;
        }

        /// <summary>Sorts three ints ascending in place.</summary>
        static void Sort3(ref int a, ref int b, ref int c)
        {
            if (a > b) { int t = a; a = b; b = t; }
            if (b > c) { int t = b; b = c; c = t; }
            if (a > b) { int t = a; a = b; b = t; }
        }
    }
}
