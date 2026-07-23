using System.Collections.Generic;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Draws semi-transparent team territory triangles and lone sticky edges in world
    /// space (vertices at each <b>planet center</b>). Reads topology from
    /// <see cref="PlanetConnectionGraphCache"/> — never runs planet/asteroid ECS gathers.
    /// <para>
    /// [TITAN-ORBIT] Planet centers (not gem moons) keep lines from crossing as moons orbit and
    /// let us rebuild the draw cache only when topology publishes — not every frame / 30 Hz.
    /// Vertices are wrapped to canonical XZ, unwrapped via shortest-path offsets from the anchor,
    /// then retiled near the camera for seam-correct display.
    /// </para>
    /// Client presentation only.
    /// </summary>
    [ExecuteAlways]
    public class PlanetConnectionShapesVisual : ImmediateModeShapeDrawer
    {
        /// <summary>Y height of the flat triangle fill (slightly below the play plane).</summary>
        [SerializeField] float triangleHeight = -0.6f;

        /// <summary>Fill alpha for territory triangles (original ~0.04).</summary>
        [SerializeField] float triangleAlpha = 0.04f;

        /// <summary>Border thickness in meters.</summary>
        [SerializeField] float triangleBorderThickness = 0.15f;

        /// <summary>Border alpha (original ~0.22).</summary>
        [SerializeField] float triangleBorderAlpha = 0.22f;

        /// <summary>Cached canonical triangle (anchor + B/C offsets + colours).</summary>
        struct CachedWorldTriangle
        {
            public Vector3 Anchor;
            public Vector3 OffsetB;
            public Vector3 OffsetC;
            public Color Fill;
            public Color Border;
        }

        /// <summary>
        /// Cached lone edge (not a triangle side) — anchor + shortest offset to the other planet.
        /// </summary>
        struct CachedWorldEdge
        {
            public Vector3 Anchor;
            public Vector3 OffsetB;
            public Color Color;
        }

        readonly List<CachedWorldTriangle> _worldCache = new List<CachedWorldTriangle>(16);
        readonly List<CachedWorldEdge> _edgeCache = new List<CachedWorldEdge>(16);
        int _lastGraphRevision = -1;

        /// <summary>
        /// Ensures a drawer exists under <c>PlanetConnectionSystems</c> when the client is in-game.
        /// </summary>
        public static void EnsureExists()
        {
            var go = GameObject.Find("PlanetConnectionSystems");
            if (go == null)
                go = new GameObject("PlanetConnectionSystems");

            if (go.GetComponent<PlanetConnectionShapesVisual>() == null)
                go.AddComponent<PlanetConnectionShapesVisual>();
        }

        /// <summary>
        /// [UNITY] Shapes draw callback — fills + borders for every published triangle, lone edges,
        /// plus wrap copies near seams. Retiles every frame; planet verts refresh only on topology.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            if (cam == null)
                return;

            var triangles = PlanetConnectionGraphCache.CurrentTriangles;
            var edges = PlanetConnectionGraphCache.CurrentEdges;
            int triCount = triangles?.Count ?? 0;
            int edgeCount = edges?.Count ?? 0;
            if (triCount == 0 && edgeCount == 0)
                return;

            // Planet centers are fixed — rebuild draw cache only when graph topology publishes.
            int revision = PlanetConnectionGraphCache.ClientPublishRevision;
            if (revision != _lastGraphRevision)
            {
                RebuildWorldCache();
                _lastGraphRevision = revision;
            }

            if (_worldCache.Count == 0 && _edgeCache.Count == 0)
                return;

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.LineGeometry = LineGeometry.Flat2D;

                Vector3 referencePos = ResolveDisplayReference(cam, em);
                float mapW = ToroidalMap.GetMapWidth();
                float mapH = ToroidalMap.GetMapHeight();

                for (int i = 0; i < _worldCache.Count; i++)
                {
                    var tri = _worldCache[i];
                    Vector3 a = ToroidalMap.GetDisplayPosition(tri.Anchor, referencePos);
                    Vector3 b = a + tri.OffsetB;
                    Vector3 c = a + tri.OffsetC;
                    a.y = triangleHeight;
                    b.y = triangleHeight;
                    c.y = triangleHeight;
                    DrawTriangleWithWraps(a, b, c, mapW, mapH, tri.Fill, tri.Border);
                }

                for (int i = 0; i < _edgeCache.Count; i++)
                {
                    var edge = _edgeCache[i];
                    Vector3 a = ToroidalMap.GetDisplayPosition(edge.Anchor, referencePos);
                    Vector3 b = a + edge.OffsetB;
                    a.y = triangleHeight;
                    b.y = triangleHeight;
                    DrawEdgeWithWraps(a, b, mapW, mapH, edge.Color);
                }
            }
        }

        /// <summary>Resolves planet-center vertices into canonical world space.</summary>
        void RebuildWorldCache()
        {
            _worldCache.Clear();
            _edgeCache.Clear();

            var triangles = PlanetConnectionGraphCache.CurrentTriangles;
            var edges = PlanetConnectionGraphCache.CurrentEdges;
            int triCount = triangles?.Count ?? 0;
            int edgeCount = edges?.Count ?? 0;
            if (triCount == 0 && edgeCount == 0)
                return;

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;
            var visualizer = EcsWorldVisualizer.Active;

            for (int i = 0; i < triCount; i++)
            {
                var tri = triangles[i];
                if (!TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdA, out Vector3 aCanon) ||
                    !TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdB, out Vector3 bCanon) ||
                    !TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdC, out Vector3 cCanon))
                    continue;

                int idA = tri.PlanetIdA, idB = tri.PlanetIdB, idC = tri.PlanetIdC;
                Vector3 anchor = aCanon, bPos = bCanon, cPos = cCanon;
                if (idB <= idA && idB <= idC)
                {
                    anchor = bCanon;
                    bPos = aCanon;
                    cPos = cCanon;
                }
                else if (idC <= idA && idC <= idB)
                {
                    anchor = cCanon;
                    bPos = aCanon;
                    cPos = bCanon;
                }

                Color baseColor = tri.Team.ToColor();
                _worldCache.Add(new CachedWorldTriangle
                {
                    Anchor = anchor,
                    OffsetB = ToroidalMap.ShortestWorldOffsetXZ(anchor, bPos),
                    OffsetC = ToroidalMap.ShortestWorldOffsetXZ(anchor, cPos),
                    Fill = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha),
                    Border = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha),
                });
            }

            for (int i = 0; i < edgeCount; i++)
            {
                var edge = edges[i];
                if (PlanetConnectionGraphLogic.EdgeIsTriangleSide(
                        edge.PlanetIdA, edge.PlanetIdB, edge.Team, triangles))
                    continue;

                if (!TryGetCanonicalPlanetVertex(em, visualizer, edge.PlanetIdA, out Vector3 aCanon) ||
                    !TryGetCanonicalPlanetVertex(em, visualizer, edge.PlanetIdB, out Vector3 bCanon))
                    continue;

                Vector3 anchor = aCanon;
                Vector3 other = bCanon;
                if (edge.PlanetIdB < edge.PlanetIdA)
                {
                    anchor = bCanon;
                    other = aCanon;
                }

                Color baseColor = edge.Team.ToColor();
                _edgeCache.Add(new CachedWorldEdge
                {
                    Anchor = anchor,
                    OffsetB = ToroidalMap.ShortestWorldOffsetXZ(anchor, other),
                    Color = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha),
                });
            }
        }

        /// <summary>
        /// Draws a triangle and ±map-tile copies only when a vertex sits near a wrap seam.
        /// </summary>
        void DrawTriangleWithWraps(
            Vector3 a, Vector3 b, Vector3 c, float mapW, float mapH, Color fillColor, Color borderColor)
        {
            Draw.Triangle(a, b, c, fillColor);
            Draw.TriangleBorder(a, b, c, triangleBorderThickness, borderColor);

            if (!NeedsWrapCopies(a, b, c, mapW, mapH))
                return;

            Vector3[] offsets =
            {
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH),
            };
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 off = offsets[i];
                Draw.Triangle(a + off, b + off, c + off, fillColor);
                Draw.TriangleBorder(a + off, b + off, c + off, triangleBorderThickness, borderColor);
            }
        }

        /// <summary>Draws a lone edge line with optional seam wrap copies.</summary>
        void DrawEdgeWithWraps(Vector3 a, Vector3 b, float mapW, float mapH, Color color)
        {
            Draw.Line(a, b, triangleBorderThickness, color);

            if (!NeedsWrapCopies(a, b, a, mapW, mapH))
                return;

            Vector3[] offsets =
            {
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH),
            };
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 off = offsets[i];
                Draw.Line(a + off, b + off, triangleBorderThickness, color);
            }
        }

        /// <summary>True when any vertex is within a margin of a map edge (seam-visible case).</summary>
        static bool NeedsWrapCopies(Vector3 a, Vector3 b, Vector3 c, float mapW, float mapH)
        {
            float margin = math.max(20f, 0.08f * math.min(mapW, mapH));
            return NearEdge(a, mapW, mapH, margin) ||
                   NearEdge(b, mapW, mapH, margin) ||
                   NearEdge(c, mapW, mapH, margin);
        }

        /// <summary>Canonical XZ near 0 or map extent.</summary>
        static bool NearEdge(Vector3 p, float mapW, float mapH, float margin)
        {
            Vector3 w = ToroidalMap.WrapPosition(p);
            return w.x < margin || w.x > mapW - margin ||
                   w.z < margin || w.z > mapH - margin;
        }

        /// <summary>
        /// Local ship presentation pose when available — keeps triangles retiled with the rest of the map.
        /// </summary>
        static Vector3 ResolveDisplayReference(Camera cam, EntityManager em)
        {
            if (EcsGameBridge.TryGetLocalShipPresentationPosition(out Vector3 shipPos))
            {
                shipPos.y = 0f;
                return shipPos;
            }

            Vector3 camPos = cam.transform.position;
            camPos.y = 0f;
            return camPos;
        }

        /// <summary>
        /// Planet core XZ wrapped into canonical toroidal space (Y forced to 0).
        /// Uses hybrid visualizer planet pose (quarantine-safe) when available.
        /// </summary>
        public static bool TryGetCanonicalPlanetVertex(
            EntityManager em,
            EcsWorldVisualizer visualizer,
            int planetId,
            out Vector3 planetCanonical)
        {
            planetCanonical = default;
            if (planetId == 0)
                return false;

            if (visualizer == null ||
                !visualizer.TryGetPlanetPoseByPlanetId(
                    em, planetId, out float3 planetPos, out _, out _))
                return false;

            planetPos.y = 0f;
            Vector3 raw = new Vector3(planetPos.x, 0f, planetPos.z);
            planetCanonical = ToroidalMap.WrapPosition(raw);
            planetCanonical.y = 0f;
            return true;
        }

        /// <summary>
        /// [LEGACY] Old moon-vertex helper — territory connections now use
        /// <see cref="TryGetCanonicalPlanetVertex"/>. Kept so any stray call sites still compile.
        /// </summary>
        public static bool TryGetCanonicalMoonVertex(
            EntityManager em,
            EcsWorldVisualizer visualizer,
            int planetId,
            double moonElapsed,
            out Vector3 moonCanonical) =>
            TryGetCanonicalPlanetVertex(em, visualizer, planetId, out moonCanonical);
    }
}
