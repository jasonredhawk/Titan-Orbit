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
        /// Adds edges/triangles for one team using <b>mutual</b> two-closest moons in toroidal space.
        /// <para>
        /// For each owned planet P, find the two nearest teammates Q,R by
        /// <see cref="PlanetInput.Position"/> (gem-moon XZ, toroidal distance). Add triangle (P,Q,R)
        /// only when P is also among Q's two nearest <b>and</b> among R's two nearest.
        /// That keeps territories on the locally closest cluster and prevents a far capture from
        /// stretching a huge triangle across the map.
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
            int n = 0;
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].Team == team)
                    n++;
            }

            if (n < 3)
                return;

            // --- Compact teammate indices into a temp list ---
            var indices = new NativeList<int>(n, Allocator.Temp);
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].Team == team)
                    indices.Add(i);
            }

            // --- Precompute each planet's two nearest teammate indices (toroidal moon distance) ---
            var nearA = new NativeArray<int>(indices.Length, Allocator.Temp);
            var nearB = new NativeArray<int>(indices.Length, Allocator.Temp);
            for (int i = 0; i < indices.Length; i++)
            {
                FindTwoClosest(planets, indices, indices[i], mapW, mapH, out int qIdx, out int rIdx);
                nearA[i] = qIdx;
                nearB[i] = rIdx;
            }

            // --- Mutual nearest: only triangles where all three claim each other as closest ---
            for (int pi = 0; pi < indices.Length; pi++)
            {
                int pIdx = indices[pi];
                int qIdx = nearA[pi];
                int rIdx = nearB[pi];
                if (qIdx < 0 || rIdx < 0)
                    continue;

                // P must be among Q's two nearest and among R's two nearest.
                if (!IsAmongTwoClosest(indices, nearA, nearB, qIdx, pIdx))
                    continue;
                if (!IsAmongTwoClosest(indices, nearA, nearB, rIdx, pIdx))
                    continue;

                var p = planets[pIdx];
                var q = planets[qIdx];
                var r = planets[rIdx];
                TryAddEdge(ref edges, p.PlanetId, q.PlanetId, team);
                TryAddEdge(ref edges, p.PlanetId, r.PlanetId, team);
                TryAddEdge(ref edges, q.PlanetId, r.PlanetId, team);
                TryAddTriangle(ref triangles, p, q, r, team);
            }

            nearA.Dispose();
            nearB.Dispose();
            indices.Dispose();
        }

        /// <summary>
        /// True when <paramref name="candidateIdx"/> is one of the two nearest teammates stored for
        /// planet <paramref name="ownerIdx"/> in the parallel nearA/nearB tables.
        /// </summary>
        static bool IsAmongTwoClosest(
            in NativeList<int> teammateIndices,
            in NativeArray<int> nearA,
            in NativeArray<int> nearB,
            int ownerIdx,
            int candidateIdx)
        {
            for (int i = 0; i < teammateIndices.Length; i++)
            {
                if (teammateIndices[i] != ownerIdx)
                    continue;
                return nearA[i] == candidateIdx || nearB[i] == candidateIdx;
            }

            return false;
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
        /// </summary>
        public static TeamId GetTeamAtPosition(
            float3 worldPos,
            in NativeArray<RuntimeTriangle> runtime,
            float mapW,
            float mapH)
        {
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
        /// Friendly territory movement multiplier: <c>1 + 0.05 × homeLevel</c> when the ship sits
        /// inside a triangle owned by <paramref name="shipTeam"/>; otherwise 1.
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

            TeamId teamAt = GetTeamAtPosition(shipPos, runtime, mapW, mapH);
            if (teamAt != shipTeam)
                return 1f;

            int homeLevel = GetHomePlanetLevel(shipTeam, homeLevelByTeamIndex);
            return 1f + PerLevelGemBonusFraction * homeLevel;
        }

        /// <summary>
        /// Gem mining multiplier when the asteroid's territory team matches the mining ship.
        /// </summary>
        public static float FriendlyTerritoryGemMultiplier(TeamId shipTeam, TeamId asteroidTerritoryTeam, int homePlanetLevel)
        {
            if (shipTeam == TeamId.None ||
                asteroidTerritoryTeam == TeamId.None ||
                shipTeam != asteroidTerritoryTeam)
                return 1f;

            int level = math.max(1, homePlanetLevel);
            return 1f + PerLevelGemBonusFraction * level;
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
