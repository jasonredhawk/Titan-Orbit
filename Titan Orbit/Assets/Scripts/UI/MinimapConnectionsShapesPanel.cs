using UnityEngine;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Draws planet connection lines and triangle territories inside the minimap UI,
    /// using the same data as the world-space PlanetConnectionSystem.
    /// Attach this to a RectTransform that sits on top of the minimap and lives under a
    /// Canvas with TitanOrbitShapesCanvas (ImmediateModeCanvas).
    /// </summary>
    public class MinimapConnectionsShapesPanel : ImmediateModePanel
    {
        [Header("Style")]
        [SerializeField] private float lineThickness = 4f;
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

            var edges = conn.CurrentEdges;
            var triangles = conn.CurrentTriangles;
            if ((edges == null || edges.Count == 0) && (triangles == null || triangles.Count == 0))
            {
                if (debugLog && !_loggedNoData)
                {
                    _loggedNoData = true;
                    int ec = edges?.Count ?? 0, tc = triangles?.Count ?? 0;
                    Debug.LogWarning($"[MinimapConnections] Skipped: no edges or triangles ({ec} edges, {tc} triangles). Need at least 2 same-team planets for a connection.");
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

            // Triangles first (background), then borders, then lines.
            // Use unwrapped positions and clip to circle so geometry stays inside the minimap mask.
            if (triangles != null)
            {
                foreach (var tri in triangles)
                {
                    if (tri.A == null || tri.B == null || tri.C == null)
                        continue;

                    Vector3 aCanon = tri.A.ToroidalPosition;
                    Vector2 bLocal = ToroidalMap.ShortestOffsetXZ(aCanon, tri.B.ToroidalPosition);
                    Vector2 cLocal = ToroidalMap.ShortestOffsetXZ(aCanon, tri.C.ToroidalPosition);
                    if (!TryProjectWorldToMinimap(rect, playerPos, radius, aCanon, out Vector2 pa, out bool inA))
                        continue;
                    Vector2 pb = pa + new Vector2(bLocal.x * scaleX, bLocal.y * scaleZ);
                    Vector2 pc = pa + new Vector2(cLocal.x * scaleX, cLocal.y * scaleZ);
                    bool inB = (pb - center).sqrMagnitude <= circleRadius * circleRadius;
                    bool inC = (pc - center).sqrMagnitude <= circleRadius * circleRadius;
                    if (!inA && !inB && !inC)
                        continue;

                    Color baseColor = TeamManager.GetTeamColor(tri.Team);
                    Color fillColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha);
                    Color borderColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha);

                    DrawTriangleClippedToCircle(center, circleRadius, pa, pb, pc, fillColor, borderColor);
                }
            }

            if (edges != null)
            {
                foreach (var e in edges)
                {
                    if (e.A == null || e.B == null)
                        continue;

                    Vector3 aCanon = e.A.ToroidalPosition;
                    Vector2 bLocal = ToroidalMap.ShortestOffsetXZ(aCanon, e.B.ToroidalPosition);
                    if (!TryProjectWorldToMinimap(rect, playerPos, radius, aCanon, out Vector2 pa, out bool inA))
                        continue;
                    Vector2 pb = pa + new Vector2(bLocal.x * scaleX, bLocal.y * scaleZ);
                    bool inB = (pb - center).sqrMagnitude <= circleRadius * circleRadius;
                    if (!inA && !inB)
                        continue;

                    Color lineColor = new Color(TeamManager.GetTeamColor(e.Team).r, TeamManager.GetTeamColor(e.Team).g, TeamManager.GetTeamColor(e.Team).b, 1f);
                    if (ClipSegmentToCircle(center, circleRadius, pa, pb, out Vector2 paOut, out Vector2 pbOut))
                        Draw.Line(paOut, pbOut, lineThickness, LineEndCap.Round, lineColor);
                }
            }

            if (debugLog && _loggedDrawing && !_loggedDrewOnce)
            {
                _loggedDrewOnce = true;
                int ec = edges?.Count ?? 0, tc = triangles?.Count ?? 0;
                Debug.Log($"[MinimapConnections] Drew {ec} edges and {tc} triangles. If you still don't see them, ensure Canvas has TitanOrbitShapesCanvas and Minimap is under that Canvas.");
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
            if (nIn == 0) return;
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
    }
}

