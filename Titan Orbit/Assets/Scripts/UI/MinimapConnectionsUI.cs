using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// UGUI mesh drawer for minimap territory triangles (gem-moon vertices).
    /// Uses <see cref="OnPopulateMesh"/> so triangles always render under the circular Mask —
    /// no dependency on Shapes <c>ImmediateModePanel</c> registration.
    /// When the minimap is expanded to the full map, also draws ±mapW / ±mapH wrap copies so
    /// toroidal territories read correctly across seams.
    /// Client presentation only.
    /// <para>
    /// [TITAN-ORBIT] Moon world verts are cached (~30 Hz). The UI mesh still rebuilds every frame
    /// while triangles exist so player-centered projection stays smooth (throttling the whole
    /// rebuild made triangles look choppy as the ship moved). Projection + wrap copies are cheap;
    /// moon ECS/proxy lookups were the hitch.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class MinimapConnectionsUI : RawImage
    {
        /// <summary>Fill alpha for minimap triangles.</summary>
        const float TriangleAlpha = 0.28f;

        /// <summary>Border alpha for triangle outlines.</summary>
        const float BorderAlpha = 0.85f;

        /// <summary>Border thickness in UI pixels.</summary>
        const float BorderThickness = 2.2f;

        /// <summary>How often we refresh moon world verts (not the projection mesh).</summary>
        const float MoonCacheIntervalSeconds = 1f / 30f;

        /// <summary>One triangle in canonical world space (anchor + shortest-path offsets for B/C).</summary>
        struct CachedWorldTriangle
        {
            public Vector3 Anchor;
            public Vector3 OffsetB;
            public Vector3 OffsetC;
            public Color Fill;
            public Color Border;
        }

        static Texture2D _whiteTex;

        MinimapController _minimap;
        readonly List<CachedWorldTriangle> _worldCache = new List<CachedWorldTriangle>(16);
        int _lastGraphRevision = -1;
        float _nextMoonCacheTime;
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
        /// Refresh moon verts on a short interval; dirty the mesh when the player/view moves or
        /// moons update — not every idle frame (Scripts hitch on VertexHelper rebuild).
        /// </summary>
        void LateUpdate()
        {
            int revision = PlanetConnectionGraphCache.ClientPublishRevision;
            int count = PlanetConnectionGraphCache.CurrentTriangles?.Count ?? 0;
            bool topologyChanged = revision != _lastGraphRevision || count != _lastDrawnCount;
            bool moonDue = Time.unscaledTime >= _nextMoonCacheTime;
            bool moonRebuilt = false;

            if (topologyChanged || (count > 0 && moonDue))
            {
                RebuildWorldCache();
                _lastGraphRevision = revision;
                _lastDrawnCount = count;
                _nextMoonCacheTime = Time.unscaledTime + MoonCacheIntervalSeconds;
                moonRebuilt = true;
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

            if (count > 0 && (topologyChanged || moonRebuilt || viewChanged))
            {
                _lastPlayerPos = playerPos;
                _lastRadius = radius;
                _lastExpanded = expanded;
                SetVerticesDirty();
            }
            else if (topologyChanged && count == 0)
            {
                SetVerticesDirty();
            }
        }

        /// <summary>
        /// Resolves gem-moon vertices into canonical world space once (expensive path).
        /// </summary>
        void RebuildWorldCache()
        {
            _worldCache.Clear();

            var triangles = PlanetConnectionGraphCache.CurrentTriangles;
            if (triangles == null || triangles.Count == 0)
                return;

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;
            var visualizer = EcsWorldVisualizer.Active;

            if (!PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double moonElapsed, includeTickFraction: true))
                moonElapsed = Time.timeAsDouble;

            for (int i = 0; i < triangles.Count; i++)
            {
                var tri = triangles[i];
                if (!PlanetConnectionShapesVisual.TryGetCanonicalMoonVertex(
                        em, visualizer, tri.PlanetIdA, moonElapsed, out Vector3 aCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalMoonVertex(
                        em, visualizer, tri.PlanetIdB, moonElapsed, out Vector3 bCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalMoonVertex(
                        em, visualizer, tri.PlanetIdC, moonElapsed, out Vector3 cCanon))
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
        }

        /// <summary>
        /// [UNITY] Projects cached world triangles into panel space (cheap — no moon lookups).
        /// </summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();
            if (_minimap == null || _worldCache.Count == 0)
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

            // --- Wrap copies only when expanded to (near) full map ---
            // [TITAN-ORBIT] Compact minimap rarely needs seam copies; 5× verts was a Scripts hitch.
            bool needWrapCopies = _minimap.IsExpanded ||
                                  radius >= 0.45f * Mathf.Min(mapW, mapH);
            int wrapCount = needWrapCopies ? 5 : 1;
            Vector3[] wrapOffsets =
            {
                Vector3.zero,
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH),
            };

            for (int i = 0; i < _worldCache.Count; i++)
            {
                var tri = _worldCache[i];
                for (int w = 0; w < wrapCount; w++)
                {
                    Vector3 aWorld = tri.Anchor + wrapOffsets[w];
                    Vector3 bWorld = aWorld + tri.OffsetB;
                    Vector3 cWorld = aWorld + tri.OffsetC;

                    if (!TryProject(rect, playerPos, radius, scale, aWorld, out Vector2 pa) ||
                        !TryProject(rect, playerPos, radius, scale, bWorld, out Vector2 pb) ||
                        !TryProject(rect, playerPos, radius, scale, cWorld, out Vector2 pc))
                        continue;

                    if (!AnyNearPanel(rect, pa, pb, pc))
                        continue;

                    AddTriangle(vh, pa, pb, pc, tri.Fill);
                    AddTriangleBorder(vh, pa, pb, pc, tri.Border, BorderThickness);
                }
            }
        }

        /// <summary>Projects a world XZ point into this RawImage's local rect (player-centered).</summary>
        bool TryProject(
            Rect rect, Vector3 playerPos, float radius, float scale, Vector3 worldPos, out Vector2 panelPos)
        {
            panelPos = default;
            if (_minimap == null || radius <= 0.001f)
                return false;

            Vector3 playerCanonical = ToroidalMap.WrapPosition(playerPos);
            float dx = worldPos.x - playerCanonical.x;
            float dz = worldPos.z - playerCanonical.z;
            panelPos = rect.center + new Vector2(dx * scale, dz * scale);
            return true;
        }

        /// <summary>True if any vertex is within a generous margin of the panel rect.</summary>
        static bool AnyNearPanel(Rect rect, Vector2 a, Vector2 b, Vector2 c)
        {
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
