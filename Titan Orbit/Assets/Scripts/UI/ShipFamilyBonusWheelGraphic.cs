using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Bicycle-wheel readout for family bonuses on the Ship Upgrade Tree.
    /// Each spoke is one <see cref="FamilyStatHudCopy.BonusRow"/> (MOVE, RAM, …).
    /// The ice ring is the stock 1.00× base; spokes past the ring are boosts,
    /// spokes short of the ring are penalties. Spokes pack on the left / right
    /// so titles are not stacked at 12 and 6. Colour follows the power-bar
    /// category (MOVE cyan, COMBAT orange, HULL green, ENERGY gold, HOLD purple, BANK fire-power).
    /// <para>
    /// Line-only: rings and spokes, no filled wedges. The mesh stops inside
    /// <see cref="LabelBand"/> so <see cref="ShipFamilyBonusListUI"/> can sit a
    /// title on each spoke tip. [UNITY] RawImage + <see cref="OnPopulateMesh"/>.
    /// Presentation-only; rebuilt at dock time, not per frame.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class ShipFamilyBonusWheelGraphic : RawImage
    {
        /// <summary>1.00× ring as a fraction of the drawable radius.</summary>
        public const float RingFrac = 0.58f;

        /// <summary>
        /// Empty rim (UI pixels) reserved for spoke titles. The disc and spokes
        /// stay inside this band so text is not drawn on top of the mesh.
        /// </summary>
        public const float LabelBand = 14f;

        /// <summary>
        /// Extra pixels past the longest spoke. Labels stay on this orbit when
        /// a value changes — only the line length moves.
        /// </summary>
        public const float LabelOrbitPad = 5f;

        /// <summary>Shortest spoke (hard penalty) as a fraction of the radius.</summary>
        const float MinFrac = 0.20f;

        /// <summary>Longest spoke (hard boost). RAM ×5 clamps here so one family cannot blow the plate.</summary>
        const float MaxFrac = 0.90f;

        /// <summary>Signed percent that maps to the outer / inner rim. ±80% saturates.</summary>
        const float SaturatePercent = 80f;

        /// <summary>
        /// Empty cone at 12 o'clock and 6 o'clock. Spokes pack onto the left
        /// and right arcs so horizontal titles are not stacked on the poles.
        /// </summary>
        const float PolarDeadZoneDeg = 36f;

        /// <summary>Category gap as a fraction of one spoke slot on a side arc.</summary>
        const float CategoryGapWeight = 0.55f;

        /// <summary>Ring tessellation. 48 stays smooth on a ~300px wheel.</summary>
        const int RingSegments = 48;

        static readonly Color RingColor = new Color(0.38f, 0.62f, 0.80f, 0.22f);
        static readonly Color HubRim = new Color(0.36f, 0.58f, 0.76f, 0.24f);

        static Texture2D s_WhiteTex;

        /// <summary>Copied bonus rows — dock refresh only, not the sim tick.</summary>
        readonly List<FamilyStatHudCopy.BonusRow> _rows = new List<FamilyStatHudCopy.BonusRow>(24);

        /// <summary>
        /// Replaces the spoke set and dirties the mesh. Called from
        /// <see cref="ShipFamilyBonusListUI.Paint"/> when the docked family changes.
        /// </summary>
        /// <param name="src">Family + BANK rows, including 1.00× identity slots.</param>
        public void SetRows(List<FamilyStatHudCopy.BonusRow> src)
        {
            _rows.Clear();
            if (src != null)
            {
                for (int i = 0; i < src.Count; i++)
                    _rows.Add(src[i]);
            }

            SetVerticesDirty();
        }

        /// <summary>How many spokes the last <see cref="SetRows"/> stored.</summary>
        public int SpokeCount => _rows.Count;

        /// <summary>
        /// Drawable wheel radius in local pixels (half the square minus the title band).
        /// Zero when the rect has not been laid out yet.
        /// </summary>
        public float DrawableRadius
        {
            get
            {
                Rect rect = rectTransform.rect;
                float size = Mathf.Min(rect.width, rect.height);
                return Mathf.Max(0f, size * 0.5f - LabelBand);
            }
        }

        /// <summary>Fixed title orbit in local pixels. Independent of spoke length.</summary>
        public float LabelOrbitRadius
        {
            get
            {
                float radius = DrawableRadius;
                return radius < 6f ? 0f : radius * MaxFrac + LabelOrbitPad;
            }
        }

        /// <summary>
        /// Title anchor on the fixed outer orbit, plus the world-UI angle.
        /// Does not follow spoke length — Paint uses this so +12% only grows the line.
        /// </summary>
        /// <param name="index">Spoke index in CollectBonusRows order.</param>
        /// <param name="fromCenter">Vector from the rect centre to the title pivot.</param>
        /// <param name="angleRad">UI angle (0 = +X, counter-clockwise).</param>
        /// <param name="row">The bonus this spoke represents.</param>
        /// <returns>False when the index is out of range or the rect is not ready.</returns>
        public bool TryGetSpokeLabelAnchor(
            int index,
            out Vector2 fromCenter,
            out float angleRad,
            out FamilyStatHudCopy.BonusRow row)
        {
            fromCenter = Vector2.zero;
            angleRad = 0f;
            row = default;
            if (index < 0 || index >= _rows.Count)
                return false;

            float orbit = LabelOrbitRadius;
            if (orbit < 8f)
                return false;

            angleRad = AngleForSpoke(index);
            row = _rows[index];
            Vector2 dir = new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad));
            fromCenter = dir * orbit;
            return true;
        }

        /// <summary>[UNITY] 1×1 white texture so vertex colours tint the quads.</summary>
        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
            EnsureWhiteTexture();
            color = Color.white;
        }

        /// <summary>Rebuilds when the tree resizes the wheel square.</summary>
        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            SetVerticesDirty();
        }

        /// <summary>
        /// Draws line-only chrome: thin 1.00× ring, hub ring, spokes.
        /// No filled wedges or void disc — cards stay visible underneath.
        /// Identity spokes stop on the ring; live trade-offs overshoot or pull in.
        /// </summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            EnsureWhiteTexture();

            Rect rect = GetPixelAdjustedRect();
            float size = Mathf.Min(rect.width, rect.height);
            if (size < 8f)
                return;

            Vector2 center = rect.center;
            // Leave LabelBand empty so titles sit on the spoke ends, not on the lines.
            float radius = size * 0.5f - LabelBand;
            if (radius < 6f)
                return;

            // --- 1.00× ring only (thin, muted). No outer ghost ring. ---
            AddRing(vh, center, radius * RingFrac, 0.85f, RingColor, RingSegments);

            // --- Spokes ---
            AddSpokes(vh, center, radius);

            // --- Hub ring ---
            AddRing(vh, center, Mathf.Max(3.5f, radius * 0.09f), 0.9f, HubRim, 16);
        }

        /// <summary>One spoke per bonus row on the left / right arcs.</summary>
        void AddSpokes(VertexHelper vh, Vector2 center, float radius)
        {
            if (_rows.Count == 0)
                return;

            for (int i = 0; i < _rows.Count; i++)
            {
                FamilyStatHudCopy.BonusRow row = _rows[i];
                float angle = AngleForSpoke(i);
                float frac = RadiusFracFor(row);
                float length = radius * frac;
                float hub = radius * 0.14f;
                Color spoke = ColorForSpoke(row);
                float thick = row.IsIdentity ? 1.05f : 2.0f;
                Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 a = center + dir * hub;
                Vector2 b = center + dir * length;
                AddLineQuad(vh, a, b, spoke, thick);

                // Tip cap — boost green / penalty amber so length polarity is obvious.
                if (!row.IsIdentity)
                {
                    Color tip = ColorForTip(row);
                    AddDisc(vh, b, row.IsIdentity ? 1.2f : 2.1f, tip, 8);
                }
            }
        }

        /// <summary>
        /// Side-biased angle for spoke <paramref name="index"/>.
        /// First half of the board walks the right arc (about 1 o'clock to 5);
        /// the rest walks the left arc (7 to 11). 12 and 6 stay empty.
        /// Unity UI: 0 = +X (3 o'clock), +angle = counter-clockwise.
        /// </summary>
        float AngleForSpoke(int index)
        {
            int n = _rows.Count;
            if (n <= 0)
                return 0f;
            if (n == 1)
                return 0f;

            int gaps = 0;
            int gapsBefore = 0;
            for (int i = 1; i < n; i++)
            {
                if (_rows[i].Category != _rows[i - 1].Category)
                {
                    gaps++;
                    if (i <= index)
                        gapsBefore++;
                }
            }

            float total = (n - 1) + gaps * CategoryGapWeight;
            float t = total > 0.0001f
                ? (index + gapsBefore * CategoryGapWeight) / total
                : 0f;
            return ProgressToSideAngle(t);
        }

        /// <summary>
        /// Maps 0..1 along the two side arcs. t=0 is upper-right, t=0.5 jumps
        /// the bottom pole, t=1 is upper-left.
        /// </summary>
        static float ProgressToSideAngle(float t)
        {
            float dead = PolarDeadZoneDeg * Mathf.Deg2Rad;
            float rightStart = 90f * Mathf.Deg2Rad - dead;
            float rightEnd = -90f * Mathf.Deg2Rad + dead;
            float leftStart = -90f * Mathf.Deg2Rad - dead;
            float leftSpan = Mathf.PI - 2f * dead;
            t = Mathf.Clamp01(t);
            if (t <= 0.5f)
                return Mathf.Lerp(rightStart, rightEnd, t * 2f);
            return leftStart - (t - 0.5f) * 2f * leftSpan;
        }

        /// <summary>
        /// Maps a signed percent onto the wheel. 0% lands on the 1.00× ring.
        /// Positive overshoots toward the rim; negative pulls toward the hub.
        /// </summary>
        public static float RadiusFracFor(in FamilyStatHudCopy.BonusRow row)
        {
            float signed = row.SignedPercent;
            if (row.IsIdentity || Mathf.Abs(signed) <= 0.05f)
                return RingFrac;

            if (signed > 0f)
            {
                float t = Mathf.Clamp01(signed / SaturatePercent);
                return Mathf.Lerp(RingFrac, MaxFrac, t);
            }

            float u = Mathf.Clamp01(-signed / SaturatePercent);
            return Mathf.Lerp(RingFrac, MinFrac, u);
        }

        /// <summary>
        /// Stock 1.00× keeps the category hue but washed out. Live spokes use
        /// the full power-bar tone. Not a flat grey.
        /// </summary>
        public static Color ColorForSpoke(in FamilyStatHudCopy.BonusRow row)
        {
            Color c = ColorForCategory(row.Category);
            if (row.IsIdentity)
                return MuteCategory(c);
            c.a = 0.90f;
            return c;
        }

        /// <summary>Washes a category colour toward the void so 1.00× stays tinted, not grey.</summary>
        public static Color MuteCategory(Color category)
        {
            Color muted = Color.Lerp(category, new Color(0.06f, 0.08f, 0.12f, 1f), 0.42f);
            muted.a = 0.55f;
            return muted;
        }

        /// <summary>Tip colour: boost green, penalty amber, camera ice.</summary>
        static Color ColorForTip(in FamilyStatHudCopy.BonusRow row)
        {
            if (row.IsNeutral)
                return new Color(0.62f, 0.78f, 0.95f, 1f);
            if (row.IsBoost)
                return new Color(0.35f, 0.98f, 0.62f, 1f);
            if (row.IsPenalty)
                return new Color(0.95f, 0.72f, 0.32f, 1f);
            return ColorForCategory(row.Category);
        }

        /// <summary>Power-bar HUD tones so the wheel matches the hull-card bars.</summary>
        public static Color ColorForCategory(FamilyStatHudCopy.BonusCategory category)
        {
            switch (category)
            {
                case FamilyStatHudCopy.BonusCategory.Mobility:
                    return ShipAbilityCategoryColors.ShipForHud;
                case FamilyStatHudCopy.BonusCategory.Combat:
                    return ShipAbilityCategoryColors.WeaponForHud;
                case FamilyStatHudCopy.BonusCategory.Hull:
                    return ShipAbilityCategoryColors.HealthForHud;
                case FamilyStatHudCopy.BonusCategory.Energy:
                    return ShipAbilityCategoryColors.EnergyForHud;
                case FamilyStatHudCopy.BonusCategory.Hold:
                    return ShipAbilityCategoryColors.CargoForHud;
                case FamilyStatHudCopy.BonusCategory.Ordnance:
                    return ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(0);
                default:
                    return RingColor;
            }
        }

        /// <summary>Filled disc as a triangle fan.</summary>
        static void AddDisc(VertexHelper vh, Vector2 center, float radius, Color color, int segs)
        {
            if (radius < 0.5f || segs < 3)
                return;

            int start = vh.currentVertCount;
            vh.AddVert(center, color, Vector2.zero);
            for (int i = 0; i <= segs; i++)
            {
                float a = (i / (float)segs) * Mathf.PI * 2f;
                vh.AddVert(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, color, Vector2.zero);
            }

            for (int i = 0; i < segs; i++)
                vh.AddTriangle(start, start + 1 + i, start + 2 + i);
        }

        /// <summary>Annulus made of quads. Thickness is in UI pixels.</summary>
        static void AddRing(VertexHelper vh, Vector2 center, float radius, float thickness, Color color, int segs)
        {
            if (radius < 0.5f || segs < 3)
                return;

            float half = thickness * 0.5f;
            float inner = Mathf.Max(0.25f, radius - half);
            float outer = radius + half;
            for (int i = 0; i < segs; i++)
            {
                float a0 = (i / (float)segs) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)segs) * Mathf.PI * 2f;
                Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                Vector2 d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                AddQuad(vh, center + d0 * inner, center + d1 * inner, center + d1 * outer, center + d0 * outer, color);
            }
        }

        /// <summary>Screen-aligned quad (two triangles).</summary>
        static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero);
            vh.AddVert(b, color, Vector2.zero);
            vh.AddVert(c, color, Vector2.zero);
            vh.AddVert(d, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }

        /// <summary>One spoke / ring segment as a screen-aligned quad.</summary>
        static void AddLineQuad(VertexHelper vh, Vector2 a, Vector2 b, Color color, float thickness)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.01f)
                return;

            Vector2 dir = delta / len;
            Vector2 n = new Vector2(-dir.y, dir.x) * (thickness * 0.5f);
            AddQuad(vh, a - n, a + n, b + n, b - n, color);
        }

        /// <summary>Shared 1×1 white so every wheel instance tints from vertex colour.</summary>
        void EnsureWhiteTexture()
        {
            if (s_WhiteTex == null)
            {
                s_WhiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                s_WhiteTex.SetPixel(0, 0, Color.white);
                s_WhiteTex.Apply();
                s_WhiteTex.hideFlags = HideFlags.HideAndDontSave;
            }

            if (texture != s_WhiteTex)
                texture = s_WhiteTex;
        }
    }
}
