using TMPro;
using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Single ship node widget in the upgrade tree prefab. Displays level, hull name, family name,
    /// price, preview sprite, and ten-segment power bar. Population and click handlers are driven by
    /// <see cref="ShipUpgradeTreeUI"/> and <see cref="OrbitStationUI"/>; this class owns layout
    /// scaling and price-button chrome.
    /// The family line sits under the hull name and just above the buy chip
    /// (CosmicShark → Cosmic Shark, smaller than the ship name).
    /// Level-7 MEGA cards use a separate bronze-void fill, gold frame, and "MEGA SHIP" caption
    /// so they read as boss hulls next to the navy L1–L6 family cards.
    /// </summary>
    public class ShipUpgradeTreeNodeUI : MonoBehaviour
    {
        /// <summary>Equal left/right inset used by tree cards and the power-bar track width.</summary>
        public const float TreeCardEdgePad = 6f;

        /// <summary>Reference metrics from <see cref="Editor.CreateShipUpgradeTreePrefab"/> node template (120├ù100).</summary>
        private static class RefLayout
        {
            /// <summary>Same inset on every side so the power bar and preview share one margin.</summary>
            public const float CardPad = TreeCardEdgePad;
            public const float RootSpacing = 4f;
            public const float ContentMinHeight = 72f;
            public const float LeftSpacing = 2f;
            public const float LeftMinWidth = 40f;
            public const float LevelFontSize = 13f;
            public const float LevelHeight = 14f;
            public const float NameFontSize = 11f;
            /// <summary>One line. The old 26px slot invited wrap even when overflow had room.</summary>
            public const float NameHeight = 16f;
            public const float NameMinHeight = 16f;
            /// <summary>Family line under the hull name — a few points smaller so Cosmic Shark reads as secondary.</summary>
            public const float FamilyFontSize = 8f;
            public const float FamilyHeight = 12f;
            public const float FamilyMinHeight = 11f;
            /// <summary>MEGA SHIP overlay — 2pt above the ship name so it reads as the card rank.</summary>
            public const float MegaCaptionFontExtra = 2f;
            /// <summary>Tight tray inset — a few pixels so lanes sit inside the dark well.</summary>
            public const float PowerBarTrackPadX = 3f;
            public const float PowerBarTrackPadY = 3f;
            public const float PriceFontSize = 11f;
            public const float PriceHeight = 16f;
            public const float PriceMinWidth = 40f;
            public const float PreviewColWidth = 56f;
            public const float PreviewSize = 56f;
            public const float PreviewMinHeight = 48f;
            public const float PowerBarHeight = 10f;
            public const float PowerBarMinWidth = 48f;
        }

        /// <summary>Height reserved for the hull name overlaid on the Your Ship preview.</summary>
        const float HeroNameOverlayHeight = 28f;

        [Header("Reference layout (prefab editor preview; runtime width comes from panel)")]
        [Tooltip("Reference width for prefab authoring. Runtime nodes scale uniformly to fill the tree row.")]
        [SerializeField] private float layoutWidth = 120f;
        [Tooltip("Reference height; runtime height scales with width using this aspect ratio.")]
        [SerializeField] private float layoutHeight = 100f;

        [SerializeField] private Button button;
        [SerializeField] private Button priceButton;
        [SerializeField] private TextMeshProUGUI levelText;
        [SerializeField] private TextMeshProUGUI shipNameText;
        /// <summary>
        /// Optional prefab wire for the family line. Runtime cards create this under LeftMiddle
        /// when the older ShipUpgradeTreeNode prefab has no child — do not require a prefab rebuild.
        /// </summary>
        [SerializeField] private TextMeshProUGUI familyNameText;
        [SerializeField] private TextMeshProUGUI priceText;
        [SerializeField] private Image previewImage;
        [SerializeField] private ShipUpgradeTreePowerBarUI powerBar;
        [SerializeField] private bool moonHorizontalLayout;

        public int Level { get; private set; }
        public int BranchIndex { get; private set; }
        public ShipUpgradeNode Node { get; private set; }
        /// <summary>Moon tree: dedicated top-left node showing the player's current hull (not a ladder slot).</summary>
        public bool IsCurrentShipDisplay { get; private set; }
        private bool _sidebarLayoutMember;
        private bool _sidebarHeroLayout;
        private bool _sidebarHeroLayoutConfigured;
        /// <summary>
        /// When true, the dark price pill is collapsed — it sat directly above the colourful power bar
        /// on the Orbit Menu "Your Ship" card and read as a useless black strip.
        /// </summary>
        private bool _sidebarHeroHidePrice;

        /// <summary>True when this node is the Orbit Menu left-panel "Your Ship" hero card.</summary>
        public bool UsesSidebarHeroLayout => _sidebarHeroLayout;
        public RectTransform Rect => transform as RectTransform;
        public float NodeButtonWidth { get; private set; }
        public float PowerBarTrackWidth { get; private set; }
        public bool UsesMoonHorizontalLayout => moonHorizontalLayout;
        public Button Button => button;
        public Button PriceButton => priceButton;
        public TextMeshProUGUI LevelNumberText => levelText;
        public TextMeshProUGUI ShipNameText => shipNameText;
        public TextMeshProUGUI PriceText => priceText;
        public Image PreviewImage => previewImage;
        public float LayoutWidth => layoutWidth;
        public float LayoutHeight => layoutHeight;

        private float _boundHeight;
        private float _appliedWidth = -1f;
        private float _appliedHeight = -1f;
        private float _lastWidthScale = 1f;
        private float _lastHeightScale = 1f;
        private bool _layoutCached;
        private VerticalLayoutGroup _rootVlg;
        private LayoutElement _contentRowLe;
        private HorizontalLayoutGroup _contentRowHlg;
        private VerticalLayoutGroup _contentRowVlg;
        private VerticalLayoutGroup _leftVlg;
        private LayoutElement _leftLe;
        private LayoutElement _leftMiddleLe;
        private VerticalLayoutGroup _leftMiddleVlg;
        private VerticalLayoutGroup _previewColVlg;
        private LayoutElement _levelLe;
        private LayoutElement _nameLe;
        private LayoutElement _familyLe;
        private LayoutElement _priceLe;
        private Image _priceButtonImage;
        private Image _priceButtonBorder;
        private bool _priceButtonEnsured;
        private LayoutElement _previewColLe;
        private LayoutElement _previewImgLe;
        private LayoutElement _powerBarLe;
        private RectTransform _powerBarTrack;
        private LayoutElement _powerBarTrackLe;
        private Image _powerBarTrackBg;
        private float _panelOverlayMargin = 8f;
        /// <summary>Store-style near-black tray that holds the colourful power bar.</summary>
        static readonly Color TreePowerBarTrackBg = new Color(0.02f, 0.03f, 0.05f, 0.94f);

        /// <summary>Runtime gold frame + top rail built on L7 MEGA cards (not in the family prefab).</summary>
        private RectTransform _megaChromeRoot;
        private Image _megaBorderN;
        private Image _megaBorderS;
        private Image _megaBorderE;
        private Image _megaBorderW;
        private TextMeshProUGUI _megaCaptionLabel;
        private Outline _megaOuterGlow;
        private bool _megaCardChromeActive;
        private bool _cachedRegularTextColors;
        private Color _cachedLevelColor = Color.white;
        private Color _cachedNameColor = Color.white;
        private Color _cachedFamilyColor = FamilyCaptionColor;
        /// <summary>Muted cyan so the family line is quieter than the hull name.</summary>
        static readonly Color FamilyCaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.88f);
        /// <summary>Warm gold on MEGA cards so the family line matches the bronze frame.</summary>
        static readonly Color MegaFamilyCaptionColor = new Color(0.86f, 0.76f, 0.52f, 0.88f);
        static readonly Color MegaFamilyOccupiedColor = new Color(0.52f, 0.52f, 0.54f, 0.85f);

        public void ConfigureLayout(bool useMoonHorizontal)
        {
            moonHorizontalLayout = useMoonHorizontal;
        }

        /// <summary>Binds a ladder slot (level + branch) with fixed pixel size for tree overlay layout.</summary>
        public void BindSlot(int level, int branchIndex, ShipUpgradeNode node, float width, float height, float powerTrackWidth)
        {
            // --- BindSlot ---
            IsCurrentShipDisplay = false;
            Level = level;
            BranchIndex = branchIndex;
            Node = node;
            NodeButtonWidth = width;
            PowerBarTrackWidth = powerTrackWidth;
            _boundHeight = height;
            ApplyFixedLayoutSize(width, height);
            // Family slots stay navy; MEGA slots get the bronze frame as soon as they exist.
            if (ShipFamilyPowerBarNorm.IsMegaTreeLevel(level))
                ApplyMegaShipCardStyle(false, false, false, false);
            else
                ClearMegaShipCardStyle();
        }

        public void BindAsCurrentShipDisplay(float width, float height, float powerTrackWidth)
        {
            // --- BindAsCurrentShipDisplay ---
            IsCurrentShipDisplay = true;
            _sidebarLayoutMember = false;
            Level = 0;
            BranchIndex = 0;
            Node = null;
            NodeButtonWidth = width;
            PowerBarTrackWidth = powerTrackWidth;
            _boundHeight = height;
            ApplyFixedLayoutSize(width, height);
        }

        /// <summary>
        /// Current-ship node in the Orbit Menu sidebar: large preview, hull name overlaid at the
        /// top of the art, power bar under the silhouette. Hides the "You"/level label and the dark price pill.
        /// </summary>
        public void ApplySidebarHeroPreviewLayout(float width, float height, float powerTrackWidth)
        {
            // --- Apply sidebar hero layout ---
            IsCurrentShipDisplay = true;
            _sidebarLayoutMember = true;
            _sidebarHeroLayout = true;
            _sidebarHeroHidePrice = true;
            _sidebarHeroLayoutConfigured = false;
            Level = 0;
            BranchIndex = 0;
            Node = null;
            NodeButtonWidth = width;
            PowerBarTrackWidth = powerTrackWidth;
            _boundHeight = height;
            ApplyFixedLayoutSize(width, height);
            // Size may be unchanged on rebuild — still force hero chrome (name on art, hide "You").
            EnsureSidebarHeroLayout();
            ApplyScaledChildLayout(width, height);
            ApplySidebarHeroChrome();
            if (Rect != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(Rect);
            // [UNITY] Layout rebuild can reset ignoreLayout rects — pin the caption again.
            EnsureHeroNameAboveArt();
        }

        /// <summary>Current-ship node stacked in <see cref="OrbitDockSidebarPanelUI"/> (uses layout group, not tree overlay).</summary>
        public void ApplySidebarPanelLayout(float width, float height, float powerTrackWidth)
        {
            // --- Apply sidebar panel layout ---
            IsCurrentShipDisplay = true;
            _sidebarLayoutMember = true;
            _sidebarHeroLayout = false;
            _sidebarHeroHidePrice = false;
            _sidebarHeroLayoutConfigured = false;
            Level = 0;
            BranchIndex = 0;
            Node = null;
            NodeButtonWidth = width;
            PowerBarTrackWidth = powerTrackWidth;
            _boundHeight = height;
            ApplyFixedLayoutSize(width, height);
        }

        /// <summary>
        /// Orbit Menu "Your Ship": hide the dark price chrome and route hull-swap clicks to the card root.
        /// The price pill was drawing a black bar immediately above the colourful stats.
        /// </summary>
        public void SetSidebarHeroCardClickHandler(UnityEngine.Events.UnityAction handler)
        {
            // --- Wire whole-card click; collapse price / "You" chrome ---
            _sidebarHeroHidePrice = true;
            ApplySidebarHeroChrome();

            if (button == null)
                return;

            button.transition = Selectable.Transition.None;
            button.interactable = handler != null;
            var rootGraphic = button.targetGraphic;
            if (rootGraphic != null)
                rootGraphic.raycastTarget = handler != null;

            button.onClick.RemoveAllListeners();
            if (handler != null)
                button.onClick.AddListener(handler);
        }

        /// <summary>
        /// Sidebar hero chrome: no "You"/level, no price pill, hull name pinned to the top of the ship art.
        /// </summary>
        private void ApplySidebarHeroChrome()
        {
            // --- Hide level + price; pin the hull name on the art ---
            if (!_sidebarHeroLayout)
                return;

            ApplySidebarHeroPriceHidden();
            EnsureHeroNameAboveArt();

            // Drop the "You" / Lv label — the hull name on the art is enough.
            if (levelText != null)
            {
                levelText.gameObject.SetActive(false);
                if (_levelLe == null)
                    _levelLe = levelText.GetComponent<LayoutElement>();
                if (_levelLe != null)
                {
                    _levelLe.ignoreLayout = true;
                    _levelLe.preferredHeight = 0f;
                    _levelLe.minHeight = 0f;
                }
            }

            if (shipNameText != null)
            {
                shipNameText.gameObject.SetActive(true);
                // [UNITY] Center so the hull name sits on the art, not left-ragged.
                shipNameText.alignment = TextAlignmentOptions.Center;
                shipNameText.fontStyle = FontStyles.Bold;
                shipNameText.enableWordWrapping = false;
                shipNameText.overflowMode = TextOverflowModes.Ellipsis;
                shipNameText.maxVisibleLines = 1;
                shipNameText.margin = new Vector4(8f, 1f, 8f, 1f);
            }

            // Hero card has no buy chip — hide the family line so it does not float on the art.
            HideFamilyNameLabel();

            if (_nameLe != null)
            {
                // Overlay uses ignoreLayout — a flow row used to shrink to 17px and RectMask2D-clip the glyphs.
                _nameLe.ignoreLayout = true;
                _nameLe.flexibleWidth = 0f;
                _nameLe.flexibleHeight = 0f;
            }
        }

        /// <summary>Collapses the price row so it cannot paint a dark strip above the power bar.</summary>
        private void ApplySidebarHeroPriceHidden()
        {
            // --- Hide price chrome on sidebar hero ---
            if (!_sidebarHeroHidePrice)
                return;

            EnsurePriceButton();
            if (_priceLe == null && priceButton != null)
                _priceLe = priceButton.GetComponent<LayoutElement>();

            if (priceButton != null)
            {
                priceButton.gameObject.SetActive(false);
                priceButton.interactable = false;
            }

            if (_priceLe != null)
            {
                _priceLe.ignoreLayout = true;
                _priceLe.preferredHeight = 0f;
                _priceLe.minHeight = 0f;
                _priceLe.flexibleHeight = 0f;
            }
        }

        /// <summary>Re-applies slot size after child layout or power-bar updates.</summary>
        public void EnforceLayoutSize(float width, float height, float powerTrackWidth)
        {
            // --- EnforceLayoutSize ---
            NodeButtonWidth = width;
            PowerBarTrackWidth = powerTrackWidth;
            _boundHeight = height;
            ApplyFixedLayoutSize(width, height);
        }

        /// <summary>Moon panel overlay: top-left of <see cref="ShipUpgradeTreeUI"/> center row (does not consume tree width).</summary>
        public void ApplyPanelOverlayTopLeft(float margin)
        {
            // --- Apply changes ---
            _panelOverlayMargin = margin;
            if (Rect == null)
                return;

            Rect.anchorMin = new Vector2(0f, 1f);
            Rect.anchorMax = new Vector2(0f, 1f);
            Rect.pivot = new Vector2(0f, 1f);
            Rect.anchoredPosition = new Vector2(margin, -margin);
        }

        /// <summary>
        /// Prefab assets are not in a scene. Unity blocks <c>SetParent</c> on them so
        /// <c>Resources.Load</c> cannot corrupt the asset. Scene instances and
        /// Prefab Mode previews have a valid scene and may rebuild hierarchy.
        /// </summary>
        bool CanRewriteHierarchy()
        {
            return gameObject != null && gameObject.scene.IsValid();
        }

        private void ApplyFixedLayoutSize(float width, float height)
        {
            // --- Apply changes ---
            if (Rect == null || !CanRewriteHierarchy())
                return;

            bool sizeChanged = Mathf.Abs(_appliedWidth - width) > 0.5f || Mathf.Abs(_appliedHeight - height) > 0.5f;

            if (IsCurrentShipDisplay && _sidebarLayoutMember)
            {
                Rect.anchorMin = new Vector2(0f, 1f);
                Rect.anchorMax = new Vector2(1f, 1f);
                Rect.pivot = new Vector2(0.5f, 1f);
                Rect.anchoredPosition = Vector2.zero;
                Rect.sizeDelta = new Vector2(0f, height);
            }
            else if (IsCurrentShipDisplay)
                ApplyPanelOverlayTopLeft(_panelOverlayMargin);
            else
            {
                Rect.anchorMin = new Vector2(0.5f, 0.5f);
                Rect.anchorMax = new Vector2(0.5f, 0.5f);
                Rect.pivot = new Vector2(0.5f, 0.5f);
                Rect.sizeDelta = new Vector2(width, height);
            }

            if (!IsCurrentShipDisplay || !_sidebarLayoutMember)
                Rect.sizeDelta = new Vector2(width, height);

            var le = Rect.GetComponent<LayoutElement>();
            if (le == null)
                le = Rect.gameObject.AddComponent<LayoutElement>();
            if (_sidebarLayoutMember)
            {
                le.ignoreLayout = false;
                le.flexibleWidth = 1f;
                le.flexibleHeight = 0f;
                le.minWidth = width;
                le.preferredWidth = width;
                le.minHeight = height;
                le.preferredHeight = height;
            }
            else
            {
                le.ignoreLayout = true;
                le.flexibleWidth = 0f;
                le.flexibleHeight = 0f;
                le.minWidth = width;
                le.preferredWidth = width;
                le.minHeight = height;
                le.preferredHeight = height;
            }

            _appliedWidth = width;
            _appliedHeight = height;

            if (sizeChanged)
            {
                if (_sidebarHeroLayout)
                    EnsureSidebarHeroLayout();
                ApplyScaledChildLayout(width, height);
                LayoutRebuilder.ForceRebuildLayoutImmediate(Rect);
            }
        }

        /// <summary>
        /// Turns the tree-card ContentRow (name | art) into a vertical stack for Your Ship:
        /// silhouette fills the body, hull name overlays the top of that art.
        /// </summary>
        private void EnsureSidebarHeroLayout()
        {
            // --- Ensure preview stack + name overlay ---
            if (!_sidebarHeroLayout)
                return;

            if (_sidebarHeroLayoutConfigured)
            {
                EnsureHeroNameAboveArt();
                return;
            }

            Transform contentRow = transform.Find("ContentRow");
            if (contentRow == null)
                return;

            var hlg = contentRow.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
            {
                _contentRowHlg = null;
                DestroyImmediate(hlg);
            }

            var vlg = contentRow.GetComponent<VerticalLayoutGroup>();
            if (vlg == null)
                vlg = contentRow.gameObject.AddComponent<VerticalLayoutGroup>();
            if (vlg == null)
                return;

            _contentRowVlg = vlg;
            _layoutCached = false;
            vlg.spacing = 1f;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            Transform previewCol = contentRow.Find("PreviewColumn");
            Transform leftCol = contentRow.Find("LeftColumn");
            if (leftCol != null)
                leftCol.gameObject.SetActive(false);
            if (previewCol != null)
                previewCol.SetAsLastSibling();

            EnsureHeroNameAboveArt();
            _sidebarHeroLayoutConfigured = true;
        }

        /// <summary>
        /// Pins the hull name to the top of the ship silhouette on Your Ship.
        /// A separate flow row used to steal height from the art, then RectMask2D clipped
        /// the 16px font inside a 17px box so the title vanished.
        /// </summary>
        void EnsureHeroNameAboveArt()
        {
            if (shipNameText == null)
                return;
            Transform contentRow = transform.Find("ContentRow");
            if (contentRow == null)
                return;

            Transform leftCol = contentRow.Find("LeftColumn");
            if (leftCol != null)
                leftCol.gameObject.SetActive(false);

            // [TITAN-ORBIT] Overlay root: hull name only — no caption plate over the silhouette.
            // ignoreLayout so the VerticalLayoutGroup still gives the art the full card body.
            Transform overlay = contentRow.Find("HeroNameOverlay");
            if (overlay == null)
            {
                var overlayGo = new GameObject("HeroNameOverlay", typeof(RectTransform));
                overlayGo.transform.SetParent(contentRow, false);
                overlay = overlayGo.transform;
                var overlayLe = overlayGo.AddComponent<LayoutElement>();
                overlayLe.ignoreLayout = true;
            }

            // Drop any leftover navy scrim from a previous session (overlay is created at runtime).
            var leftoverScrim = overlay.GetComponent<Image>();
            if (leftoverScrim != null)
                leftoverScrim.enabled = false;

            var overlayRt = overlay as RectTransform;
            overlayRt.anchorMin = new Vector2(0f, 1f);
            overlayRt.anchorMax = new Vector2(1f, 1f);
            overlayRt.pivot = new Vector2(0.5f, 1f);
            overlayRt.anchoredPosition = Vector2.zero;
            overlayRt.sizeDelta = new Vector2(0f, HeroNameOverlayHeight);
            overlay.SetAsLastSibling();

            if (shipNameText.transform.parent != overlay)
                shipNameText.transform.SetParent(overlay, false);
            StretchRectToFill(shipNameText.rectTransform);
            shipNameText.gameObject.SetActive(true);

            // [UNITY] Prefab ShipName carries RectMask2D. Overlay height is 28px; the mask
            // still clips TMP padding if the rect is stale, so turn it off on the hero.
            var nameMask = shipNameText.GetComponent<RectMask2D>();
            if (nameMask != null)
                nameMask.enabled = false;

            if (_nameLe == null)
                _nameLe = shipNameText.GetComponent<LayoutElement>();
            if (_nameLe == null)
                _nameLe = shipNameText.gameObject.AddComponent<LayoutElement>();
            _nameLe.ignoreLayout = true;
            _nameLe.flexibleWidth = 0f;
            _nameLe.flexibleHeight = 0f;
        }

        /// <summary>
        /// Tree card body: Level on top of the left column, name + buy chip centered
        /// in the remaining left space. The caption overlay is not used — it stole
        /// height from the silhouette.
        /// </summary>
        void EnsureTreeCardBodyLayout()
        {
            if (_sidebarHeroLayout)
                return;

            Transform contentRow = transform.Find("ContentRow");
            if (contentRow == null)
                return;
            Transform leftCol = contentRow.Find("LeftColumn");
            if (leftCol == null)
                return;

            leftCol.gameObject.SetActive(true);
            RestoreInFlowLevelLabel();

            Transform middle = leftCol.Find("LeftMiddle");
            if (middle == null)
            {
                var go = new GameObject("LeftMiddle", typeof(RectTransform));
                go.transform.SetParent(leftCol, false);
                middle = go.transform;
                _leftMiddleVlg = go.AddComponent<VerticalLayoutGroup>();
                _leftMiddleVlg.spacing = 2f;
                _leftMiddleVlg.childAlignment = TextAnchor.MiddleLeft;
                _leftMiddleVlg.childControlWidth = true;
                _leftMiddleVlg.childControlHeight = true;
                _leftMiddleVlg.childForceExpandWidth = true;
                _leftMiddleVlg.childForceExpandHeight = false;
                _leftMiddleLe = go.AddComponent<LayoutElement>();
                _leftMiddleLe.flexibleHeight = 1f;
                _leftMiddleLe.flexibleWidth = 1f;
                _leftMiddleLe.minHeight = 0f;
            }
            else
            {
                if (_leftMiddleVlg == null)
                    _leftMiddleVlg = middle.GetComponent<VerticalLayoutGroup>();
                if (_leftMiddleLe == null)
                    _leftMiddleLe = middle.GetComponent<LayoutElement>();
            }

            if (shipNameText != null && shipNameText.transform.parent != middle)
                shipNameText.transform.SetParent(middle, false);

            // Family sits under the hull name and just above the buy chip.
            EnsureFamilyNameLabel();
            if (familyNameText != null && familyNameText.transform.parent != middle)
                familyNameText.transform.SetParent(middle, false);

            Transform priceRoot = ResolvePriceRootTransform();
            if (priceRoot != null && priceRoot.parent != middle)
                priceRoot.SetParent(middle, false);

            if (levelText != null)
                levelText.transform.SetAsFirstSibling();
            if (shipNameText != null)
                shipNameText.transform.SetAsFirstSibling();
            if (familyNameText != null)
                familyNameText.transform.SetSiblingIndex(shipNameText != null ? 1 : 0);
            if (priceRoot != null)
                priceRoot.SetAsLastSibling();
            middle.SetAsLastSibling();
        }

        /// <summary>
        /// Builds or finds the family-name line under the hull name. Older ShipUpgradeTreeNode
        /// prefabs have no FamilyName child, so runtime cards create one in LeftMiddle.
        /// Sidebar hero hides this — that card overlays the hull name on the silhouette
        /// and has no price button for the family line to sit above.
        /// </summary>
        void EnsureFamilyNameLabel()
        {
            if (_sidebarHeroLayout)
            {
                HideFamilyNameLabel();
                return;
            }

            Transform middle = transform.Find("ContentRow/LeftColumn/LeftMiddle");
            if (middle == null)
                return;

            if (familyNameText == null)
            {
                Transform existing = middle.Find("FamilyName");
                if (existing != null)
                    familyNameText = existing.GetComponent<TextMeshProUGUI>();
            }

            if (familyNameText == null)
            {
                // [UNITY] Runtime widget — prefab assets cannot SetParent, but scene instances can.
                var go = new GameObject("FamilyName", typeof(RectTransform));
                go.transform.SetParent(middle, false);
                familyNameText = go.AddComponent<TextMeshProUGUI>();
                familyNameText.raycastTarget = false;
                familyNameText.fontStyle = FontStyles.Normal;
                familyNameText.color = FamilyCaptionColor;
                familyNameText.alignment = TextAlignmentOptions.Left;
                familyNameText.enableWordWrapping = false;
                familyNameText.overflowMode = TextOverflowModes.Overflow;
                familyNameText.maxVisibleLines = 1;
                if (shipNameText != null && shipNameText.font != null)
                    familyNameText.font = shipNameText.font;
                else if (TMP_Settings.defaultFontAsset != null)
                    familyNameText.font = TMP_Settings.defaultFontAsset;
            }

            if (_familyLe == null)
                _familyLe = familyNameText.GetComponent<LayoutElement>();
            if (_familyLe == null)
                _familyLe = familyNameText.gameObject.AddComponent<LayoutElement>();
            _familyLe.ignoreLayout = false;
            _familyLe.flexibleHeight = 0f;
            _familyLe.flexibleWidth = 1f;
        }

        /// <summary>Collapses the family row so hero / empty slots do not keep a leftover caption.</summary>
        void HideFamilyNameLabel()
        {
            if (familyNameText != null)
                familyNameText.gameObject.SetActive(false);
            if (_familyLe == null && familyNameText != null)
                _familyLe = familyNameText.GetComponent<LayoutElement>();
            if (_familyLe != null)
            {
                _familyLe.ignoreLayout = true;
                _familyLe.preferredHeight = 0f;
                _familyLe.minHeight = 0f;
            }
        }

        /// <summary>Puts Lv N / MEGA SHIP back in the left-column stack (not a card overlay).</summary>
        void RestoreInFlowLevelLabel()
        {
            if (levelText == null)
                return;
            if (_levelLe == null)
                _levelLe = levelText.GetComponent<LayoutElement>();
            if (_levelLe == null)
                _levelLe = levelText.gameObject.AddComponent<LayoutElement>();
            _levelLe.ignoreLayout = false;
            _levelLe.flexibleHeight = 0f;
            levelText.gameObject.SetActive(true);
            Transform leftCol = transform.Find("ContentRow/LeftColumn");
            if (leftCol != null && levelText.transform.parent != leftCol)
                levelText.transform.SetParent(leftCol, false);
            levelText.transform.SetAsFirstSibling();
        }

        private void EnsureLayoutCached()
        {
            // --- Ensure setup ---
            if (_layoutCached)
                return;

            _layoutCached = true;
            _rootVlg = GetComponent<VerticalLayoutGroup>();

            var contentRow = transform.Find("ContentRow");
            if (contentRow != null)
            {
                _contentRowLe = contentRow.GetComponent<LayoutElement>();
                _contentRowHlg = contentRow.GetComponent<HorizontalLayoutGroup>();
                _contentRowVlg = contentRow.GetComponent<VerticalLayoutGroup>();
            }

            var leftCol = contentRow != null ? contentRow.Find("LeftColumn") : null;
            if (leftCol != null)
            {
                _leftVlg = leftCol.GetComponent<VerticalLayoutGroup>();
                _leftLe = leftCol.GetComponent<LayoutElement>();
            }

            if (levelText != null)
                _levelLe = levelText.GetComponent<LayoutElement>();
            if (shipNameText != null)
                _nameLe = shipNameText.GetComponent<LayoutElement>();
            if (familyNameText != null)
                _familyLe = familyNameText.GetComponent<LayoutElement>();
            EnsurePriceButton();
            if (priceButton != null)
                _priceLe = priceButton.GetComponent<LayoutElement>();
            else if (priceText != null)
                _priceLe = priceText.GetComponent<LayoutElement>();

            if (previewImage != null)
            {
                _previewImgLe = previewImage.GetComponent<LayoutElement>();
                var previewCol = previewImage.transform.parent;
                if (previewCol != null)
                {
                    _previewColLe = previewCol.GetComponent<LayoutElement>();
                    _previewColVlg = previewCol.GetComponent<VerticalLayoutGroup>();
                }
            }

            if (powerBar != null)
                _powerBarLe = powerBar.GetComponent<LayoutElement>();
            EnsurePowerBarTrack();
        }

        /// <summary>
        /// Wraps the colourful bar in a dark tray like the moon-dock store cards.
        /// The tray is the VLG child so the segments cannot paint past the card.
        /// </summary>
        void EnsurePowerBarTrack()
        {
            if (powerBar == null)
                return;

            if (_powerBarTrack != null)
            {
                ApplyPowerBarTrackPadding();
                return;
            }

            Transform currentParent = powerBar.transform.parent;
            if (currentParent != null && currentParent.name == "PowerBarTrack")
            {
                _powerBarTrack = currentParent as RectTransform;
                _powerBarTrackLe = currentParent.GetComponent<LayoutElement>();
                _powerBarTrackBg = currentParent.GetComponent<Image>();
                return;
            }

            int sibling = powerBar.transform.GetSiblingIndex();
            var trackGo = new GameObject("PowerBarTrack", typeof(RectTransform));
            trackGo.transform.SetParent(currentParent != null ? currentParent : transform, false);
            trackGo.transform.SetSiblingIndex(sibling);
            powerBar.transform.SetParent(trackGo.transform, false);

            _powerBarTrack = trackGo.GetComponent<RectTransform>();
            _powerBarTrack.SetAsLastSibling();

            _powerBarTrackBg = trackGo.AddComponent<Image>();
            _powerBarTrackBg.color = TreePowerBarTrackBg;
            _powerBarTrackBg.raycastTarget = false;

            var trackVlg = trackGo.AddComponent<VerticalLayoutGroup>();
            trackVlg.spacing = 0f;
            trackVlg.childAlignment = TextAnchor.MiddleCenter;
            trackVlg.childControlWidth = true;
            trackVlg.childControlHeight = true;
            trackVlg.childForceExpandWidth = true;
            trackVlg.childForceExpandHeight = false;

            _powerBarTrackLe = trackGo.AddComponent<LayoutElement>();
            _powerBarTrackLe.flexibleHeight = 0f;
            _powerBarTrackLe.flexibleWidth = 1f;
            ApplyPowerBarTrackPadding();
        }

        /// <summary>Extra left/right inset so the colourful lanes do not kiss the tray edge.</summary>
        void ApplyPowerBarTrackPadding()
        {
            if (_powerBarTrack == null)
                return;
            var trackVlg = _powerBarTrack.GetComponent<VerticalLayoutGroup>();
            if (trackVlg == null)
                return;
            int padX = Mathf.RoundToInt(RefLayout.PowerBarTrackPadX);
            int padY = Mathf.RoundToInt(RefLayout.PowerBarTrackPadY);
            trackVlg.padding = new RectOffset(padX, padX, padY, padY);
        }

        /// <summary>
        /// Scales child LayoutElements from the prefab reference size to the bound slot size.
        /// Sidebar hero layout uses a fixed chrome budget so the power bar stays inside the card
        /// (above Bank / Auto-deposit) instead of overflowing when height-scale is large.
        /// </summary>
        private void ApplyScaledChildLayout(float width, float height)
        {
            // --- Scale child layout to bound size ---
            if (layoutWidth < 1f || layoutHeight < 1f)
                return;

            EnsureLayoutCached();
            if (_sidebarHeroLayout)
                EnsureSidebarHeroLayout();
            else
                EnsureTreeCardBodyLayout();

            // Uniform scale from the 120×100 prefab reference. Tree nodes use this fully;
            // sidebar hero clamps text/chrome so the stats bar still fits.
            float wScale = width / layoutWidth;
            float hScale = height / layoutHeight;
            float fontScale = Mathf.Min(wScale, hScale);

            // [TITAN-ORBIT] Sidebar hero ("Your Ship"): give the silhouette the full body height.
            // The hull name is a ignoreLayout overlay on top of the art (not a flow row).
            // Inflating ContentRow past its children left a dark empty strip above the stats.
            float heroPadTop = 2f;
            float heroPadBottom = 4f;
            float heroRootSpacing = 4f; // Tight gap between ship art and the colour stats bar.
            float heroBarH = 12f;
            float heroPreviewH = 0f;
            float heroContentH = 0f;
            // One pad on every side so left/right of the power bar and preview match.
            float padScale = Mathf.Min(wScale, hScale);
            int cardPad = ScalePxInt(RefLayout.CardPad, padScale);
            bool megaTree = !_sidebarHeroLayout
                && !IsCurrentShipDisplay
                && ShipFamilyPowerBarNorm.IsMegaTreeLevel(Level);
            float trackPadX = RefLayout.PowerBarTrackPadX;
            float trackPadY = RefLayout.PowerBarTrackPadY;
            if (_sidebarHeroLayout)
            {
                float heroTrackH = heroBarH + trackPadY * 2f;
                float chrome = heroPadTop + heroPadBottom + heroRootSpacing + heroTrackH;
                float available = Mathf.Max(72f, height - chrome);
                // Name is overlaid on the art — do not reserve a flow row (that row was clipping the title).
                heroPreviewH = Mathf.Max(96f, available);
                heroContentH = heroPreviewH;
            }

            if (_rootVlg != null)
            {
                if (_sidebarHeroLayout)
                {
                    _rootVlg.padding = new RectOffset(
                        cardPad, cardPad,
                        Mathf.RoundToInt(heroPadTop),
                        Mathf.RoundToInt(heroPadBottom));
                    _rootVlg.spacing = heroRootSpacing;
                }
                else
                {
                    // Equal card inset. Top = text + art; bottom = power-bar tray.
                    _rootVlg.padding = new RectOffset(cardPad, cardPad, cardPad, cardPad);
                    _rootVlg.spacing = 3f;
                }
            }

            if (_contentRowLe != null)
            {
                if (_sidebarHeroLayout)
                {
                    // Tight fit — do not stretch; leftover height would read as a black bar above stats.
                    _contentRowLe.minHeight = heroContentH;
                    _contentRowLe.preferredHeight = heroContentH;
                    _contentRowLe.flexibleHeight = 0f;
                }
                else
                {
                    _contentRowLe.minHeight = ScalePx(RefLayout.ContentMinHeight, hScale);
                    _contentRowLe.flexibleHeight = 1f;
                }
            }

            if (_sidebarHeroLayout && _contentRowVlg != null)
                _contentRowVlg.spacing = 1f;
            else if (_contentRowHlg != null)
            {
                _contentRowHlg.spacing = cardPad;
                _contentRowHlg.childAlignment = TextAnchor.MiddleLeft;
                _contentRowHlg.childForceExpandWidth = false;
                _contentRowHlg.childForceExpandHeight = true;
            }

            // Hero labels stay readable without eating the power-bar row (cap scale ~1.35×).
            float heroFontScale = _sidebarHeroLayout ? Mathf.Min(fontScale, 1.35f) : fontScale;
            float heroHScale = _sidebarHeroLayout ? Mathf.Min(hScale, 1.35f) : hScale;

            if (_leftVlg != null)
            {
                _leftVlg.spacing = _sidebarHeroLayout ? 0f : RefLayout.LeftSpacing * heroHScale;
                if (_sidebarHeroLayout)
                    _leftVlg.childAlignment = TextAnchor.UpperCenter;
                else
                {
                    _leftVlg.childAlignment = TextAnchor.UpperLeft;
                    _leftVlg.childForceExpandHeight = false;
                    _leftVlg.childControlHeight = true;
                }
            }
            if (_leftLe != null && !_sidebarHeroLayout)
            {
                _leftLe.minWidth = ScalePx(RefLayout.LeftMinWidth, wScale);
                _leftLe.preferredWidth = ScalePx(72f, wScale);
                _leftLe.flexibleWidth = 1f;
                _leftLe.flexibleHeight = 1f;
            }

            if (_leftMiddleLe != null && !_sidebarHeroLayout)
            {
                _leftMiddleLe.flexibleHeight = 1f;
                _leftMiddleLe.flexibleWidth = 1f;
                _leftMiddleLe.minHeight = 0f;
            }
            if (_leftMiddleVlg != null && !_sidebarHeroLayout)
                _leftMiddleVlg.childAlignment = TextAnchor.MiddleLeft;

            if (_sidebarHeroLayout)
            {
                // Overlay caption — scale the font only. LayoutElement stays ignoreLayout.
                ApplyTextScale(shipNameText, null, RefLayout.NameFontSize + 2f, RefLayout.NameHeight, heroFontScale, heroHScale);
                if (shipNameText != null)
                    shipNameText.fontSize = Mathf.Max(13f, shipNameText.fontSize);
                ApplySidebarHeroChrome();
            }
            else
            {
                RestoreInFlowLevelLabel();
                float levelFont = megaTree
                    ? RefLayout.NameFontSize + RefLayout.MegaCaptionFontExtra
                    : RefLayout.LevelFontSize;
                ApplyTextScale(levelText, _levelLe, levelFont, RefLayout.LevelHeight, heroFontScale, heroHScale);
                if (levelText != null)
                {
                    levelText.fontStyle = FontStyles.Bold;
                    levelText.alignment = TextAlignmentOptions.Left;
                    levelText.enableWordWrapping = false;
                    levelText.overflowMode = TextOverflowModes.Overflow;
                    levelText.maxVisibleLines = 1;
                    if (megaTree)
                        levelText.color = MegaCaptionGold;
                }
                if (_levelLe != null)
                {
                    _levelLe.ignoreLayout = false;
                    _levelLe.flexibleHeight = 0f;
                }

                ApplyTextScale(shipNameText, _nameLe, RefLayout.NameFontSize, RefLayout.NameHeight, heroFontScale, heroHScale);
                ApplyTreeTitleTextSettings();
                if (_nameLe != null)
                    _nameLe.minHeight = ScalePx(RefLayout.NameMinHeight, heroHScale);

                EnsureFamilyNameLabel();
                ApplyTextScale(familyNameText, _familyLe, RefLayout.FamilyFontSize, RefLayout.FamilyHeight, heroFontScale, heroHScale);
                ApplyTreeFamilyTextSettings();
                if (_familyLe != null)
                    _familyLe.minHeight = ScalePx(RefLayout.FamilyMinHeight, heroHScale);

                ApplyTextScale(priceText, _priceLe, RefLayout.PriceFontSize, RefLayout.PriceHeight, heroFontScale, heroHScale);
                if (_priceLe != null)
                    _priceLe.minWidth = ScalePx(RefLayout.PriceMinWidth, wScale);

                if (_megaCaptionLabel != null)
                    _megaCaptionLabel.gameObject.SetActive(false);
            }

            float innerBodyW = Mathf.Max(48f, width - cardPad * 2f);
            float barHEarly = _sidebarHeroLayout ? heroBarH : RefLayout.PowerBarHeight;
            float trackHEarly = barHEarly + trackPadY * 2f;
            float treeContentH = 0f;
            if (!_sidebarHeroLayout)
            {
                float spacing = _rootVlg != null ? _rootVlg.spacing : RefLayout.RootSpacing;
                treeContentH = Mathf.Max(36f, height - cardPad * 2f - spacing - trackHEarly);
            }

            float previewColW;
            float previewW;
            float previewH;
            float previewMinH;
            if (_sidebarHeroLayout)
            {
                previewColW = innerBodyW;
                previewW = previewColW;
                previewH = heroPreviewH;
                previewMinH = previewH * 0.85f;
            }
            else
            {
                // Right column matches the top-section height so the silhouette fills it
                // vertically and stays on the right. Left keeps only enough for Lv + chip.
                float leftReserve = Mathf.Max(48f, ScalePx(52f, wScale));
                previewColW = Mathf.Clamp(treeContentH, ScalePx(48f, hScale), innerBodyW - leftReserve);
                previewW = previewColW;
                previewH = treeContentH;
                previewMinH = previewH;
            }

            if (_previewColLe != null)
            {
                _previewColLe.preferredWidth = previewColW;
                _previewColLe.minWidth = previewColW;
                _previewColLe.flexibleWidth = _sidebarHeroLayout ? 1f : 0f;
                if (_sidebarHeroLayout)
                {
                    _previewColLe.preferredHeight = previewH;
                    _previewColLe.minHeight = previewMinH;
                    _previewColLe.flexibleHeight = 0f;
                }
                else
                {
                    _previewColLe.flexibleHeight = 1f;
                    _previewColLe.preferredHeight = previewH;
                    _previewColLe.minHeight = previewMinH;
                }
            }
            if (_previewColVlg != null && !_sidebarHeroLayout)
            {
                // Image fills the column height and sits on the right edge of the card.
                _previewColVlg.childAlignment = TextAnchor.MiddleRight;
                _previewColVlg.childControlWidth = true;
                _previewColVlg.childControlHeight = true;
                _previewColVlg.childForceExpandWidth = true;
                _previewColVlg.childForceExpandHeight = true;
                _previewColVlg.padding = new RectOffset(0, 0, 0, 0);
            }
            if (_previewImgLe != null)
            {
                _previewImgLe.preferredWidth = previewW;
                _previewImgLe.preferredHeight = previewH;
                _previewImgLe.minWidth = previewW;
                _previewImgLe.minHeight = previewMinH;
                _previewImgLe.flexibleWidth = 1f;
                _previewImgLe.flexibleHeight = 1f;
            }

            if (previewImage != null)
            {
                previewImage.preserveAspect = true;
                var previewRt = previewImage.transform as RectTransform;
                if (previewRt != null && _sidebarHeroLayout)
                {
                    previewRt.anchorMin = Vector2.zero;
                    previewRt.anchorMax = Vector2.one;
                    previewRt.pivot = new Vector2(0.5f, 0.5f);
                    previewRt.offsetMin = Vector2.zero;
                    previewRt.offsetMax = Vector2.zero;
                }
            }

            float barH = _sidebarHeroLayout ? heroBarH : RefLayout.PowerBarHeight;
            float trackH = barH + trackPadY * 2f;
            float innerW = Mathf.Max(RefLayout.PowerBarMinWidth, width - cardPad * 2f);
            ApplyPowerBarTrackPadding();

            if (!_sidebarHeroLayout)
            {
                // Colourful lanes sit inside the tray inset; tray sits inside card pad.
                PowerBarTrackWidth = Mathf.Max(RefLayout.PowerBarMinWidth, innerW - trackPadX * 2f);

                if (_contentRowLe != null && treeContentH > 0f)
                {
                    _contentRowLe.minHeight = treeContentH;
                    _contentRowLe.preferredHeight = treeContentH;
                    _contentRowLe.flexibleHeight = 0f;
                }
            }
            else
            {
                PowerBarTrackWidth = Mathf.Max(RefLayout.PowerBarMinWidth, innerW - trackPadX * 2f);
            }

            if (_powerBarTrackLe != null)
            {
                _powerBarTrackLe.preferredHeight = trackH;
                _powerBarTrackLe.minHeight = trackH;
                _powerBarTrackLe.preferredWidth = innerW;
                _powerBarTrackLe.minWidth = innerW;
                _powerBarTrackLe.flexibleWidth = 1f;
                _powerBarTrackLe.flexibleHeight = 0f;
            }

            if (_powerBarLe != null)
            {
                _powerBarLe.preferredHeight = barH;
                _powerBarLe.minHeight = barH;
                _powerBarLe.minWidth = 0f;
                _powerBarLe.preferredWidth = -1f;
                _powerBarLe.flexibleWidth = 1f;
                _powerBarLe.flexibleHeight = 0f;
            }

            // Tree cards keep the authored bar thickness. Mega 1.5× height used to inflate
            // the segments until they spilled past the card.
            float barHeightScale = _sidebarHeroLayout ? (heroBarH / RefLayout.PowerBarHeight) : 1f;
            if (powerBar != null)
                powerBar.ConfigureLayoutScale(1f, barHeightScale);

            _lastWidthScale = wScale;
            // [TITAN-ORBIT] Store the scale used for the power bar so Refresh/ApplyBreakdown does not re-inflate it.
            _lastHeightScale = barHeightScale;
        }

        private static void ApplyTextScale(
            TextMeshProUGUI text,
            LayoutElement le,
            float refFontSize,
            float refHeight,
            float fontScale,
            float heightScale)
        {
            if (text != null)
                text.fontSize = Mathf.Max(6f, refFontSize * fontScale);

            if (le != null)
            {
                float h = ScalePx(refHeight, heightScale);
                le.preferredHeight = h;
                le.minHeight = h;
            }
        }

        private static float ScalePx(float reference, float scale) =>
            Mathf.Max(1f, Mathf.Round(reference * scale));

        private static int ScalePxInt(float reference, float scale) =>
            Mathf.Max(1, Mathf.RoundToInt(reference * scale));

#if UNITY_EDITOR
        private void OnValidate()
        {
            // --- OnValidate ---
            // Resources.Load fires this on the prefab *asset*. Do not reparent there.
            if (!CanRewriteHierarchy())
                return;
            if (NodeButtonWidth > 0.01f)
                return;
            if (Rect == null || layoutWidth < 1f || layoutHeight < 1f)
                return;
            _layoutCached = false;
            ApplyFixedLayoutSize(layoutWidth, layoutHeight);
        }
#endif

        /// <summary>
        /// Feeds power breakdown into the child bar. <paramref name="globalMaxes"/> is the
        /// pool for this hull: regular-family maxes on L1–L6 nodes, MEGA catalog maxes on L7.
        /// Hover tips use the same pool for RANK 1.
        /// </summary>
        public void ApplyPowerBreakdown(ShipFamilyPowerScoreBreakdown breakdown, in ShipPowerBarStatMaxes globalMaxes)
        {
            // --- Apply changes ---
            if (powerBar == null)
                return;

            if (_appliedWidth < 0.5f && NodeButtonWidth > 0.01f)
                ApplyFixedLayoutSize(NodeButtonWidth, _boundHeight > 0.01f ? _boundHeight : layoutHeight);
            else
                powerBar.ConfigureLayoutScale(_lastWidthScale, _lastHeightScale);

            float track = PowerBarTrackWidth > 0.01f ? PowerBarTrackWidth : NodeButtonWidth;
            // MEGA cards use the MEGA catalog maxes — RANK 1 must come from that same pool.
            bool megaPool = ShipFamilyPowerBarNorm.UsesMegaPowerBarPool(Level);
            powerBar.ApplyBreakdown(breakdown, in globalMaxes, track, megaPool);
            if (_powerBarLe != null)
            {
                _powerBarLe.minWidth = 0f;
                _powerBarLe.preferredWidth = -1f;
                _powerBarLe.flexibleWidth = 1f;
            }
            if (_powerBarTrack != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_powerBarTrack);
        }

        private static readonly Color PriceEnabledFill = new Color(0.14f, 0.46f, 0.24f, 1f);
        private static readonly Color PriceEnabledBorder = new Color(0.38f, 0.88f, 0.48f, 1f);
        private static readonly Color PriceEnabledText = new Color(0.96f, 1f, 0.97f, 1f);
        private static readonly Color PriceDisabledFill = new Color(0.1f, 0.11f, 0.13f, 0.95f);
        private static readonly Color PriceDisabledBorder = new Color(0.24f, 0.26f, 0.3f, 0.9f);
        private static readonly Color PriceDisabledText = new Color(0.46f, 0.5f, 0.54f, 1f);
        /// <summary>Claimed unique MEGA — gold name on dark glass so the owner is readable (not a buy button).</summary>
        private static readonly Color PriceOwnedFill = new Color(0.08f, 0.07f, 0.04f, 0.96f);
        private static readonly Color PriceOwnedBorder = new Color(0.78f, 0.62f, 0.28f, 0.95f);
        private static readonly Color PriceOwnedText = new Color(0.96f, 0.86f, 0.55f, 1f);
        private const float PriceBorderInset = 1f;

        /// <summary>L7 HUD caption. Regular slots keep "Lv N". Must not be ellipsed.</summary>
        public const string MegaShipLevelCaption = "MEGA SHIP";

        // --- MEGA card palette (warm bronze void vs cool navy family cards) ---
        // [TITAN-ORBIT] Thin gold rails, not a full-panel gold flood — same rule as tooltip chrome.
        private static readonly Color MegaFillIdle = new Color(0.055f, 0.038f, 0.018f, 0.98f);
        private static readonly Color MegaFillCurrent = new Color(0.14f, 0.10f, 0.035f, 0.98f);
        private static readonly Color MegaFillReady = new Color(0.11f, 0.08f, 0.03f, 0.98f);
        private static readonly Color MegaFillBlocked = new Color(0.04f, 0.03f, 0.02f, 0.96f);
        /// <summary>Claimed unique MEGA — slate glass so the card reads disabled, not bronze.</summary>
        private static readonly Color MegaFillOccupied = new Color(0.18f, 0.19f, 0.22f, 0.96f);
        private static readonly Color MegaBorderIdle = new Color(0.72f, 0.54f, 0.20f, 0.92f);
        private static readonly Color MegaBorderCurrent = new Color(0.96f, 0.82f, 0.36f, 0.98f);
        private static readonly Color MegaBorderReady = new Color(0.88f, 0.70f, 0.26f, 0.96f);
        private static readonly Color MegaBorderBlocked = new Color(0.46f, 0.36f, 0.16f, 0.72f);
        private static readonly Color MegaBorderOccupied = new Color(0.40f, 0.42f, 0.46f, 0.85f);
        private static readonly Color MegaCaptionOccupied = new Color(0.58f, 0.58f, 0.60f, 1f);
        private static readonly Color MegaNameOccupied = new Color(0.55f, 0.56f, 0.58f, 1f);
        private static readonly Color MegaCaptionGold = new Color(0.96f, 0.86f, 0.52f, 1f);
        private static readonly Color MegaNameWarm = new Color(0.96f, 0.90f, 0.72f, 1f);
        private static readonly Color MegaGlow = new Color(0.95f, 0.74f, 0.22f, 0.42f);
        private static readonly Color MegaPriceReadyFill = new Color(0.32f, 0.22f, 0.07f, 1f);
        private static readonly Color MegaPriceReadyBorder = new Color(0.90f, 0.74f, 0.30f, 1f);
        private static readonly Color MegaPriceReadyText = new Color(0.99f, 0.94f, 0.72f, 1f);
        private static readonly Color MegaPriceIdleFill = new Color(0.12f, 0.08f, 0.04f, 0.96f);
        private static readonly Color MegaPriceIdleBorder = new Color(0.55f, 0.42f, 0.18f, 0.88f);
        private static readonly Color MegaPriceIdleText = new Color(0.72f, 0.62f, 0.40f, 1f);
        private const float MegaBorderSidePx = 2f;

        private void EnsurePriceButton()
        {
            // --- Ensure setup ---
            Transform priceRoot = ResolvePriceRootTransform();
            if (priceRoot == null)
                return;

            if (_priceButtonEnsured && priceButton != null && _priceButtonImage != null
                && _priceButtonBorder != null && priceText != null)
                return;

            MigratePriceTextOffRoot(priceRoot);
            ResolvePriceTextReference(priceRoot);

            _priceButtonBorder = FindOrCreatePriceBorderImage(priceRoot);
            _priceButtonImage = FindOrCreatePriceBackgroundImage(priceRoot);
            if (_priceButtonImage == null)
                return;

            _priceButtonImage.raycastTarget = true;
            EnsurePriceLabelOnTop(priceRoot);

            priceButton = priceRoot.GetComponent<Button>();
            if (priceButton == null)
            {
                priceButton = priceRoot.gameObject.AddComponent<Button>();
                priceButton.transition = Selectable.Transition.None;
            }
            else
            {
                priceButton.transition = Selectable.Transition.None;
            }

            priceButton.targetGraphic = _priceButtonImage;

            if (priceText != null)
            {
                priceText.alignment = TextAlignmentOptions.Center;
                priceText.raycastTarget = false;
                priceText.fontStyle = FontStyles.Bold;
            }

            _priceButtonEnsured = true;
        }

        private void MigratePriceTextOffRoot(Transform priceRoot)
        {
            // --- MigratePriceTextOffRoot ---
            var rootTmp = priceRoot.GetComponent<TextMeshProUGUI>();
            if (rootTmp == null)
                return;

            Transform labelTransform = priceRoot.Find("Label");
            GameObject labelGo;
            if (labelTransform != null)
            {
                labelGo = labelTransform.gameObject;
            }
            else
            {
                labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(priceRoot, false);
            }

            StretchRectToFill(labelGo.transform as RectTransform);

            var labelTmp = labelGo.GetComponent<TextMeshProUGUI>();
            if (labelTmp == null)
            {
                labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
                CopyTextMeshSettings(rootTmp, labelTmp);
            }

            labelTmp.text = rootTmp.text;
            priceText = labelTmp;

            if (Application.isPlaying)
                Destroy(rootTmp);
            else
                DestroyImmediate(rootTmp);
        }

        private static void CopyTextMeshSettings(TextMeshProUGUI from, TextMeshProUGUI to)
        {
            // --- CopyTextMeshSettings ---
            to.font = from.font;
            to.fontSize = from.fontSize;
            to.fontStyle = FontStyles.Bold;
            to.color = from.color;
            to.alignment = TextAlignmentOptions.Center;
            to.enableWordWrapping = false;
            to.richText = false;
            to.raycastTarget = false;
        }

        private void EnsurePriceLabelOnTop(Transform priceRoot)
        {
            // --- Ensure setup ---
            Transform label = priceRoot.Find("Label");
            if (label != null)
                label.SetAsLastSibling();
        }

        private static Image FindOrCreatePriceBorderImage(Transform priceRoot)
        {
            // --- FindOrCreatePriceBorderImage ---
            const string borderName = "Border";
            Transform border = priceRoot.Find(borderName);
            if (border == null)
            {
                var borderGo = new GameObject(borderName, typeof(RectTransform));
                borderGo.transform.SetParent(priceRoot, false);
                border = borderGo.transform;
            }

            border.SetAsFirstSibling();
            StretchRectToFill(border as RectTransform);

            if (!border.TryGetComponent(out Image image))
            {
                image = border.gameObject.AddComponent<Image>();
                image.raycastTarget = false;
            }

            return image;
        }

        private Transform ResolvePriceRootTransform()
        {
            // --- Resolve value ---
            if (priceButton != null)
                return priceButton.transform;

            if (priceText != null)
            {
                Transform t = priceText.transform;
                if (t.name == "Label" && t.parent != null)
                    return t.parent;
                if (t.name == "Price")
                    return t;
                if (t.parent != null && t.parent.name == "Price")
                    return t.parent;
            }

            return transform.Find("ContentRow/LeftColumn/Price");
        }

        private void ResolvePriceTextReference(Transform priceRoot)
        {
            // --- Resolve value ---
            if (priceText != null)
                return;

            priceText = priceRoot.GetComponent<TextMeshProUGUI>();
            if (priceText != null)
                return;

            Transform label = priceRoot.Find("Label");
            if (label != null)
                priceText = label.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        /// <summary>
        /// Price may already have an Image (new prefabs) or only TMP on the root (legacy).
        /// Unity allows one Graphic per GameObject, so legacy prefabs get a stretched Background child.
        /// </summary>
        private static Image FindOrCreatePriceBackgroundImage(Transform priceRoot)
        {
            // --- FindOrCreatePriceBackgroundImage ---
            if (priceRoot.TryGetComponent(out Image legacyRootImage))
            {
                if (Application.isPlaying)
                    Object.Destroy(legacyRootImage);
                else
                    Object.DestroyImmediate(legacyRootImage);
            }

            const string backgroundName = "Background";
            Transform background = priceRoot.Find(backgroundName);
            if (background != null && background.TryGetComponent(out Image childImage))
                return childImage;

            var bgGo = background != null
                ? background.gameObject
                : new GameObject(backgroundName, typeof(RectTransform));
            if (background == null)
            {
                bgGo.transform.SetParent(priceRoot, false);
                background = bgGo.transform;
            }

            StretchRectToFill(background as RectTransform);
            background.SetSiblingIndex(1);

            var bgRt = background as RectTransform;
            if (bgRt != null)
            {
                bgRt.offsetMin = new Vector2(PriceBorderInset, PriceBorderInset);
                bgRt.offsetMax = new Vector2(-PriceBorderInset, -PriceBorderInset);
            }

            if (!bgGo.TryGetComponent(out Image image))
            {
                image = bgGo.AddComponent<Image>();
                image.color = PriceDisabledFill;
            }

            return image;
        }

        private static void StretchRectToFill(RectTransform rt)
        {
            // --- StretchRectToFill ---
            if (rt == null)
                return;

            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        public void EnsureStableButtonRendering()
        {
            // --- Ensure setup ---
            if (_sidebarHeroLayout)
            {
                ApplySidebarHeroChrome();
                return;
            }

            if (button != null)
            {
                button.transition = Selectable.Transition.None;
                button.interactable = false;
                var rootGraphic = button.targetGraphic;
                if (rootGraphic != null)
                    rootGraphic.raycastTarget = false;
            }

            EnsurePriceButton();
            if (priceButton != null)
                priceButton.transition = Selectable.Transition.None;
        }

        public void SetButtonBackgroundColor(Color color)
        {
            // --- SetButtonBackgroundColor ---
            if (button == null)
                return;
            var graphic = button.targetGraphic;
            if (graphic == null || graphic.color == color)
                return;
            graphic.color = color;
        }

        /// <summary>
        /// Orbit Menu tree caption for one slot. Regular hulls stay "Lv 3"; MEGA slots
        /// show <c>MEGA SHIP</c> instead of "Lv 7".
        /// </summary>
        public static string FormatTreeLevelCaption(int level, bool moonHorizontal)
        {
            if (ShipFamilyPowerBarNorm.IsMegaTreeLevel(level))
                return MegaShipLevelCaption;
            if (moonHorizontal)
                return level == 1 ? "Lv 1" : $"Lv {level}";
            return level == 1 ? "1" : level.ToString();
        }

        public void SetLevelLabel(string text)
        {
            // --- SetLevelLabel ---
            if (_sidebarHeroLayout)
            {
                ApplySidebarHeroChrome();
                return;
            }

            if (levelText != null)
                levelText.text = text;
            if (!IsCurrentShipDisplay && Level >= 1)
                RestoreInFlowLevelLabel();
        }

        /// <summary>
        /// Tree-card ship name: one line that may paint toward the preview. Wrapping
        /// was eating names that already had room beside the art.
        /// </summary>
        void ApplyTreeTitleTextSettings()
        {
            if (shipNameText == null || _sidebarHeroLayout)
                return;

            shipNameText.enableWordWrapping = false;
            shipNameText.overflowMode = TextOverflowModes.Overflow;
            shipNameText.maxVisibleLines = 1;
            shipNameText.alignment = TextAlignmentOptions.Left;
        }

        /// <summary>
        /// Family line: same overflow as the hull name, not bold, so Cosmic Shark stays
        /// secondary to Hawk. Word wrap is off — camel-split already inserted the space.
        /// </summary>
        void ApplyTreeFamilyTextSettings()
        {
            if (familyNameText == null || _sidebarHeroLayout)
                return;

            familyNameText.enableWordWrapping = false;
            familyNameText.overflowMode = TextOverflowModes.Overflow;
            familyNameText.maxVisibleLines = 1;
            familyNameText.alignment = TextAlignmentOptions.Left;
            familyNameText.fontStyle = FontStyles.Normal;
            if (!_megaCardChromeActive && !_cachedRegularTextColors)
                familyNameText.color = FamilyCaptionColor;
        }

        /// <summary>
        /// Sidebar hero only: drop the in-flow level so the hull name sits above the art.
        /// Tree cards keep Lv N / MEGA SHIP in the left column.
        /// </summary>
        void CollapseInFlowLevelLabel()
        {
            if (levelText == null)
                return;
            if (_levelLe == null)
                _levelLe = levelText.GetComponent<LayoutElement>();
            if (_levelLe == null)
                _levelLe = levelText.gameObject.AddComponent<LayoutElement>();
            _levelLe.ignoreLayout = true;
            _levelLe.preferredHeight = 0f;
            _levelLe.minHeight = 0f;
        }

        public void SetShipName(string text)
        {
            // --- SetShipName ---
            if (shipNameText != null)
                shipNameText.text = DisplayNameFormatting.SplitCamelCase(text);
            if (_sidebarHeroLayout)
                ApplySidebarHeroChrome();
        }

        /// <summary>
        /// Writes the family line under the hull name and just above the buy chip.
        /// CamelCase ids are split (ForceBadger → Force Badger). Empty text hides the row
        /// so leftover labels do not linger on unassigned slots. Sidebar hero ignores this.
        /// </summary>
        /// <param name="text">Family id or already-spaced display name. Null / blank hides the line.</param>
        public void SetFamilyName(string text)
        {
            // --- Family line under hull name ---
            if (_sidebarHeroLayout)
            {
                HideFamilyNameLabel();
                return;
            }

            EnsureFamilyNameLabel();
            if (familyNameText == null)
                return;

            string display = DisplayNameFormatting.SplitCamelCase(text);
            bool show = !string.IsNullOrWhiteSpace(display);
            familyNameText.text = show ? display : string.Empty;
            familyNameText.gameObject.SetActive(show);
            if (_familyLe != null)
            {
                _familyLe.ignoreLayout = !show;
                if (!show)
                {
                    _familyLe.preferredHeight = 0f;
                    _familyLe.minHeight = 0f;
                }
            }

            ApplyTreeFamilyTextSettings();
        }
        public void SetPrice(string text)
        {
            // --- SetPrice ---
            if (_sidebarHeroHidePrice)
            {
                ApplySidebarHeroPriceHidden();
                return;
            }

            EnsurePriceButton();
            if (priceText != null)
                priceText.text = text;
        }

        public void SetPriceButtonStyle(bool clickable)
        {
            // --- SetPriceButtonStyle ---
            if (_sidebarHeroHidePrice)
            {
                ApplySidebarHeroPriceHidden();
                return;
            }

            EnsurePriceButton();
            Color fill;
            Color border;
            Color label;
            if (_megaCardChromeActive)
            {
                // Gold / bronze chip so Free and gem prices match the MEGA frame.
                fill = clickable ? MegaPriceReadyFill : MegaPriceIdleFill;
                border = clickable ? MegaPriceReadyBorder : MegaPriceIdleBorder;
                label = clickable ? MegaPriceReadyText : MegaPriceIdleText;
            }
            else
            {
                fill = clickable ? PriceEnabledFill : PriceDisabledFill;
                border = clickable ? PriceEnabledBorder : PriceDisabledBorder;
                label = clickable ? PriceEnabledText : PriceDisabledText;
            }

            if (_priceButtonBorder != null)
                _priceButtonBorder.color = border;
            if (_priceButtonImage != null)
                _priceButtonImage.color = fill;
            if (priceText != null)
                priceText.color = label;
        }

        /// <summary>
        /// Price chip for a unique MEGA that already has an owner. Call after
        /// <see cref="SetInteractable"/> so this gold chrome replaces the grey disabled look —
        /// the card stays unclickable, but the owner's name stays readable.
        /// </summary>
        public void SetOwnedOccupantStyle()
        {
            // --- Owned unique MEGA chip ---
            // [TITAN-ORBIT] Not a purchase button. Amber rail + name so every docked player
            // can see who holds this hull, including Debug Free Ship Upgrade Tree.
            if (_sidebarHeroHidePrice)
            {
                ApplySidebarHeroPriceHidden();
                return;
            }

            EnsurePriceButton();
            if (priceButton != null)
                priceButton.interactable = false;
            if (_priceButtonBorder != null)
                _priceButtonBorder.color = PriceOwnedBorder;
            if (_priceButtonImage != null)
                _priceButtonImage.color = PriceOwnedFill;
            if (priceText != null)
                priceText.color = PriceOwnedText;
        }

        public void SetPreview(Sprite sprite)
        {
            // --- SetPreview ---
            if (previewImage == null) return;
            previewImage.sprite = sprite;
            previewImage.preserveAspect = sprite != null;
            previewImage.color = sprite != null ? Color.white : new Color(0.07f, 0.09f, 0.12f, 0.95f);
        }

        public void SetButtonColors(Color normal) => SetButtonBackgroundColor(normal);
        public void SetInteractable(bool on)
        {
            // --- SetInteractable ---
            if (_sidebarHeroHidePrice)
            {
                // Card-root click stays wired from SetSidebarHeroCardClickHandler; only toggle interactable.
                if (button != null)
                    button.interactable = on;
                ApplySidebarHeroPriceHidden();
                return;
            }

            EnsurePriceButton();
            if (priceButton != null)
                priceButton.interactable = on;
            SetPriceButtonStyle(on);
        }

        /// <summary>
        /// Paints L7 MEGA tree cards as bronze-void tiles with a gold frame and
        /// <see cref="MegaShipLevelCaption"/>. Regular L1–L6 nodes call
        /// <see cref="ClearMegaShipCardStyle"/> so leftover chrome never sticks.
        /// </summary>
        /// <param name="isCurrent">This is the hull the local player is flying.</param>
        /// <param name="purchasable">Next-tier buy (or debug-free pick) is legal.</param>
        /// <param name="occupied">Unique MEGA already claimed by someone.</param>
        /// <param name="blocked">Locked (moon gems, no weapons, planet level).</param>
        public void ApplyMegaShipCardStyle(bool isCurrent, bool purchasable, bool occupied, bool blocked)
        {
            // --- MEGA boss-card chrome ---
            // [TITAN-ORBIT] Family cards stay cool navy glass. MEGAs get a warm void fill
            // and a gold rail so the last column reads as a different roster, not "Lv 7".
            if (IsCurrentShipDisplay || !ShipFamilyPowerBarNorm.IsMegaTreeLevel(Level))
            {
                ClearMegaShipCardStyle();
                return;
            }

            CacheRegularTextColorsIfNeeded();
            EnsureMegaCardChrome();
            _megaCardChromeActive = true;

            Color fill;
            Color border;
            // Any claimed unique MEGA uses the disabled slate — including the hull you
            // are flying. Solo debug otherwise kept that card gold and hid the state.
            if (occupied)
            {
                fill = MegaFillOccupied;
                border = MegaBorderOccupied;
            }
            else if (isCurrent)
            {
                fill = MegaFillCurrent;
                border = MegaBorderCurrent;
            }
            else if (purchasable)
            {
                fill = MegaFillReady;
                border = MegaBorderReady;
            }
            else if (blocked)
            {
                fill = MegaFillBlocked;
                border = MegaBorderBlocked;
            }
            else
            {
                fill = MegaFillIdle;
                border = MegaBorderIdle;
            }

            SetButtonBackgroundColor(fill);
            ApplyMegaBorderColor(border);
            if (_megaOuterGlow != null)
            {
                _megaOuterGlow.enabled = !occupied;
                if (_megaOuterGlow.enabled)
                {
                    _megaOuterGlow.effectColor = isCurrent || purchasable
                        ? MegaGlow
                        : new Color(MegaGlow.r, MegaGlow.g, MegaGlow.b, 0.22f);
                }
            }

            RestoreInFlowLevelLabel();
            EnsureFamilyNameLabel();
            if (levelText != null)
                levelText.color = occupied ? MegaCaptionOccupied : MegaCaptionGold;
            if (_megaCaptionLabel != null)
                _megaCaptionLabel.gameObject.SetActive(false);
            if (shipNameText != null)
                shipNameText.color = occupied ? MegaNameOccupied : MegaNameWarm;
            if (familyNameText != null)
                familyNameText.color = occupied ? MegaFamilyOccupiedColor : MegaFamilyCaptionColor;
            ApplyTreeTitleTextSettings();
            ApplyTreeFamilyTextSettings();

            if (priceButton != null)
                SetPriceButtonStyle(priceButton.interactable);
        }

        /// <summary>
        /// Removes MEGA frame / gold caption so a family card never keeps boss chrome.
        /// Sidebar hero cards also skip MEGA styling (they hide the level row).
        /// </summary>
        public void ClearMegaShipCardStyle()
        {
            // --- Strip MEGA chrome ---
            _megaCardChromeActive = false;
            if (_megaChromeRoot != null)
                _megaChromeRoot.gameObject.SetActive(false);
            if (_megaOuterGlow != null)
                _megaOuterGlow.enabled = false;

            if (_megaCaptionLabel != null)
                _megaCaptionLabel.gameObject.SetActive(false);

            if (!IsCurrentShipDisplay && Level >= 1)
                RestoreInFlowLevelLabel();
            if (levelText != null && _cachedRegularTextColors)
                levelText.color = _cachedLevelColor;

            if (shipNameText != null && _cachedRegularTextColors)
                shipNameText.color = _cachedNameColor;
            if (familyNameText != null && _cachedRegularTextColors)
                familyNameText.color = _cachedFamilyColor;
            else if (familyNameText != null)
                familyNameText.color = FamilyCaptionColor;
        }

        /// <summary>
        /// Builds the four-edge gold frame once. Edges sit as children so they paint
        /// on top of the navy/bronze fill without needing a 9-slice border sprite.
        /// </summary>
        void EnsureMegaCardChrome()
        {
            if (_megaChromeRoot != null)
            {
                _megaChromeRoot.gameObject.SetActive(true);
                _megaChromeRoot.SetAsLastSibling();
                return;
            }

            var chromeGo = new GameObject("MegaCardChrome", typeof(RectTransform));
            chromeGo.transform.SetParent(transform, false);
            _megaChromeRoot = chromeGo.GetComponent<RectTransform>();
            StretchRectToFill(_megaChromeRoot);
            // [UNITY] ignoreLayout — otherwise VerticalLayoutGroup treats the frame as a new row
            // and shoves the preview / power bar down.
            var ignore = chromeGo.AddComponent<LayoutElement>();
            ignore.ignoreLayout = true;
            _megaChromeRoot.SetAsLastSibling();

            // Same thickness on every side so inner content margins stay even.
            _megaBorderN = CreateMegaBorderEdge(_megaChromeRoot, "North",
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, MegaBorderSidePx));
            _megaBorderS = CreateMegaBorderEdge(_megaChromeRoot, "South",
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, MegaBorderSidePx));
            _megaBorderW = CreateMegaBorderEdge(_megaChromeRoot, "West",
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                new Vector2(MegaBorderSidePx, 0f));
            _megaBorderE = CreateMegaBorderEdge(_megaChromeRoot, "East",
                new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
                new Vector2(MegaBorderSidePx, 0f));

            var captionGo = new GameObject("MegaCaption", typeof(RectTransform));
            captionGo.transform.SetParent(_megaChromeRoot, false);
            _megaCaptionLabel = captionGo.AddComponent<TextMeshProUGUI>();
            _megaCaptionLabel.raycastTarget = false;
            _megaCaptionLabel.fontStyle = FontStyles.Bold;
            _megaCaptionLabel.color = MegaCaptionGold;
            _megaCaptionLabel.alignment = TextAlignmentOptions.Left;
            _megaCaptionLabel.enableWordWrapping = false;
            _megaCaptionLabel.overflowMode = TextOverflowModes.Overflow;
            _megaCaptionLabel.maxVisibleLines = 1;
            _megaCaptionLabel.text = MegaShipLevelCaption;
            if (levelText != null && levelText.font != null)
                _megaCaptionLabel.font = levelText.font;

            _megaOuterGlow = gameObject.GetComponent<Outline>();
            if (_megaOuterGlow == null)
                _megaOuterGlow = gameObject.AddComponent<Outline>();
            _megaOuterGlow.effectDistance = new Vector2(1.5f, -1.5f);
            _megaOuterGlow.useGraphicAlpha = true;
        }

        /// <summary>One edge of the MEGA frame. Anchored to a card side; size is thickness in pixels.</summary>
        static Image CreateMegaBorderEdge(
            Transform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 sizeDelta)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            image.color = MegaBorderIdle;
            return image;
        }

        void ApplyMegaBorderColor(Color color)
        {
            if (_megaBorderN != null) _megaBorderN.color = color;
            if (_megaBorderS != null) _megaBorderS.color = color;
            if (_megaBorderE != null) _megaBorderE.color = color;
            if (_megaBorderW != null) _megaBorderW.color = color;
        }

        void CacheRegularTextColorsIfNeeded()
        {
            if (_cachedRegularTextColors)
                return;
            if (levelText != null)
                _cachedLevelColor = levelText.color;
            if (shipNameText != null)
                _cachedNameColor = shipNameText.color;
            if (familyNameText != null)
                _cachedFamilyColor = familyNameText.color;
            _cachedRegularTextColors = true;
        }

        public void SetClickHandler(UnityEngine.Events.UnityAction handler)
        {
            // --- SetClickHandler ---
            if (button == null) return;
            button.onClick.RemoveAllListeners();
            if (handler != null)
                button.onClick.AddListener(handler);
        }

        public void SetPriceClickHandler(UnityEngine.Events.UnityAction handler)
        {
            // --- SetPriceClickHandler ---
            if (_sidebarHeroHidePrice)
            {
                SetSidebarHeroCardClickHandler(handler);
                return;
            }

            EnsurePriceButton();
            if (priceButton == null) return;
            priceButton.onClick.RemoveAllListeners();
            if (handler != null)
                priceButton.onClick.AddListener(handler);
        }
    }
}
