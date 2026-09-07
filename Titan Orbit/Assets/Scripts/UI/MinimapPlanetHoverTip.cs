using TitanOrbit.Data;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Small planet-name tooltip for minimap blips and edge markers.
    /// The player hovers a planet disc (or the off-screen arrow) and sees the same family name
    /// that floats above the planet in the world (<see cref="Game.PlanetWorldStatsLabel"/>).
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

        /// <summary>Cached ScriptableObject that maps planet id → ship-family display name.</summary>
        static PlanetShipFamilyConfig s_FamilyConfig;

        /// <summary>True after we tried Resources.Load so we do not retry every hover.</summary>
        static bool s_TriedFamilyConfig;

        /// <summary>Reused by GetWorldCorners so hover placement does not allocate every LateUpdate.</summary>
        static readonly Vector3[] s_WorldCorners = new Vector3[4];

        /// <summary>
        /// Tiny HUD card: dark glass fill, thin ice-blue rail, planet name only.
        /// Kept smaller than the ship-stat calculation cards on purpose.
        /// </summary>
        struct TipChrome
        {
            public GameObject Root;
            public RectTransform RootRect;
            public TextMeshProUGUI NameLabel;
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
        /// Enables pointer hits on an existing Graphic (edge-marker arrow) and binds the planet name.
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

            // [TITAN-ORBIT] Marker clicks still go through HandleMinimapClicks (Input System),
            // not EventSystem, so enabling raycast here does not block attack/defend placement.
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
        /// [UNITY] EventSystem hover enter. Builds the shared tip if needed, writes the name, and shows it.
        /// </summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            // --- Resolve name ---
            string planetName = ResolvePlanetName(_anchor);
            if (string.IsNullOrWhiteSpace(planetName))
                return;

            EnsureChrome();
            if (s_Chrome.Root == null || s_Chrome.NameLabel == null)
                return;

            s_Active = this;
            s_Chrome.NameLabel.text = planetName;
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
        /// Same name the world-space planet label shows: designer <c>familyName</c>, else camel-split familyId.
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

            // [TITAN-ORBIT] Homes always resolve to AstroEagle (config index 0). Neutrals use the
            // ghosted ShipFamilyConfigIndex rolled at spawn — do not key only on PlanetId.
            return config.GetPlanetDisplayName(
                anchor.PlanetId,
                anchor.IsHomePlanet,
                anchor.ShipFamilyConfigIndex);
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
        /// </summary>
        void EnsureChrome()
        {
            if (s_Chrome.Root != null)
            {
                // Hot-reload: keep the sit-on-top pivot even if this card was built with the old (0,0) pivot.
                if (s_Chrome.RootRect != null)
                    s_Chrome.RootRect.pivot = new Vector2(0.5f, 0f);
                return;
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
            rootRt.sizeDelta = new Vector2(80f, 22f);

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

            var textGo = new GameObject("Name", typeof(RectTransform));
            textGo.transform.SetParent(root.transform, false);
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(8f, 3f);
            textRt.offsetMax = new Vector2(-8f, -5f);
            var label = textGo.AddComponent<TextMeshProUGUI>();
            label.fontSize = 11f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            ApplyHudFont(label);

            root.transform.SetAsLastSibling();
            root.SetActive(false);

            s_Chrome = new TipChrome
            {
                Root = root,
                RootRect = rootRt,
                NameLabel = label,
                HostCanvas = canvas
            };
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

        /// <summary>Shrinks or grows the card to the current planet name plus padding.</summary>
        static void FitToText()
        {
            if (s_Chrome.NameLabel == null || s_Chrome.RootRect == null)
                return;

            s_Chrome.NameLabel.ForceMeshUpdate();
            Vector2 preferred = s_Chrome.NameLabel.GetPreferredValues(s_Chrome.NameLabel.text);
            float width = Mathf.Clamp(preferred.x + 16f, 48f, 220f);
            float height = Mathf.Max(20f, preferred.y + 10f);
            s_Chrome.RootRect.sizeDelta = new Vector2(width, height);
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
