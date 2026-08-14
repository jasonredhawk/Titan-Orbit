using TMPro;
using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Single ship node widget in the upgrade tree prefab. Displays level, name, price, preview sprite,
    /// and ten-segment power bar. Population and click handlers are driven by <see cref="ShipUpgradeTreeUI"/>
    /// and <see cref="OrbitStationUI"/>; this class owns layout scaling and price-button chrome.
    /// </summary>
    public class ShipUpgradeTreeNodeUI : MonoBehaviour
    {
        /// <summary>Reference metrics from <see cref="Editor.CreateShipUpgradeTreePrefab"/> node template (120├ù100).</summary>
        private static class RefLayout
        {
            public const float RootPadLeft = 4f;
            public const float RootPadRight = 6f;
            public const float RootPadTop = 4f;
            public const float RootPadBottom = 4f;
            public const float RootSpacing = 4f;
            public const float ContentMinHeight = 72f;
            public const float ContentHSpacing = 5f;
            public const float LeftSpacing = 2f;
            public const float LeftMinWidth = 40f;
            public const float LevelFontSize = 13f;
            public const float LevelHeight = 14f;
            public const float NameFontSize = 11f;
            public const float NameHeight = 26f;
            public const float NameMinHeight = 22f;
            public const float PriceFontSize = 11f;
            public const float PriceHeight = 16f;
            public const float PriceMinWidth = 40f;
            public const float PreviewColWidth = 56f;
            public const float PreviewSize = 56f;
            public const float PreviewMinHeight = 48f;
            public const float PowerBarHeight = 10f;
            public const float PowerBarMinWidth = 48f;
        }

        [Header("Reference layout (prefab editor preview; runtime width comes from panel)")]
        [Tooltip("Reference width for prefab authoring. Runtime nodes scale uniformly to fill the tree row.")]
        [SerializeField] private float layoutWidth = 120f;
        [Tooltip("Reference height; runtime height scales with width using this aspect ratio.")]
        [SerializeField] private float layoutHeight = 100f;

        [SerializeField] private Button button;
        [SerializeField] private Button priceButton;
        [SerializeField] private TextMeshProUGUI levelText;
        [SerializeField] private TextMeshProUGUI shipNameText;
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
        private LayoutElement _levelLe;
        private LayoutElement _nameLe;
        private LayoutElement _priceLe;
        private Image _priceButtonImage;
        private Image _priceButtonBorder;
        private bool _priceButtonEnsured;
        private LayoutElement _previewColLe;
        private LayoutElement _previewImgLe;
        private LayoutElement _powerBarLe;
        private float _panelOverlayMargin = 8f;

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
        /// Current-ship node in the Orbit Menu sidebar: centered name on top, large preview, power bar under art.
        /// Hides the "You"/level label and the dark price pill.
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
            // Size may be unchanged on rebuild — still force hero chrome (name-above-art, hide "You").
            EnsureSidebarHeroLayout();
            ApplyScaledChildLayout(width, height);
            ApplySidebarHeroChrome();
            if (Rect != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(Rect);
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
        /// Sidebar hero chrome: no "You"/level, no price pill, name centered above the ship art.
        /// </summary>
        private void ApplySidebarHeroChrome()
        {
            // --- Hide level + price; center the ship name ---
            if (!_sidebarHeroLayout)
                return;

            ApplySidebarHeroPriceHidden();

            // Drop the "You" / Lv label — name above the art is enough.
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
                // [UNITY] TMP center alignment so the title sits over the preview, not left-ragged.
                shipNameText.alignment = TextAlignmentOptions.Center;
                shipNameText.enableWordWrapping = true;
                shipNameText.overflowMode = TextOverflowModes.Ellipsis;
            }

            if (_leftVlg != null)
            {
                _leftVlg.childAlignment = TextAnchor.UpperCenter;
                _leftVlg.spacing = 0f;
            }

            if (_nameLe != null)
                _nameLe.flexibleWidth = 1f;
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

        private void ApplyFixedLayoutSize(float width, float height)
        {
            // --- Apply changes ---
            if (Rect == null)
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

        private void EnsureSidebarHeroLayout()
        {
            // --- Ensure name-above-art vertical stack ---
            if (!_sidebarHeroLayout || _sidebarHeroLayoutConfigured)
                return;

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
            vlg.spacing = 4f;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // [TITAN-ORBIT] Order: centered ship name, then large preview. Power bar stays a root sibling under ContentRow.
            Transform previewCol = contentRow.Find("PreviewColumn");
            Transform leftCol = contentRow.Find("LeftColumn");
            if (leftCol != null)
                leftCol.SetAsFirstSibling();
            if (previewCol != null)
                previewCol.SetAsLastSibling();

            _sidebarHeroLayoutConfigured = true;
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
                    _previewColLe = previewCol.GetComponent<LayoutElement>();
            }

            if (powerBar != null)
                _powerBarLe = powerBar.GetComponent<LayoutElement>();
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

            // Uniform scale from the 120×100 prefab reference. Tree nodes use this fully;
            // sidebar hero clamps text/chrome so the stats bar still fits.
            float wScale = width / layoutWidth;
            float hScale = height / layoutHeight;
            float fontScale = Mathf.Min(wScale, hScale);

            // [TITAN-ORBIT] Sidebar hero ("Your Ship"): pack preview + labels tightly, then place the
            // power bar immediately underneath. Inflating ContentRow past its children left a dark empty
            // strip above the colourful stats (card background showing through).
            float heroPadTop = 6f;
            float heroPadBottom = 6f;
            float heroRootSpacing = 8f; // Breathing room between ship art and the colour stats bar.
            float heroBarH = 12f;
            float heroPreviewH = 0f;
            float heroContentH = 0f;
            float heroNameH = 0f;
            if (_sidebarHeroLayout)
            {
                float chrome = heroPadTop + heroPadBottom + heroRootSpacing + heroBarH;
                float available = Mathf.Max(96f, height - chrome);
                // Name only above the art ("You"/level and price are hidden) — rest goes to the preview.
                float heroLabelScale = 1.35f;
                heroNameH = ScalePx(RefLayout.NameHeight, heroLabelScale) + 2f;
                float heroLabelsH = heroNameH + 4f; // ContentRow spacing between name and preview
                heroPreviewH = Mathf.Max(80f, available - heroLabelsH);
                heroContentH = heroPreviewH + heroLabelsH;
            }

            if (_rootVlg != null)
            {
                if (_sidebarHeroLayout)
                {
                    _rootVlg.padding = new RectOffset(
                        ScalePxInt(RefLayout.RootPadLeft, wScale),
                        ScalePxInt(RefLayout.RootPadRight, wScale),
                        Mathf.RoundToInt(heroPadTop),
                        Mathf.RoundToInt(heroPadBottom));
                    _rootVlg.spacing = heroRootSpacing;
                }
                else
                {
                    _rootVlg.padding = new RectOffset(
                        ScalePxInt(RefLayout.RootPadLeft, wScale),
                        ScalePxInt(RefLayout.RootPadRight, wScale),
                        ScalePxInt(RefLayout.RootPadTop, hScale),
                        ScalePxInt(RefLayout.RootPadBottom, hScale));
                    _rootVlg.spacing = RefLayout.RootSpacing * hScale;
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
                _contentRowVlg.spacing = 4f;
            else if (_contentRowHlg != null)
                _contentRowHlg.spacing = RefLayout.ContentHSpacing * wScale;

            // Hero labels stay readable without eating the power-bar row (cap scale ~1.35×).
            float heroFontScale = _sidebarHeroLayout ? Mathf.Min(fontScale, 1.35f) : fontScale;
            float heroHScale = _sidebarHeroLayout ? Mathf.Min(hScale, 1.35f) : hScale;

            if (_leftVlg != null)
            {
                _leftVlg.spacing = _sidebarHeroLayout ? 0f : RefLayout.LeftSpacing * heroHScale;
                if (_sidebarHeroLayout)
                    _leftVlg.childAlignment = TextAnchor.UpperCenter;
            }
            if (_leftLe != null)
            {
                if (_sidebarHeroLayout)
                {
                    // Full-width name row so centered TMP can span the card.
                    float nameRowW = width - ScalePx(RefLayout.RootPadLeft + RefLayout.RootPadRight, wScale);
                    _leftLe.minWidth = nameRowW;
                    _leftLe.preferredWidth = nameRowW;
                    _leftLe.flexibleWidth = 1f;
                    _leftLe.flexibleHeight = 0f;
                    _leftLe.preferredHeight = heroNameH;
                    _leftLe.minHeight = heroNameH;
                }
                else
                {
                    _leftLe.minWidth = ScalePx(RefLayout.LeftMinWidth, wScale);
                }
            }

            if (_sidebarHeroLayout)
            {
                // Title above art — slightly larger, fixed row height; level stays collapsed.
                ApplyTextScale(shipNameText, _nameLe, RefLayout.NameFontSize + 1f, RefLayout.NameHeight, heroFontScale, heroHScale);
                if (_nameLe != null)
                {
                    _nameLe.minHeight = heroNameH;
                    _nameLe.preferredHeight = heroNameH;
                    _nameLe.flexibleWidth = 1f;
                }
                ApplySidebarHeroChrome();
            }
            else
            {
                ApplyTextScale(levelText, _levelLe, RefLayout.LevelFontSize, RefLayout.LevelHeight, heroFontScale, heroHScale);
                ApplyTextScale(shipNameText, _nameLe, RefLayout.NameFontSize, RefLayout.NameHeight, heroFontScale, heroHScale);
                if (_nameLe != null)
                    _nameLe.minHeight = ScalePx(RefLayout.NameMinHeight, heroHScale);
                ApplyTextScale(priceText, _priceLe, RefLayout.PriceFontSize, RefLayout.PriceHeight, heroFontScale, heroHScale);
                if (_priceLe != null)
                    _priceLe.minWidth = ScalePx(RefLayout.PriceMinWidth, wScale);
            }

            float previewColW = ScalePx(RefLayout.PreviewColWidth, wScale);
            float previewW = ScalePx(RefLayout.PreviewSize, wScale);
            float previewH = ScalePx(RefLayout.PreviewSize, hScale);
            float previewMinH = ScalePx(RefLayout.PreviewMinHeight, hScale);

            if (_sidebarHeroLayout)
            {
                previewColW = width - ScalePx(RefLayout.RootPadLeft + RefLayout.RootPadRight, wScale);
                previewW = previewColW;
                previewH = heroPreviewH;
                previewMinH = previewH * 0.85f;
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
            }
            if (_previewImgLe != null)
            {
                _previewImgLe.preferredWidth = previewW;
                _previewImgLe.preferredHeight = previewH;
                _previewImgLe.minHeight = previewMinH;
                _previewImgLe.flexibleWidth = _sidebarHeroLayout ? 1f : 0f;
                if (_sidebarHeroLayout)
                    _previewImgLe.flexibleHeight = 0f;
            }

            // Hero art: stretch the Image rect to the preview column (preserveAspect keeps silhouette readable).
            if (_sidebarHeroLayout && previewImage != null)
            {
                previewImage.preserveAspect = true;
                var previewRt = previewImage.transform as RectTransform;
                if (previewRt != null)
                {
                    previewRt.anchorMin = Vector2.zero;
                    previewRt.anchorMax = Vector2.one;
                    previewRt.offsetMin = Vector2.zero;
                    previewRt.offsetMax = Vector2.zero;
                }
            }

            float barH = _sidebarHeroLayout ? heroBarH : ScalePx(RefLayout.PowerBarHeight, hScale);
            if (_powerBarLe != null)
            {
                _powerBarLe.preferredHeight = barH;
                _powerBarLe.minHeight = barH;
                _powerBarLe.minWidth = ScalePx(RefLayout.PowerBarMinWidth, wScale);
                _powerBarLe.flexibleHeight = 0f;
            }

            // Keep segment thickness near the reserved bar height in sidebar hero.
            float barHeightScale = _sidebarHeroLayout ? (heroBarH / RefLayout.PowerBarHeight) : hScale;
            if (powerBar != null)
                powerBar.ConfigureLayoutScale(wScale, barHeightScale);

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
            if (NodeButtonWidth > 0.01f)
                return;
            if (Rect == null || layoutWidth < 1f || layoutHeight < 1f)
                return;
            _layoutCached = false;
            ApplyFixedLayoutSize(layoutWidth, layoutHeight);
        }
#endif

        /// <summary>Feeds power breakdown into the child bar, each stat vs the global catalog max.</summary>
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
            powerBar.ApplyBreakdown(breakdown, in globalMaxes, track);
        }

        private static readonly Color PriceEnabledFill = new Color(0.14f, 0.46f, 0.24f, 1f);
        private static readonly Color PriceEnabledBorder = new Color(0.38f, 0.88f, 0.48f, 1f);
        private static readonly Color PriceEnabledText = new Color(0.96f, 1f, 0.97f, 1f);
        private static readonly Color PriceDisabledFill = new Color(0.1f, 0.11f, 0.13f, 0.95f);
        private static readonly Color PriceDisabledBorder = new Color(0.24f, 0.26f, 0.3f, 0.9f);
        private static readonly Color PriceDisabledText = new Color(0.46f, 0.5f, 0.54f, 1f);
        private const float PriceBorderInset = 1f;

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
        }

        public void SetShipName(string text)
        {
            // --- SetShipName ---
            if (shipNameText != null)
                shipNameText.text = text;
            if (_sidebarHeroLayout)
                ApplySidebarHeroChrome();
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
            Color fill = clickable ? PriceEnabledFill : PriceDisabledFill;
            Color border = clickable ? PriceEnabledBorder : PriceDisabledBorder;
            Color label = clickable ? PriceEnabledText : PriceDisabledText;

            if (_priceButtonBorder != null)
                _priceButtonBorder.color = border;
            if (_priceButtonImage != null)
                _priceButtonImage.color = fill;
            if (priceText != null)
                priceText.color = label;
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
