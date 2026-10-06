using TitanOrbit;
using TitanOrbit.Generation;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Temporary wrap-test overlay on the minimap: the canonical map rectangle
    /// (four seams). Uses player-relative Euclidean offsets plus 3×3 tile copies
    /// so a nearby wrap edge still reads when you sit on the opposite side.
    /// The stroke is a solid core plus a one-pixel alpha ramp. A hard strip
    /// changes how many pixels it covers as the ship moves, so the whole edge
    /// looks thicker and thinner; the ramp keeps the same ink on the pixel grid.
    /// Turn off with <see cref="TitanOrbitDebugFlags.ShowMapSeamLines"/>.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class MinimapSeamDebugUI : RawImage
    {
        /// <summary>Full-alpha width of each seam, in the same units as the minimap rect.</summary>
        const float Thickness = 1.2f;

        /// <summary>
        /// Soft edge past each side of the stroke, in screen pixels.
        /// One pixel is enough to stop the thickness pulse without turning the seam into a glow.
        /// </summary>
        const float FeatherScreenPixels = 1f;

        /// <summary>Cyan — matches the world seam overlay.</summary>
        static readonly Color SeamColor = new Color(0.15f, 0.95f, 1f, 0.95f);

        static Texture2D s_WhiteTex;

        MinimapController _minimap;
        Vector3 _lastPlayerPos;
        float _lastRadius = -1f;
        bool _lastExpanded;
        bool _lastEnabled;

        /// <summary>[UNITY] Disable raycasts; 1×1 white texture for tinted quads.</summary>
        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
            if (s_WhiteTex == null)
            {
                s_WhiteTex = new Texture2D(1, 1);
                s_WhiteTex.SetPixel(0, 0, Color.white);
                s_WhiteTex.Apply();
            }

            texture = s_WhiteTex;
            color = Color.white;
            _minimap = GetComponentInParent<MinimapController>();
        }

        /// <summary>Rebuilds when the player, zoom, or debug flag changes.</summary>
        void LateUpdate()
        {
            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();

            bool enabled = TitanOrbitDebugFlags.ShowMapSeamLines;
            Vector3 playerPos = _minimap != null ? _minimap.PlayerPosition : Vector3.zero;
            float radius = _minimap != null ? _minimap.MinimapRadius : 0f;
            bool expanded = _minimap != null && _minimap.IsExpanded;
            bool changed =
                enabled != _lastEnabled ||
                (playerPos - _lastPlayerPos).sqrMagnitude > 0.0025f ||
                Mathf.Abs(radius - _lastRadius) > 0.01f ||
                expanded != _lastExpanded;

            if (!changed)
                return;

            _lastEnabled = enabled;
            _lastPlayerPos = playerPos;
            _lastRadius = radius;
            _lastExpanded = expanded;
            SetVerticesDirty();
        }

        /// <summary>
        /// [UNITY] Projects the four wrap edges into panel space. Copies at ±map so the
        /// destination seam sits next to you on the radar after a wrap.
        /// </summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (!TitanOrbitDebugFlags.ShowMapSeamLines)
                return;
            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();
            if (_minimap == null)
                return;
            if (!ToroidalMap.TryGetMapSize(out float mapW, out float mapH))
                return;

            Rect rect = GetPixelAdjustedRect();
            if (rect.width < 1f || rect.height < 1f)
                return;

            Vector3 playerPos = _minimap.PlayerPosition;
            float radius = Mathf.Max(1f, _minimap.MinimapRadius);
            float scale = (_minimap.DisplaySize * 0.5f) / radius;
            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;

            // [UNITY] OnPopulateMesh positions are canvas units. Divide by the canvas
            // scale so the falloff stays one screen pixel when the HUD is scaled up.
            float feather = FeatherScreenPixels;
            Canvas hostCanvas = canvas;
            if (hostCanvas != null && hostCanvas.scaleFactor > 0.01f)
                feather /= hostCanvas.scaleFactor;

            // --- Player-relative Euclidean box (do not shortest-path the long edges) ---
            float left = -halfW - playerPos.x;
            float right = halfW - playerPos.x;
            float south = -halfH - playerPos.z;
            float north = halfH - playerPos.z;

            // 3×3 copies so a wrap edge that is “far” Euclidean still appears on the radar.
            for (int ox = -1; ox <= 1; ox++)
            {
                for (int oz = -1; oz <= 1; oz++)
                {
                    float dx = ox * mapW;
                    float dz = oz * mapH;
                    Vector2 sw = ToPanel(rect, (left + dx) * scale, (south + dz) * scale);
                    Vector2 se = ToPanel(rect, (right + dx) * scale, (south + dz) * scale);
                    Vector2 ne = ToPanel(rect, (right + dx) * scale, (north + dz) * scale);
                    Vector2 nw = ToPanel(rect, (left + dx) * scale, (north + dz) * scale);
                    AddLine(vh, sw, se, feather);
                    AddLine(vh, se, ne, feather);
                    AddLine(vh, ne, nw, feather);
                    AddLine(vh, nw, sw, feather);
                }
            }
        }

        /// <summary>Panel-local pixel from a scaled XZ offset (minimap +X / +Z).</summary>
        static Vector2 ToPanel(Rect rect, float x, float z) =>
            rect.center + new Vector2(x, z);

        /// <summary>
        /// One seam segment: a solid core with a transparent ramp on each side.
        /// Map edges are horizontal or vertical, so a hard quad covers either one
        /// pixel or two as it slides, and the whole border appears to change thickness.
        /// Diagonal territory lines hide that, because their coverage varies along the stroke.
        /// </summary>
        /// <param name="vh">Minimap mesh being filled this rebuild.</param>
        /// <param name="a">Segment start in panel pixels.</param>
        /// <param name="b">Segment end in panel pixels.</param>
        /// <param name="feather">Falloff width on each side, in the same units as <paramref name="a"/>.</param>
        static void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float feather)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.01f)
                return;

            // Unit direction along the seam, then a perpendicular for the stroke width.
            Vector2 dir = delta / len;
            Vector2 n = new Vector2(-dir.y, dir.x);
            float half = Thickness * 0.5f;
            Vector2 inner = n * half;
            Vector2 outer = n * (half + Mathf.Max(0.01f, feather));

            // Outer verts fade out; inner verts hold the cyan. Sliding the line
            // moves that ramp across the pixel grid instead of adding or dropping a full pixel.
            Color clear = SeamColor;
            clear.a = 0f;

            int i = vh.currentVertCount;
            vh.AddVert(a - outer, clear, Vector2.zero);
            vh.AddVert(a - inner, SeamColor, Vector2.zero);
            vh.AddVert(a + inner, SeamColor, Vector2.zero);
            vh.AddVert(a + outer, clear, Vector2.zero);
            vh.AddVert(b - outer, clear, Vector2.zero);
            vh.AddVert(b - inner, SeamColor, Vector2.zero);
            vh.AddVert(b + inner, SeamColor, Vector2.zero);
            vh.AddVert(b + outer, clear, Vector2.zero);

            // Fade-in, solid core, fade-out. UI does not cull back faces.
            AddQuad(vh, i + 0, i + 1, i + 5, i + 4);
            AddQuad(vh, i + 1, i + 2, i + 6, i + 5);
            AddQuad(vh, i + 2, i + 3, i + 7, i + 6);
        }

        /// <summary>Two triangles for one strip of the seam (core or one feather).</summary>
        static void AddQuad(VertexHelper vh, int a, int b, int c, int d)
        {
            vh.AddTriangle(a, b, c);
            vh.AddTriangle(a, c, d);
        }
    }
}
