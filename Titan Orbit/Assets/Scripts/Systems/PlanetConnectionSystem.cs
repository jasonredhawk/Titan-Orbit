using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
        /// <summary>
        /// Maintains persistent, capture-driven connections between same‑team planets.
        /// Triangles and edges are only added when a planet is captured; they do not shift or recompute.
        /// Rules: new capture adds one triangle to the two closest team planets; if captured inside an
        /// existing triangle, that triangle is replaced by three new triangles (new planet to each corner).
        /// First law: no edges shall cross. New triangles are only added when all three angles are acute.
        /// </summary>
    public class PlanetConnectionSystem : MonoBehaviour
    {
        public static PlanetConnectionSystem Instance { get; private set; }

        /// <summary>Single edge between two same‑team planets.</summary>
        public struct PlanetEdge
        {
            public Planet A;
            public Planet B;
            public TeamManager.Team Team;
        }

        /// <summary>Triangle territory formed by three same‑team planets.</summary>
        public struct PlanetTriangle
        {
            public Planet A;
            public Planet B;
            public Planet C;
            public TeamManager.Team Team;
            /// <summary>Average of the three planet levels, used for bonus strength.</summary>
            public float AverageLevel;
            /// <summary>Bonus factor applied to gems inside this triangle. 1.0 = no bonus.</summary>
            public float GemBonusMultiplier;
        }

        [Header("Computation")]
        [SerializeField] private float recomputeInterval = 3f;
        [Tooltip("Delay between each planet when animating the rebuild. 0 = one frame per planet (smooth, no lagged blits).")]
        [SerializeField] private float rebuildStepDelay = 0f;

        [Header("Bonuses")]
        [Tooltip("Per‑planet max population and growth bonus per triangle, as a fraction (e.g. 0.05 = +5% per effective triangle).")]
        [SerializeField] private float perTrianglePlanetBonusFraction = 0.05f;
        [Tooltip("Per‑level gem bonus inside a triangle. 0.05 = +5% per average planet level.")]
        [SerializeField] private float perLevelGemBonusFraction = 0.05f;

        private readonly List<PlanetEdge> edges = new List<PlanetEdge>();
        private readonly List<PlanetTriangle> triangles = new List<PlanetTriangle>();
        private readonly Dictionary<Planet, TeamManager.Team> _previousTeamPerPlanet = new Dictionary<Planet, TeamManager.Team>();
        private readonly Dictionary<Planet, float> planetBonusByPlanet = new Dictionary<Planet, float>();
        // Reusable collections to avoid per-recompute allocations (helps idle performance).
        private readonly HashSet<TeamManager.Team> _dirtyTeamsReusable = new HashSet<TeamManager.Team>();
        private readonly List<Planet> _stalePlanetsReusable = new List<Planet>();
        private float lastRecomputeTime = -999f;
        private Coroutine _rebuildCoroutine;

        public IReadOnlyList<PlanetEdge> CurrentEdges => edges;
        public IReadOnlyList<PlanetTriangle> CurrentTriangles => triangles;

        /// <summary>Returns triangle vertices with a stable anchor (smallest PlanetId) so drawing does not flip when camera moves.</summary>
        public static void GetStableTriangleOrder(PlanetTriangle tri, out Planet anchor, out Planet b, out Planet c)
        {
            int idA = tri.A != null ? tri.A.PlanetId : int.MaxValue;
            int idB = tri.B != null ? tri.B.PlanetId : int.MaxValue;
            int idC = tri.C != null ? tri.C.PlanetId : int.MaxValue;
            if (idA <= idB && idA <= idC) { anchor = tri.A; b = tri.B; c = tri.C; return; }
            if (idB <= idA && idB <= idC) { anchor = tri.B; b = tri.A; c = tri.C; return; }
            anchor = tri.C; b = tri.A; c = tri.B;
        }

        /// <summary>Returns edge endpoints with stable order (smallest PlanetId first) so drawing does not flip.</summary>
        public static void GetStableEdgeOrder(PlanetEdge e, out Planet a, out Planet b)
        {
            if (e.A == null) { a = e.B; b = e.A; return; }
            if (e.B == null) { a = e.A; b = e.B; return; }
            if (e.A.PlanetId <= e.B.PlanetId) { a = e.A; b = e.B; return; }
            a = e.B; b = e.A;
        }

        private void Awake()
        {
            BootTrace.Mark("PlanetConnectionSystem.Awake - enter");
            if (Instance != null && Instance != this)
            {
                BootTrace.Mark("PlanetConnectionSystem.Awake - duplicate instance, destroying");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            BootTrace.Mark("PlanetConnectionSystem.Awake - instance set");
            // Ensure recompute interval is not too aggressive in existing scenes (helps idle FPS).
            if (recomputeInterval < 3f)
                recomputeInterval = 3f;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (Time.time - lastRecomputeTime < recomputeInterval)
                return;

            lastRecomputeTime = Time.time;
            BootTrace.Mark("PlanetConnectionSystem.Update - recomputing graph");
            RecomputeGraph();
        }

        private void RecomputeGraph()
        {
            float startTime = Time.realtimeSinceStartup;

            Planet[] allPlanets = FindObjectsOfType<Planet>();
            if (allPlanets == null || allPlanets.Length == 0)
                return;

            // Detect ownership changes and track which teams need a full graph rebuild.
            _dirtyTeamsReusable.Clear();

            foreach (Planet p in allPlanets)
            {
                if (p == null) continue;
                TeamManager.Team current = p.TeamOwnership;
                _previousTeamPerPlanet.TryGetValue(p, out TeamManager.Team previous);

                if (current != previous)
                {
                    if (previous != TeamManager.Team.None)
                    {
                        RemoveEdgesAndTrianglesContaining(p, previous);
                        _dirtyTeamsReusable.Add(previous);
                    }
                    if (current != TeamManager.Team.None)
                    {
                        _dirtyTeamsReusable.Add(current);
                    }
                    _previousTeamPerPlanet[p] = current;
                }
            }

            // Clean stale entries (destroyed planets) without allocating a new list each time.
            _stalePlanetsReusable.Clear();
            foreach (var kvp in _previousTeamPerPlanet)
            {
                if (kvp.Key == null)
                    _stalePlanetsReusable.Add(kvp.Key);
            }
            for (int i = 0; i < _stalePlanetsReusable.Count; i++)
            {
                _previousTeamPerPlanet.Remove(_stalePlanetsReusable[i]);
            }
            edges.RemoveAll(e => e.A == null || e.B == null);
            triangles.RemoveAll(t => t.A == null || t.B == null || t.C == null);

            // When any capture/loss happened: clear ALL links (screen goes blank), then animate rebuild one planet at a time.
            if (_dirtyTeamsReusable.Count > 0)
            {
                if (_rebuildCoroutine != null)
                    StopCoroutine(_rebuildCoroutine);
                edges.Clear();
                triangles.Clear();
                var teamsWithPlanets = new HashSet<TeamManager.Team>();
                foreach (var p in allPlanets)
                {
                    if (p == null) continue;
                    if (p.TeamOwnership != TeamManager.Team.None)
                        teamsWithPlanets.Add(p.TeamOwnership);
                }
                _rebuildCoroutine = StartCoroutine(RebuildAllGraphsAnimated(teamsWithPlanets, allPlanets));
                return;
            }

            ApplyBonusesFromTriangles(allPlanets);
            // #region agent log
            float durMs = (Time.realtimeSinceStartup - startTime) * 1000f;
            DebugSessionLog.Write(
                "PlanetConnectionSystem.RecomputeGraph",
                "edges and triangles",
                "{\"edges\":" + edges.Count + ",\"triangles\":" + triangles.Count + ",\"durationMs\":" + durMs + "}",
                "D");
            // #endregion
        }

        private void ApplyBonusesFromTriangles(Planet[] allPlanets)
        {
            planetBonusByPlanet.Clear();
            foreach (var tri in triangles)
            {
                float triangleStrength = perTrianglePlanetBonusFraction * tri.AverageLevel;
                if (triangleStrength <= 0f) continue;
                AccumulateBonusForPlanet(tri.A, triangleStrength);
                AccumulateBonusForPlanet(tri.B, triangleStrength);
                AccumulateBonusForPlanet(tri.C, triangleStrength);
            }

            bool isServer = Unity.Netcode.NetworkManager.Singleton != null &&
                            Unity.Netcode.NetworkManager.Singleton.IsServer;
            if (isServer)
            {
                foreach (var planet in allPlanets)
                {
                    if (planet == null) continue;
                    float bonus = planetBonusByPlanet.TryGetValue(planet, out float b) ? b : 0f;
                    planet.SetConnectionBonuses(bonus, bonus);
                }
            }
        }

        /// <summary>Animates the rebuild: one planet per frame (or per step delay) so updates are smooth with no lagged blits.</summary>
        private IEnumerator RebuildAllGraphsAnimated(HashSet<TeamManager.Team> teamsWithPlanets, Planet[] allPlanets)
        {
            WaitForSeconds stepWait = rebuildStepDelay > 0f ? new WaitForSeconds(rebuildStepDelay) : null;
            foreach (var team in teamsWithPlanets)
            {
                var teamPlanets = new List<Planet>();
                foreach (var p in allPlanets)
                {
                    if (p == null) continue;
                    if (p.TeamOwnership != team) continue;
                    teamPlanets.Add(p);
                }
                foreach (Planet p in teamPlanets)
                {
                    AddEdgesAndTrianglesForPlanet(p, team, teamPlanets);
                    if (stepWait != null)
                        yield return stepWait;
                    else
                        yield return null; // One frame per planet = smooth, no stutter
                }
            }
            _rebuildCoroutine = null;
            ApplyBonusesFromTriangles(allPlanets);
        }

        private void RemoveEdgesAndTrianglesContaining(Planet planet, TeamManager.Team team)
        {
            edges.RemoveAll(e => e.Team == team && (e.A == planet || e.B == planet));
            triangles.RemoveAll(t => t.Team == team && (t.A == planet || t.B == planet || t.C == planet));
        }

        private bool HasEdge(Planet a, Planet b, TeamManager.Team team)
        {
            return edges.Exists(e => e.Team == team && ((e.A == a && e.B == b) || (e.A == b && e.B == a)));
        }

        /// <summary>
        /// Adds two triangles for planet P with its three closest teammates (Q, R, S): (P,Q,R) and (P,R,S).
        /// Used for animated rebuild so we add one planet's triangles per step. Lines can cross.
        /// </summary>
        private void AddEdgesAndTrianglesForPlanet(Planet p, TeamManager.Team team, List<Planet> teamPlanets)
        {
            var others = teamPlanets
                .Where(o => o != p)
                .OrderBy(o => ToroidalMap.ToroidalDistance(p.ToroidalPosition, o.ToroidalPosition))
                .ToList();
            if (others.Count < 2) return;

            Planet q = others[0];
            Planet r = others[1];
            AddEdge(p, q, team);
            AddEdge(p, r, team);
            AddEdge(q, r, team);
            if (HasEdge(p, q, team) && HasEdge(p, r, team) && HasEdge(q, r, team) && !HasTriangle(p, q, r, team))
                AddTriangle(p, q, r, team);

            if (others.Count < 3) return;
            Planet s = others[2];
            AddEdge(p, s, team);
            AddEdge(r, s, team);
            if (HasEdge(p, r, team) && HasEdge(p, s, team) && HasEdge(r, s, team) && !HasTriangle(p, r, s, team))
                AddTriangle(p, r, s, team);
        }

        /// <summary>
        /// Rebuilds edges and triangles for the given team. All links were already cleared globally.
        /// Rule: for each captured planet P, form two triangles with its three closest other planets (P,Q,R) and (P,R,S). Lines may cross.
        /// </summary>
        private void RebuildTeamGraph(TeamManager.Team team)
        {
            Planet[] allPlanets = FindObjectsOfType<Planet>();
            var teamPlanets = new List<Planet>();
            foreach (var p in allPlanets)
            {
                if (p == null) continue;
                if (p.TeamOwnership != team) continue;
                teamPlanets.Add(p);
            }

            int n = teamPlanets.Count;
            if (n < 2) return;

            foreach (Planet p in teamPlanets)
                AddEdgesAndTrianglesForPlanet(p, team, teamPlanets);
        }

        /// <summary>
        /// True if the segment (a,b) would cross any existing edge that does not share an endpoint
        /// (first law: no lines shall cross). Checks in canonical XZ space.
        /// </summary>
        private bool WouldEdgeCrossExisting(Planet a, Planet b, TeamManager.Team team)
        {
            Vector2 a2 = ToroidalMapWrapXZ(new Vector2(a.ToroidalPosition.x, a.ToroidalPosition.z));
            Vector2 b2 = ToroidalMapWrapXZ(new Vector2(b.ToroidalPosition.x, b.ToroidalPosition.z));
            foreach (var e in edges)
            {
                if (e.A == null || e.B == null) continue;
                if (e.A == a || e.A == b || e.B == a || e.B == b) continue; // share endpoint -> no crossing
                Vector2 c2 = ToroidalMapWrapXZ(new Vector2(e.A.ToroidalPosition.x, e.A.ToroidalPosition.z));
                Vector2 d2 = ToroidalMapWrapXZ(new Vector2(e.B.ToroidalPosition.x, e.B.ToroidalPosition.z));
                if (SegmentsCrossInterior(a2, b2, c2, d2))
                    return true;
            }
            return false;
        }

        /// <summary>True if the two segments intersect at an interior point (not at an endpoint). All coordinates in same local space.</summary>
        private static bool SegmentsCrossInterior(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            Vector2 v1 = p2 - p1;
            Vector2 v2 = p4 - p3;
            float cross = Cross(v1, v2);
            if (Mathf.Approximately(cross, 0f)) return false; // parallel
            Vector2 w = p1 - p3;
            float t = Cross(w, v2) / cross;
            float s = Cross(w, v1) / cross;
            const float eps = 1e-5f;
            return t > eps && t < 1f - eps && s > eps && s < 1f - eps;
        }

        /// <summary>Whether we already have a triangle with these three planets (order-independent).</summary>
        private bool HasTriangle(Planet a, Planet b, Planet c, TeamManager.Team team)
        {
            foreach (var t in triangles)
            {
                if (t.Team != team) continue;
                var set = new HashSet<Planet> { t.A, t.B, t.C };
                if (set.Count != 3) continue;
                if (set.Contains(a) && set.Contains(b) && set.Contains(c)) return true;
            }
            return false;
        }

        /// <summary>After edges are updated, add any new triangles for this team: triples that are fully connected and not yet a triangle.</summary>
        private void TryAddNewTrianglesForTeam(TeamManager.Team team)
        {
            var teamPlanets = new List<Planet>();
            foreach (var e in edges)
            {
                if (e.Team != team) continue;
                if (e.A != null && !teamPlanets.Contains(e.A)) teamPlanets.Add(e.A);
                if (e.B != null && !teamPlanets.Contains(e.B)) teamPlanets.Add(e.B);
            }
            if (teamPlanets.Count < 3) return;
            for (int i = 0; i < teamPlanets.Count; i++)
            {
                Planet a = teamPlanets[i];
                for (int j = i + 1; j < teamPlanets.Count; j++)
                {
                    Planet b = teamPlanets[j];
                    if (!HasEdge(a, b, team)) continue;
                    for (int k = j + 1; k < teamPlanets.Count; k++)
                    {
                        Planet c = teamPlanets[k];
                        if (!HasEdge(b, c, team) || !HasEdge(c, a, team)) continue;
                        if (HasTriangle(a, b, c, team)) continue;
                        AddTriangle(a, b, c, team);
                    }
                }
            }
        }

        private void AddEdge(Planet a, Planet b, TeamManager.Team team)
        {
            if (a == null || b == null || a == b) return;
            if (HasEdge(a, b, team)) return;
            edges.Add(new PlanetEdge { A = a, B = b, Team = team });
        }

        private void AddTriangle(Planet a, Planet b, Planet c, TeamManager.Team team)
        {
            if (a == null || b == null || c == null) return;
            int levelA = a.PlanetLevel, levelB = b.PlanetLevel, levelC = c.PlanetLevel;
            float avgLevel = (levelA + levelB + levelC) / 3f;
            float gemBonusMult = 1f + avgLevel * perLevelGemBonusFraction;
            triangles.Add(new PlanetTriangle
            {
                A = a,
                B = b,
                C = c,
                Team = team,
                AverageLevel = avgLevel,
                GemBonusMultiplier = gemBonusMult
            });
        }

        private void AccumulateBonusForPlanet(Planet planet, float triangleStrength)
        {
            if (planet == null)
                return;

            if (planetBonusByPlanet.TryGetValue(planet, out float existing))
                planetBonusByPlanet[planet] = existing + triangleStrength;
            else
                planetBonusByPlanet[planet] = triangleStrength;
        }

        /// <summary>
        /// Returns the strongest gem bonus multiplier at the given world position based on
        /// any triangle that contains this point in the XZ plane.
        /// 1.0 means no bonus.
        /// </summary>
        public float GetGemBonusMultiplierAtPosition(Vector3 worldPosition)
        {
            if (triangles.Count == 0)
                return 1f;

            Vector2 p = ToroidalMapWrapXZ(new Vector2(worldPosition.x, worldPosition.z));
            float best = 1f;

            foreach (var tri in triangles)
            {
                if (PointInTriangleXZCanonical(p, tri))
                {
                    if (tri.GemBonusMultiplier > best)
                        best = tri.GemBonusMultiplier;
                }
            }

            return best;
        }

        /// <summary>Returns the team that owns the triangle (if any) at given position; Team.None if none.</summary>
        public TeamManager.Team GetTeamAtPosition(Vector3 worldPosition)
        {
            if (triangles.Count == 0)
                return TeamManager.Team.None;

            Vector2 p = ToroidalMapWrapXZ(new Vector2(worldPosition.x, worldPosition.z));
            TeamManager.Team team = TeamManager.Team.None;
            float bestBonus = 0f;

            foreach (var tri in triangles)
            {
                if (!PointInTriangleXZCanonical(p, tri))
                    continue;
                if (tri.GemBonusMultiplier > bestBonus)
                {
                    bestBonus = tri.GemBonusMultiplier;
                    team = tri.Team;
                }
            }

            return team;
        }

        /// <summary>Returns the home planet instance for the given team; null if none.</summary>
        private static HomePlanet FindHomePlanetForTeam(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None) return null;
            foreach (var hp in UnityEngine.Object.FindObjectsByType<HomePlanet>(UnityEngine.FindObjectsSortMode.None))
            {
                if (hp != null && hp.AssignedTeam == team)
                    return hp;
            }
            return null;
        }

        /// <summary>Returns the home planet level for the given team (1 if no home planet found). Used for team-asteroid gem bonus.</summary>
        public static int GetHomePlanetLevelForTeam(TeamManager.Team team)
        {
            HomePlanet home = FindHomePlanetForTeam(team);
            return home != null ? home.PlanetLevel : 1;
        }

        private static bool PointInTriangleXZ(Vector2 p, PlanetTriangle tri)
        {
            Vector3 a3 = tri.A.ToroidalPosition;
            Vector3 b3 = tri.B.ToroidalPosition;
            Vector3 c3 = tri.C.ToroidalPosition;
            Vector2 a = new Vector2(a3.x, a3.z);
            Vector2 b = new Vector2(b3.x, b3.z);
            Vector2 c = new Vector2(c3.x, c3.z);

            float area = Cross(b - a, c - a);
            if (Mathf.Approximately(area, 0f))
                return false;

            float s = Cross(p - a, c - a) / area;
            float t = Cross(b - a, p - a) / area;
            float u = 1f - s - t;

            const float eps = -0.0001f;
            return s >= eps && t >= eps && u >= eps;
        }

        /// <summary>Point-in-triangle test using canonical (wrapped) XZ positions so results are stable on a toroidal map.
        /// Builds the triangle in "local" space with A at origin and B,C at shortest-path offsets so triangles
        /// that wrap the map boundary are tested correctly.</summary>
        private static bool PointInTriangleXZCanonical(Vector2 pCanonical, PlanetTriangle tri)
        {
            Vector3 wa = tri.A.ToroidalPosition;
            Vector3 wb = tri.B.ToroidalPosition;
            Vector3 wc = tri.C.ToroidalPosition;
            Vector3 wp = new Vector3(pCanonical.x, 0f, pCanonical.y);

            // Unwrap triangle: A at origin, B and C at shortest offsets from A
            Vector2 a = Vector2.zero;
            Vector2 b = ToroidalMap.ShortestOffsetXZ(wa, wb);
            Vector2 c = ToroidalMap.ShortestOffsetXZ(wa, wc);
            Vector2 p = ToroidalMap.ShortestOffsetXZ(wa, wp);

            float area = Cross(b - a, c - a);
            if (Mathf.Approximately(area, 0f))
                return false;

            float s = Cross(p - a, c - a) / area;
            float t = Cross(b - a, p - a) / area;
            float u = 1f - s - t;

            const float eps = -0.0001f;
            return s >= eps && t >= eps && u >= eps;
        }

        private static Vector2 ToroidalMapWrapXZ(Vector2 p)
        {
            Vector3 v = ToroidalMap.WrapPosition(new Vector3(p.x, 0f, p.y));
            return new Vector2(v.x, v.z);
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}

