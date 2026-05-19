using UnityEngine;
using System.Collections.Generic;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Draws triangle territories inside the minimap UI (vertices at each planet's gem moon),
    /// using the same data as the world-space PlanetConnectionSystem.
    /// Attach this to a RectTransform that sits on top of the minimap and lives under a
    /// Canvas with TitanOrbitShapesCanvas (ImmediateModeCanvas).
    /// </summary>
    public class MinimapConnectionsShapesPanel : ImmediateModePanel
    {
        [Header("Style")]
        [SerializeField] private float triangleAlpha = 0.22f;
        [SerializeField] private float triangleBorderThickness = 2.5f;
        [SerializeField] private float triangleBorderAlpha = 0.75f;

        [Header("Debug (enable to diagnose why lines/triangles don't show)")]
        [SerializeField] private bool debugLog = true;

        private MinimapController minimap;
        private static bool _loggedNoCanvas;
        private static bool _loggedNoConn;
        private static bool _loggedNoData;
        private static bool _loggedRectSmall;
        private static bool _loggedDrawing;
        private static bool _loggedDrewOnce;

        private readonly List<Vector2> _triangleClipScratch = new List<Vector2>(8);
        private const int DiskFanSlices = 28;

        private void Awake()
        {
            if (minimap == null)
                minimap = GetComponentInParent<MinimapController>();
        }

        public override void OnEnable()
        {
            base.OnEnable();
            if (debugLog && !Valid && !_loggedNoCanvas)
            {
                _loggedNoCanvas = true;
                Debug.LogWarning("[MinimapConnections] Panel has no ImmediateModeCanvas in parent. Add TitanOrbitShapesCanvas to the Canvas that contains the Minimap (e.g. run Titan Orbit > Setup Game Scene or Quick Setup).");
            }
        }

        public override void DrawPanelShapes(Rect rect, ImCanvasContext ctx)
        {
            if (debugLog && !_loggedDrawing)
            {
                _loggedDrawing = true;
                Debug.Log($"[MinimapConnections] DrawPanelShapes called. rect={rect.width}x{rect.height} center={rect.center}");
            }
            if (rect.width < 1f || rect.height < 1f)
            {
                if (debugLog && !_loggedRectSmall)
                {
                    _loggedRectSmall = true;
                    Debug.LogWarning($"[MinimapConnections] Skipped: rect too small ({rect.width}x{rect.height}). Is the panel under the Minimap with stretch anchors?");
                }
                return;
            }
            if (minimap == null)
                minimap = GetComponentInParent<MinimapController>();
            if (minimap == null)
                return;

            var conn = PlanetConnectionSystem.Instance;
            if (conn == null)
            {
                if (debugLog && !_loggedNoConn)
                {
                    _loggedNoConn = true;
                    Debug.LogWarning("[MinimapConnections] Skipped: no PlanetConnectionSystem in scene. Ensure GameManager/network is running and map has planets.");
                }
                return;
            }

            var triangles = conn.CurrentTriangles;
            if (triangles == null || triangles.Count == 0)
            {
                if (debugLog && !_loggedNoData)
                {
                    _loggedNoData = true;
                    Debug.LogWarning("[MinimapConnections] Skipped: no triangles. Need at least 3 same-team planets for a territory.");
                }
                return;
            }

            Vector3 playerPos = minimap.PlayerPosition;
            float radius = Mathf.Max(1f, minimap.MinimapRadius);

            Draw.ResetAllDrawStates();
            Draw.ThicknessSpace = ThicknessSpace.Pixels;
            Draw.LineGeometry = LineGeometry.Flat2D;

            float halfW = rect.width * 0.5f;
            float halfH = rect.height * 0.5f;
            Vector2 center = rect.center;
            float circleRadius = Mathf.Min(halfW, halfH);
            // Use DisplaySize/2 for projection so positions match blips (same as MinimapConnectionsUI)
            float displayHalf = minimap.DisplaySize * 0.5f;
            float scaleX = displayHalf / radius;
            float scaleZ = displayHalf / radius;
            Vector2 stableCenter = rect.center;

            foreach (var tri in triangles)
            {
                if (tri.A == null || tri.B == null || tri.C == null)
                    continue;

                PlanetConnectionSystem.GetStableTriangleOrder(tri, out Planet anchor, out Planet b, out Planet c);
                Vector3 aCanon = PlanetConnectionSystem.GetTriangleVertexToroidalPosition(anchor);
                Vector2 bLocal = ToroidalMap.ShortestOffsetXZ(aCanon, PlanetConnectionSystem.GetTriangleVertexToroidalPosition(b));
                Vector2 cLocal = ToroidalMap.ShortestOffsetXZ(aCanon, PlanetConnectionSystem.GetTriangleVertexToroidalPosition(c));
                if (!TryProjectWorldToMinimap(rect, playerPos, radius, aCanon, out Vector2 pa, out _))
                    continue;
                Vector2 pb = pa + new Vector2(bLocal.x * scaleX, bLocal.y * scaleZ);
                Vector2 pc = pa + new Vector2(cLocal.x * scaleX, cLocal.y * scaleZ);

                Color baseColor = TeamManager.GetTeamColor(tri.Team);
                Color fillColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha);
                Color borderColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha);

                DrawTriangleClippedToCircle(stableCenter, circleRadius, pa, pb, pc, fillColor, borderColor);
            }

            if (debugLog && _loggedDrawing && !_loggedDrewOnce)
            {
                _loggedDrewOnce = true;
                Debug.Log($"[MinimapConnections] Drew {triangles.Count} moon-vertex triangles. Ensure Canvas has TitanOrbitShapesCanvas and Minimap is under that Canvas.");
            }
        }

        private bool TryProjectWorldToMinimap(Rect rect, Vector3 playerPos, float radius, Vector3 worldPos, out Vector2 panelPos, out bool insideRadius)
        {
            panelPos = Vector2.zero;
            insideRadius = false;

            if (minimap == null)
                return false;

            Vector3 playerCanonical = ToroidalMap.WrapPosition(playerPos);
            Vector3 worldCanonical = ToroidalMap.WrapPosition(worldPos);
            minimap.GetToroidalDeltaForMinimap(playerCanonical, worldCanonical, out float dx, out float dz);
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            insideRadius = dist <= radius;

            if (radius <= 0.001f)
                return false;

            float normX = dx / radius;
            float normZ = dz / radius;

            // Use DisplaySize/2 so positions match blips (blips use normX * displaySize/2)
            float displayHalf = minimap.DisplaySize * 0.5f;
            Vector2 offset = new Vector2(normX * displayHalf, normZ * displayHalf);
            panelPos = rect.center + offset;
            return true;
        }

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

        private void DrawTriangleClippedToCircle(Vector2 center, float radius, Vector2 a, Vector2 b, Vector2 c, Color fillColor, Color borderColor)
        {
            float r2 = radius * radius;
            bool aIn = (a - center).sqrMagnitude <= r2;
            bool bIn = (b - center).sqrMagnitude <= r2;
            bool cIn = (c - center).sqrMagnitude <= r2;
            int nIn = (aIn ? 1 : 0) + (bIn ? 1 : 0) + (cIn ? 1 : 0);
            if (nIn == 0)
            {
                DrawTriangleCircleOverlapNoVerticesInside(center, radius, a, b, c, fillColor);
                return;
            }
            if (nIn == 3)
            {
                Draw.Triangle(a, b, c, fillColor);
                Draw.TriangleBorder(a, b, c, triangleBorderThickness, borderColor);
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
                Draw.Triangle(v0, p1, p2, fillColor);
                Draw.TriangleBorder(v0, p1, p2, triangleBorderThickness, borderColor);
                return;
            }
            Vector2 in0, in1, outPt;
            if (aIn && bIn) { in0 = a; in1 = b; outPt = c; }
            else if (aIn && cIn) { in0 = a; in1 = c; outPt = b; }
            else { in0 = b; in1 = c; outPt = a; }
            if (!ClipSegmentToCircle(center, radius, outPt, in0, out Vector2 q0a, out Vector2 q0b) ||
                !ClipSegmentToCircle(center, radius, outPt, in1, out Vector2 q1a, out Vector2 q1b))
                return;
            Vector2 q0 = (q0a - in0).sqrMagnitude < 1e-6f ? q0b : q0a;
            Vector2 q1 = (q1a - in1).sqrMagnitude < 1e-6f ? q1b : q1a;
            Draw.Triangle(in0, in1, q1, fillColor);
            Draw.Triangle(in0, q1, q0, fillColor);
            Draw.TriangleBorder(in0, in1, q1, triangleBorderThickness, borderColor);
            Draw.TriangleBorder(in0, q1, q0, triangleBorderThickness, borderColor);
        }

        private void DrawTriangleCircleOverlapNoVerticesInside(Vector2 center, float radius, Vector2 a, Vector2 b, Vector2 c, Color fillColor)
        {
            if (PointInTriangleMinimap(center, a, b, c))
            {
                DrawDiskTrianglePolarFanShapes(center, radius, a, b, c, fillColor);
                return;
            }

            _triangleClipScratch.Clear();
            CollectEdgeCircleIntersectionsShapes(center, radius, a, b, _triangleClipScratch);
            CollectEdgeCircleIntersectionsShapes(center, radius, b, c, _triangleClipScratch);
            CollectEdgeCircleIntersectionsShapes(center, radius, c, a, _triangleClipScratch);

            int n = _triangleClipScratch.Count;
            if (n < 2)
                return;

            if (n == 2)
            {
                DrawCircularSegmentInsideTriangleShapes(center, radius, _triangleClipScratch[0], _triangleClipScratch[1], a, b, c, fillColor);
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
                Draw.Triangle(hub, _triangleClipScratch[i], _triangleClipScratch[j], fillColor);
            }
        }

        private void DrawDiskTrianglePolarFanShapes(Vector2 center, float radius, Vector2 a, Vector2 b, Vector2 c, Color fillColor)
        {
            const float eps = 1e-5f;
            for (int i = 0; i < DiskFanSlices; i++)
            {
                float t0 = (i / (float)DiskFanSlices) * (2f * Mathf.PI);
                float t1 = ((i + 1) / (float)DiskFanSlices) * (2f * Mathf.PI);
                Vector2 d0 = new Vector2(Mathf.Cos(t0), Mathf.Sin(t0));
                Vector2 d1 = new Vector2(Mathf.Cos(t1), Mathf.Sin(t1));
                float e0 = MinPositiveRayTriangleExitShapes(center, d0, a, b, c);
                float e1 = MinPositiveRayTriangleExitShapes(center, d1, a, b, c);
                if (float.IsPositiveInfinity(e0) || float.IsPositiveInfinity(e1))
                    continue;
                float len0 = Mathf.Min(radius, e0);
                float len1 = Mathf.Min(radius, e1);
                if (len0 < eps || len1 < eps)
                    continue;
                Vector2 p0 = center + d0 * len0;
                Vector2 p1 = center + d1 * len1;
                Draw.Triangle(center, p0, p1, fillColor);
            }
        }

        private void DrawCircularSegmentInsideTriangleShapes(Vector2 center, float radius, Vector2 p, Vector2 q, Vector2 ta, Vector2 tb, Vector2 tc, Color fillColor)
        {
            float ap = Mathf.Atan2(p.y - center.y, p.x - center.x);
            float aq = Mathf.Atan2(q.y - center.y, q.x - center.x);
            float daShortDeg = Mathf.DeltaAngle(ap * Mathf.Rad2Deg, aq * Mathf.Rad2Deg);
            float daShort = daShortDeg * Mathf.Deg2Rad;
            float daLong = daShort > 0f ? daShort - 2f * Mathf.PI : daShort + 2f * Mathf.PI;
            float da = PickCircularArcInsideTriangleShapes(center, radius, ap, daShort, daLong, ta, tb, tc);
            const int steps = 14;
            Vector2 hub = (p + q) * 0.5f;
            Vector2 prev = p;
            for (int s = 1; s <= steps; s++)
            {
                float ft = s / (float)steps;
                float ang = ap + da * ft;
                Vector2 cur = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius;
                Draw.Triangle(hub, prev, cur, fillColor);
                prev = cur;
            }
        }

        private static float PickCircularArcInsideTriangleShapes(Vector2 center, float radius, float ap, float daShort, float daLong, Vector2 ta, Vector2 tb, Vector2 tc)
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

        private static void CollectEdgeCircleIntersectionsShapes(Vector2 center, float radius, Vector2 va, Vector2 vb, List<Vector2> outPts)
        {
            float r2 = radius * radius;
            if ((va - center).sqrMagnitude <= r2 || (vb - center).sqrMagnitude <= r2)
                return;
            if (!ClipSegmentToCircle(center, radius, va, vb, out Vector2 p0, out Vector2 p1))
                return;
            AppendUniqueClipPointShapes(outPts, p0);
            if ((p1 - p0).sqrMagnitude > 1e-6f)
                AppendUniqueClipPointShapes(outPts, p1);
        }

        private static void AppendUniqueClipPointShapes(List<Vector2> list, Vector2 p)
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

        private static float Cross2Shapes(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        private static float MinPositiveRayTriangleExitShapes(Vector2 o, Vector2 dir, Vector2 a, Vector2 b, Vector2 c)
        {
            float best = float.PositiveInfinity;
            best = Mathf.Min(best, RaySegmentPositiveTShapes(o, dir, a, b));
            best = Mathf.Min(best, RaySegmentPositiveTShapes(o, dir, b, c));
            best = Mathf.Min(best, RaySegmentPositiveTShapes(o, dir, c, a));
            return best;
        }

        private static float RaySegmentPositiveTShapes(Vector2 o, Vector2 d, Vector2 p0, Vector2 p1)
        {
            Vector2 ab = p1 - p0;
            float det = Cross2Shapes(d, ab);
            if (Mathf.Abs(det) < 1e-10f)
                return float.PositiveInfinity;
            float t = Cross2Shapes(p0 - o, ab) / det;
            float u = Cross2Shapes(p0 - o, d) / det;
            if (t >= 0f && u >= 0f && u <= 1f)
                return t;
            return float.PositiveInfinity;
        }
    }
}

