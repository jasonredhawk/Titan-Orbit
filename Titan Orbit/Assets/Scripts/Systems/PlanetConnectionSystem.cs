using System;
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
        [SerializeField] private float recomputeInterval = 0.5f;

        [Header("Bonuses")]
        [Tooltip("Per‑planet max population and growth bonus per triangle, as a fraction (e.g. 0.05 = +5% per effective triangle).")]
        [SerializeField] private float perTrianglePlanetBonusFraction = 0.05f;
        [Tooltip("Per‑level gem bonus inside a triangle. 0.05 = +5% per average planet level.")]
        [SerializeField] private float perLevelGemBonusFraction = 0.05f;

        private readonly List<PlanetEdge> edges = new List<PlanetEdge>();
        private readonly List<PlanetTriangle> triangles = new List<PlanetTriangle>();
        private readonly Dictionary<Planet, TeamManager.Team> _previousTeamPerPlanet = new Dictionary<Planet, TeamManager.Team>();
        private readonly Dictionary<Planet, float> planetBonusByPlanet = new Dictionary<Planet, float>();
        private float lastRecomputeTime = -999f;

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
            Planet[] allPlanets = FindObjectsOfType<Planet>();
            if (allPlanets == null || allPlanets.Length == 0)
                return;

            // Detect ownership changes and update persistent edges/triangles (capture-driven).
            foreach (Planet p in allPlanets)
            {
                if (p == null) continue;
                TeamManager.Team current = p.TeamOwnership;
                _previousTeamPerPlanet.TryGetValue(p, out TeamManager.Team previous);

                if (current != previous)
                {
                    if (previous != TeamManager.Team.None)
                        RemoveEdgesAndTrianglesContaining(p, previous);
                    if (current != TeamManager.Team.None)
                        OnPlanetCaptured(p, current);
                    _previousTeamPerPlanet[p] = current;
                }
            }

            // Clean stale entries (destroyed planets).
            var toRemove = _previousTeamPerPlanet.Keys.Where(k => k == null).ToList();
            foreach (var k in toRemove)
                _previousTeamPerPlanet.Remove(k);
            edges.RemoveAll(e => e.A == null || e.B == null);
            triangles.RemoveAll(t => t.A == null || t.B == null || t.C == null);

            // Recompute bonuses from persistent triangles.
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

        private void RemoveEdgesAndTrianglesContaining(Planet planet, TeamManager.Team team)
        {
            edges.RemoveAll(e => e.Team == team && (e.A == planet || e.B == planet));
            triangles.RemoveAll(t => t.Team == team && (t.A == planet || t.B == planet || t.C == planet));
        }

        private void OnPlanetCaptured(Planet newPlanet, TeamManager.Team team)
        {
            List<Planet> others = new List<Planet>();
            foreach (var p in FindObjectsOfType<Planet>())
            {
                if (p == null || p == newPlanet || p.TeamOwnership != team) continue;
                others.Add(p);
            }

            if (others.Count == 0) return;
            if (others.Count == 1)
            {
                AddEdge(newPlanet, others[0], team);
                return;
            }

            Vector2 newPos = new Vector2(newPlanet.ToroidalPosition.x, newPlanet.ToroidalPosition.z);
            newPos = ToroidalMapWrapXZ(newPos);
            PlanetTriangle? containing = null;
            foreach (var t in triangles)
            {
                if (t.Team != team) continue;
                if (PointInTriangleXZCanonical(newPos, t))
                {
                    containing = t;
                    break;
                }
            }

            if (containing.HasValue)
            {
                PlanetTriangle tri = containing.Value;
                triangles.Remove(tri);
                AddEdge(newPlanet, tri.A, team);
                AddEdge(newPlanet, tri.B, team);
                AddEdge(newPlanet, tri.C, team);
                AddTriangle(newPlanet, tri.A, tri.B, team);
                AddTriangle(newPlanet, tri.B, tri.C, team);
                AddTriangle(newPlanet, tri.C, tri.A, team);
                return;
            }

            // Not inside any triangle: add one new triangle to the two closest team planets.
            var sorted = others
                .Select(o => (planet: o, dist: ToroidalMap.ToroidalDistance(newPlanet.ToroidalPosition, o.ToroidalPosition)))
                .OrderBy(x => x.dist)
                .ToList();
            Planet closest = sorted[0].planet;
            Planet secondClosest = sorted[1].planet;
            AddEdge(newPlanet, closest, team);
            AddEdge(newPlanet, secondClosest, team);
            if (!HasEdge(closest, secondClosest, team))
                AddEdge(closest, secondClosest, team);
            AddTriangle(newPlanet, closest, secondClosest, team);
        }

        private bool HasEdge(Planet a, Planet b, TeamManager.Team team)
        {
            return edges.Exists(e => e.Team == team && ((e.A == a && e.B == b) || (e.A == b && e.B == a)));
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

