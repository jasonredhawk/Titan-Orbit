using System.Collections.Generic;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Draws semi-transparent team territory fills and sticky edges in world space
    /// (vertices at each <b>planet center</b>). Reads topology from
    /// <see cref="PlanetConnectionGraphCache"/> — never runs planet/asteroid ECS gathers.
    /// <para>
    /// [TITAN-ORBIT] Every visible line is a great-circle between the two planet centers.
    /// Fills are tessellated onto the shell. Far-hemisphere segments are skipped so they
    /// do not show through the globe. Billboard lines (Flat2D is invisible from a radial camera).
    /// </para>
    /// Client presentation only.
    /// </summary>
    [ExecuteAlways]
    public class PlanetConnectionShapesVisual : ImmediateModeShapeDrawer
    {
        /// <summary>Y height of the flat triangle fill (slightly below the play plane).</summary>
        [SerializeField] float triangleHeight = -0.6f;

        /// <summary>
        /// How far above the fill to draw edge lines (meters). Same Y as the fill lets the
        /// transparent triangle win the depth test and hide every border.
        /// </summary>
        [SerializeField] float edgeHeightAboveFill = 0.08f;

        /// <summary>Fill alpha for territory triangles (original ~0.04).</summary>
        [SerializeField] float triangleAlpha = 0.04f;

        /// <summary>Edge thickness in meters (triangle sides + lone edges).</summary>
        [SerializeField] float triangleBorderThickness = 0.2f;

        /// <summary>Edge alpha — slightly stronger than the old TriangleBorder so lines read over fill.</summary>
        [SerializeField] float triangleBorderAlpha = 0.35f;

        /// <summary>Cached fill using the three planet centers on the shell.</summary>
        struct CachedWorldTriangle
        {
            public Vector3 PosA;
            public Vector3 PosB;
            public Vector3 PosC;
            public Color Fill;
        }

        /// <summary>Cached graph edge — both planet centers (includes triangle sides).</summary>
        struct CachedWorldEdge
        {
            public Vector3 PosA;
            public Vector3 PosB;
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
        /// [UNITY] Shapes draw callback — shell-hugging fills and great-circle edges.
        /// Verts refresh on topology revision.
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

            int revision = PlanetConnectionGraphCache.ClientPublishRevision;
            bool cacheEmpty = _worldCache.Count == 0 && _edgeCache.Count == 0;
            if (revision != _lastGraphRevision || cacheEmpty)
            {
                RebuildWorldCache();
                if (_worldCache.Count > 0 || _edgeCache.Count > 0)
                    _lastGraphRevision = revision;
            }

            if (_worldCache.Count == 0 && _edgeCache.Count == 0)
                return;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.BlendMode = ShapesBlendMode.Transparent;

                Vector3 camPos = cam.transform.position;

                // --- Fills hug the shell (planar chords dive inside the globe) ---
                for (int i = 0; i < _worldCache.Count; i++)
                {
                    var tri = _worldCache[i];
                    DrawSphericalTriangleFill(tri.PosA, tri.PosB, tri.PosC, tri.Fill, camPos);
                }

                // --- Every graph edge as a great-circle between the two planets ---
                Draw.LineGeometry = LineGeometry.Billboard;
                for (int i = 0; i < _edgeCache.Count; i++)
                {
                    var edge = _edgeCache[i];
                    DrawGeodesicEdge(edge.PosA, edge.PosB, edge.Color, camPos);
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
            EntityManager em = default;
            EcsWorldVisualizer visualizer = null;
            if (world != null && world.IsCreated)
            {
                em = world.EntityManager;
                visualizer = EcsWorldVisualizer.Active;
            }

            // --- Short-embeddable fills (graph already filtered non-embeddable cliques) ---
            for (int i = 0; i < triCount; i++)
            {
                var tri = triangles[i];
                if (!TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdA, out Vector3 aCanon) ||
                    !TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdB, out Vector3 bCanon) ||
                    !TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdC, out Vector3 cCanon))
                    continue;

                Color baseColor = tri.Team.ToColor();
                _worldCache.Add(new CachedWorldTriangle
                {
                    PosA = aCanon,
                    PosB = bCanon,
                    PosC = cCanon,
                    Fill = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha),
                });
            }

            // --- All graph edges as great-circles (do not skip triangle sides) ---
            for (int i = 0; i < edgeCount; i++)
            {
                var edge = edges[i];
                if (!TryGetCanonicalPlanetVertex(em, visualizer, edge.PlanetIdA, out Vector3 aCanon) ||
                    !TryGetCanonicalPlanetVertex(em, visualizer, edge.PlanetIdB, out Vector3 bCanon))
                    continue;

                Color baseColor = edge.Team.ToColor();
                _edgeCache.Add(new CachedWorldEdge
                {
                    PosA = aCanon,
                    PosB = bCanon,
                    Color = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha),
                });
            }
        }

        static Vector3 LiftOnShell(Vector3 p) =>
            p + (Vector3)SphericalMapEcs.LocalUp((float3)p) * 0.35f;

        /// <summary>True when a shell point is on the camera's near hemisphere.</summary>
        static bool OnNearHemisphere(Vector3 shellPoint, Vector3 camPos) =>
            Vector3.Dot(shellPoint, camPos) > 0f;

        /// <summary>
        /// Fills the spherical triangle ABC with a fan of shell-hugging sub-triangles.
        /// Far-hemisphere fans are skipped so they do not show through the globe gap.
        /// </summary>
        void DrawSphericalTriangleFill(Vector3 a, Vector3 b, Vector3 c, Color fill, Vector3 camPos)
        {
            if (!SphericalMapEcs.TryGetRadius(out float radius) || radius < 1f)
            {
                if (OnNearHemisphere(a, camPos) || OnNearHemisphere(b, camPos) || OnNearHemisphere(c, camPos))
                    Draw.Triangle(LiftOnShell(a), LiftOnShell(b), LiftOnShell(c), fill);
                return;
            }

            float3 mid = SphericalMapEcs.ProjectToSphere(((float3)a + (float3)b + (float3)c) * (1f / 3f), radius);
            DrawSphericalFan(a, b, (Vector3)mid, radius, fill, camPos);
            DrawSphericalFan(b, c, (Vector3)mid, radius, fill, camPos);
            DrawSphericalFan(c, a, (Vector3)mid, radius, fill, camPos);
        }

        void DrawSphericalFan(Vector3 from, Vector3 to, Vector3 mid, float radius, Color fill, Vector3 camPos)
        {
            float arc = SphericalMap.GeodesicDistance(from, to);
            int steps = Mathf.Clamp(Mathf.CeilToInt(arc / 18f), 1, 12);
            Vector3 midLift = LiftOnShell(mid);
            Vector3 prev = LiftOnShell(from);
            bool prevNear = OnNearHemisphere(from, camPos);
            bool midNear = OnNearHemisphere(mid, camPos);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 p = (Vector3)SphericalMapEcs.SphericalLerp((float3)from, (float3)to, t, radius);
                Vector3 pLift = LiftOnShell(p);
                bool pNear = OnNearHemisphere(p, camPos);
                if (midNear || prevNear || pNear)
                    Draw.Triangle(midLift, prev, pLift, fill);
                prev = pLift;
                prevNear = pNear;
            }
        }

        void DrawGeodesicEdge(Vector3 a, Vector3 b, Color color, Vector3 camPos)
        {
            if (!SphericalMapEcs.TryGetRadius(out float radius) || radius < 1f)
            {
                if (OnNearHemisphere(a, camPos) || OnNearHemisphere(b, camPos))
                    Draw.Line(LiftOnShell(a), LiftOnShell(b), triangleBorderThickness, LineEndCap.None, color);
                return;
            }

            float arc = SphericalMap.GeodesicDistance(a, b);
            int steps = Mathf.Clamp(Mathf.CeilToInt(arc / 10f), 1, 32);
            Vector3 prev = LiftOnShell(a);
            bool prevNear = OnNearHemisphere(a, camPos);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float3 p = SphericalMapEcs.SphericalLerp((float3)a, (float3)b, t, radius);
                Vector3 world = LiftOnShell((Vector3)p);
                bool near = OnNearHemisphere((Vector3)p, camPos);
                if (prevNear || near)
                    Draw.Line(prev, world, triangleBorderThickness, LineEndCap.None, color);
                prev = world;
                prevNear = near;
            }
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

            float3 planetPos = default;
            bool gotPose = visualizer != null &&
                visualizer.TryGetPlanetPoseByPlanetId(em, planetId, out planetPos, out _, out _);
            if (!gotPose &&
                !EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out planetPos, out _, out _))
                return false;

            planetCanonical = (Vector3)SphericalMapEcs.ProjectToSphere(planetPos);
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
