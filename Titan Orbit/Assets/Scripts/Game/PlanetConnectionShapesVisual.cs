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
    /// [HYBRID] Draws semi-transparent team territory fills and sticky edges in world space
    /// (vertices at each <b>planet center</b>). Reads topology from
    /// <see cref="PlanetConnectionGraphCache"/> — never runs planet/asteroid ECS gathers.
    /// <para>
    /// [TITAN-ORBIT] Every visible line is a shortest-path embedding of a graph edge — never a
    /// <c>TriangleBorder</c> opposite side (that Euclidean chord invented mid-map crosses).
    /// Published triangles are already short-embeddable; we fill them only. Borders come from the
    /// full edge list as Billboard lines (Flat2D is invisible from a top-down XZ camera).
    /// Vertices are canonical XZ → shortest offsets from a short-embed anchor → camera retile for seams.
    /// Ship / asteroid membership uses the same short-embed charts in
    /// <see cref="PlanetConnectionGraphLogic.PointInTriangleXZ"/> (not a VertexA-only unwrap).
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

        /// <summary>Cached short-embeddable fill (anchor + B/C shortest offsets + colour).</summary>
        struct CachedWorldTriangle
        {
            public Vector3 Anchor;
            public Vector3 OffsetB;
            public Vector3 OffsetC;
            public Color Fill;
        }

        /// <summary>
        /// Cached graph edge — anchor + shortest offset to the other planet (includes triangle sides).
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
        /// [UNITY] Shapes draw callback — fills for embeddable triangles, shortest lines for every
        /// graph edge, plus wrap copies near seams. Retiles every frame; verts refresh on topology.
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
                Draw.BlendMode = ShapesBlendMode.Transparent;

                Vector3 referencePos = ResolveDisplayReference(cam, em);
                float mapW = ToroidalMap.GetMapWidth();
                float mapH = ToroidalMap.GetMapHeight();
                float edgeY = triangleHeight + edgeHeightAboveFill;

                // --- Fills only (borders come from the edge pass — never TriangleBorder) ---
                for (int i = 0; i < _worldCache.Count; i++)
                {
                    var tri = _worldCache[i];
                    Vector3 a = ToroidalMap.GetDisplayPosition(tri.Anchor, referencePos);
                    Vector3 b = a + tri.OffsetB;
                    Vector3 c = a + tri.OffsetC;
                    a.y = triangleHeight;
                    b.y = triangleHeight;
                    c.y = triangleHeight;
                    DrawTriangleFillWithWraps(a, b, c, mapW, mapH, tri.Fill);
                }

                // --- Every graph edge as a shortest chord ---
                // [TITAN-ORBIT] Flat2D lines lie in a plane that is edge-on to our top-down XZ
                // camera (zero visible thickness). Billboard matches Shapes' 3D default; gem beams
                // use Volumetric3D for the same reason. Slightly above fill so depth doesn't hide them.
                Draw.LineGeometry = LineGeometry.Billboard;
                for (int i = 0; i < _edgeCache.Count; i++)
                {
                    var edge = _edgeCache[i];
                    Vector3 a = ToroidalMap.GetDisplayPosition(edge.Anchor, referencePos);
                    Vector3 b = a + edge.OffsetB;
                    a.y = edgeY;
                    b.y = edgeY;
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

            // --- Short-embeddable fills (graph already filtered non-embeddable cliques) ---
            for (int i = 0; i < triCount; i++)
            {
                var tri = triangles[i];
                if (!TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdA, out Vector3 aCanon) ||
                    !TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdB, out Vector3 bCanon) ||
                    !TryGetCanonicalPlanetVertex(em, visualizer, tri.PlanetIdC, out Vector3 cCanon))
                    continue;

                if (!TryPickShortEmbedAnchor(
                        aCanon, bCanon, cCanon,
                        tri.PlanetIdA, tri.PlanetIdB, tri.PlanetIdC,
                        out Vector3 anchor, out Vector3 bPos, out Vector3 cPos))
                    continue;

                Color baseColor = tri.Team.ToColor();
                _worldCache.Add(new CachedWorldTriangle
                {
                    Anchor = anchor,
                    OffsetB = ToroidalMap.ShortestWorldOffsetXZ(anchor, bPos),
                    OffsetC = ToroidalMap.ShortestWorldOffsetXZ(anchor, cPos),
                    Fill = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha),
                });
            }

            // --- All graph edges as shortest lines (do not skip triangle sides) ---
            for (int i = 0; i < edgeCount; i++)
            {
                var edge = edges[i];
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
        /// Picks a corner where the triangle is short-embeddable so fill offsets match graph edges.
        /// Falls back to lowest planet-id anchor if embed check fails (should be rare — graph gated).
        /// </summary>
        static bool TryPickShortEmbedAnchor(
            Vector3 aCanon,
            Vector3 bCanon,
            Vector3 cCanon,
            int idA,
            int idB,
            int idC,
            out Vector3 anchor,
            out Vector3 bPos,
            out Vector3 cPos)
        {
            float mapW = ToroidalMap.GetMapWidth();
            float mapH = ToroidalMap.GetMapHeight();
            float3 a = new float3(aCanon.x, 0f, aCanon.z);
            float3 b = new float3(bCanon.x, 0f, bCanon.z);
            float3 c = new float3(cCanon.x, 0f, cCanon.z);

            if (PlanetConnectionGraphLogic.IsShortEmbeddableTriangle(a, b, c, mapW, mapH))
            {
                // Prefer the embeddable chart: try lowest-id corner first (stable), then others.
                if (TryAnchorIfEmbeds(aCanon, bCanon, cCanon, a, b, c, mapW, mapH,
                        out anchor, out bPos, out cPos))
                    return true;
                if (TryAnchorIfEmbeds(bCanon, aCanon, cCanon, b, a, c, mapW, mapH,
                        out anchor, out bPos, out cPos))
                    return true;
                if (TryAnchorIfEmbeds(cCanon, aCanon, bCanon, c, a, b, mapW, mapH,
                        out anchor, out bPos, out cPos))
                    return true;
            }

            // Fallback: lowest planet id (same as old drawer) — fill may be skipped if offsets fail.
            anchor = aCanon;
            bPos = bCanon;
            cPos = cCanon;
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

            return true;
        }

        /// <summary>
        /// If <paramref name="anchor"/> yields a short-embeddable chart for the other two verts,
        /// writes that chart and returns true.
        /// </summary>
        static bool TryAnchorIfEmbeds(
            Vector3 anchorCanon,
            Vector3 pCanon,
            Vector3 qCanon,
            float3 anchor,
            float3 p,
            float3 q,
            float mapW,
            float mapH,
            out Vector3 outAnchor,
            out Vector3 outB,
            out Vector3 outC)
        {
            outAnchor = default;
            outB = default;
            outC = default;

            // Reuse the public embed check on the full triple; then verify this corner works by
            // matching chart(PQ) to Shortest(P,Q) — same criterion as graph logic.
            float3 offP = ToroidalMapEcs.ShortestOffsetXZ(anchor, p, mapW, mapH);
            float3 offQ = ToroidalMapEcs.ShortestOffsetXZ(anchor, q, mapW, mapH);
            float2 P = new float2(offP.x, offP.z);
            float2 Q = new float2(offQ.x, offQ.z);
            if (math.abs(P.x * Q.y - P.y * Q.x) < 1e-3f)
                return false;

            float3 shortQP = ToroidalMapEcs.ShortestOffsetXZ(q, p, mapW, mapH);
            float2 chartQP = P - Q;
            float2 geodesicQP = new float2(shortQP.x, shortQP.z);
            if (math.lengthsq(chartQP - geodesicQP) > 0.25f)
                return false;

            outAnchor = anchorCanon;
            outB = pCanon;
            outC = qCanon;
            return true;
        }

        /// <summary>
        /// Draws a fill-only triangle and ±map-tile copies when near a wrap seam.
        /// Borders are drawn separately from the edge list (shortest chords only).
        /// </summary>
        void DrawTriangleFillWithWraps(
            Vector3 a, Vector3 b, Vector3 c, float mapW, float mapH, Color fillColor)
        {
            Draw.Triangle(a, b, c, fillColor);

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
            }
        }

        /// <summary>
        /// Draws a graph edge line with optional seam wrap copies.
        /// Caller must set <see cref="Draw.LineGeometry"/> to Billboard (or Volumetric3D) first.
        /// </summary>
        void DrawEdgeWithWraps(Vector3 a, Vector3 b, float mapW, float mapH, Color color)
        {
            // [SHAPES] None caps — Round/Square can look odd on short territory chords.
            Draw.Line(a, b, triangleBorderThickness, LineEndCap.None, color);

            // Pass b twice so NeedsWrapCopies still checks both endpoints + the AB span.
            if (!NeedsWrapCopies(a, b, b, mapW, mapH))
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
                Draw.Line(a + off, b + off, triangleBorderThickness, LineEndCap.None, color);
            }
        }

        /// <summary>
        /// True when wrap-tile copies are needed: a vertex near the periodic seam, or a side that
        /// itself uses the toroidal wrap (shortest ≠ Euclidean in canonical space).
        /// </summary>
        static bool NeedsWrapCopies(Vector3 a, Vector3 b, Vector3 c, float mapW, float mapH)
        {
            // ~8% of the smaller map side — enough for seam-adjacent fills without firing mid-map.
            float margin = math.max(20f, 0.08f * math.min(mapW, mapH));
            return NearSeam(a, mapW, mapH, margin) ||
                   NearSeam(b, mapW, mapH, margin) ||
                   NearSeam(c, mapW, mapH, margin) ||
                   SideWrapsToroidally(a, b) ||
                   SideWrapsToroidally(b, c) ||
                   SideWrapsToroidally(c, a);
        }

        /// <summary>
        /// True when canonical XZ is within <paramref name="margin"/> of a wrap seam.
        /// <para>
        /// [TITAN-ORBIT] <see cref="ToroidalMap.WrapPosition"/> returns
        /// <c>[-halfW, halfW) × [-halfH, halfH)</c>. Comparing against <c>[0, mapW]</c> falsely
        /// fired wrap copies across most of the left half of the map.
        /// </para>
        /// </summary>
        static bool NearSeam(Vector3 p, float mapW, float mapH, float margin)
        {
            Vector3 w = ToroidalMap.WrapPosition(p);
            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;

            // Distance to the identified edges x=±halfW / z=±halfH (0 on the seam, half at center).
            float distToSeamX = halfW - math.abs(w.x);
            float distToSeamZ = halfH - math.abs(w.z);
            return distToSeamX < margin || distToSeamZ < margin;
        }

        /// <summary>
        /// True when the shortest toroidal path between two points differs from the straight
        /// Euclidean segment in canonical space — i.e. the connection wraps a seam.
        /// </summary>
        static bool SideWrapsToroidally(Vector3 a, Vector3 b)
        {
            Vector3 wa = ToroidalMap.WrapPosition(a);
            Vector3 wb = ToroidalMap.WrapPosition(b);
            Vector3 euclidean = wb - wa;
            euclidean.y = 0f;
            Vector3 shortest = ToroidalMap.ShortestWorldOffsetXZ(wa, wb);

            // [STANDARD] Any tile-period difference means the short chord is not the canonical line.
            const float eps = 1f;
            return math.abs(euclidean.x - shortest.x) > eps ||
                   math.abs(euclidean.z - shortest.z) > eps;
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
