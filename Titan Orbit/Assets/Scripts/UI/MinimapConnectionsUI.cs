using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
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
    /// When expanded, draws a 3×3 toroidal tile of each fill/edge so seam-crossing links still
    /// read next to planet blips on both sides of the wrap (same shortest-path chart as blips).
    /// Client presentation only.
    /// <para>
    /// [TITAN-ORBIT] Every visible line is a shortest-path graph edge — never a Euclidean triangle
    /// opposite side. Fills pick the short-embeddable corner nearest the player at mesh time so
    /// compact minimap does not drop the fill when a fixed anchor sits off-circle. Borders come
    /// from the full edge list (nearer endpoint as anchor).
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
            int revision = PlanetConnectionGraphCache.PresentationRevision;
            int triCount = PlanetConnectionGraphCache.CurrentTriangles?.Count ?? 0;
            int edgeCount = PlanetConnectionGraphCache.CurrentEdges?.Count ?? 0;
            int count = triCount + edgeCount;
            bool topologyChanged = revision != _lastGraphRevision || count != _lastDrawnCount;
            bool vertsRebuilt = false;

            // Planet centers are fixed — only rebuild world verts when topology publishes.
            // Retry while topology exists but verts are still unresolved.
            bool cacheEmpty = _worldCache.Count == 0 && _edgeCache.Count == 0;
            if (topologyChanged || (cacheEmpty && count > 0))
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
            EntityManager em = default;
            EcsWorldVisualizer visualizer = null;
            if (world != null && world.IsCreated)
            {
                em = world.EntityManager;
                visualizer = EcsWorldVisualizer.Active;
            }

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
        /// <para>
        /// Compact minimap: each fill uses the short-embeddable corner <b>nearest the player</b>
        /// so the triangle stays on-screen with its edges. Expanded: same chart plus 3×3 tile
        /// copies in panel space for seam blips.
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
            if (!ToroidalMap.TryGetMapSize(out float mapW, out float mapH) &&
                !ToroidalMapEcs.TryGetMapSize(out mapW, out mapH))
                return;

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
                // Nearest short-embeddable corner → fill stays with local edges on compact view.
                if (!TryPickNearestEmbedChart(
                        playerPos, tri.PosA, tri.PosB, tri.PosC, mapW, mapH,
                        out Vector3 anchor, out Vector3 offsetB, out Vector3 offsetC))
                    continue;

                _minimap.GetToroidalDeltaForMinimap(playerPos, anchor, out float baseDx, out float baseDz);

                for (int w = 0; w < wrapCount; w++)
                {
                    float ax = baseDx + _wrapsX[w];
                    float az = baseDz + _wrapsZ[w];
                    Vector2 pa = rect.center + new Vector2(ax * scale, az * scale);
                    Vector2 pb = pa + new Vector2(offsetB.x * scale, offsetB.z * scale);
                    Vector2 pc = pa + new Vector2(offsetC.x * scale, offsetC.z * scale);

                    if (!AnyNearPanel(rect, pa, pb, pc))
                        continue;

                    AddTriangle(vh, pa, pb, pc, tri.Fill);
                }
            }

            for (int i = 0; i < _edgeCache.Count; i++)
            {
                var edge = _edgeCache[i];
                // Nearer endpoint as anchor — same reason as triangle chart pick.
                Vector3 anchor = edge.PosA;
                Vector3 other = edge.PosB;
                if (ToroidalMap.ToroidalDistance(playerPos, edge.PosB) <
                    ToroidalMap.ToroidalDistance(playerPos, edge.PosA))
                {
                    anchor = edge.PosB;
                    other = edge.PosA;
                }

                Vector3 offsetB = ToroidalMap.ShortestWorldOffsetXZ(anchor, other);
                _minimap.GetToroidalDeltaForMinimap(playerPos, anchor, out float baseDx, out float baseDz);

                for (int w = 0; w < wrapCount; w++)
                {
                    float ax = baseDx + _wrapsX[w];
                    float az = baseDz + _wrapsZ[w];
                    Vector2 pa = rect.center + new Vector2(ax * scale, az * scale);
                    Vector2 pb = pa + new Vector2(offsetB.x * scale, offsetB.z * scale);

                    if (!AnyNearPanel(rect, pa, pb, pa))
                        continue;

                    AddLineQuad(vh, pa, pb, edge.Color, BorderThickness);
                }
            }
        }

        /// <summary>
        /// Among short-embeddable corner charts, picks the one whose anchor is nearest the player
        /// (toroidal distance). Keeps compact-minimap fills from vanishing when a fixed id-order
        /// anchor sits outside the circle while a nearby edge still draws.
        /// </summary>
        static bool TryPickNearestEmbedChart(
            Vector3 playerPos,
            Vector3 posA,
            Vector3 posB,
            Vector3 posC,
            float mapW,
            float mapH,
            out Vector3 anchor,
            out Vector3 offsetB,
            out Vector3 offsetC)
        {
            anchor = default;
            offsetB = default;
            offsetC = default;

            float3 a = new float3(posA.x, 0f, posA.z);
            float3 b = new float3(posB.x, 0f, posB.z);
            float3 c = new float3(posC.x, 0f, posC.z);

            float bestDist = float.MaxValue;
            bool found = false;

            // Try each corner as chart origin; keep the nearest valid short-embed.
            TryConsiderEmbedCorner(playerPos, posA, posB, posC, a, b, c, mapW, mapH,
                ref bestDist, ref found, ref anchor, ref offsetB, ref offsetC);
            TryConsiderEmbedCorner(playerPos, posB, posA, posC, b, a, c, mapW, mapH,
                ref bestDist, ref found, ref anchor, ref offsetB, ref offsetC);
            TryConsiderEmbedCorner(playerPos, posC, posA, posB, c, a, b, mapW, mapH,
                ref bestDist, ref found, ref anchor, ref offsetB, ref offsetC);

            if (found)
                return true;

            // Fallback: nearest corner even if embed check is strict (published tris should embed).
            float dA = ToroidalMap.ToroidalDistance(playerPos, posA);
            float dB = ToroidalMap.ToroidalDistance(playerPos, posB);
            float dC = ToroidalMap.ToroidalDistance(playerPos, posC);
            if (dA <= dB && dA <= dC)
            {
                anchor = posA;
                offsetB = ToroidalMap.ShortestWorldOffsetXZ(posA, posB);
                offsetC = ToroidalMap.ShortestWorldOffsetXZ(posA, posC);
            }
            else if (dB <= dA && dB <= dC)
            {
                anchor = posB;
                offsetB = ToroidalMap.ShortestWorldOffsetXZ(posB, posA);
                offsetC = ToroidalMap.ShortestWorldOffsetXZ(posB, posC);
            }
            else
            {
                anchor = posC;
                offsetB = ToroidalMap.ShortestWorldOffsetXZ(posC, posA);
                offsetC = ToroidalMap.ShortestWorldOffsetXZ(posC, posB);
            }

            return true;
        }

        /// <summary>Updates the best chart if this corner short-embeds and is nearer the player.</summary>
        static void TryConsiderEmbedCorner(
            Vector3 playerPos,
            Vector3 anchorCanon,
            Vector3 pCanon,
            Vector3 qCanon,
            float3 anchor,
            float3 p,
            float3 q,
            float mapW,
            float mapH,
            ref float bestDist,
            ref bool found,
            ref Vector3 outAnchor,
            ref Vector3 outOffsetB,
            ref Vector3 outOffsetC)
        {
            float3 offP = ToroidalMapEcs.ShortestOffsetXZ(anchor, p, mapW, mapH);
            float3 offQ = ToroidalMapEcs.ShortestOffsetXZ(anchor, q, mapW, mapH);
            float2 P = new float2(offP.x, offP.z);
            float2 Q = new float2(offQ.x, offQ.z);
            if (math.abs(P.x * Q.y - P.y * Q.x) < 1e-3f)
                return;

            float3 shortQP = ToroidalMapEcs.ShortestOffsetXZ(q, p, mapW, mapH);
            float2 chartQP = P - Q;
            float2 geodesicQP = new float2(shortQP.x, shortQP.z);
            if (math.lengthsq(chartQP - geodesicQP) > 0.25f)
                return;

            float dist = ToroidalMap.ToroidalDistance(playerPos, anchorCanon);
            if (found && dist >= bestDist)
                return;

            found = true;
            bestDist = dist;
            outAnchor = anchorCanon;
            outOffsetB = new Vector3(offP.x, 0f, offP.z);
            outOffsetC = new Vector3(offQ.x, 0f, offQ.z);
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
