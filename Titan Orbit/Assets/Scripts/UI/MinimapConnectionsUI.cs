using UnityEngine;
using UnityEngine.UI;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using System;
using System.Collections.Generic;
using System.IO;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Draws planet connection lines and triangle territories on the minimap using standard Unity UI.
    /// Uses RawImage so the Canvas always includes it in rebuilds and calls OnPopulateMesh.
    /// Team-colored using TeamManager.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class MinimapConnectionsUI : RawImage
    {
        [Header("Style")]
        [SerializeField] private float lineThicknessPx = 4f;
        [SerializeField] private float triangleAlpha = 0.22f;
        [SerializeField] private float triangleBorderPx = 2f;
        [SerializeField] private float triangleBorderAlpha = 0.75f;

        private MinimapController _minimap;

        /// <summary>Cached connection counts so we only mark dirty when data or player position changes (avoids full mesh rebuild every frame).</summary>
        private int _lastEdgesCount = -1;
        private int _lastTrianglesCount = -1;
        private Vector3 _lastPlayerPosition;
        private float _lastMinimapRadius = -1f;
        private bool _havePlayerSampleForConnections;
        private float _lastDisplaySize = -1f;

        private readonly List<Vector2> _triangleClipScratch = new List<Vector2>(8);
        private const int DiskFanSlices = 28;

        private static Texture2D _whiteTex;
        private static Texture2D WhiteTex => _whiteTex != null ? _whiteTex : (_whiteTex = CreateWhiteTex());
        private static Texture2D CreateWhiteTex()
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, Color.white);
            t.Apply();
            return t;
        }

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
            texture = WhiteTex;
            color = Color.white;
            _minimap = GetComponentInParent<MinimapController>();
            // #region agent log
            DebugLog("MinimapConnectionsUI.cs:Awake", "Awake", "{\"hasMinimap\":" + (_minimap != null) + "}", "H1");
            // #endregion
        }

        // #region agent log
        static int _populateCallCount;
        static void DebugLog(string location, string message, string dataJson, string hypothesisId)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "debug-441e0e.log");
                string escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string line = "{\"sessionId\":\"441e0e\",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"location\":\"" + location + "\",\"message\":\"" + escaped + "\",\"data\":" + (string.IsNullOrEmpty(dataJson) ? "{}" : dataJson) + ",\"hypothesisId\":\"" + hypothesisId + "\"}\n";
                File.AppendAllText(path, line);
                if (_populateCallCount <= 5) UnityEngine.Debug.Log("[MinimapConnections] " + message + " " + dataJson);
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[MinimapConnections] Log failed: " + ex.Message); }
        }
        // #endregion

        private void Update()
        {
            var conn = PlanetConnectionSystem.Instance;
            int ec = conn != null && conn.CurrentEdges != null ? conn.CurrentEdges.Count : 0;
            int tc = conn != null && conn.CurrentTriangles != null ? conn.CurrentTriangles.Count : 0;

            if (ec == 0 && tc == 0)
            {
                if (_lastEdgesCount > 0 || _lastTrianglesCount > 0)
                {
                    _lastEdgesCount = 0;
                    _lastTrianglesCount = 0;
                    _havePlayerSampleForConnections = false;
                    SetVerticesDirty();
                }
                return;
            }

            Vector3 playerPos = _minimap != null ? _minimap.PlayerPosition : Vector3.zero;
            float radius = _minimap != null ? Mathf.Max(1f, _minimap.MinimapRadius) : 1f;
            bool dataChanged = ec != _lastEdgesCount || tc != _lastTrianglesCount;
            // Float position never equals last frame exactly — that was marking dirty every frame and
            // forcing Canvas/mesh rebuilds that make planet lines + TMP shimmer. Only redraw when the
            // ship moves enough to shift the minimap projection by ~half a pixel (XZ only).
            float disp = _minimap != null ? Mathf.Max(1f, _minimap.DisplaySize) : 150f;
            bool displayChanged = Mathf.Abs(disp - _lastDisplaySize) > 0.5f;
            float worldPerHalfPx = radius / disp;
            float threshSq = worldPerHalfPx * worldPerHalfPx;
            Vector3 delta = playerPos - _lastPlayerPosition;
            float dxzSq = delta.x * delta.x + delta.z * delta.z;
            bool significantMove = dxzSq > threshSq || Mathf.Abs(radius - _lastMinimapRadius) > 0.01f;
            bool playerMoved = (ec > 0 || tc > 0) && (!_havePlayerSampleForConnections || significantMove);
            if (dataChanged || playerMoved || displayChanged)
            {
                _lastEdgesCount = ec;
                _lastTrianglesCount = tc;
                _lastPlayerPosition = playerPos;
                _lastMinimapRadius = radius;
                _lastDisplaySize = disp;
                _havePlayerSampleForConnections = true;
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            float startTime = Time.realtimeSinceStartup;
            vh.Clear();
            var conn = PlanetConnectionSystem.Instance;
            int ec = conn != null && conn.CurrentEdges != null ? conn.CurrentEdges.Count : 0;
            int tc = conn != null && conn.CurrentTriangles != null ? conn.CurrentTriangles.Count : 0;
            if (ec == 0 && tc == 0)
                return;

            // #region agent log
            int frame = Time.frameCount;
            if (frame % 60 == 0)
            {
                int verts = vh.currentVertCount;
                float durMsSoFar = (Time.realtimeSinceStartup - startTime) * 1000f;
                TitanOrbit.Core.DebugSessionLog.Write(
                    "MinimapConnectionsUI.OnPopulateMesh",
                    "mesh rebuild",
                    "{\"frame\":" + frame + ",\"edges\":" + ec + ",\"triangles\":" + tc + ",\"vertsBeforeClear\":" + verts + ",\"durationMs\":" + durMsSoFar + "}",
                    "A");
            }
            // #endregion
            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();
            if (_minimap == null)
            {
                // #region agent log
                DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "early return: minimap null", "{}", "H1");
                // #endregion
                return;
            }

            if (conn == null)
            {
                // #region agent log
                DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "early return: PlanetConnectionSystem.Instance null", "{}", "H2");
                // #endregion
                return;
            }

            var edges = conn.CurrentEdges;
            var triangles = conn.CurrentTriangles;
            ec = edges?.Count ?? 0;
            tc = triangles?.Count ?? 0;
            // #region agent log
            // (vertCount logged at end of method)
            // #endregion
            if ((edges == null || edges.Count == 0) && (triangles == null || triangles.Count == 0))
            {
                // #region agent log
                DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "early return: no edges and no triangles", "{\"edges\":" + ec + ",\"triangles\":" + tc + "}", "H2");
                // #endregion
                return;
            }

            Rect r = rectTransform.rect;
            // #region agent log
            if (_populateCallCount <= 5) DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "rect", "{\"rectW\":" + r.width + ",\"rectH\":" + r.height + ",\"centerX\":" + r.center.x + ",\"centerY\":" + r.center.y + "}", "H4");
            // #endregion
            if (r.width < 1f || r.height < 1f)
            {
                // #region agent log
                DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "early return: rect too small", "{\"rectW\":" + r.width + ",\"rectH\":" + r.height + "}", "H4");
                // #endregion
                return;
            }

            Vector3 playerPos = _minimap.PlayerPosition;
            float radius = Mathf.Max(1f, _minimap.MinimapRadius);
            float halfW = r.width * 0.5f;
            float halfH = r.height * 0.5f;
            Vector2 center = r.center;
            float circleRadius = Mathf.Min(halfW, halfH);
            // Use DisplaySize/2 for projection so triangle positions match blips (blips use normX * displaySize/2)
            float displayHalf = _minimap.DisplaySize * 0.5f;
            // Use stable center: content pivot is (0.5,0.5) so local center is (0,0); avoids flip when rect layout varies
            Vector2 stableCenter = Vector2.zero;

            // Triangles first (fill then border), then lines
            if (triangles != null)
            {
                foreach (var tri in triangles)
                {
                    if (tri.A == null || tri.B == null || tri.C == null) continue;
                    PlanetConnectionSystem.GetStableTriangleOrder(tri, out Planet anchor, out Planet b, out Planet c);
                    Vector3 aCanon = anchor.ToroidalPosition;
                    Vector2 bLocal = ToroidalMap.ShortestOffsetXZ(aCanon, b.ToroidalPosition);
                    Vector2 cLocal = ToroidalMap.ShortestOffsetXZ(aCanon, c.ToroidalPosition);
                    if (!TryProject(stableCenter, displayHalf, displayHalf, playerPos, radius, aCanon, out Vector2 pa, out _)) continue;
                    float scaleX = displayHalf / radius;
                    float scaleZ = displayHalf / radius;
                    Vector2 pb = pa + new Vector2(bLocal.x * scaleX, bLocal.y * scaleZ);
                    Vector2 pc = pa + new Vector2(cLocal.x * scaleX, cLocal.y * scaleZ);
                    // Do not cull when all vertices are outside the minimap circle: the triangle can still
                    // overlap the view (e.g. player inside territory). AddTriangleClippedToCircle handles that.

                    Color baseColor = TeamManager.GetTeamColor(tri.Team);
                    Color fillColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha);
                    Color borderColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha);

                    AddTriangleClippedToCircle(vh, stableCenter, circleRadius, pa, pb, pc, fillColor);
                    if (ClipSegmentToCircle(stableCenter, circleRadius, pa, pb, out Vector2 paPb, out Vector2 pbPa))
                        AddLine(vh, paPb, pbPa, triangleBorderPx, borderColor);
                    if (ClipSegmentToCircle(stableCenter, circleRadius, pb, pc, out Vector2 pbPc, out Vector2 pcPb))
                        AddLine(vh, pbPc, pcPb, triangleBorderPx, borderColor);
                    if (ClipSegmentToCircle(stableCenter, circleRadius, pc, pa, out Vector2 pcPa, out Vector2 paPc))
                        AddLine(vh, pcPa, paPc, triangleBorderPx, borderColor);
                }
            }

            if (edges != null)
            {
                foreach (var e in edges)
                {
                    if (e.A == null || e.B == null) continue;
                    PlanetConnectionSystem.GetStableEdgeOrder(e, out Planet ea, out Planet eb);
                    Vector3 aCanon = ea.ToroidalPosition;
                    Vector2 bLocal = ToroidalMap.ShortestOffsetXZ(aCanon, eb.ToroidalPosition);
                    if (!TryProject(stableCenter, displayHalf, displayHalf, playerPos, radius, aCanon, out Vector2 pa, out _)) continue;
                    float scaleX = displayHalf / radius;
                    float scaleZ = displayHalf / radius;
                    Vector2 pb = pa + new Vector2(bLocal.x * scaleX, bLocal.y * scaleZ);
                    // Segment can cross the minimap circle even when both endpoints are outside.
                    Color lineColor = TeamManager.GetTeamColor(e.Team);
                    if (ClipSegmentToCircle(stableCenter, circleRadius, pa, pb, out Vector2 paOut, out Vector2 pbOut))
                        AddLine(vh, paOut, pbOut, lineThicknessPx, lineColor);
                }
            }

            // #region agent log
            if (frame % 60 == 0)
            {
                float totalDurMs = (Time.realtimeSinceStartup - startTime) * 1000f;
                TitanOrbit.Core.DebugSessionLog.Write(
                    "MinimapConnectionsUI.OnPopulateMesh",
                    "after populate",
                    "{\"frame\":" + frame + ",\"edges\":" + ec + ",\"triangles\":" + tc + ",\"vertCount\":" + vh.currentVertCount + ",\"durationMs\":" + totalDurMs + "}",
                    "A");
            }
            if (_populateCallCount <= 5) DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "after populate", "{\"vertCount\":" + vh.currentVertCount + "}", "H3");
            if (vh.currentVertCount > 0 && _populateCallCount == 1)
                UnityEngine.Debug.Log("[MinimapConnections] Minimap is drawing " + (triangles?.Count ?? 0) + " triangles and " + (edges?.Count ?? 0) + " lines (same data as main map).");
            // #endregion
        }

        private bool TryProject(Vector2 center, float halfW, float halfH, Vector3 playerPos, float radius, Vector3 worldPos,
            out Vector2 localPos, out bool insideRadius)
        {
            localPos = center;
            insideRadius = false;
            if (_minimap == null || radius <= 0.001f) return false;

            Vector3 playerCanonical = ToroidalMap.WrapPosition(playerPos);
            Vector3 worldCanonical = ToroidalMap.WrapPosition(worldPos);
            _minimap.GetToroidalDeltaForMinimap(playerCanonical, worldCanonical, out float dx, out float dz);
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            insideRadius = dist <= radius;
            float normX = dx / radius;
            float normZ = dz / radius;
            localPos = center + new Vector2(normX * halfW, normZ * halfH);
            return true;
        }

        private void AddTriangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int baseIdx = vh.currentVertCount;
            var vert = UIVertex.simpleVert;
            vert.color = color;
            vert.uv0 = Vector2.zero;
            vert.position = new Vector3(a.x, a.y, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(b.x, b.y, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(c.x, c.y, 0f);
            vh.AddVert(vert);
            vh.AddTriangle(baseIdx, baseIdx + 1, baseIdx + 2);
        }

        /// <summary>Clip line segment to circle. Returns true if any part of the segment is inside; outputs clipped segment.</summary>
        private static bool ClipSegmentToCircle(Vector2 center, float radius, Vector2 a, Vector2 b, out Vector2 aOut, out Vector2 bOut)
        {
            aOut = a;
            bOut = b;
            float r2 = radius * radius;
            bool aIn = (a - center).sqrMagnitude <= r2;
            bool bIn = (b - center).sqrMagnitude <= r2;
            if (aIn && bIn) return true;
            if (!aIn && !bIn)
            {
                Vector2 d = b - a;
                Vector2 o = a - center;
                float A = d.sqrMagnitude;
                if (A < 1e-10f) return false;
                float B = 2f * (o.x * d.x + o.y * d.y);
                float C = o.sqrMagnitude - r2;
                float disc = B * B - 4f * A * C;
                if (disc < 0f) return false;
                float sqrt = Mathf.Sqrt(disc);
                float t0 = (-B - sqrt) / (2f * A);
                float t1 = (-B + sqrt) / (2f * A);
                float tLo = Mathf.Clamp01(Mathf.Min(t0, t1));
                float tHi = Mathf.Clamp01(Mathf.Max(t0, t1));
                if (tLo >= tHi) return false;
                aOut = a + (b - a) * tLo;
                bOut = a + (b - a) * tHi;
                return true;
            }
            // One inside, one outside: one intersection on the segment
            Vector2 seg = b - a;
            Vector2 toA = a - center;
            float segSq = seg.sqrMagnitude;
            if (segSq < 1e-10f) return aIn;
            float B2 = 2f * (toA.x * seg.x + toA.y * seg.y);
            float C2 = toA.sqrMagnitude - r2;
            float disc2 = B2 * B2 - 4f * segSq * C2;
            if (disc2 < 0f) return aIn || bIn;
            float sqrt2 = Mathf.Sqrt(disc2);
            float tA = (-B2 - sqrt2) / (2f * segSq);
            float tB = (-B2 + sqrt2) / (2f * segSq);
            float tHit = (tA >= 0f && tA <= 1f) ? tA : tB;
            if (tHit < 0f || tHit > 1f) return aIn || bIn;
            if (aIn) bOut = a + seg * tHit;
            else aOut = a + seg * tHit;
            return true;
        }

        /// <summary>Clip triangle to circle and add resulting geometry. Center/radius in same space as a,b,c.</summary>
        private void AddTriangleClippedToCircle(VertexHelper vh, Vector2 center, float radius, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            float r2 = radius * radius;
            bool aIn = (a - center).sqrMagnitude <= r2;
            bool bIn = (b - center).sqrMagnitude <= r2;
            bool cIn = (c - center).sqrMagnitude <= r2;
            int nIn = (aIn ? 1 : 0) + (bIn ? 1 : 0) + (cIn ? 1 : 0);
            if (nIn == 0)
            {
                AddTriangleCircleOverlapNoVerticesInside(vh, center, radius, a, b, c, color);
                return;
            }
            if (nIn == 3)
            {
                AddTriangle(vh, a, b, c, color);
                return;
            }
            if (nIn == 1)
            {
                Vector2 v0 = aIn ? a : (bIn ? b : c);
                Vector2 v1 = aIn ? b : (bIn ? c : a);
                Vector2 v2 = aIn ? c : (bIn ? a : b);
                if (!ClipSegmentToCircle(center, radius, v0, v1, out Vector2 p1a, out Vector2 p1b)) return;
                if (!ClipSegmentToCircle(center, radius, v0, v2, out Vector2 p2a, out Vector2 p2b)) return;
                Vector2 p1 = (p1a - v0).sqrMagnitude < 1e-6f ? p1b : p1a;
                Vector2 p2 = (p2a - v0).sqrMagnitude < 1e-6f ? p2b : p2a;
                AddTriangle(vh, v0, p1, p2, color);
                return;
            }
            // nIn == 2
            Vector2 in0, in1, outPt;
            if (aIn && bIn) { in0 = a; in1 = b; outPt = c; }
            else if (aIn && cIn) { in0 = a; in1 = c; outPt = b; }
            else { in0 = b; in1 = c; outPt = a; }
            if (!ClipSegmentToCircle(center, radius, outPt, in0, out Vector2 q0a, out Vector2 q0b) ||
                !ClipSegmentToCircle(center, radius, outPt, in1, out Vector2 q1a, out Vector2 q1b))
                return;
            Vector2 q0 = (q0a - in0).sqrMagnitude < 1e-6f ? q0b : q0a;
            Vector2 q1 = (q1a - in1).sqrMagnitude < 1e-6f ? q1b : q1a;
            AddTriangle(vh, in0, in1, q1, color);
            AddTriangle(vh, in0, q1, q0, color);
        }

        /// <summary>Triangle ∩ disk when every vertex is outside the disk (still can overlap the minimap).</summary>
        private void AddTriangleCircleOverlapNoVerticesInside(VertexHelper vh, Vector2 center, float radius, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            if (PointInTriangleMinimap(center, a, b, c))
            {
                AddDiskTrianglePolarFan(vh, center, radius, a, b, c, color);
                return;
            }

            _triangleClipScratch.Clear();
            CollectEdgeCircleIntersections(center, radius, a, b, _triangleClipScratch);
            CollectEdgeCircleIntersections(center, radius, b, c, _triangleClipScratch);
            CollectEdgeCircleIntersections(center, radius, c, a, _triangleClipScratch);

            int n = _triangleClipScratch.Count;
            if (n < 2)
                return;

            if (n == 2)
            {
                AddCircularSegmentInsideTriangle(vh, center, radius, _triangleClipScratch[0], _triangleClipScratch[1], a, b, c, color);
                return;
            }

            _triangleClipScratch.Sort((p, q) =>
            {
                float ap = Mathf.Atan2(p.y - center.y, p.x - center.x);
                float aq = Mathf.Atan2(q.y - center.y, q.x - center.x);
                return ap.CompareTo(aq);
            });

            Vector2 hub = Vector2.zero;
            for (int i = 0; i < n; i++)
                hub += _triangleClipScratch[i];
            hub /= n;
            if (!PointInTriangleMinimap(hub, a, b, c))
                hub = (a + b + c) / 3f;

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                AddTriangle(vh, hub, _triangleClipScratch[i], _triangleClipScratch[j], color);
            }
        }

        private static void CollectEdgeCircleIntersections(Vector2 center, float radius, Vector2 va, Vector2 vb, List<Vector2> outPts)
        {
            float r2 = radius * radius;
            if ((va - center).sqrMagnitude <= r2 || (vb - center).sqrMagnitude <= r2)
                return;
            if (!ClipSegmentToCircle(center, radius, va, vb, out Vector2 p0, out Vector2 p1))
                return;
            AppendUniqueClipPoint(outPts, p0);
            if ((p1 - p0).sqrMagnitude > 1e-6f)
                AppendUniqueClipPoint(outPts, p1);
        }

        private static void AppendUniqueClipPoint(List<Vector2> list, Vector2 p)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i] - p).sqrMagnitude < 2e-5f)
                    return;
            }
            list.Add(p);
        }

        private static bool PointInTriangleMinimap(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = SignMinimap(p, a, b);
            float d2 = SignMinimap(p, b, c);
            float d3 = SignMinimap(p, c, a);
            bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNeg && hasPos);
        }

        private static float SignMinimap(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }

        private static float Cross2(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        private void AddDiskTrianglePolarFan(VertexHelper vh, Vector2 center, float radius, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            const float eps = 1e-5f;
            for (int i = 0; i < DiskFanSlices; i++)
            {
                float t0 = (i / (float)DiskFanSlices) * (2f * Mathf.PI);
                float t1 = ((i + 1) / (float)DiskFanSlices) * (2f * Mathf.PI);
                Vector2 d0 = new Vector2(Mathf.Cos(t0), Mathf.Sin(t0));
                Vector2 d1 = new Vector2(Mathf.Cos(t1), Mathf.Sin(t1));
                float e0 = MinPositiveRayTriangleExit(center, d0, a, b, c);
                float e1 = MinPositiveRayTriangleExit(center, d1, a, b, c);
                if (float.IsPositiveInfinity(e0) || float.IsPositiveInfinity(e1))
                    continue;
                float len0 = Mathf.Min(radius, e0);
                float len1 = Mathf.Min(radius, e1);
                if (len0 < eps || len1 < eps)
                    continue;
                Vector2 p0 = center + d0 * len0;
                Vector2 p1 = center + d1 * len1;
                AddTriangle(vh, center, p0, p1, color);
            }
        }

        private static float MinPositiveRayTriangleExit(Vector2 o, Vector2 dir, Vector2 a, Vector2 b, Vector2 c)
        {
            float best = float.PositiveInfinity;
            best = Mathf.Min(best, RaySegmentPositiveT(o, dir, a, b));
            best = Mathf.Min(best, RaySegmentPositiveT(o, dir, b, c));
            best = Mathf.Min(best, RaySegmentPositiveT(o, dir, c, a));
            return best;
        }

        private static float RaySegmentPositiveT(Vector2 o, Vector2 d, Vector2 p0, Vector2 p1)
        {
            Vector2 ab = p1 - p0;
            float det = Cross2(d, ab);
            if (Mathf.Abs(det) < 1e-10f)
                return float.PositiveInfinity;
            float t = Cross2(p0 - o, ab) / det;
            float u = Cross2(p0 - o, d) / det;
            if (t >= 0f && u >= 0f && u <= 1f)
                return t;
            return float.PositiveInfinity;
        }

        private void AddCircularSegmentInsideTriangle(VertexHelper vh, Vector2 center, float radius, Vector2 p, Vector2 q, Vector2 ta, Vector2 tb, Vector2 tc, Color color)
        {
            float ap = Mathf.Atan2(p.y - center.y, p.x - center.x);
            float aq = Mathf.Atan2(q.y - center.y, q.x - center.x);
            float daShortDeg = Mathf.DeltaAngle(ap * Mathf.Rad2Deg, aq * Mathf.Rad2Deg);
            float daShort = daShortDeg * Mathf.Deg2Rad;
            float daLong = daShort > 0f ? daShort - 2f * Mathf.PI : daShort + 2f * Mathf.PI;

            float da = PickCircularArcInsideTriangle(center, radius, ap, daShort, daLong, ta, tb, tc);
            const int steps = 14;
            Vector2 hub = (p + q) * 0.5f;
            Vector2 prev = p;
            for (int s = 1; s <= steps; s++)
            {
                float ft = s / (float)steps;
                float ang = ap + da * ft;
                Vector2 cur = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius;
                AddTriangle(vh, hub, prev, cur, color);
                prev = cur;
            }
        }

        private static float PickCircularArcInsideTriangle(Vector2 center, float radius, float ap, float daShort, float daLong, Vector2 ta, Vector2 tb, Vector2 tc)
        {
            Vector2 MidOnArc(float a0, float delta) => center + new Vector2(Mathf.Cos(a0 + delta * 0.5f), Mathf.Sin(a0 + delta * 0.5f)) * radius;

            Vector2 midS = MidOnArc(ap, daShort);
            Vector2 midL = MidOnArc(ap, daLong);
            bool inS = PointInTriangleMinimap(midS, ta, tb, tc);
            bool inL = PointInTriangleMinimap(midL, ta, tb, tc);
            if (inS && !inL)
                return daShort;
            if (inL && !inS)
                return daLong;
            if (inS && inL)
                return Mathf.Abs(daShort) <= Mathf.Abs(daLong) ? daShort : daLong;
            return daShort;
        }

        private void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float thicknessPx, Color color)
        {
            Vector2 dir = (b - a).normalized;
            if (dir.sqrMagnitude < 0.0001f) return;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            float half = thicknessPx * 0.5f;
            Vector2 p0 = a - perp * half;
            Vector2 p1 = a + perp * half;
            Vector2 p2 = b + perp * half;
            Vector2 p3 = b - perp * half;

            int baseIdx = vh.currentVertCount;
            var vert = UIVertex.simpleVert;
            vert.color = color;
            vert.uv0 = Vector2.zero;
            vert.position = new Vector3(p0.x, p0.y, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(p1.x, p1.y, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(p2.x, p2.y, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(p3.x, p3.y, 0f);
            vh.AddVert(vert);
            vh.AddTriangle(baseIdx, baseIdx + 1, baseIdx + 2);
            vh.AddTriangle(baseIdx, baseIdx + 2, baseIdx + 3);
        }
    }
}
