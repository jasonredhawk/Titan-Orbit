using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Shared floating telemetry card for colourful power-bar slot hovers
    /// (upgrade-tree nodes, sidebar hero, moon-dock equipment).
    /// Builds one <see cref="ShipStatTooltipChrome"/> panel and reuses it for every
    /// bar so hover does not spawn cards.
    /// <para>
    /// Presentation-only — no ECS writes. RANK 1 copy comes from
    /// <see cref="ShipPowerBarStatCopy"/>; the catalog winner is
    /// <see cref="ShipFamilyPowerBarNorm.GetStatLeader"/>.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] The Orbit Menu lives on its own overlay canvas (sorting order 200).
    /// Parenting this card to <c>FindFirstObjectByType&lt;Canvas&gt;()</c> used to park it
    /// on a gameplay HUD canvas (order 0–80). Sibling order cannot beat another canvas,
    /// so the panel sat behind the whole menu and looked like hover was gone.
    /// We parent to the hovered bar's Orbit Menu canvas and give the card its own
    /// nested Canvas (sort 260) so it paints above the dock. Hover show/hide is
    /// pointer-vs-tray math in <see cref="ShipPowerBarStatHoverRelay"/> — not
    /// EventSystem enter/exit — so this draw batch cannot hide the card.
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

        /// <summary>
        /// Nested-canvas sort so the card paints above Orbit Menu (200) and below
        /// death / match-end / escape overlays (8500+).
        /// </summary>
        const int TipSortingOrder = 260;

        static ShipStatTooltipChrome.Handles s_Chrome;
        static Image s_RankThumb;
        static Canvas s_HostCanvas;
        static int s_ActiveSlot = -1;
        /// <summary>Hover relay that opened the card. Exit / disable on a different bar must not steal this tip.</summary>
        static object s_ActiveOwner;

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
        /// <param name="owner">Hover relay that owns this showing; used so another bar's exit cannot hide it.</param>
        public static void Show(
            int statIndex,
            in ShipFamilyPowerScoreBreakdown breakdown,
            in ShipPowerBarStatMaxes maxes,
            bool megaPool,
            RectTransform anchor,
            string thisChassisId,
            object owner)
        {
            if (statIndex < 0 || statIndex >= ShipAbilityCategoryColors.PowerBreakdownStatCount)
                return;

            // --- Host canvas ---
            // [TITAN-ORBIT] Must be the Orbit Menu canvas (or whatever canvas owns this bar).
            // A HUD / loading / leftover canvas would hide the card behind the dock.
            Canvas host = ResolveHostCanvas(anchor);
            EnsureChrome(host);
            if (s_Chrome.Root == null)
                return;

            AttachToHostCanvas(host);

            s_ActiveSlot = statIndex;
            s_ActiveOwner = owner;

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
            EnsureTipCanvas();

            if (!s_Chrome.Root.activeSelf)
                s_Chrome.Root.SetActive(true);
        }

        /// <summary>Hides the shared card. Safe to call when nothing is showing.</summary>
        public static void Hide()
        {
            s_ActiveSlot = -1;
            s_ActiveOwner = null;
            if (s_Chrome.Root != null && s_Chrome.Root.activeSelf)
                s_Chrome.Root.SetActive(false);
        }

        /// <summary>Hides only if <paramref name="owner"/> is the relay that opened the card.</summary>
        public static void HideIfOwner(object owner)
        {
            if (owner == null || !ReferenceEquals(s_ActiveOwner, owner))
                return;
            Hide();
        }

        /// <summary>Slot currently shown, or -1 when hidden. Hover relays use this to skip rebuilds.</summary>
        public static int ActiveSlot => s_ActiveSlot;

        /// <summary>Relay that opened the card, or null when hidden.</summary>
        public static object ActiveOwner => s_ActiveOwner;

        /// <summary>
        /// Canvas that should own the floating card. Prefers the hovered bar's root
        /// canvas so Screen Space Overlay math and draw order match the Orbit Menu.
        /// </summary>
        /// <param name="anchor">Slot or bar rect under the pointer.</param>
        /// <returns>Root canvas, or null when the scene has none.</returns>
        static Canvas ResolveHostCanvas(RectTransform anchor)
        {
            if (anchor != null)
            {
                // [UNITY] GetComponentInParent walks up from the slot → card → OrbitStationCanvas.
                Canvas fromAnchor = anchor.GetComponentInParent<Canvas>();
                if (fromAnchor != null)
                    return fromAnchor.rootCanvas != null ? fromAnchor.rootCanvas : fromAnchor;
            }

            return s_HostCanvas != null ? s_HostCanvas : Object.FindFirstObjectByType<Canvas>();
        }

        /// <summary>
        /// Creates the chrome once under <paramref name="host"/>. Starts hidden.
        /// Later hovers reuse the same GameObject and only reparent if the menu canvas changed.
        /// </summary>
        /// <param name="host">Orbit Menu (or other) overlay that owns the hovered bar.</param>
        static void EnsureChrome(Canvas host)
        {
            if (s_Chrome.Root != null)
                return;
            if (host == null)
                return;

            s_HostCanvas = host;
            s_Chrome = ShipStatTooltipChrome.Build(
                "ShipPowerBarStatTooltip",
                host.transform,
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

            EnsureTipCanvas();
            EnsureHostTmpChannels(host);

            if (s_Chrome.Root != null)
                s_Chrome.Root.SetActive(false);
        }

        /// <summary>
        /// Moves the reused card under the canvas that owns the hovered bar.
        /// Needed when the first hover built chrome on a different overlay.
        /// </summary>
        /// <param name="host">Canvas resolved from the current anchor.</param>
        static void AttachToHostCanvas(Canvas host)
        {
            if (host == null || s_Chrome.RootRect == null)
                return;

            s_HostCanvas = host;
            if (s_Chrome.RootRect.parent != host.transform)
                s_Chrome.RootRect.SetParent(host.transform, false);
            EnsureTipCanvas();
            EnsureHostTmpChannels(host);
        }

        /// <summary>
        /// Nested canvas so the card draws above Orbit Menu chrome. No GraphicRaycaster —
        /// hover is tray containment, not EventSystem enter, so this batch cannot steal the pointer.
        /// </summary>
        static void EnsureTipCanvas()
        {
            if (s_Chrome.Root == null)
                return;

            Canvas tipCanvas = s_Chrome.Root.GetComponent<Canvas>();
            if (tipCanvas == null)
                tipCanvas = s_Chrome.Root.AddComponent<Canvas>();

            tipCanvas.overrideSorting = true;
            tipCanvas.sortingOrder = TipSortingOrder;
            tipCanvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;
            s_Chrome.Root.transform.SetAsLastSibling();
        }

        /// <summary>
        /// TMP body text needs TexCoord1 on the host canvas. We used to put those
        /// channels on a nested tip canvas; they now live on the Orbit Menu canvas
        /// so the body stays visible without a second draw batch.
        /// </summary>
        static void EnsureHostTmpChannels(Canvas host)
        {
            if (host == null)
                return;
            host.additionalShaderChannels |=
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;
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
        /// Parks the tip to the right of the hovered slot (or left if it would clip),
        /// then clamps to the canvas. Kept beside the lane so the card does not sit
        /// on top of the power-bar hit pad.
        /// </summary>
        static void PositionNear(RectTransform anchor)
        {
            if (anchor == null || s_Chrome.RootRect == null || s_HostCanvas == null)
                return;

            RectTransform canvasRt = s_HostCanvas.transform as RectTransform;
            if (canvasRt == null)
                return;

            s_Chrome.RootRect.SetParent(canvasRt, false);
            s_Chrome.RootRect.SetAsLastSibling();

            Vector3[] corners = new Vector3[4];
            anchor.GetWorldCorners(corners);
            // [UNITY] Qualify Camera — TitanOrbit.Camera is a namespace and would steal the short name.
            // Overlay canvases use a null camera (screen pixels = canvas space).
            UnityEngine.Camera cam = s_HostCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : s_HostCanvas.worldCamera;

            // Slot top-right and bottom-right — we sit just to the right, vertically centered.
            Vector2 screenRight = RectTransformUtility.WorldToScreenPoint(cam, (corners[2] + corners[3]) * 0.5f);
            Vector2 screenLeft = RectTransformUtility.WorldToScreenPoint(cam, (corners[0] + corners[1]) * 0.5f);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRt, screenRight, cam, out Vector2 localRight);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRt, screenLeft, cam, out Vector2 localLeft);

            Vector2 size = s_Chrome.RootRect.sizeDelta;
            Rect canvasRect = canvasRt.rect;
            const float gap = 14f;
            bool placeRight = localRight.x + gap + size.x <= canvasRect.xMax - 8f;
            s_Chrome.RootRect.pivot = placeRight ? new Vector2(0f, 0.5f) : new Vector2(1f, 0.5f);
            Vector2 pos = placeRight
                ? localRight + new Vector2(gap, 0f)
                : localLeft + new Vector2(-gap, 0f);

            float maxX = canvasRect.xMax - (placeRight ? size.x : 0f) - 8f;
            float minX = canvasRect.xMin + (placeRight ? 0f : size.x) + 8f;
            float maxY = canvasRect.yMax - size.y * 0.5f - 8f;
            float minY = canvasRect.yMin + size.y * 0.5f + 8f;
            pos.x = Mathf.Clamp(pos.x, minX, maxX);
            pos.y = Mathf.Clamp(pos.y, minY, maxY);
            s_Chrome.RootRect.anchoredPosition = pos;
        }
    }
}
