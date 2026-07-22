using TitanOrbit.Core;
using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Pure toroidal planet-connection graph math for same-team territory triangles.
    /// Port of the NGO <c>PlanetConnectionSystem</c> rebuild rule: each owned planet forms one
    /// triangle with its two closest teammates (lines may cross). Point-in-triangle uses
    /// shortest-path unwrap so territories work across map seams.
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
            /// Gem-moon XZ in canonical toroidal space (Y ignored) for nearest-neighbor edges.
            /// [TITAN-ORBIT] Must be moon vertices, not planet centers — matches drawn triangles.
            /// </summary>
            public float3 Position;

            /// <summary>True for team home worlds — used when resolving home level for bonuses.</summary>
            public bool IsHomePlanet;
        }

        /// <summary>Undirected same-team edge between two planet ids.</summary>
        public struct Edge
        {
            public int PlanetIdA;
            public int PlanetIdB;
            public TeamId Team;
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
        /// Runtime triangle with live gem-moon vertices for point-in-triangle and drawing.
        /// Topology comes from <see cref="Triangle"/>; positions update every tick as moons orbit.
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
        /// Clears and rebuilds edges/triangles for every team present in <paramref name="planets"/>.
        /// Rule: for each owned planet P, connect to the two closest teammates and form (P,Q,R).
        /// </summary>
        /// <param name="planets">All planets (neutral entries are skipped).</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="edges">Destination edge list (cleared).</param>
        /// <param name="triangles">Destination triangle list (cleared).</param>
        public static void RebuildFullGraph(
            in NativeArray<PlanetInput> planets,
            float mapW,
            float mapH,
            ref NativeList<Edge> edges,
            ref NativeList<Triangle> triangles)
        {
            edges.Clear();
            triangles.Clear();
            if (!planets.IsCreated || planets.Length < 3)
                return;

            // --- Collect distinct non-None teams ---
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
                RebuildTeamGraph(planets, team, mapW, mapH, ref edges, ref triangles);
            }
        }

        /// <summary>
        /// Adds edges/triangles for one team using each planet's two closest teammates.
        /// <para>
        /// [TITAN-ORBIT] For every owned planet P, find the two nearest teammates Q and R by
        /// toroidal distance on <see cref="PlanetInput.Position"/> (gem-moon XZ), then form
        /// triangle (P,Q,R). Several planets can propose the same three corners — 
        /// <see cref="TryAddTriangle"/> and <see cref="TryAddEdge"/> dedupe by sorted planet ids
        /// so we never store a duplicate.
        /// </para>
        /// <para>
        /// Intentional (not mutual nearest-neighbor): a far capture still gets a triangle with
        /// its two closest friends even when those friends prefer closer neighbors. Mutual
        /// filtering left some captured planets with no connection at all.
        /// </para>
        /// </summary>
        public static void RebuildTeamGraph(
            in NativeArray<PlanetInput> planets,
            TeamId team,
            float mapW,
            float mapH,
            ref NativeList<Edge> edges,
            ref NativeList<Triangle> triangles)
        {
            if (team == TeamId.None || !planets.IsCreated)
                return;

            // --- Count teammates ---
            // Need at least three same-team planets to form any triangle.
            int n = 0;
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].Team == team)
                    n++;
            }

            if (n < 3)
                return;

            // --- Compact teammate indices into a temp list ---
            // [STANDARD] Allocator.Temp — freed before this method returns (Burst-friendly).
            var indices = new NativeList<int>(n, Allocator.Temp);
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].Team == team)
                    indices.Add(i);
            }

            // --- One triangle per planet: connect P to its two closest teammates ---
            // [TITAN-ORBIT] Distances use gem-moon positions (matches drawn lines), toroidal so
            // neighbors across the map seam still win when they are truly closest.
            for (int pi = 0; pi < indices.Length; pi++)
            {
                int pIdx = indices[pi];
                FindTwoClosest(planets, indices, pIdx, mapW, mapH, out int qIdx, out int rIdx);
                if (qIdx < 0 || rIdx < 0)
                    continue;

                var p = planets[pIdx];
                var q = planets[qIdx];
                var r = planets[rIdx];

                // Edges + triangle; helpers no-op when the same undirected set already exists.
                TryAddEdge(ref edges, p.PlanetId, q.PlanetId, team);
                TryAddEdge(ref edges, p.PlanetId, r.PlanetId, team);
                TryAddEdge(ref edges, q.PlanetId, r.PlanetId, team);
                TryAddTriangle(ref triangles, p, q, r, team);
            }

            indices.Dispose();
        }

        /// <summary>
        /// True when <paramref name="worldPos"/> lies inside the triangle after toroidal unwrap
        /// (anchor A at origin, B/C/P as shortest offsets from A).
        /// </summary>
        public static bool PointInTriangleXZ(
            float3 worldPos,
            float3 vertexA,
            float3 vertexB,
            float3 vertexC,
            float mapW,
            float mapH)
        {
            // --- Unwrap into local XZ with A at origin ---
            // [TITAN-ORBIT] Same as NGO PointInTriangleXZCanonical — required across seams.
            float2 a = float2.zero;
            float3 offB = ToroidalMapEcs.ShortestOffsetXZ(vertexA, vertexB, mapW, mapH);
            float3 offC = ToroidalMapEcs.ShortestOffsetXZ(vertexA, vertexC, mapW, mapH);
            float3 offP = ToroidalMapEcs.ShortestOffsetXZ(vertexA, worldPos, mapW, mapH);
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
        /// <param name="runtime">Live moon-vertex triangles.</param>
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
        /// Gem mining / destroy multiplier when the ship's team is one of the asteroid's
        /// territory owners (mask bit set). Enemy-only rocks return 1 (no yellow bonus).
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

        /// <summary>2D cross product (x*y components) for barycentric triangle tests.</summary>
        static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        /// <summary>Finds the two nearest teammates to <paramref name="pIdx"/> by toroidal distance.</summary>
        static void FindTwoClosest(
            in NativeArray<PlanetInput> planets,
            in NativeList<int> teammateIndices,
            int pIdx,
            float mapW,
            float mapH,
            out int qIdx,
            out int rIdx)
        {
            qIdx = -1;
            rIdx = -1;
            float bestQ = float.MaxValue;
            float bestR = float.MaxValue;
            float3 pPos = planets[pIdx].Position;

            for (int i = 0; i < teammateIndices.Length; i++)
            {
                int idx = teammateIndices[i];
                if (idx == pIdx)
                    continue;

                float d = ToroidalMapEcs.ToroidalDistance(pPos, planets[idx].Position, mapW, mapH);
                if (d < bestQ)
                {
                    bestR = bestQ;
                    rIdx = qIdx;
                    bestQ = d;
                    qIdx = idx;
                }
                else if (d < bestR)
                {
                    bestR = d;
                    rIdx = idx;
                }
            }
        }

        /// <summary>Adds an undirected edge if missing.</summary>
        static void TryAddEdge(ref NativeList<Edge> edges, int a, int b, TeamId team)
        {
            if (a == b)
                return;
            if (a > b)
            {
                int tmp = a;
                a = b;
                b = tmp;
            }

            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                if (e.Team == team && e.PlanetIdA == a && e.PlanetIdB == b)
                    return;
            }

            edges.Add(new Edge { PlanetIdA = a, PlanetIdB = b, Team = team });
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

        /// <summary>Sorts three ints ascending in place.</summary>
        static void Sort3(ref int a, ref int b, ref int c)
        {
            if (a > b) { int t = a; a = b; b = t; }
            if (b > c) { int t = b; b = c; c = t; }
            if (a > b) { int t = a; a = b; b = t; }
        }
    }
}
