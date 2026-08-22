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
    /// Turn off with <see cref="TitanOrbitDebugFlags.ShowMapSeamLines"/>.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class MinimapSeamDebugUI : RawImage
    {
        /// <summary>Seam stroke in UI pixels.</summary>
        const float Thickness = 2.4f;

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
                    AddLine(vh, sw, se);
                    AddLine(vh, se, ne);
                    AddLine(vh, ne, nw);
                    AddLine(vh, nw, sw);
                }
            }
        }

        /// <summary>Panel-local pixel from a scaled XZ offset (minimap +X / +Z).</summary>
        static Vector2 ToPanel(Rect rect, float x, float z) =>
            rect.center + new Vector2(x, z);

        /// <summary>One seam segment as a screen-aligned quad.</summary>
        static void AddLine(VertexHelper vh, Vector2 a, Vector2 b)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.01f)
                return;

            Vector2 dir = delta / len;
            Vector2 n = new Vector2(-dir.y, dir.x) * (Thickness * 0.5f);
            int i = vh.currentVertCount;
            vh.AddVert(a - n, SeamColor, Vector2.zero);
            vh.AddVert(a + n, SeamColor, Vector2.zero);
            vh.AddVert(b + n, SeamColor, Vector2.zero);
            vh.AddVert(b - n, SeamColor, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }
    }
}
