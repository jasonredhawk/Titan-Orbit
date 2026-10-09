using TitanOrbit.Data;
using TMPro;
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

        /// <summary>
        /// RANK 1 hull portrait in the bottom-right of the card.
        /// Large enough to read the silhouette (wings, nose, color) without
        /// turning the telemetry card into a second ship panel.
        /// The portrait overlaps the corner that was already there. It does not
        /// add a blank row under the copy — that left a wide empty strip.
        /// </summary>
        const float ThumbSize = 84f;

        /// <summary>
        /// TMP right margin for lines the portrait covers, in ems (1 em = body font size).
        /// About the portrait width, so those lines wrap in the column left of the hull.
        /// Lines above the sprite stay full width — a right gutter on the whole card
        /// would just move the empty space.
        /// </summary>
        const string RankCornerMargin = "<margin-right=8.5em>";

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
        /// <param name="megaPool">True when the bar used Titan catalog maxes. Ignored when <paramref name="pool"/> is set.</param>
        /// <param name="anchor">Slot or bar rect to sit the tip next to.</param>
        /// <param name="thisChassisId">Hull id, or a component leader key on gear cards.</param>
        /// <param name="owner">Hover relay that owns this showing; used so another bar's exit cannot hide it.</param>
        /// <param name="pool">Regular ships, Titans, or gear components. Unset follows <paramref name="megaPool"/>.</param>
        /// <param name="componentShipLevel">Gear only. Extra Level of the component ceiling.</param>
        public static void Show(
            int statIndex,
            in ShipFamilyPowerScoreBreakdown breakdown,
            in ShipPowerBarStatMaxes maxes,
            bool megaPool,
            RectTransform anchor,
            string thisChassisId,
            object owner,
            ShipPowerBarComparisonPool pool = ShipPowerBarComparisonPool.Unset,
            int componentShipLevel = 1)
        {
            if (statIndex < 0 || statIndex >= ShipAbilityCategoryColors.PowerBreakdownStatCount)
                return;

            if (pool == ShipPowerBarComparisonPool.Unset)
                pool = megaPool ? ShipPowerBarComparisonPool.Titans : ShipPowerBarComparisonPool.RegularShips;

            EnsureOverlay();
            if (s_Chrome.Root == null)
                return;

            s_ActiveSlot = statIndex;
            s_ActiveOwner = owner;

            // Gear slot 0 is empty. Ship slot 0 is gun DPS. The percent must match the fill.
            bool componentPool = pool == ShipPowerBarComparisonPool.Components;
            float thisValue = componentPool
                ? breakdown.GetComponentCompareStatValue(statIndex)
                : breakdown.GetDisplayStatValue(statIndex);
            float maxValue = maxes.Get(statIndex);
            string body = ShipPowerBarStatCopy.BuildPowerBarTipBody(
                statIndex, thisValue, maxValue, pool, thisChassisId, componentShipLevel);

            ApplyRankThumb(statIndex, pool, thisChassisId, thisValue, componentShipLevel);

            if (s_Chrome.CaptionLabel != null)
                s_Chrome.CaptionLabel.text = "STAT TELEMETRY";
            if (s_Chrome.BodyLabel != null)
                s_Chrome.BodyLabel.text = body;

            ShipStatTooltipChrome.ApplyAccent(
                in s_Chrome,
                ShipStatTooltipChrome.AccentForAbilityIndex(statIndex));

            // Height first, then wrap only the lines the portrait actually covers.
            // Wrapping before the measure would narrow the RANK 1 block even when
            // the sprite sits in padding that was already empty.
            SizeToBody();
            TuckPortraitIntoCorner();
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

            // --- RANK 1 portrait ---
            // Bottom-right corner. The body already names the hull; this art
            // is how the player recognizes which ship that name belongs to.
            // Parenting after the chrome body keeps the sprite above the text.
            var thumbGo = new GameObject("RankThumb");
            thumbGo.transform.SetParent(s_Chrome.Root.transform, false);
            RectTransform thumbRt = thumbGo.AddComponent<RectTransform>();
            thumbRt.anchorMin = new Vector2(1f, 0f);
            thumbRt.anchorMax = new Vector2(1f, 0f);
            thumbRt.pivot = new Vector2(1f, 0f);
            thumbRt.anchoredPosition = new Vector2(-12f, 12f);
            thumbRt.sizeDelta = new Vector2(ThumbSize, ThumbSize);
            s_RankThumb = thumbGo.AddComponent<Image>();
            s_RankThumb.raycastTarget = false;
            s_RankThumb.preserveAspect = true;
            s_RankThumb.enabled = false;

            if (s_Chrome.Root != null)
                s_Chrome.Root.SetActive(true);
        }

        /// <summary>Shows the winner's menu sprite when the hovered hull or part is not RANK 1.</summary>
        static void ApplyRankThumb(
            int statIndex,
            ShipPowerBarComparisonPool pool,
            string thisChassisId,
            float thisValue,
            int componentShipLevel)
        {
            if (s_RankThumb == null)
                return;

            ShipPowerBarStatLeader leader = ShipFamilyPowerBarNorm.GetStatLeader(
                statIndex, pool, componentShipLevel);
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

        /// <summary>
        /// Wraps copy that runs into the portrait so it sits in the column to the left.
        /// Does not grow a blank footer. Text above the sprite stays full width, and
        /// any corner that was already empty stays empty — the hull just occupies it.
        /// </summary>
        static void TuckPortraitIntoCorner()
        {
            if (s_RankThumb == null || !s_RankThumb.enabled || s_Chrome.BodyLabel == null)
                return;

            TextMeshProUGUI body = s_Chrome.BodyLabel;
            if (string.IsNullOrEmpty(body.text) || body.text.Contains(RankCornerMargin))
                return;

            // Mesh has to exist before line bottoms are real. SizeToBody already forced one pass.
            body.ForceMeshUpdate(true);
            TMP_TextInfo info = body.textInfo;
            if (info == null || info.lineCount <= 0)
                return;

            // World y grows upward. The portrait's top corner is the line we must clear.
            Vector3[] corners = new Vector3[4];
            s_RankThumb.rectTransform.GetWorldCorners(corners);
            float shipTop = corners[1].y;

            RectTransform bodyRt = body.rectTransform;
            int insertAt = -1;
            bool overlapsText = false;
            for (int i = 0; i < info.lineCount; i++)
            {
                TMP_LineInfo line = info.lineInfo[i];
                if (line.characterCount <= 0)
                    continue;
                if (line.firstCharacterIndex < 0 || line.firstCharacterIndex >= info.characterCount)
                    continue;

                // bottomLeft is a mesh vertex in the text's local space (y up).
                TMP_CharacterInfo ch = info.characterInfo[line.firstCharacterIndex];
                float lineBottom = bodyRt.TransformPoint(new Vector3(0f, ch.bottomLeft.y, 0f)).y;
                if (lineBottom >= shipTop)
                    continue;

                // This line runs into the hull. Lines above it stay full width.
                overlapsText = true;
                insertAt = ch.index;
                // index is the source-string position of this glyph. If it does not
                // match, the mesh index drifted from the rich-text string — fall
                // through and use the RANK 1 banner instead of splicing a tag.
                if (insertAt >= 0 && insertAt < body.text.Length && body.text[insertAt] == ch.character)
                    break;

                insertAt = -1;
                break;
            }

            // Portrait already sits in empty padding. Leave the copy full width.
            if (!overlapsText)
                return;

            if (insertAt < 0)
                insertAt = IndexOfRankBanner(body.text);
            if (insertAt < 0 || insertAt > body.text.Length)
                return;

            // Tag goes in front of the first glyph the hull would cover. TMP keeps
            // that right margin through the rest of the card (the RANK 1 block).
            body.text = body.text.Insert(insertAt, RankCornerMargin);
            SizeToBody();
        }

        /// <summary>
        /// Source index of the RANK 1 colour tag, or -1 when this tip has no leader line.
        /// Used when the mesh index cannot be trusted.
        /// </summary>
        static int IndexOfRankBanner(string body)
        {
            if (string.IsNullOrEmpty(body))
                return -1;

            const string marker = "> RANK 1";
            int markerAt = body.IndexOf(marker, System.StringComparison.Ordinal);
            if (markerAt < 0)
                return -1;

            int colorAt = body.LastIndexOf("<color=", markerAt, System.StringComparison.Ordinal);
            return colorAt >= 0 ? colorAt : markerAt;
        }

        /// <summary>
        /// Fits the card height to the TMP body so short stats do not leave a tall empty plate.
        /// The RANK 1 portrait overlaps the bottom-right corner. It does not add height —
        /// extra height would stack as a blank strip to the left of the hull.
        /// </summary>
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
