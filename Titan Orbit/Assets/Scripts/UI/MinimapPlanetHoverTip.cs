using TitanOrbit.Data;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Small planet tooltip for minimap blips and edge markers.
    /// The player hovers a planet disc (or the off-screen arrow) and sees the same proper world
    /// name that floats above the planet in the world (<see cref="Game.PlanetWorldStatsLabel"/>),
    /// plus the ship family that planet rolls (Astro Eagle, Cosmic Shark, …) in a smaller subtitle.
    /// <para>
    /// Client presentation only — reads <see cref="MinimapBlipAnchor"/> plus
    /// <see cref="PlanetShipFamilyConfig"/>. No ECS gathers, no sim writes.
    /// Paired with <see cref="MinimapController"/> (creates the hover hit pad) and
    /// <see cref="MinimapEcsEntitySync"/> (fills PlanetId / home / family index).
    /// </para>
    /// [TITAN-ORBIT] One shared tip instance for the whole minimap so we do not spawn a
    /// GameObject per planet. Dark void glass matches <see cref="ShipStatTooltipChrome"/>.
    /// </summary>
    public sealed class MinimapPlanetHoverTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Minimum hover pad in canvas pixels so tiny collapsed-map discs stay clickable.</summary>
        public const float MinHitSize = 20f;

        /// <summary>World-space planet this pad belongs to. Set by <see cref="Bind"/>.</summary>
        MinimapBlipAnchor _anchor;

        /// <summary>Shared floating card — created once, shown/hidden on hover.</summary>
        static TipChrome s_Chrome;

        /// <summary>Which hover pad currently owns the tip (so Exit from an old pad cannot hide a newer hover).</summary>
        static MinimapPlanetHoverTip s_Active;

        /// <summary>Cached ScriptableObject that maps planet id → world name and ship family.</summary>
        static PlanetShipFamilyConfig s_FamilyConfig;

        /// <summary>True after we tried Resources.Load so we do not retry every hover.</summary>
        static bool s_TriedFamilyConfig;

        /// <summary>Reused by GetWorldCorners so hover placement does not allocate every LateUpdate.</summary>
        static readonly Vector3[] s_WorldCorners = new Vector3[4];

        /// <summary>World-name title on the hover card — same size the old single-line tip used.</summary>
        const float PlanetNameFontSize = 11f;

        /// <summary>Ship-family subtitle — smaller and lighter so the place name stays primary.</summary>
        const float FamilyNameFontSize = 8f;

        /// <summary>Horizontal padding inside the card (left + right each).</summary>
        const float CardPadX = 8f;

        /// <summary>Top padding under the ice-blue rail.</summary>
        const float CardPadTop = 5f;

        /// <summary>Bottom padding under the family line (or the name when family is missing).</summary>
        const float CardPadBottom = 4f;

        /// <summary>Gap between the world name and the family subtitle.</summary>
        const float NameToFamilyGap = 1f;

        /// <summary>
        /// Tiny HUD card: dark glass fill, thin ice-blue rail, world name + smaller family subtitle.
        /// Kept smaller than the ship-stat calculation cards on purpose.
        /// </summary>
        struct TipChrome
        {
            public GameObject Root;
            public RectTransform RootRect;
            public TextMeshProUGUI NameLabel;
            public TextMeshProUGUI FamilyLabel;
            public Canvas HostCanvas;
        }

        /// <summary>
        /// Adds (or reuses) a transparent circular hit pad on <paramref name="blipRoot"/>
        /// and binds it to <paramref name="anchor"/>. Call from planet-blip create / update.
        /// </summary>
        /// <param name="blipRoot">Planet blip RectTransform under the circular mask.</param>
        /// <param name="anchor">Hidden world anchor that holds PlanetId and family index.</param>
        /// <param name="blipSize">Current planet disc size in canvas pixels.</param>
        public static void AttachToPlanetBlip(RectTransform blipRoot, MinimapBlipAnchor anchor, float blipSize)
        {
            // --- Guard ---
            if (blipRoot == null || anchor == null)
                return;

            // --- Find or create the invisible hit pad ---
            // [UNITY] Children named HoverHit stay under the planet so the pad moves with the blip.
            Transform existing = blipRoot.Find("HoverHit");
            RectTransform hitRt;
            Image hitImg;
            MinimapPlanetHoverTip tip;
            if (existing == null)
            {
                var hitGo = new GameObject("HoverHit", typeof(RectTransform));
                hitGo.transform.SetParent(blipRoot, false);
                hitRt = hitGo.GetComponent<RectTransform>();
                hitRt.anchorMin = new Vector2(0.5f, 0.5f);
                hitRt.anchorMax = new Vector2(0.5f, 0.5f);
                hitRt.pivot = new Vector2(0.5f, 0.5f);
                hitRt.anchoredPosition = Vector2.zero;
                hitImg = hitGo.AddComponent<Image>();
                // [UNITY] alphaHitTestMinimumThreshold defaults to 0, so a fully transparent Image still receives rays.
                hitImg.color = Color.clear;
                hitImg.raycastTarget = true;
                tip = hitGo.AddComponent<MinimapPlanetHoverTip>();
            }
            else
            {
                hitRt = existing as RectTransform;
                hitImg = existing.GetComponent<Image>();
                tip = existing.GetComponent<MinimapPlanetHoverTip>();
                if (hitRt == null)
                    return;
                if (hitImg == null)
                    hitImg = existing.gameObject.AddComponent<Image>();
                if (tip == null)
                    tip = existing.gameObject.AddComponent<MinimapPlanetHoverTip>();
                hitImg.color = Color.clear;
                hitImg.raycastTarget = true;
            }

            // --- Size the pad ---
            // Disc can be smaller than a comfortable mouse target on the collapsed radar.
            float hit = Mathf.Max(MinHitSize, blipSize);
            hitRt.sizeDelta = new Vector2(hit, hit);
            tip.Bind(anchor);
        }

        /// <summary>
        /// Enables pointer hits on an existing Graphic (edge-marker arrow) and binds the planet.
        /// Edge markers live outside the circular mask, so we reuse their Image instead of adding a child.
        /// </summary>
        /// <param name="markerRoot">Edge-marker GameObject with an Image.</param>
        /// <param name="anchor">Planet (or home) anchor for that arrow.</param>
        public static void AttachToEdgeMarker(GameObject markerRoot, MinimapBlipAnchor anchor)
        {
            // --- Guard ---
            if (markerRoot == null || anchor == null)
                return;

            Image img = markerRoot.GetComponent<Image>();
            if (img == null)
                return;

            // [TITAN-ORBIT] Death-picker / comms Here clicks go through HandleMinimapClicks
            // (Input System), not EventSystem, so enabling raycast here does not steal those hits.
            img.raycastTarget = true;

            var tip = markerRoot.GetComponent<MinimapPlanetHoverTip>();
            if (tip == null)
                tip = markerRoot.AddComponent<MinimapPlanetHoverTip>();
            tip.Bind(anchor);
        }

        /// <summary>
        /// Stores the planet anchor this pad will name on hover.
        /// Called at create time and again if the same UI object is recycled.
        /// </summary>
        /// <param name="anchor">Minimap world anchor for this planet.</param>
        public void Bind(MinimapBlipAnchor anchor)
        {
            _anchor = anchor;
        }

        /// <summary>
        /// [UNITY] EventSystem hover enter. Builds the shared tip if needed, writes the world name
        /// plus ship-family subtitle, and shows the card.
        /// </summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            // --- Resolve names ---
            // Planet name is required — no card without a place name. Family can be blank
            // (missing catalog row) and the subtitle simply hides.
            string planetName = ResolvePlanetName(_anchor);
            if (string.IsNullOrWhiteSpace(planetName))
                return;

            EnsureChrome();
            if (s_Chrome.Root == null || s_Chrome.NameLabel == null)
                return;

            s_Active = this;
            s_Chrome.NameLabel.text = planetName;
            ApplyFamilySubtitle(ResolveFamilyName(_anchor));
            s_Chrome.Root.SetActive(true);
            FitToText();
            PlaceBesideHoveredPlanet();
        }

        /// <summary>
        /// [UNITY] EventSystem hover exit. Hides the tip only if this pad still owns it
        /// (moving onto another planet fires Enter on the new pad first in some frames).
        /// </summary>
        public void OnPointerExit(PointerEventData eventData)
        {
            if (s_Active != this)
                return;

            HideSharedTip();
        }

        /// <summary>Hides the tip if this pad is destroyed, deactivated, or the blip leaves the circle.</summary>
        void OnDisable()
        {
            if (s_Active == this)
                HideSharedTip();
        }

        /// <summary>
        /// Keeps the tip glued to the hovered planet blip while this pad owns it.
        /// LateUpdate runs after <see cref="MinimapController"/> moves blips so the card tracks the disc.
        /// </summary>
        void LateUpdate()
        {
            if (s_Active != this || s_Chrome.Root == null || !s_Chrome.Root.activeSelf)
                return;

            PlaceBesideHoveredPlanet();
        }

        /// <summary>Deactivates the shared card and clears the active-pad pointer.</summary>
        static void HideSharedTip()
        {
            s_Active = null;
            if (s_Chrome.Root != null)
                s_Chrome.Root.SetActive(false);
        }

        /// <summary>
        /// Same proper world name the world-space planet label shows (not the ship family).
        /// </summary>
        /// <param name="anchor">Planet blip anchor (null-safe).</param>
        /// <returns>Display name, or empty when config / family is missing.</returns>
        static string ResolvePlanetName(MinimapBlipAnchor anchor)
        {
            if (anchor == null)
                return string.Empty;

            PlanetShipFamilyConfig config = GetFamilyConfig();
            if (config == null)
                return string.Empty;

            // [TITAN-ORBIT] Homes name by team id (Helios, Thalassa, …). Neutrals name by
            // PlanetId (100+). Family index only matters if the designer filled planetName.
            return config.GetPlanetDisplayName(
                anchor.PlanetId,
                anchor.IsHomePlanet,
                anchor.ShipFamilyConfigIndex);
        }

        /// <summary>
        /// Ship-tree label for this planet (Astro Eagle, Cosmic Shark, …).
        /// Same catalog string hulls and the upgrade tree use — not the gun-type subtitle
        /// the world label shows under the place name.
        /// </summary>
        /// <param name="anchor">Planet blip anchor (null-safe).</param>
        /// <returns>Family display name, or empty when the catalog row is missing.</returns>
        static string ResolveFamilyName(MinimapBlipAnchor anchor)
        {
            if (anchor == null)
                return string.Empty;

            PlanetShipFamilyConfig config = GetFamilyConfig();
            if (config == null)
                return string.Empty;

            // [TITAN-ORBIT] Homes always resolve to the home family (index 0 / Astro Eagle).
            // Neutrals use the ghosted ShipFamilyConfigIndex written by MinimapEcsEntitySync.
            return config.GetFamilyDisplayName(
                anchor.PlanetId,
                anchor.IsHomePlanet,
                anchor.ShipFamilyConfigIndex);
        }

        /// <summary>
        /// Writes or hides the family subtitle. Empty family keeps a one-line card (name only).
        /// </summary>
        /// <param name="familyName">Resolved ship family, or empty.</param>
        static void ApplyFamilySubtitle(string familyName)
        {
            if (s_Chrome.FamilyLabel == null)
                return;

            bool show = !string.IsNullOrWhiteSpace(familyName);
            s_Chrome.FamilyLabel.gameObject.SetActive(show);
            if (show)
                s_Chrome.FamilyLabel.text = familyName;
        }

        /// <summary>Loads <c>Resources/PlanetShipFamilyConfig</c> once.</summary>
        static PlanetShipFamilyConfig GetFamilyConfig()
        {
            if (s_TriedFamilyConfig)
                return s_FamilyConfig;

            s_TriedFamilyConfig = true;
            s_FamilyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            return s_FamilyConfig;
        }

        /// <summary>
        /// Builds the shared card under the minimap root (outside the circular Mask) so the
        /// name is not clipped and is not faded when the expanded map hides sibling HUD.
        /// Rebuilds if this session still has the older name-only card from a domain reload.
        /// </summary>
        void EnsureChrome()
        {
            if (s_Chrome.Root != null)
            {
                // --- Reuse current two-line card ---
                if (s_Chrome.FamilyLabel != null)
                {
                    // Hot-reload: keep the sit-on-top pivot even if this card was built with the old (0,0) pivot.
                    if (s_Chrome.RootRect != null)
                        s_Chrome.RootRect.pivot = new Vector2(0.5f, 0f);
                    return;
                }

                // --- Stale name-only chrome ---
                // [UNITY] Destroy the leftover GameObject so we do not leak a second tooltip.
                Destroy(s_Chrome.Root);
                s_Chrome = default;
            }

            // --- Host: minimap root, not canvas ---
            // ApplyHideNonMinimapUi fades canvas siblings when expanded. Parenting here keeps the tip visible.
            var minimap = GetComponentInParent<MinimapController>();
            Transform host = minimap != null ? minimap.transform : transform;
            Canvas canvas = host.GetComponentInParent<Canvas>();

            var root = new GameObject("MinimapPlanetNameTooltip");
            root.transform.SetParent(host, false);
            var rootRt = root.AddComponent<RectTransform>();
            // Point-anchor at the parent centre. Collapsed minimap pivot is bottom-right —
            // PlaceBesideHoveredPlanet converts pivot-local screen points into this anchor space.
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            // Bottom-centre: the card sits on the planet's top, not under the cursor.
            rootRt.pivot = new Vector2(0.5f, 0f);
            rootRt.sizeDelta = new Vector2(80f, 28f);

            // [TITAN-ORBIT] Same void glass as ShipStatTooltipChrome — small nameplate, not a calc card.
            Image fill = root.AddComponent<Image>();
            fill.color = new Color(0.012f, 0.016f, 0.028f, 0.96f);
            fill.raycastTarget = false;

            var accentGo = new GameObject("Accent", typeof(RectTransform));
            accentGo.transform.SetParent(root.transform, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = Vector2.zero;
            accentRt.sizeDelta = new Vector2(0f, 2f);
            Image accent = accentGo.AddComponent<Image>();
            accent.color = new Color(0.35f, 0.72f, 0.95f, 0.95f);
            accent.raycastTarget = false;

            // --- Two-line stack: place name on top, family under it ---
            // Both labels hang from the top so FitToText can grow the card downward.
            TextMeshProUGUI nameLabel = CreateLineLabel(
                "Name",
                root.transform,
                PlanetNameFontSize,
                FontStyles.Bold,
                new Color(0.88f, 0.92f, 0.98f, 1f));
            TextMeshProUGUI familyLabel = CreateLineLabel(
                "Family",
                root.transform,
                FamilyNameFontSize,
                FontStyles.Normal,
                new Color(0.62f, 0.78f, 0.95f, 0.88f));

            root.transform.SetAsLastSibling();
            root.SetActive(false);

            s_Chrome = new TipChrome
            {
                Root = root,
                RootRect = rootRt,
                NameLabel = nameLabel,
                FamilyLabel = familyLabel,
                HostCanvas = canvas
            };
        }

        /// <summary>
        /// Builds one left-aligned HUD line under the tooltip root.
        /// Anchored to the top so the family line can sit under the world name without a layout group.
        /// </summary>
        /// <param name="objectName">Child name (Name / Family).</param>
        /// <param name="parent">Tooltip root.</param>
        /// <param name="fontSize">TMP point size.</param>
        /// <param name="style">Bold for the place name, Normal for the family subtitle.</param>
        /// <param name="color">Near-white title vs cooler caption blue.</param>
        static TextMeshProUGUI CreateLineLabel(
            string objectName,
            Transform parent,
            float fontSize,
            FontStyles style,
            Color color)
        {
            var go = new GameObject(objectName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(-(CardPadX * 2f), fontSize + 4f);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = color;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            ApplyHudFont(label);
            return label;
        }

        /// <summary>Prefers Shift Rajdhani so the tip matches other HUD chrome.</summary>
        static void ApplyHudFont(TextMeshProUGUI tmp)
        {
            if (tmp == null)
                return;

            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
        }

        /// <summary>
        /// Shrinks or grows the card to the world name (and family subtitle when shown).
        /// Also parks each line: name under the top pad, family under the name.
        /// </summary>
        static void FitToText()
        {
            if (s_Chrome.NameLabel == null || s_Chrome.RootRect == null)
                return;

            // --- Measure both lines ---
            s_Chrome.NameLabel.ForceMeshUpdate();
            Vector2 namePref = s_Chrome.NameLabel.GetPreferredValues(s_Chrome.NameLabel.text);

            bool showFamily = s_Chrome.FamilyLabel != null && s_Chrome.FamilyLabel.gameObject.activeSelf;
            Vector2 familyPref = Vector2.zero;
            if (showFamily)
            {
                s_Chrome.FamilyLabel.ForceMeshUpdate();
                familyPref = s_Chrome.FamilyLabel.GetPreferredValues(s_Chrome.FamilyLabel.text);
            }

            float textWidth = showFamily ? Mathf.Max(namePref.x, familyPref.x) : namePref.x;
            float width = Mathf.Clamp(textWidth + (CardPadX * 2f), 48f, 220f);
            float gap = showFamily ? NameToFamilyGap : 0f;
            float height = Mathf.Max(20f, CardPadTop + namePref.y + gap + familyPref.y + CardPadBottom);
            s_Chrome.RootRect.sizeDelta = new Vector2(width, height);

            // --- Park lines from the top ---
            // Labels are top-stretched; sizeDelta.x is the inset (negative = pad both sides).
            float lineWidth = -(CardPadX * 2f);
            PlaceLine(s_Chrome.NameLabel.rectTransform, -CardPadTop, namePref.y, lineWidth);
            if (showFamily)
            {
                float familyY = -(CardPadTop + namePref.y + gap);
                PlaceLine(s_Chrome.FamilyLabel.rectTransform, familyY, familyPref.y, lineWidth);
            }
        }

        /// <summary>
        /// Sets one top-anchored line's local Y and height inside the card.
        /// </summary>
        /// <param name="rt">Name or Family RectTransform.</param>
        /// <param name="anchoredY">Negative offset from the card top (under the rail).</param>
        /// <param name="height">Preferred TMP height for this line.</param>
        /// <param name="widthDelta">Negative width inset so left/right padding stays even.</param>
        static void PlaceLine(RectTransform rt, float anchoredY, float height, float widthDelta)
        {
            if (rt == null)
                return;

            rt.anchoredPosition = new Vector2(0f, anchoredY);
            rt.sizeDelta = new Vector2(widthDelta, Mathf.Max(8f, height));
        }

        /// <summary>
        /// Parks the card just above the hovered planet disc (or edge arrow).
        /// <para>
        /// Collapsed radar uses pivot (1,0) bottom-right; expanded uses (0.5, 0.5).
        /// <see cref="RectTransformUtility.ScreenPointToLocalPointInRectangle"/> is pivot-local,
        /// but this card's <c>anchoredPosition</c> is centre-anchor — those only match when expanded.
        /// We convert explicitly so the name stays on the planet in both modes.
        /// </para>
        /// The tip is allowed to sit just outside the small circle (clamped to the HUD canvas,
        /// not the 150px radar) so a wide name is not shoved to the opposite side of the disc.
        /// </summary>
        void PlaceBesideHoveredPlanet()
        {
            if (s_Chrome.RootRect == null || s_Chrome.HostCanvas == null)
                return;

            var hoverRt = transform as RectTransform;
            if (hoverRt == null)
                return;

            // Planet pads live on a HoverHit child — pin to the disc itself, not the larger hit box.
            RectTransform visualRt = hoverRt;
            if (hoverRt.name == "HoverHit" && hoverRt.parent is RectTransform parentBlip)
                visualRt = parentBlip;

            var canvasRt = s_Chrome.HostCanvas.transform as RectTransform;
            if (canvasRt == null)
                return;

            RectTransform parentRt = s_Chrome.RootRect.parent as RectTransform;
            if (parentRt == null)
                parentRt = canvasRt;

            // [UNITY] Qualify Camera — TitanOrbit.Camera is a namespace and would steal the short name.
            UnityEngine.Camera uiCam = s_Chrome.HostCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : s_Chrome.HostCanvas.worldCamera;

            // --- Anchor point: top-centre of the disc, or screen-up from a rotated edge arrow ---
            // GetWorldCorners is clockwise from bottom-left: 0=BL, 1=TL, 2=TR, 3=BR.
            visualRt.GetWorldCorners(s_WorldCorners);
            bool rotated = Mathf.Abs(Mathf.DeltaAngle(visualRt.eulerAngles.z, 0f)) > 1f;
            Vector3 attachWorld;
            if (rotated)
            {
                // Edge arrows spin to point off-map; "local top" is not screen-up. Use the AABB centre.
                attachWorld = (s_WorldCorners[0] + s_WorldCorners[2]) * 0.5f;
            }
            else
            {
                attachWorld = (s_WorldCorners[1] + s_WorldCorners[2]) * 0.5f;
            }

            Vector2 screen = RectTransformUtility.WorldToScreenPoint(uiCam, attachWorld);
            // A few pixels above the disc so the card does not cover the population number.
            screen += new Vector2(0f, rotated ? 14f : 6f);

            if (!TryScreenToAnchoredPosition(s_Chrome.RootRect, parentRt, screen, uiCam, out Vector2 anchored))
                return;

            s_Chrome.RootRect.anchoredPosition = ClampAnchoredToCanvas(
                s_Chrome.RootRect, parentRt, canvasRt, anchored);
            s_Chrome.Root.transform.SetAsLastSibling();
        }

        /// <summary>
        /// Converts a screen pixel into <paramref name="child"/>.<c>anchoredPosition</c>.
        /// Handles the collapsed-minimap case where parent pivot ≠ child point-anchor.
        /// </summary>
        /// <returns>False when the screen point cannot be mapped into the parent rectangle.</returns>
        static bool TryScreenToAnchoredPosition(
            RectTransform child,
            RectTransform parent,
            Vector2 screen,
            UnityEngine.Camera uiCam,
            out Vector2 anchored)
        {
            anchored = default;
            if (child == null || parent == null)
                return false;

            // Pivot-local: (0,0) is parent.pivot, not the child's anchor.
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, uiCam, out Vector2 fromPivot))
                return false;

            // Point-anchor children: anchoredPosition is the child's pivot relative to that anchor.
            // Anchor location in the same pivot-local space = parentSize * (anchor - pivot).
            Vector2 parentSize = parent.rect.size;
            Vector2 anchorFromPivot = Vector2.Scale(parentSize, child.anchorMin - parent.pivot);
            anchored = fromPivot - anchorFromPivot;
            return true;
        }

        /// <summary>
        /// Nudges <paramref name="anchored"/> so the card stays on the HUD canvas.
        /// Does <b>not</b> clamp to the small minimap rect — a name wider than the radar
        /// is allowed to hang into the game view.
        /// </summary>
        static Vector2 ClampAnchoredToCanvas(
            RectTransform child,
            RectTransform parent,
            RectTransform canvasRt,
            Vector2 anchored)
        {
            if (child == null || parent == null || canvasRt == null)
                return anchored;

            Vector2 size = child.rect.size;
            if (size.x < 1f)
                size = child.sizeDelta;

            // Child AABB in parent pivot-local space (same space as ScreenPointToLocalPointInRectangle).
            Vector2 parentSize = parent.rect.size;
            Vector2 anchorFromPivot = Vector2.Scale(parentSize, child.anchorMin - parent.pivot);
            Vector2 pivotLocal = anchored + anchorFromPivot;
            Vector2 childPivot = child.pivot;
            float xMin = pivotLocal.x - size.x * childPivot.x;
            float xMax = pivotLocal.x + size.x * (1f - childPivot.x);
            float yMin = pivotLocal.y - size.y * childPivot.y;
            float yMax = pivotLocal.y + size.y * (1f - childPivot.y);

            canvasRt.GetWorldCorners(s_WorldCorners);
            Vector2 canvasMin = parent.InverseTransformPoint(s_WorldCorners[0]);
            Vector2 canvasMax = parent.InverseTransformPoint(s_WorldCorners[2]);

            const float pad = 4f;
            float dx = 0f;
            float dy = 0f;
            if (xMin < canvasMin.x + pad)
                dx = canvasMin.x + pad - xMin;
            else if (xMax > canvasMax.x - pad)
                dx = canvasMax.x - pad - xMax;
            if (yMin < canvasMin.y + pad)
                dy = canvasMin.y + pad - yMin;
            else if (yMax > canvasMax.y - pad)
                dy = canvasMax.y - pad - yMax;

            return anchored + new Vector2(dx, dy);
        }
    }
}
