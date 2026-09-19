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
    /// [TITAN-ORBIT] This card lives on its own DontDestroyOnLoad overlay canvas
    /// (sort 260), not as a child of OrbitStationCanvas. Parenting it to the dock
    /// used to look like hover was dead on the second menu open: Hide() SetActive
    /// the nested tip canvas, then the dock backdrop jumped to last sibling and
    /// Unity 6 stopped painting that nested batch. The overlay never goes inactive.
    /// We hide with a CanvasGroup so the player just sees it fade. Hover show/hide
    /// is pointer-vs-tray math in <see cref="ShipPowerBarStatHoverRelay"/>.
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
        /// Overlay sort so the card paints above Orbit Menu (200) and below
        /// death / match-end / escape overlays (8500+).
        /// </summary>
        const int TipSortingOrder = 260;

        static GameObject s_OverlayRoot;
        static Canvas s_OverlayCanvas;
        static CanvasGroup s_OverlayGroup;
        static ShipStatTooltipChrome.Handles s_Chrome;
        static Image s_RankThumb;
        static int s_ActiveSlot = -1;
        /// <summary>Hover relay that opened the card. Exit / disable on a different bar must not steal this tip.</summary>
        static object s_ActiveOwner;

        /// <summary>
        /// Shows (or retargets) the shared card for one power-bar slot.
        /// Call from the hover probe when the slot index changes.
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

            EnsureOverlay();
            if (s_Chrome.Root == null)
                return;

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

            // --- Reveal ---
            // [UNITY] Do not SetActive the card. The old nested-canvas path died on the
            // second Orbit Menu open after SetActive(false). CanvasGroup.alpha is enough.
            if (s_Chrome.Root != null && !s_Chrome.Root.activeSelf)
                s_Chrome.Root.SetActive(true);
            if (s_OverlayGroup != null)
                s_OverlayGroup.alpha = 1f;
        }

        /// <summary>Hides the shared card. Safe to call when nothing is showing.</summary>
        public static void Hide()
        {
            s_ActiveSlot = -1;
            s_ActiveOwner = null;
            if (s_OverlayGroup != null)
                s_OverlayGroup.alpha = 0f;
        }

        /// <summary>Hides only if <paramref name="owner"/> is the relay that opened the card.</summary>
        public static void HideIfOwner(object owner)
        {
            if (owner == null || !ReferenceEquals(s_ActiveOwner, owner))
                return;
            Hide();
        }

        /// <summary>
        /// Tears down and rebuilds the overlay. Call when the Orbit Menu is shown so a
        /// leftover Unity 6 canvas from the last dock cannot stay invisible.
        /// </summary>
        public static void RecreateOverlay()
        {
            DestroyLeftoverDockTips();
            if (s_OverlayRoot != null)
                Object.Destroy(s_OverlayRoot);
            s_OverlayRoot = null;
            s_OverlayCanvas = null;
            s_OverlayGroup = null;
            s_Chrome = default;
            s_RankThumb = null;
            s_ActiveSlot = -1;
            s_ActiveOwner = null;
            EnsureOverlay();
        }

        /// <summary>
        /// Removes STAT TELEMETRY cards left on OrbitStationCanvas from the old
        /// nested-canvas path. Those leftovers stay invisible behind the dock.
        /// </summary>
        static void DestroyLeftoverDockTips()
        {
            Canvas[] canvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null)
                    continue;
                Transform child = canvas.transform.Find("ShipPowerBarStatTooltip");
                if (child == null)
                    continue;
                if (s_OverlayRoot != null && child.IsChildOf(s_OverlayRoot.transform))
                    continue;
                Object.Destroy(child.gameObject);
            }
        }

        /// <summary>Slot currently shown, or -1 when hidden. Hover relays use this to skip rebuilds.</summary>
        public static int ActiveSlot => s_ActiveSlot;

        /// <summary>Relay that opened the card, or null when hidden.</summary>
        public static object ActiveOwner => s_ActiveOwner;

        /// <summary>
        /// Creates (or reuses) the DontDestroyOnLoad overlay. Starts hidden via CanvasGroup.
        /// No GraphicRaycaster — this batch must not steal clicks from the tree.
        /// </summary>
        static void EnsureOverlay()
        {
            // [UNITY] Destroyed objects compare as null. A leftover Handles struct after
            // a failed dock close must not skip rebuild.
            if (s_OverlayRoot == null || s_OverlayCanvas == null || s_Chrome.Root == null)
            {
                if (s_OverlayRoot != null)
                    Object.Destroy(s_OverlayRoot);
                s_OverlayRoot = null;
                s_OverlayCanvas = null;
                s_OverlayGroup = null;
                s_Chrome = default;
                s_RankThumb = null;
            }

            if (s_OverlayRoot != null)
                return;

            // --- Overlay shell ---
            // [TITAN-ORBIT] Sort 260 sits above OrbitStationCanvas (200). Matching the
            // dock scaler (1920×1080) keeps the card size stable on laptop and 4K.
            var go = new GameObject("ShipPowerBarStatTooltipOverlay");
            Object.DontDestroyOnLoad(go);
            s_OverlayRoot = go;

            s_OverlayCanvas = go.AddComponent<Canvas>();
            s_OverlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            s_OverlayCanvas.sortingOrder = TipSortingOrder;
            s_OverlayCanvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            s_OverlayGroup = go.AddComponent<CanvasGroup>();
            s_OverlayGroup.alpha = 0f;
            s_OverlayGroup.blocksRaycasts = false;
            s_OverlayGroup.interactable = false;
            s_OverlayGroup.ignoreParentGroups = true;

            s_Chrome = ShipStatTooltipChrome.Build(
                "ShipPowerBarStatTooltip",
                go.transform,
                "STAT TELEMETRY",
                TipWidth,
                TipMinHeight,
                1f);

            // --- RANK 1 thumb ---
            // Small preview only. The body already names the hull.
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
                s_Chrome.Root.SetActive(true);
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
        /// then clamps to the overlay. Uses the slot's world corners so a 0×0 local
        /// rect on a freshly-shown dock still places the card on-screen.
        /// </summary>
        static void PositionNear(RectTransform anchor)
        {
            if (anchor == null || s_Chrome.RootRect == null || s_OverlayCanvas == null)
                return;

            RectTransform canvasRt = s_OverlayCanvas.transform as RectTransform;
            if (canvasRt == null)
                return;

            s_Chrome.RootRect.SetParent(canvasRt, false);
            s_Chrome.RootRect.SetAsLastSibling();

            Vector3[] corners = new Vector3[4];
            anchor.GetWorldCorners(corners);

            // Overlay canvas — screen pixels map with a null camera.
            Vector2 screenRight = RectTransformUtility.WorldToScreenPoint(null, (corners[2] + corners[3]) * 0.5f);
            Vector2 screenLeft = RectTransformUtility.WorldToScreenPoint(null, (corners[0] + corners[1]) * 0.5f);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRt, screenRight, null, out Vector2 localRight);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRt, screenLeft, null, out Vector2 localLeft);

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
