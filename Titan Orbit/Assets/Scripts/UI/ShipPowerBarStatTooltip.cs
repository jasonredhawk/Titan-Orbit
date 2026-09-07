using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Shared floating telemetry card for colourful power-bar slot hovers
    /// (upgrade-tree nodes, sidebar hero, moon-dock equipment).
    /// Builds one <see cref="ShipStatTooltipChrome"/> panel under the parent canvas
    /// and reuses it for every bar so hover does not spawn cards.
    /// <para>
    /// Presentation-only — no ECS writes. RANK 1 copy comes from
    /// <see cref="ShipPowerBarStatCopy"/>; the catalog winner is
    /// <see cref="ShipFamilyPowerBarNorm.GetStatLeader"/>.
    /// </para>
    /// </summary>
    public static class ShipPowerBarStatTooltip
    {
        /// <summary>Canvas-space width of the hover card.</summary>
        const float TipWidth = 320f;

        /// <summary>Minimum height before TMP preferredHeight is measured.</summary>
        const float TipMinHeight = 160f;

        /// <summary>Tiny RANK 1 hull thumb in the bottom-left of the card.</summary>
        const float ThumbSize = 28f;

        static ShipStatTooltipChrome.Handles s_Chrome;
        static Image s_RankThumb;
        static Canvas s_Canvas;
        static int s_ActiveSlot = -1;

        /// <summary>
        /// Shows (or retargets) the shared card for one power-bar slot.
        /// Call from pointer-enter / pointer-move when the slot index changes.
        /// </summary>
        /// <param name="statIndex">Slot 0–9.</param>
        /// <param name="breakdown">The painted hull or equipment breakdown.</param>
        /// <param name="maxes">Pool maxes used as the fill denominator.</param>
        /// <param name="megaPool">True when the bar used MEGA catalog maxes.</param>
        /// <param name="anchor">Slot or bar rect to sit the tip next to.</param>
        /// <param name="thisChassisId">Optional chassis on this card.</param>
        public static void Show(
            int statIndex,
            in ShipFamilyPowerScoreBreakdown breakdown,
            in ShipPowerBarStatMaxes maxes,
            bool megaPool,
            RectTransform anchor,
            string thisChassisId)
        {
            if (statIndex < 0 || statIndex >= ShipAbilityCategoryColors.PowerBreakdownStatCount)
                return;

            EnsureChrome();
            if (s_Chrome.Root == null)
                return;

            s_ActiveSlot = statIndex;

            float thisValue = breakdown.GetDisplayStatValue(statIndex);
            float maxValue = maxes.Get(statIndex);
            string body = ShipPowerBarStatCopy.BuildPowerBarTipBody(
                statIndex, thisValue, maxValue, megaPool, thisChassisId);

            if (s_Chrome.CaptionLabel != null)
                s_Chrome.CaptionLabel.text = "STAT TELEMETRY";
            if (s_Chrome.BodyLabel != null)
                s_Chrome.BodyLabel.text = body;

            ShipStatTooltipChrome.ApplyAccent(
                in s_Chrome,
                ShipStatTooltipChrome.AccentForAbilityIndex(statIndex));

            ApplyRankThumb(statIndex, megaPool, thisChassisId, thisValue);
            SizeToBody();
            PositionNear(anchor);

            if (!s_Chrome.Root.activeSelf)
                s_Chrome.Root.SetActive(true);
        }

        /// <summary>Hides the shared card. Safe to call when nothing is showing.</summary>
        public static void Hide()
        {
            s_ActiveSlot = -1;
            if (s_Chrome.Root != null && s_Chrome.Root.activeSelf)
                s_Chrome.Root.SetActive(false);
        }

        /// <summary>Slot currently shown, or -1 when hidden. Hover relays use this to skip rebuilds.</summary>
        public static int ActiveSlot => s_ActiveSlot;

        /// <summary>
        /// Creates the chrome once under the first canvas. Starts hidden.
        /// [UNITY] FindFirstObjectByType is a load-time / first-hover cost, not per frame.
        /// </summary>
        static void EnsureChrome()
        {
            if (s_Chrome.Root != null)
                return;

            s_Canvas = Object.FindFirstObjectByType<Canvas>();
            if (s_Canvas == null)
                return;

            s_Chrome = ShipStatTooltipChrome.Build(
                "ShipPowerBarStatTooltip",
                s_Canvas.transform,
                "STAT TELEMETRY",
                TipWidth,
                TipMinHeight,
                1f);

            // --- RANK 1 thumb ---
            // [TITAN-ORBIT] Small preview only. The body already names the hull;
            // this is a glance icon in the corner, not a second card.
            var thumbGo = new GameObject("RankThumb");
            thumbGo.transform.SetParent(s_Chrome.Root.transform, false);
            RectTransform thumbRt = thumbGo.AddComponent<RectTransform>();
            thumbRt.anchorMin = new Vector2(1f, 0f);
            thumbRt.anchorMax = new Vector2(1f, 0f);
            thumbRt.pivot = new Vector2(1f, 0f);
            thumbRt.anchoredPosition = new Vector2(-10f, 10f);
            thumbRt.sizeDelta = new Vector2(ThumbSize, ThumbSize);
            s_RankThumb = thumbGo.AddComponent<Image>();
            s_RankThumb.raycastTarget = false;
            s_RankThumb.preserveAspect = true;
            s_RankThumb.enabled = false;

            if (s_Chrome.Root != null)
                s_Chrome.Root.SetActive(false);
        }

        /// <summary>Shows the winner's menu sprite when the hovered hull is not RANK 1.</summary>
        static void ApplyRankThumb(int statIndex, bool megaPool, string thisChassisId, float thisValue)
        {
            if (s_RankThumb == null)
                return;

            ShipPowerBarStatLeader leader = ShipFamilyPowerBarNorm.GetStatLeader(statIndex, megaPool);
            bool thisIsLeader = leader.MatchesChassis(thisChassisId)
                                || (thisValue >= 0f && thisValue + 0.0001f >= leader.value);
            if (!leader.IsValid || thisIsLeader || leader.previewSprite == null)
            {
                s_RankThumb.enabled = false;
                s_RankThumb.sprite = null;
                return;
            }

            s_RankThumb.sprite = leader.previewSprite;
            s_RankThumb.enabled = true;
        }

        /// <summary>Fits the card height to the TMP body so short stats do not leave a tall empty plate.</summary>
        static void SizeToBody()
        {
            if (s_Chrome.RootRect == null || s_Chrome.BodyLabel == null)
                return;

            s_Chrome.BodyLabel.ForceMeshUpdate(true);
            float tipH = Mathf.Max(
                TipMinHeight,
                s_Chrome.BodyLabel.preferredHeight + s_Chrome.ExtraHeightPadding);
            s_Chrome.RootRect.sizeDelta = new Vector2(TipWidth, tipH);
        }

        /// <summary>
        /// Parks the tip above-right of the hovered slot, then clamps to the canvas
        /// so a left-edge card does not spill off-screen.
        /// </summary>
        static void PositionNear(RectTransform anchor)
        {
            if (anchor == null || s_Chrome.RootRect == null || s_Canvas == null)
                return;

            RectTransform canvasRt = s_Canvas.transform as RectTransform;
            if (canvasRt == null)
                return;

            s_Chrome.RootRect.SetParent(canvasRt, false);
            s_Chrome.RootRect.SetAsLastSibling();

            Vector3[] corners = new Vector3[4];
            anchor.GetWorldCorners(corners);
            // corners[2] = top-right of the slot in world space.
            // [UNITY] Qualify Camera — TitanOrbit.Camera is a namespace and would steal the short name.
            UnityEngine.Camera cam = s_Canvas.worldCamera;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRt, screen, cam, out Vector2 local);

            s_Chrome.RootRect.pivot = new Vector2(0f, 0f);
            Vector2 pos = local + new Vector2(8f, 8f);

            // --- Clamp to canvas ---
            Vector2 size = s_Chrome.RootRect.sizeDelta;
            Rect canvasRect = canvasRt.rect;
            float maxX = canvasRect.xMax - size.x - 8f;
            float maxY = canvasRect.yMax - size.y - 8f;
            float minX = canvasRect.xMin + 8f;
            float minY = canvasRect.yMin + 8f;
            pos.x = Mathf.Clamp(pos.x, minX, maxX);
            pos.y = Mathf.Clamp(pos.y, minY, maxY);
            s_Chrome.RootRect.anchoredPosition = pos;
        }
    }
}
