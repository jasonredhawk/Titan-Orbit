using UnityEngine;
using UnityEngine.UI;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Generation;
using System;
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
                string dir = Path.Combine(Application.dataPath, "StreamingAssets");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "debug-441e0e.log");
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
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            // #region agent log
            _populateCallCount++;
            if (_populateCallCount <= 2) DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "OnPopulateMesh called", "{\"call\":" + _populateCallCount + "}", "H1");
            // #endregion
            vh.Clear();
            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();
            if (_minimap == null)
            {
                // #region agent log
                DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "early return: minimap null", "{}", "H1");
                // #endregion
                return;
            }

            var conn = PlanetConnectionSystem.Instance;
            if (conn == null)
            {
                // #region agent log
                DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "early return: PlanetConnectionSystem.Instance null", "{}", "H2");
                // #endregion
                return;
            }

            var edges = conn.CurrentEdges;
            var triangles = conn.CurrentTriangles;
            int ec = edges?.Count ?? 0, tc = triangles?.Count ?? 0;
            // #region agent log
            if (_populateCallCount <= 5) DebugLog("MinimapConnectionsUI.cs:OnPopulateMesh", "entry", "{\"edges\":" + ec + ",\"triangles\":" + tc + "}", "H2");
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

            // Triangles first (fill then border), then lines
            // Use unwrapped positions (A + shortest offsets for B,C) so we draw the short triangle, not the long way.
            // Clip to circle so geometry stays inside the minimap mask.
            if (triangles != null)
            {
                foreach (var tri in triangles)
                {
                    if (tri.A == null || tri.B == null || tri.C == null) continue;
                    Vector3 aCanon = tri.A.ToroidalPosition;
                    Vector2 bLocal = ToroidalMap.ShortestOffsetXZ(aCanon, tri.B.ToroidalPosition);
                    Vector2 cLocal = ToroidalMap.ShortestOffsetXZ(aCanon, tri.C.ToroidalPosition);
                    if (!TryProject(center, displayHalf, displayHalf, playerPos, radius, aCanon, out Vector2 pa, out bool inA)) continue;
                    float scaleX = displayHalf / radius;
                    float scaleZ = displayHalf / radius;
                    Vector2 pb = pa + new Vector2(bLocal.x * scaleX, bLocal.y * scaleZ);
                    Vector2 pc = pa + new Vector2(cLocal.x * scaleX, cLocal.y * scaleZ);
                    bool inB = (pb - center).sqrMagnitude <= circleRadius * circleRadius;
                    bool inC = (pc - center).sqrMagnitude <= circleRadius * circleRadius;
                    if (!inA && !inB && !inC) continue;

                    Color baseColor = TeamManager.GetTeamColor(tri.Team);
                    Color fillColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha);
                    Color borderColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha);

                    AddTriangleClippedToCircle(vh, center, circleRadius, pa, pb, pc, fillColor);
                    if (ClipSegmentToCircle(center, circleRadius, pa, pb, out Vector2 paPb, out Vector2 pbPa))
                        AddLine(vh, paPb, pbPa, triangleBorderPx, borderColor);
                    if (ClipSegmentToCircle(center, circleRadius, pb, pc, out Vector2 pbPc, out Vector2 pcPb))
                        AddLine(vh, pbPc, pcPb, triangleBorderPx, borderColor);
                    if (ClipSegmentToCircle(center, circleRadius, pc, pa, out Vector2 pcPa, out Vector2 paPc))
                        AddLine(vh, pcPa, paPc, triangleBorderPx, borderColor);
                }
            }

            if (edges != null)
            {
                foreach (var e in edges)
                {
                    if (e.A == null || e.B == null) continue;
                    Vector3 aCanon = e.A.ToroidalPosition;
                    Vector2 bLocal = ToroidalMap.ShortestOffsetXZ(aCanon, e.B.ToroidalPosition);
                    if (!TryProject(center, displayHalf, displayHalf, playerPos, radius, aCanon, out Vector2 pa, out bool inA)) continue;
                    float scaleX = displayHalf / radius;
                    float scaleZ = displayHalf / radius;
                    Vector2 pb = pa + new Vector2(bLocal.x * scaleX, bLocal.y * scaleZ);
                    bool inB = (pb - center).sqrMagnitude <= circleRadius * circleRadius;
                    if (!inA && !inB) continue;

                    Color lineColor = TeamManager.GetTeamColor(e.Team);
                    if (ClipSegmentToCircle(center, circleRadius, pa, pb, out Vector2 paOut, out Vector2 pbOut))
                        AddLine(vh, paOut, pbOut, lineThicknessPx, lineColor);
                }
            }

            // #region agent log
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
            if (nIn == 0) return;
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
