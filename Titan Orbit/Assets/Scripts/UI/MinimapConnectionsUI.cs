using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// UGUI mesh drawer for minimap territory triangles and lone sticky edges (planet-center vertices).
    /// Uses <see cref="OnPopulateMesh"/> so geometry always renders under the circular Mask —
    /// no dependency on Shapes <c>ImmediateModePanel</c> registration.
    /// When expanded, draws a 3×3 toroidal tile of each triangle/edge so seam-crossing links still
    /// read next to planet blips on both sides of the wrap (same shortest-path chart as blips).
    /// Client presentation only.
    /// <para>
    /// [TITAN-ORBIT] Planet centers are fixed — world verts rebuild only when graph topology
    /// publishes. Projection still updates when the player/view moves.
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

        /// <summary>One triangle in canonical world space (anchor + shortest-path offsets for B/C).</summary>
        struct CachedWorldTriangle
        {
            public Vector3 Anchor;
            public Vector3 OffsetB;
            public Vector3 OffsetC;
            public Color Fill;
            public Color Border;
        }

        /// <summary>Lone edge (not a triangle side) in canonical world space.</summary>
        struct CachedWorldEdge
        {
            public Vector3 Anchor;
            public Vector3 OffsetB;
            public Color Color;
        }

        static Texture2D _whiteTex;

        MinimapController _minimap;
        readonly List<CachedWorldTriangle> _worldCache = new List<CachedWorldTriangle>(16);
        readonly List<CachedWorldEdge> _edgeCache = new List<CachedWorldEdge>(16);
        int _lastGraphRevision = -1;
        int _lastDrawnCount = -1;
        Vector3 _lastPlayerPos;
        float _lastRadius = -1f;
        bool _lastExpanded;

        /// <summary>Scratch X offsets for 3×3 toroidal tile copies (reused each mesh rebuild).</summary>
        readonly float[] _wrapsX = new float[9];

        /// <summary>Scratch Z offsets for 3×3 toroidal tile copies (reused each mesh rebuild).</summary>
        readonly float[] _wrapsZ = new float[9];

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
                    Fill = new Color(baseColor.r, baseColor.g, baseColor.b, TriangleAlpha),
                    Border = new Color(baseColor.r, baseColor.g, baseColor.b, BorderAlpha),
                });
            }

            // Lone sticky edges — skip sides already drawn as triangle borders.
            for (int i = 0; i < edgeCount; i++)
            {
                var edge = edges[i];
                if (PlanetConnectionGraphLogic.EdgeIsTriangleSide(
                        edge.PlanetIdA, edge.PlanetIdB, edge.Team, triangles))
                    continue;

                if (!PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, edge.PlanetIdA, out Vector3 aCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, edge.PlanetIdB, out Vector3 bCanon))
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
                    Color = new Color(baseColor.r, baseColor.g, baseColor.b, BorderAlpha),
                });
            }
        }

        /// <summary>
        /// [UNITY] Projects cached world triangles / lone edges into panel space (cheap — no ECS).
        /// <para>
        /// Anchor uses the same toroidal shortest delta as planet blips. When expanded, each
        /// triangle/edge is also drawn on the 8 neighboring map tiles (3×3) by shifting in
        /// <b>panel</b> space — not re-wrapping — so seam links extend past the circle edge next
        /// to the wrap-side planet blips.
        /// </para>
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
            float mapW = ToroidalMap.GetMapWidth();
            float mapH = ToroidalMap.GetMapHeight();

            // --- 3×3 tile copies when showing (near) the full map ---
            // [TITAN-ORBIT] Compact minimap: primary tile only (Scripts hitch if we always 9×).
            bool needWrapCopies = _minimap.IsExpanded ||
                                  radius >= 0.45f * Mathf.Min(mapW, mapH);
            int wrapCount = needWrapCopies ? 9 : 1;
            // (0,0) first, then 8 neighbors — reuse instance scratch (no per-mesh alloc).
            _wrapsX[0] = 0f;
            _wrapsZ[0] = 0f;
            if (needWrapCopies)
            {
                int wi = 1;
                for (int ox = -1; ox <= 1; ox++)
                {
                    for (int oz = -1; oz <= 1; oz++)
                    {
                        if (ox == 0 && oz == 0)
                            continue;
                        _wrapsX[wi] = ox * mapW;
                        _wrapsZ[wi] = oz * mapH;
                        wi++;
                    }
                }
            }

            for (int i = 0; i < _worldCache.Count; i++)
            {
                var tri = _worldCache[i];
                // Same chart as planet blips — then tile-shift in panel space for wrap copies.
                _minimap.GetToroidalDeltaForMinimap(playerPos, tri.Anchor, out float baseDx, out float baseDz);

                for (int w = 0; w < wrapCount; w++)
                {
                    float ax = baseDx + _wrapsX[w];
                    float az = baseDz + _wrapsZ[w];
                    Vector2 pa = rect.center + new Vector2(ax * scale, az * scale);
                    Vector2 pb = pa + new Vector2(tri.OffsetB.x * scale, tri.OffsetB.z * scale);
                    Vector2 pc = pa + new Vector2(tri.OffsetC.x * scale, tri.OffsetC.z * scale);

                    if (!AnyNearPanel(rect, pa, pb, pc))
                        continue;

                    AddTriangle(vh, pa, pb, pc, tri.Fill);
                    AddTriangleBorder(vh, pa, pb, pc, tri.Border, BorderThickness);
                }
            }

            for (int i = 0; i < _edgeCache.Count; i++)
            {
                var edge = _edgeCache[i];
                _minimap.GetToroidalDeltaForMinimap(playerPos, edge.Anchor, out float baseDx, out float baseDz);

                for (int w = 0; w < wrapCount; w++)
                {
                    float ax = baseDx + _wrapsX[w];
                    float az = baseDz + _wrapsZ[w];
                    Vector2 pa = rect.center + new Vector2(ax * scale, az * scale);
                    Vector2 pb = pa + new Vector2(edge.OffsetB.x * scale, edge.OffsetB.z * scale);

                    if (!AnyNearPanel(rect, pa, pb, pa))
                        continue;

                    AddLineQuad(vh, pa, pb, edge.Color, BorderThickness);
                }
            }
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

        /// <summary>Appends three thin quads as a triangle border.</summary>
        static void AddTriangleBorder(
            VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color color, float thickness)
        {
            AddLineQuad(vh, a, b, color, thickness);
            AddLineQuad(vh, b, c, color, thickness);
            AddLineQuad(vh, c, a, color, thickness);
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
