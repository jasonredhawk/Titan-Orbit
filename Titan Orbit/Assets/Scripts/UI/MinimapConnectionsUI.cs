using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// UGUI mesh drawer for minimap territory fills and sticky edges (planet-center vertices).
    /// Uses <see cref="OnPopulateMesh"/> so geometry always renders under the circular Mask —
    /// no dependency on Shapes <c>ImmediateModePanel</c> registration.
    /// Client presentation only.
    /// <para>
    /// Local faces are the same short geodesics the world fill / PIT use, sampled densely
    /// and projected onto the radar. Continent-spanning chords use short rim stubs.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class MinimapConnectionsUI : RawImage
    {
        /// <summary>Fill alpha for minimap triangles.</summary>
        const float TriangleAlpha = 0.28f;

        /// <summary>Border alpha for triangle outlines / lone edges.</summary>
        const float BorderAlpha = 0.85f;

        /// <summary>Border thickness in UI pixels.</summary>
        const float BorderThickness = 2.2f;

        /// <summary>
        /// Canonical planet corners for a published fill. Chart (which corner is the draw anchor)
        /// is chosen at mesh time from the player — a fixed embed anchor can sit off the compact
        /// minimap while an edge near the player still draws, leaving a lone line.
        /// </summary>
        struct CachedWorldTriangle
        {
            public Vector3 PosA;
            public Vector3 PosB;
            public Vector3 PosC;
            public Color Fill;
        }

        /// <summary>
        /// Both endpoints of a graph edge. Draw anchor = endpoint nearer the player so compact
        /// minimap keeps local chords on-screen.
        /// </summary>
        struct CachedWorldEdge
        {
            public Vector3 PosA;
            public Vector3 PosB;
            public Color Color;
        }

        static Texture2D _whiteTex;

        MinimapController _minimap;
        readonly List<CachedWorldTriangle> _worldCache = new List<CachedWorldTriangle>(16);
        readonly List<CachedWorldEdge> _edgeCache = new List<CachedWorldEdge>(16);
        readonly List<Vector2> _scratchRing = new List<Vector2>(96);
        readonly List<Vector2> _scratchArc = new List<Vector2>(32);
        int _lastGraphRevision = -1;
        int _lastDrawnCount = -1;
        Vector3 _lastPlayerPos;
        float _lastRadius = -1f;
        bool _lastExpanded;

        /// <summary>[UNITY] Disable raycasts; assign 1×1 white texture for UI mesh tinting.</summary>
        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
            if (_whiteTex == null)
            {
                _whiteTex = new Texture2D(1, 1);
                _whiteTex.SetPixel(0, 0, Color.white);
                _whiteTex.Apply();
            }

            texture = _whiteTex;
            color = Color.white;
            _minimap = GetComponentInParent<MinimapController>();
        }

        /// <summary>
        /// Dirties the UI mesh when topology publishes or the player/view moves.
        /// Planet-center verts are cached; only projection changes with camera/player.
        /// </summary>
        void LateUpdate()
        {
            int revision = PlanetConnectionGraphCache.ClientPublishRevision;
            int triCount = PlanetConnectionGraphCache.CurrentTriangles?.Count ?? 0;
            int edgeCount = PlanetConnectionGraphCache.CurrentEdges?.Count ?? 0;
            int count = triCount + edgeCount;
            bool topologyChanged = revision != _lastGraphRevision || count != _lastDrawnCount;
            bool vertsRebuilt = false;

            // Planet centers are fixed — only rebuild world verts when topology publishes.
            if (topologyChanged)
            {
                RebuildWorldCache();
                _lastGraphRevision = revision;
                _lastDrawnCount = count;
                vertsRebuilt = true;
            }

            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();

            Vector3 playerPos = _minimap != null ? _minimap.PlayerPosition : Vector3.zero;
            float radius = _minimap != null ? _minimap.MinimapRadius : 0f;
            bool expanded = _minimap != null && _minimap.IsExpanded;
            bool viewChanged =
                (playerPos - _lastPlayerPos).sqrMagnitude > 0.0025f ||
                Mathf.Abs(radius - _lastRadius) > 0.01f ||
                expanded != _lastExpanded;

            bool hasGeom = _worldCache.Count > 0 || _edgeCache.Count > 0;
            if (hasGeom && (topologyChanged || vertsRebuilt || viewChanged))
            {
                _lastPlayerPos = playerPos;
                _lastRadius = radius;
                _lastExpanded = expanded;
                SetVerticesDirty();
            }
            else if (topologyChanged && !hasGeom)
            {
                SetVerticesDirty();
            }
        }

        /// <summary>
        /// Resolves planet-center vertices into canonical world space once (expensive path).
        /// </summary>
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

            // --- Store canonical corners; draw-time picks the chart nearest the player ---
            for (int i = 0; i < triCount; i++)
            {
                var tri = triangles[i];
                if (!PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdA, out Vector3 aCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdB, out Vector3 bCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdC, out Vector3 cCanon))
                    continue;

                Color baseColor = tri.Team.ToColor();
                _worldCache.Add(new CachedWorldTriangle
                {
                    PosA = aCanon,
                    PosB = bCanon,
                    PosC = cCanon,
                    Fill = new Color(baseColor.r, baseColor.g, baseColor.b, TriangleAlpha),
                });
            }

            // --- Both endpoints; draw-time picks the nearer endpoint as anchor ---
            for (int i = 0; i < edgeCount; i++)
            {
                var edge = edges[i];
                if (!PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, edge.PlanetIdA, out Vector3 aCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, edge.PlanetIdB, out Vector3 bCanon))
                    continue;

                Color baseColor = edge.Team.ToColor();
                _edgeCache.Add(new CachedWorldEdge
                {
                    PosA = aCanon,
                    PosB = bCanon,
                    Color = new Color(baseColor.r, baseColor.g, baseColor.b, BorderAlpha),
                });
            }
        }

        /// <summary>
        /// [UNITY] Projects cached world triangles / edges into panel space (cheap — no ECS).
        /// </summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();
            if (_minimap == null || (_worldCache.Count == 0 && _edgeCache.Count == 0))
                return;

            Rect rect = GetPixelAdjustedRect();
            if (rect.width < 1f || rect.height < 1f)
                return;

            Vector3 playerPos = _minimap.PlayerPosition;
            float radius = Mathf.Max(1f, _minimap.MinimapRadius);
            float displayHalf = _minimap.DisplaySize * 0.5f;
            float scale = displayHalf / radius;
            if (!ToroidalMap.HasValidMapSize)
                return;

            Vector2 center = rect.center;
            float half = Mathf.Min(rect.width, rect.height) * 0.5f;
            float innerR = half * 0.28f;
            float outerR = half * 0.96f;
            float maxChord = half * 0.72f;
            float maxJump = half * 0.42f;
            bool haveRadius = SphericalMap.TryGetRadius(out float mapR) && mapR > 1f;

            for (int i = 0; i < _worldCache.Count; i++)
            {
                var tri = _worldCache[i];
                if (!TryProject(playerPos, scale, rect, tri.PosA, out Vector2 pa) ||
                    !TryProject(playerPos, scale, rect, tri.PosB, out Vector2 pb) ||
                    !TryProject(playerPos, scale, rect, tri.PosC, out Vector2 pc))
                    continue;

                if (!AnyNearPanel(rect, pa, pb, pc))
                    continue;
                if (IsContinentSpan(pa, pb, maxChord) ||
                    IsContinentSpan(pb, pc, maxChord) ||
                    IsContinentSpan(pc, pa, maxChord))
                    continue;

                if (haveRadius)
                    AddGeodesicFill(vh, playerPos, scale, rect, mapR, tri, pa, pb, pc, maxJump);
                else
                    AddTriangle(vh, pa, pb, pc, tri.Fill);
            }

            for (int i = 0; i < _edgeCache.Count; i++)
            {
                var edge = _edgeCache[i];
                if (!TryProject(playerPos, scale, rect, edge.PosA, out Vector2 pa) ||
                    !TryProject(playerPos, scale, rect, edge.PosB, out Vector2 pb))
                    continue;

                if (IsContinentSpan(pa, pb, maxChord))
                {
                    DrawRimStubsIfShorter(vh, pa, pb, center, outerR, edge.Color);
                    continue;
                }

                if (haveRadius)
                    DrawGeodesicLine(vh, playerPos, scale, rect, mapR, edge.PosA, edge.PosB,
                        pa, pb, edge.Color, center, innerR, maxJump);
                else
                    AddLineQuad(vh, pa, pb, edge.Color, BorderThickness);
            }
        }

        /// <summary>True when the 2D blip chord is a wrap / far-side span, not a local cluster.</summary>
        static bool IsContinentSpan(Vector2 a, Vector2 b, float maxChord)
        {
            return (a - b).sqrMagnitude > maxChord * maxChord;
        }

        /// <summary>
        /// Rim stubs only when each stub is shorter than the actual planet-to-planet chart span.
        /// A stub longer than that pair is the wrong path and is dropped.
        /// </summary>
        static void DrawRimStubsIfShorter(
            VertexHelper vh, Vector2 pa, Vector2 pb, Vector2 center, float outerR, Color color)
        {
            float chord = Vector2.Distance(pa, pb);
            Vector2 ra = RadialRim(pa, center, outerR);
            Vector2 rb = RadialRim(pb, center, outerR);
            if (Vector2.Distance(pa, ra) + 1f < chord)
                AddLineQuad(vh, pa, ra, color, BorderThickness);
            if (Vector2.Distance(pb, rb) + 1f < chord)
                AddLineQuad(vh, pb, rb, color, BorderThickness);
        }

        static Vector2 RadialRim(Vector2 p, Vector2 center, float outerR)
        {
            Vector2 d = p - center;
            float len = d.magnitude;
            if (len < 1e-3f)
                return center + new Vector2(outerR, 0f);
            return center + d * (outerR / len);
        }

        void AddGeodesicFill(
            VertexHelper vh,
            Vector3 playerPos,
            float scale,
            Rect rect,
            float mapR,
            in CachedWorldTriangle tri,
            Vector2 pa,
            Vector2 pb,
            Vector2 pc,
            float maxJump)
        {
            _scratchRing.Clear();
            AppendGeodesicChart(playerPos, scale, rect, mapR, tri.PosA, tri.PosB, pa, pb, _scratchRing, true, maxJump);
            AppendGeodesicChart(playerPos, scale, rect, mapR, tri.PosB, tri.PosC, pb, pc, _scratchRing, false, maxJump);
            AppendGeodesicChart(playerPos, scale, rect, mapR, tri.PosC, tri.PosA, pc, pa, _scratchRing, false, maxJump);

            Vector3 midWorld = (Vector3)SphericalMapEcs.ProjectToSphere(
                ((float3)tri.PosA + (float3)tri.PosB + (float3)tri.PosC) * (1f / 3f), mapR);
            TryProject(playerPos, scale, rect, midWorld, out Vector2 mid);
            if (!PointInTri2D(mid, pa, pb, pc))
                mid = (pa + pb + pc) / 3f;

            FanRing(vh, mid, tri.Fill);
        }

        void DrawGeodesicLine(
            VertexHelper vh,
            Vector3 playerPos,
            float scale,
            Rect rect,
            float mapR,
            Vector3 wa,
            Vector3 wb,
            Vector2 pa,
            Vector2 pb,
            Color color,
            Vector2 center,
            float innerR,
            float maxJump)
        {
            _scratchArc.Clear();
            AppendGeodesicChart(playerPos, scale, rect, mapR, wa, wb, pa, pb, _scratchArc, true, maxJump);
            for (int i = 1; i < _scratchArc.Count; i++)
            {
                Vector2 prev = _scratchArc[i - 1];
                Vector2 cur = _scratchArc[i];
                if (!SegmentCrossesInnerDisk(prev, cur, center, innerR))
                    AddLineQuad(vh, prev, cur, color, BorderThickness);
            }
        }

        void AppendGeodesicChart(
            Vector3 playerPos,
            float scale,
            Rect rect,
            float mapR,
            Vector3 a,
            Vector3 b,
            Vector2 ca,
            Vector2 cb,
            List<Vector2> dest,
            bool includeStart,
            float maxJump)
        {
            if (includeStart)
                dest.Add(ca);

            float arc = SphericalMap.GeodesicDistance(a, b);
            int steps = Mathf.Clamp(Mathf.CeilToInt(arc / 8f), 8, 28);
            Vector2 prev = ca;
            float jump2 = maxJump * maxJump;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 p = (Vector3)SphericalMapEcs.SphericalLerp((float3)a, (float3)b, t, mapR);
                if (!TryProject(playerPos, scale, rect, p, out Vector2 cur))
                    continue;
                if ((cur - prev).sqrMagnitude > jump2)
                    continue;
                dest.Add(cur);
                prev = cur;
            }

            if ((prev - cb).sqrMagnitude > 0.25f && (cb - prev).sqrMagnitude <= jump2)
                dest.Add(cb);
        }

        void FanRing(VertexHelper vh, Vector2 mid, Color fill)
        {
            int n = _scratchRing.Count;
            if (n < 3)
                return;
            if ((_scratchRing[0] - _scratchRing[n - 1]).sqrMagnitude < 4f)
            {
                _scratchRing.RemoveAt(n - 1);
                n = _scratchRing.Count;
                if (n < 3)
                    return;
            }

            for (int i = 0; i < n; i++)
                AddTriangle(vh, mid, _scratchRing[i], _scratchRing[(i + 1) % n], fill);
        }

        static bool PointInTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b);
            float d2 = Cross(p, b, c);
            float d3 = Cross(p, c, a);
            bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNeg && hasPos);
        }

        static float Cross(Vector2 p, Vector2 a, Vector2 b)
        {
            return (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
        }

        static bool SegmentCrossesInnerDisk(Vector2 a, Vector2 b, Vector2 center, float innerR)
        {
            float r2 = innerR * innerR;
            if ((a - center).sqrMagnitude <= r2 || (b - center).sqrMagnitude <= r2)
                return false;

            Vector2 ab = b - a;
            float ab2 = ab.sqrMagnitude;
            if (ab2 < 1e-6f)
                return false;

            float t = Mathf.Clamp01(Vector2.Dot(center - a, ab) / ab2);
            Vector2 closest = a + ab * t;
            return (closest - center).sqrMagnitude < r2;
        }

        bool TryProject(Vector3 playerPos, float scale, Rect rect, Vector3 world, out Vector2 panel)
        {
            _minimap.GetToroidalDeltaForMinimap(playerPos, world, out float dx, out float dz);
            panel = rect.center + new Vector2(dx * scale, dz * scale);
            return true;
        }

        /// <summary>True if any vertex is within a generous margin of the panel rect (incl. off-edge wraps).</summary>
        static bool AnyNearPanel(Rect rect, Vector2 a, Vector2 b, Vector2 c)
        {
            // Pad by a full panel so ±map tile copies just outside the circle still draw.
            float pad = Mathf.Max(rect.width, rect.height);
            Rect fat = Rect.MinMaxRect(rect.xMin - pad, rect.yMin - pad, rect.xMax + pad, rect.yMax + pad);
            return fat.Contains(a) || fat.Contains(b) || fat.Contains(c);
        }

        /// <summary>Appends one filled triangle to the UI mesh.</summary>
        static void AddTriangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero);
            vh.AddVert(b, color, Vector2.zero);
            vh.AddVert(c, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
        }

        /// <summary>One border segment as a screen-aligned quad of width <paramref name="thickness"/>.</summary>
        static void AddLineQuad(VertexHelper vh, Vector2 a, Vector2 b, Color color, float thickness)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.01f)
                return;

            Vector2 dir = delta / len;
            Vector2 n = new Vector2(-dir.y, dir.x) * (thickness * 0.5f);
            Vector2 v0 = a - n;
            Vector2 v1 = a + n;
            Vector2 v2 = b + n;
            Vector2 v3 = b - n;

            int i = vh.currentVertCount;
            vh.AddVert(v0, color, Vector2.zero);
            vh.AddVert(v1, color, Vector2.zero);
            vh.AddVert(v2, color, Vector2.zero);
            vh.AddVert(v3, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }
    }
}
