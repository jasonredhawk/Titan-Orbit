using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using TitanOrbit.Game;
using TitanOrbit.Systems;
using TitanOrbit.Data;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace TitanOrbit.UI
{
    /// <summary>
    /// Combined orbit station UI: ship loadout grids (stacked vertically) at top, orbit actions and store below.
    /// Single left-anchored panel. Optional Shift Sci-Fi UI sprites/font assignable in inspector.
    /// </summary>
    public partial class OrbitStationUI : MonoBehaviour, IOrbitStationHost
    {
        [Header("Shift Sci-Fi UI (optional)")]
        [Tooltip("Assign Shift UI panel/sprite for sci-fi look.")]
        [SerializeField] private Sprite panelBackgroundSprite;
        [SerializeField] private Sprite buttonSprite;
        [Tooltip("Spin cards: defaults to Resources/SpinCardShiftVisuals if unset.")]
        [SerializeField] private SpinCardShiftVisuals spinCardShiftVisuals;
        [Tooltip("e.g. Rajdhani from Shift UI/Fonts.")]
        [SerializeField] private TMP_FontAsset fontAsset;

        [Header("Ship upgrade tree")]
        [Tooltip("Prefab with ShipUpgradeTreeUI (hint, nodes). Create via Titan Orbit/UI/Create Ship Upgrade Tree Prefab.")]
        [SerializeField] private ShipUpgradeTreeUI shipUpgradeTreePrefab;

        private SpinCardShiftVisuals _cachedSpinCardShiftVisuals;
        private bool _spinCardShiftResolveAttempted;
        private Image cardSpinButtonImage;

        private const float PanelWidth = 486f;
        private const float LeftMargin = 12f;
        /// <summary>Vertical offset from top so orbit panel sits below ShipStatsPanel (top-left anchor).</summary>
        private const float TopOffsetBelowShipStats = 168f;
        private const float SectionSpacing = 12f;
        private const int MaxSlotRows = 12;
        private const int SlotGridColumns = 6;
        /// <summary>Roomier slot card height so title/description/level bubble all fit.</summary>
        private const float SlotCardWidth = 110f;
        private const float SlotCardHeight = 82f;
        private const float SlotCellSpacing = 11f;
        private const float SlotPanelWidthConst = 12f + 6 * SlotCardWidth + 5 * SlotCellSpacing + 12f; // 6 cards + spacing
        private const float SlotPanelHeaderHeight = 28f;
        private const int SidebarSlotColumns = 1;
        private const float SidebarSlotCardWidth = 228f;
        private const float SidebarSlotCardHeight = 68f;
        private const float SidebarSlotCellSpacing = 8f;
        private const float SidebarEquipmentSlotCardHeight = 300f;
        private const float SidebarEquipmentIconHeight = 64f;
        private const float SidebarEquipmentIconMinHeight = 48f;
        private const float SidebarEquipmentAbilityFontSize = 10f;
        private const float SidebarEquipmentAbilityAreaHeight = 54f;
        private const float SidebarEquipmentStatsFooterHeight = 74f;
        private static readonly Color SidebarEquipmentEmptyAccent = new Color(0.35f, 0.4f, 0.48f, 0.85f);
        private const float MoonDockUpgradeSpinCardHeight = 168f;
        private const float MoonDockUpgradeSpinIconHeight = 48f;
        private const float MoonDockUpgradeSpinDescHeight = 44f;
        private const float MoonDockUpgradeSpinDescFontSize = 10f;
        private const float SidebarUpgradeCardSlotHeight = 172f;
        private const float SidebarUpgradeCardIconHeight = 56f;
        private const float SidebarUpgradeCardIconMinHeight = 44f;
        private const float SidebarUpgradeCardDescHeight = 52f;
        private const float SidebarUpgradeCardDescFontSize = 10f;
        private static readonly Color SidebarUpgradeCardEmptyAccent = new Color(0.35f, 0.42f, 0.55f, 0.85f);

        private GameObject rootPanel;
        private GameObject slotPanel;
        private GameObject storePanel;
        private ScrollRect storeScrollRect;
        private RectTransform storeContentRoot;
        private GameObject cardsTabContent;
        private GameObject shipsTabContent;
        private int activeStoreTab = 0; // 0 = Cards, 1 = Ships
        private const int MaxStoreShips = 80;
        private const int MaxShipCards = 20;
        private const float ShipCardPreviewSize = 80f;
        private const float ShipCardWidth = 140f;
        private const float ShipCardHeight = 88f;
        private const float ShipRowSpacing = 8f;
        private Transform shipsRowsContainer;
        private Starship currentShip;
        private Planet currentPlanet;
        private HomePlanet currentHomePlanet;
        private float contributedGems;
        private static float lastReceivedGems;
        private static bool pendingGemsRequest;

        private GameObject slotGridRoot;
        private RectTransform slotPanelRect;
        private RectTransform slotGridRect;
        private RectTransform storePanelRect;
        private TextMeshProUGUI loadoutSectionLabel;
        private GameObject[] slotBoxes;
        private Image[] slotBgImages;
        private Image[] slotBorderImages;
        private TextMeshProUGUI[] slotLevelTexts;
        private TextMeshProUGUI[] slotTitleTexts;
        private TextMeshProUGUI[] slotDescTexts;
        private Button[] slotDeleteButtons;

        private GameObject equipmentPanel;
        private RectTransform equipmentPanelRect;
        private GameObject equipmentGridRoot;
        private RectTransform equipmentGridRect;
        private TextMeshProUGUI equipmentSectionLabel;
        private GameObject[] equipmentBoxes;
        private Image[] equipmentBgImages;
        private Image[] equipmentBorderImages;
        private TextMeshProUGUI[] equipmentChargeTexts;
        private TextMeshProUGUI[] equipmentTitleTexts;
        private TextMeshProUGUI[] equipmentDescTexts;
        private Button[] equipmentDeleteButtons;
        private GameObject equipmentRemoveConfirmRoot;
        private TextMeshProUGUI equipmentRemoveConfirmBodyText;
        private int _pendingRemoveEquipmentSlotIndex = -1;
        private Starship _equipmentUiWatchShip;

        private GameObject cardRemoveConfirmRoot;
        private TextMeshProUGUI cardRemoveConfirmBodyText;
        private int _pendingRemoveSlotIndex = -1;

        private TextMeshProUGUI gemsText;
        private GameObject[] cardRoots;
        private Image[] cardBgImages;
        private TextMeshProUGUI[] cardTitleTexts;
        private TextMeshProUGUI[] cardLevelTexts;
        private TextMeshProUGUI[] cardDescTexts;
        private Button[] cardButtons;
        private CardData[] cardEntries;
        private Button[] chassisButtons;
        private TextMeshProUGUI[] chassisLabels;
        private ShipUnlockEntry[] shipUnlockEntries;
        private ShipUpgradeTreeUI shipUpgradeTree;
        private RectTransform shipTreeCenterRow;
        private RectTransform shipTreeCanvas;
        private TextMeshProUGUI shipTreeHintText;
        /// <summary>When unchanged, only update labels/colors — full rebuild was causing visible blinking every store refresh.</summary>
        private string _shipTreeStructureKey = "";
        private const int MaxShipTreeColumns = 7;
        private const float ShipTreeColGap = 6f;
        private const int ShipTreeMaxColumns = 6;
        /// <summary>Left/right inset from canvas edge to the node layout area (matches <see cref="BuildShipUpgradeTreeVisualFull"/> margin).</summary>
        private const float ShipTreeCanvasInnerMargin = 8f;
        private const float ShipTreeNodeHeight = 188f;
        /// <summary>Vertical distance between node centers; must exceed <see cref="ShipTreeNodeHeight"/> so rows do not overlap.</summary>
        private const float ShipTreeLevelSpacing = ShipTreeNodeHeight + 44f;
        private const string ShipTreeStructureKey = "vertical_tree_prefab_v1";
        private const string MoonDockShipTreeStructureKey = "moon_hlayout_prefab_v1";
        private float _cachedShipTreeBasisWidth = -999f;
        private int _cachedShipTreeWidthBucket = -1;
        private float _shipTreeLayoutCheckAccum;
        private const float ShipTreeLayoutCheckInterval = 0.25f;
        private const float ShipTreeLayoutWidthChangeThreshold = 12f;
        private HorizontalLayoutGroup _cardSpinRowLayout;
        private LayoutElement _cardSpinButtonLayout;
        private Button cardSpinButton;
        private TextMeshProUGUI cardSpinButtonLabel;
        private Image[] cardRarityFrameImages;
        private Image[] cardIconImages;
        private TextMeshProUGUI[] cardRarityLabels;

        private bool _moonDockLayoutActive;
        private bool _moonDockChromeReady;
        private bool _moonDockShipTreeHorizontal;
        private enum MoonDockCenterView { None, Store, Ships }
        private MoonDockCenterView _moonDockCenterView = MoonDockCenterView.None;
        private bool _moonDockMenuClosedByUser;
        private GameObject moonDockStoreSection;
        private GameObject _moonDockStoreToCardsDivider;
        private GameObject _moonDockCardsToEquipmentDivider;
        private RectTransform _moonDockStoreScrollViewport;
        private RectTransform _moonDockStoreGridContent;
        private GridLayoutGroup _moonDockStoreGrid;
        private string _moonDockStoreBuiltForFamilyKey;
        private int _moonDockEquipmentCardLayoutVersionBuilt = -1;
        private const int MoonDockEquipmentCardLayoutVersion = 3;

        private sealed class MoonDockStoreCardBinding
        {
            public bool isComponent;
            public string componentId;
            public StoreItemType supportItem;
            public GameObject root;
            public Image bgImage;
            public Image iconImage;
            public TextMeshProUGUI iconGlyph;
            public TextMeshProUGUI titleText;
            public TextMeshProUGUI descriptionText;
            public TextMeshProUGUI sublineText;
            public ShipUpgradeTreePowerBarUI powerBar;
            public Button buyButton;
            public Image buyImage;
            public TextMeshProUGUI buyLabel;
        }

        private readonly List<MoonDockStoreCardBinding> _moonDockStoreCards = new List<MoonDockStoreCardBinding>();

        private const float EquipmentPlacementNudgeStep = 0.03f;
        private const float EquipmentRotationSnapStep = 45f;

        private sealed class SidebarEquipmentSlotUi
        {
            public Image accentImage;
            public Image iconImage;
            public TextMeshProUGUI iconGlyph;
            public TextMeshProUGUI sublineText;
            public ShipUpgradeTreePowerBarUI powerBar;
            public GameObject statsFooter;
            public GameObject iconRoot;
            public GameObject placementPanel;
            public TextMeshProUGUI placementReadout;
        }

        private SidebarEquipmentSlotUi[] _sidebarEquipmentSlotUi;
        private bool _equipmentSlotRichLayoutActive;

        private sealed class SidebarUpgradeCardSlotUi
        {
            public Image accentImage;
            public Image iconImage;
            public TextMeshProUGUI iconGlyph;
            public TextMeshProUGUI sublineText;
            public GameObject iconRoot;
            public GameObject descFooter;
        }

        private SidebarUpgradeCardSlotUi[] _sidebarUpgradeCardSlotUi;
        private bool _upgradeCardSlotRichLayoutActive;
        private bool _upgradeSpinRowUsesTallLayout;

        private const int MoonDockStoreTilesPerRow = 7;
        private const float MoonDockStoreTileSpacing = 6f;
        private const float MoonDockStoreTileMinWidth = 72f;
        private const float MoonDockStoreCardHeight = 118f;
        private const float MoonDockEquipmentCardHeight = 324f;
        private const float MoonDockEquipmentIconHeight = 136f;
        private const float MoonDockEquipmentIconMinHeight = 104f;
        private const float MoonDockEquipmentAbilityFontSize = 11f;
        private const float MoonDockEquipmentAbilityAreaHeight = 80f;
        private const float MoonDockEquipmentStatsFooterHeight = 112f;
        private const float MoonDockEquipmentPowerBarHeight = 8f;
        private const float MoonDockEquipmentPowerBarPairGap = 2f;
        private const float MoonDockEquipmentScrollMinHeight = 200f;
        private const float MoonDockSectionHeaderHeight = 60f;
        private const float MoonDockSectionHeaderTitleHeight = 22f;
        private const float MoonDockSectionHeaderSubtitleHeight = 36f;
        private const float MoonDockSectionHeaderTextSpacing = 2f;
        private const float MoonDockCardsToEquipmentGap = 12f;
        private const float MoonDockEquipmentHeaderToGridGap = 10f;
        private static readonly Color MoonDockEquipmentCardBg = new Color(0.1f, 0.14f, 0.22f, 0.98f);
        private static readonly Color MoonDockEquipmentStatsFooterBg = new Color(0.06f, 0.08f, 0.12f, 0.96f);
        private static readonly Color MoonDockEquipmentPowerBarTrackBg = new Color(0.04f, 0.05f, 0.08f, 0.98f);
        private static readonly Color MoonDockStoreCardFrameColor = new Color(0.95f, 0.98f, 1f, 0.42f);
        private static readonly Color MoonDockStoreCardInnerShade = new Color(0f, 0f, 0f, 0.22f);
        private static readonly Color MoonDockItemTileButtonIdle = new Color(0.08f, 0.1f, 0.16f, 0.88f);
        private static readonly Color MoonDockItemTileButtonDisabled = new Color(0.08f, 0.1f, 0.16f, 0.55f);
        private static readonly Color MoonDockSpinButtonIdle = new Color(0.16f, 0.24f, 0.40f, 0.96f);
        private static readonly Color MoonDockSpinButtonDisabled = new Color(0.12f, 0.16f, 0.26f, 0.82f);

        private GameObject moonDockCenterBackdrop;
        private RectTransform moonDockSplitRow;
        private RectTransform moonDockSidebarHost;
        private RectTransform moonDockMainHost;
        private OrbitDockSidebarPanelUI orbitDockSidebar;
        private RectTransform moonDockCenterCardsHost;
        private ScrollRect moonDockCardsScroll;
        private RectTransform moonDockCenterShipsHost;
        private Button moonDockCloseButton;
        private Transform _moonDockSavedSlotPanelParent;
        private int _moonDockSavedSlotPanelSibling;
        private Transform _moonDockSavedEquipmentPanelParent;
        private int _moonDockSavedEquipmentPanelSibling;
        private Transform _moonDockSavedCardsTabParent;
        private int _moonDockSavedCardsTabSibling;
        private Transform _moonDockSavedShipsTabParent;
        private int _moonDockSavedShipsTabSibling;

        private readonly List<int> _shipTreeNextTargets = new List<int>(4);

        private float _cardsContentHeight;
        private float _shipsContentHeight;

        private Vector2 _storeScrollLastMousePos;
        private bool _storeScrollDragging;
        /// <summary>Track which tab was active so we only reset scroll position when switching tabs, not when using the scrollbar.</summary>
        private int _lastActiveStoreTab = -1;

        public static OrbitStationUI GetOrCreate()
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<OrbitStationUI>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }

            Canvas canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                var go = new GameObject("Canvas");
                canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = go.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                go.AddComponent<GraphicRaycaster>();
            }

            var uiObj = new GameObject("OrbitStationUI");
            uiObj.transform.SetParent(canvas.transform, false);
            var ui = uiObj.AddComponent<OrbitStationUI>();
            Instance = ui;
            return ui;
        }

        public static void OnContributedGemsReceived(float gems)
        {
            lastReceivedGems = gems;
            pendingGemsRequest = false;
            var ui = UnityEngine.Object.FindFirstObjectByType<OrbitStationUI>();
            if (ui != null) ui.RefreshFromReceivedGems();
        }

        /// <summary>Switch moon dock main panel (Upgrades vs Store+Cards). Sidebar nav uses the same paths.</summary>
        public void OpenGemMoonDockPanelFromWorld(bool upgradesPanel)
        {
            if (!_moonDockLayoutActive) return;
            OpenMoonDockMenu(upgradesPanel);
        }

        private void OnSidebarNavSelected(OrbitDockSidebarPanelUI.NavTarget target)
        {
            if (!_moonDockLayoutActive) return;
            SetMoonDockCenterView(target == OrbitDockSidebarPanelUI.NavTarget.Upgrades
                ? MoonDockCenterView.Ships
                : MoonDockCenterView.Store);
        }

        private float storeRefreshAccum;
        private const float StoreRefreshInterval = 0.35f;
        private float contributedGemsRequestAccum;
        private const float ContributedGemsRequestInterval = 1f; // Request contributed gems periodically so deposits show up

        private void Awake()
        {
            OnOrbitStationEcsAwake();
            EnsurePanelExists();
            if (rootPanel != null) rootPanel.SetActive(false);
        }

        void OnDestroy()
        {
            OnOrbitStationEcsDestroy();
        }

        partial void OnOrbitStationEcsUpdate();
        partial void OnOrbitStationEcsHide();

        private void OnEnable()
        {
            CardShopSystem.ClientSpinOfferReceived += OnClientSpinOfferReceived;
            CardShopSystem.ClientSpinOfferConsumed += OnClientSpinOfferConsumed;
        }

        private void OnDisable()
        {
            CardShopSystem.ClientSpinOfferReceived -= OnClientSpinOfferReceived;
            CardShopSystem.ClientSpinOfferConsumed -= OnClientSpinOfferConsumed;
        }

        private void OnClientSpinOfferReceived()
        {
            RefreshStoreLabels();
        }

        private void OnClientSpinOfferConsumed()
        {
            RefreshStoreLabels();
            RefreshSlots();
            RefreshEquipmentSlots();
        }

        private SpinCardShiftVisuals GetSpinCardShiftVisuals()
        {
            if (spinCardShiftVisuals != null) return spinCardShiftVisuals;
            if (_spinCardShiftResolveAttempted) return _cachedSpinCardShiftVisuals;
            _spinCardShiftResolveAttempted = true;
            _cachedSpinCardShiftVisuals = Resources.Load<SpinCardShiftVisuals>("SpinCardShiftVisuals");
            return _cachedSpinCardShiftVisuals;
        }

        private void ApplySpinCardImageSprite(Image img, Sprite sp, Image.Type type)
        {
            if (img == null) return;
            if (sp != null)
            {
                img.sprite = sp;
                img.type = type;
            }
            else if (buttonSprite != null)
            {
                img.sprite = buttonSprite;
                img.type = Image.Type.Sliced;
            }
        }

        private void OnRectTransformDimensionsChange()
        {
            _cachedShipTreeBasisWidth = -999f;
            _cachedShipTreeWidthBucket = -1;
        }

        /// <summary>Closes the dock menu but keeps the ship in orbit (undock with move input).</summary>
        public void CloseMoonDockMenu()
        {
            if (!_moonDockLayoutActive || _moonDockCenterView == MoonDockCenterView.None)
                return;
            _moonDockMenuClosedByUser = true;
            SetMoonDockCenterView(MoonDockCenterView.None);
        }

        /// <summary>Reopens the dock menu after the player dismissed it (still gem-moon docked).</summary>
        public void OpenMoonDockMenu(bool upgradesPanel = true)
        {
            if (!_moonDockLayoutActive || _moonDockCenterView != MoonDockCenterView.None)
                return;
            _moonDockMenuClosedByUser = false;
            SetMoonDockCenterView(upgradesPanel ? MoonDockCenterView.Ships : MoonDockCenterView.Store);
        }

        public bool IsMoonDockMenuOpen =>
            _moonDockLayoutActive && _moonDockCenterView != MoonDockCenterView.None;

        private void HandleMoonDockDismissInput()
        {
            if (!_moonDockLayoutActive || currentShip == null || !currentShip.GemMoonDocked)
                return;

#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
                return;
#else
            if (!Input.GetKeyDown(KeyCode.Escape))
                return;
#endif

            if (IsMoonDockMenuOpen)
                CloseMoonDockMenu();
            else
                OpenMoonDockMenu();
        }

        private void Update()
        {
            OnOrbitStationEcsUpdate();
            HandleMoonDockDismissInput();
            if (currentShip == null || currentPlanet == null) return;
            if (!_moonDockLayoutActive && (rootPanel == null || !rootPanel.activeSelf)) return;
            storeRefreshAccum += Time.deltaTime;
            contributedGemsRequestAccum += Time.deltaTime;
            if (storeRefreshAccum >= StoreRefreshInterval)
            {
                storeRefreshAccum = 0f;
                RefreshStoreLabels();
                RefreshSlots();
                RefreshEquipmentSlots();
            }
            // Periodically request contributed gems so deposits show up without closing/reopening
            if (contributedGemsRequestAccum >= ContributedGemsRequestInterval && HomePlanetStoreSystem.Instance != null)
            {
                contributedGemsRequestAccum = 0f;
                HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
            }
            // Fallback: apply mouse scroll and drag to store ScrollRect when pointer is over the viewport (works even if event system doesn't deliver to children)
            ApplyStoreScrollFallback();
            bool shipsTreeActive = activeStoreTab == 1 && (!_moonDockLayoutActive || _moonDockCenterView == MoonDockCenterView.Ships);
            bool treeUiOpen = shipsTreeActive && (
                (_moonDockLayoutActive && moonDockCenterBackdrop != null && moonDockCenterBackdrop.activeSelf) ||
                (rootPanel != null && rootPanel.activeSelf));
            if (treeUiOpen)
            {
                _shipTreeLayoutCheckAccum += Time.deltaTime;
                if (_shipTreeLayoutCheckAccum >= ShipTreeLayoutCheckInterval)
                {
                    _shipTreeLayoutCheckAccum = 0f;
                    CheckShipTreeLayoutBasisChanged();
                }
            }
        }

        private void ApplyStoreScrollFallback()
        {
            if (_moonDockLayoutActive) return;
            if (storeScrollRect == null || storeScrollRect.viewport == null || storeScrollRect.content == null || !storeScrollRect.vertical) return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            Vector2 mousePos;
            bool pointerDown;
            float scrollY;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current == null) return;
            mousePos = Mouse.current.position.ReadValue();
            pointerDown = Mouse.current.leftButton.isPressed;
            scrollY = Mouse.current.scroll.ReadValue().y;
#else
            mousePos = UnityEngine.Input.mousePosition;
            pointerDown = UnityEngine.Input.GetMouseButton(0);
            scrollY = UnityEngine.Input.mouseScrollDelta.y;
#endif

            RectTransform viewport = storeScrollRect.viewport;
            RectTransform content = storeScrollRect.content;
            UnityEngine.Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            bool overViewport = RectTransformUtility.RectangleContainsScreenPoint(viewport, mousePos, cam);

            float viewportHeight = viewport.rect.height;
            float contentHeight = content.sizeDelta.y;
            float scrollable = contentHeight - viewportHeight;
            if (scrollable <= 0f) return;

            const float scrollWheelScale = 0.04f;
            const float dragScale = 1f;

            float currentY = content.anchoredPosition.y;

            // Mouse wheel: apply whenever orbit panel is open and we have scroll input (no hit test so it always works)
            if (Mathf.Abs(scrollY) > 0.001f)
            {
                float delta = scrollY * scrollWheelScale * scrollable;
                ApplyStoreScrollByDelta(-delta);
                return; // applied wheel, skip drag handling this frame
            }

            if (pointerDown)
            {
                if (overViewport)
                {
                    if (_storeScrollDragging)
                    {
                        float deltaY = _storeScrollLastMousePos.y - mousePos.y;
                        float nextY = Mathf.Clamp(currentY + (deltaY * dragScale), 0f, scrollable);
                        content.anchoredPosition = new Vector2(content.anchoredPosition.x, nextY);
                        storeScrollRect.verticalNormalizedPosition = 1f - (nextY / scrollable);
                    }
                    _storeScrollLastMousePos = mousePos;
                    _storeScrollDragging = true;
                }
                else
                    _storeScrollDragging = false;
            }
            else
                _storeScrollDragging = false;
        }

        /// <summary>Scroll the store list by a delta in content space (positive = scroll down/see lower items).</summary>
        private void ApplyStoreScrollByDelta(float deltaY)
        {
            if (storeScrollRect == null || storeScrollRect.content == null || storeScrollRect.viewport == null) return;
            RectTransform content = storeScrollRect.content;
            float viewportHeight = storeScrollRect.viewport.rect.height;
            float contentHeight = content.sizeDelta.y;
            float scrollable = contentHeight - viewportHeight;
            if (scrollable <= 0f) return;
            float currentY = content.anchoredPosition.y;
            float nextY = Mathf.Clamp(currentY + deltaY, 0f, scrollable);
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, nextY);
            storeScrollRect.verticalNormalizedPosition = 1f - (nextY / scrollable);
        }

        private void OnStoreScrollUp()
        {
            float viewportHeight = storeScrollRect != null && storeScrollRect.viewport != null ? storeScrollRect.viewport.rect.height : 200f;
            ApplyStoreScrollByDelta(viewportHeight * 0.4f);
        }

        private void OnStoreScrollDown()
        {
            float viewportHeight = storeScrollRect != null && storeScrollRect.viewport != null ? storeScrollRect.viewport.rect.height : 200f;
            ApplyStoreScrollByDelta(-viewportHeight * 0.4f);
        }

        private Button CreateStoreScrollButton(Transform parent, string label, bool atTop, float storeY, float height)
        {
            var go = new GameObject("StoreScroll_" + label);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, atTop ? 1f : 0f);
            rect.anchorMax = new Vector2(1f, atTop ? 1f : 0f);
            rect.pivot = new Vector2(1f, atTop ? 1f : 0f);
            rect.anchoredPosition = Vector2.zero;
            rect.offsetMin = new Vector2(-28f, atTop ? storeY - height : 12f);
            rect.offsetMax = new Vector2(-12f, atTop ? storeY : 12f + height);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.3f, 0.5f, 0.95f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            var btn = go.AddComponent<Button>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
            return btn;
        }

        private float _lastHomePlanetLookupTime = -999f;
        private const float HomePlanetLookupInterval = 1f;

        public void Show(Starship ship, Planet planet)
        {
            UnsubscribeEquipmentUiWatch();
            currentShip = ship;
            SubscribeEquipmentUiWatch(ship);
            currentPlanet = planet;
            if (ship != null && (currentHomePlanet == null || Time.time - _lastHomePlanetLookupTime >= HomePlanetLookupInterval))
            {
                _lastHomePlanetLookupTime = Time.time;
                foreach (var h in HomePlanet.AllHomePlanets)
                {
                    if (h != null && h.AssignedTeam == ship.ShipTeam) { currentHomePlanet = h; break; }
                }
            }
            contributedGems = lastReceivedGems;
            EnsurePanelExists();
            EnterMoonDockLayout();
            if (rootPanel != null)
            {
                transform.SetAsLastSibling(); // Bring orbit panel to front so it draws above other HUD
            }
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null)
                HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
            RefreshAll();
            if (_moonDockLayoutActive)
            {
                RebuildMoonDockLayoutsAfterShow();
                if (!_moonDockMenuClosedByUser)
                    SetMoonDockCenterView(MoonDockCenterView.Ships);
            }
            else
            {
                RefreshStoreTabVisibility();
                if (rootPanel != null)
                {
                    var rootRect = rootPanel.transform as RectTransform;
                    if (rootRect != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rootRect);
                    if (storeContentRoot != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);
                    if (slotGridRoot != null)
                    {
                        var slotRect = slotGridRoot.transform as RectTransform;
                        if (slotRect != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(slotRect);
                    }
                    Canvas.ForceUpdateCanvases();
                }
                RefreshShipsTab(scrollToActiveShipNode: activeStoreTab == 1);
            }
        }

        public void Hide()
        {
            HideCardRemoveConfirm();
            _moonDockMenuClosedByUser = false;
            ExitMoonDockLayout();
            UnsubscribeEquipmentUiWatch();
            currentShip = null;
            currentPlanet = null;
            currentHomePlanet = null; // Clear so next Show does fresh lookup
            if (rootPanel != null) rootPanel.SetActive(false);
            OnOrbitStationEcsHide();
        }

        public void RefreshFromReceivedGems()
        {
            contributedGems = lastReceivedGems;
            RefreshStoreLabels();
        }

        private void SubscribeEquipmentUiWatch(Starship ship)
        {
            _equipmentUiWatchShip = ship;
        }

        private void UnsubscribeEquipmentUiWatch()
        {
            _equipmentUiWatchShip = null;
        }

        private void RefreshAll()
        {
            RefreshStoreLabels();
            RefreshSlots();
            RefreshEquipmentSlots();
            RefreshSidebar();
        }

        private void EnsurePanelExists()
        {
            if (rootPanel != null && rootPanel) return;

            rootPanel = null;
            storeContentRoot = null;
            cardsTabContent = null;
            shipsTabContent = null;
            shipTreeCenterRow = null;
            shipTreeCanvas = null;
            shipsRowsContainer = null;

            Canvas canvas = GetComponentInParent<Canvas>(true);
            if (canvas == null) canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            if (transform == null || !transform) return;

            // Always position this panel top-left under ShipStatsPanel (fixes center positioning in existing scenes).
            var myRect = transform as RectTransform;
            if (myRect == null) myRect = gameObject.AddComponent<RectTransform>();
            myRect.anchorMin = new Vector2(0f, 1f);
            myRect.anchorMax = new Vector2(0f, 1f);
            myRect.pivot = new Vector2(0f, 1f);
            myRect.anchoredPosition = new Vector2(LeftMargin, -TopOffsetBelowShipStats);
            myRect.sizeDelta = new Vector2(Mathf.Max(PanelWidth, SlotPanelWidthConst), 860f);

            // Build content as child of this panel so it appears under ShipStatsPanel (position comes from this transform).
            rootPanel = new GameObject("OrbitStationRoot");
            rootPanel.transform.SetParent(transform, false);
            var rootRect = rootPanel.AddComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootPanel.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

            // —— Independent Ship Slot Panel (6 wide, roomy cards) ——
            int slotGridRows = Mathf.Min(2, (MaxSlotRows + SlotGridColumns - 1) / SlotGridColumns);
            float slotGridTotalH = slotGridRows * SlotCardHeight + (slotGridRows - 1) * SlotCellSpacing;
            float slotPanelHeight = SlotPanelHeaderHeight + 8f + slotGridTotalH + 12f;

            slotPanel = new GameObject("ShipSlotPanel");
            slotPanel.transform.SetParent(rootPanel.transform, false);
            slotPanelRect = slotPanel.AddComponent<RectTransform>();
            slotPanelRect.anchorMin = new Vector2(0f, 1f);
            slotPanelRect.anchorMax = new Vector2(1f, 1f);
            slotPanelRect.pivot = new Vector2(0.5f, 1f);
            slotPanelRect.anchoredPosition = Vector2.zero;
            slotPanelRect.offsetMin = new Vector2(12f, -slotPanelHeight);
            slotPanelRect.offsetMax = new Vector2(-12f, 0f);
            var slotPanelImg = slotPanel.AddComponent<Image>();
            slotPanelImg.color = new Color(0.08f, 0.1f, 0.16f, 0.94f);
            if (panelBackgroundSprite != null) { slotPanelImg.sprite = panelBackgroundSprite; slotPanelImg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }

            float slotY = -4f;
            loadoutSectionLabel = CreateSectionLabelWithRef(slotPanel.transform, "Loadout", OrbitDockSidebarPanelUI.SectionTitleUpgradeCards, ref slotY);
            slotY -= 8f;
            slotGridRoot = new GameObject("SlotGrid");
            slotGridRoot.transform.SetParent(slotPanel.transform, false);
            slotGridRect = slotGridRoot.AddComponent<RectTransform>();
            slotGridRect.anchorMin = new Vector2(0f, 1f);
            slotGridRect.anchorMax = new Vector2(1f, 1f);
            slotGridRect.pivot = new Vector2(0.5f, 1f);
            slotGridRect.anchoredPosition = new Vector2(0f, slotY);
            slotGridRect.sizeDelta = new Vector2(-24f, slotGridTotalH);
            var gridLayout = slotGridRoot.AddComponent<GridLayoutGroup>();
            gridLayout.cellSize = new Vector2(SlotCardWidth, SlotCardHeight);
            gridLayout.spacing = new Vector2(SlotCellSpacing, SlotCellSpacing);
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = SlotGridColumns;
            gridLayout.childAlignment = TextAnchor.UpperLeft;
            slotBoxes = new GameObject[MaxSlotRows];
            slotBgImages = new Image[MaxSlotRows];
            slotBorderImages = new Image[MaxSlotRows];
            slotLevelTexts = new TextMeshProUGUI[MaxSlotRows];
            slotTitleTexts = new TextMeshProUGUI[MaxSlotRows];
            slotDescTexts = new TextMeshProUGUI[MaxSlotRows];
            slotDeleteButtons = new Button[MaxSlotRows];
            for (int i = 0; i < MaxSlotRows; i++)
            {
                CreateSlotBoxForGrid(slotGridRoot.transform, SlotCardWidth, SlotCardHeight, i, out slotBoxes[i], out slotBgImages[i], out slotBorderImages[i], out slotLevelTexts[i], out slotTitleTexts[i], out slotDescTexts[i], out slotDeleteButtons[i]);
                int idx = i;
                if (slotDeleteButtons[i] != null)
                    slotDeleteButtons[i].onClick.AddListener(() => ShowCardRemoveConfirm(idx));
            }

            // —— Equipment Slot Panel ——
            int equipmentGridRows = Mathf.Min(2, (MaxSlotRows + SlotGridColumns - 1) / SlotGridColumns);
            float equipmentGridTotalH = equipmentGridRows * SlotCardHeight + (equipmentGridRows - 1) * SlotCellSpacing;
            float equipmentPanelHeight = SlotPanelHeaderHeight + 8f + equipmentGridTotalH + 12f;

            equipmentPanel = new GameObject("ShipEquipmentPanel");
            equipmentPanel.transform.SetParent(rootPanel.transform, false);
            equipmentPanelRect = equipmentPanel.AddComponent<RectTransform>();
            equipmentPanelRect.anchorMin = new Vector2(0f, 1f);
            equipmentPanelRect.anchorMax = new Vector2(1f, 1f);
            equipmentPanelRect.pivot = new Vector2(0.5f, 1f);
            equipmentPanelRect.anchoredPosition = Vector2.zero;
            equipmentPanelRect.offsetMin = new Vector2(12f, -(slotPanelHeight + 8f + equipmentPanelHeight));
            equipmentPanelRect.offsetMax = new Vector2(-12f, -(slotPanelHeight + 8f));
            var equipmentPanelImg = equipmentPanel.AddComponent<Image>();
            equipmentPanelImg.color = new Color(0.08f, 0.1f, 0.16f, 0.94f);
            if (panelBackgroundSprite != null) { equipmentPanelImg.sprite = panelBackgroundSprite; equipmentPanelImg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }

            float equipmentY = -4f;
            equipmentSectionLabel = CreateSectionLabelWithRef(equipmentPanel.transform, "Equipment", OrbitDockSidebarPanelUI.SectionTitleEquipment, ref equipmentY);
            equipmentY -= 8f;
            equipmentGridRoot = new GameObject("EquipmentGrid");
            equipmentGridRoot.transform.SetParent(equipmentPanel.transform, false);
            equipmentGridRect = equipmentGridRoot.AddComponent<RectTransform>();
            equipmentGridRect.anchorMin = new Vector2(0f, 1f);
            equipmentGridRect.anchorMax = new Vector2(1f, 1f);
            equipmentGridRect.pivot = new Vector2(0.5f, 1f);
            equipmentGridRect.anchoredPosition = new Vector2(0f, equipmentY);
            equipmentGridRect.sizeDelta = new Vector2(-24f, equipmentGridTotalH);
            var equipmentGridLayout = equipmentGridRoot.AddComponent<GridLayoutGroup>();
            equipmentGridLayout.cellSize = new Vector2(SlotCardWidth, SlotCardHeight);
            equipmentGridLayout.spacing = new Vector2(SlotCellSpacing, SlotCellSpacing);
            equipmentGridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            equipmentGridLayout.constraintCount = SlotGridColumns;
            equipmentGridLayout.childAlignment = TextAnchor.UpperLeft;
            equipmentBoxes = new GameObject[MaxSlotRows];
            equipmentBgImages = new Image[MaxSlotRows];
            equipmentBorderImages = new Image[MaxSlotRows];
            equipmentChargeTexts = new TextMeshProUGUI[MaxSlotRows];
            equipmentTitleTexts = new TextMeshProUGUI[MaxSlotRows];
            equipmentDescTexts = new TextMeshProUGUI[MaxSlotRows];
            equipmentDeleteButtons = new Button[MaxSlotRows];
            for (int i = 0; i < MaxSlotRows; i++)
            {
                CreateSlotBoxForGrid(equipmentGridRoot.transform, SlotCardWidth, SlotCardHeight, i, out equipmentBoxes[i], out equipmentBgImages[i], out equipmentBorderImages[i], out equipmentChargeTexts[i], out equipmentTitleTexts[i], out equipmentDescTexts[i], out equipmentDeleteButtons[i]);
                int idx = i;
                if (equipmentDeleteButtons[i] != null)
                    equipmentDeleteButtons[i].onClick.AddListener(() => ShowEquipmentRemoveConfirm(idx));
            }

            // —— Store Panel ——
            float storePanelTop = slotPanelHeight + equipmentPanelHeight + 16f;
            storePanel = new GameObject("StorePanel");
            storePanel.transform.SetParent(rootPanel.transform, false);
            storePanelRect = storePanel.AddComponent<RectTransform>();
            storePanelRect.anchorMin = new Vector2(0f, 0f);
            storePanelRect.anchorMax = new Vector2(1f, 1f);
            storePanelRect.offsetMin = new Vector2(12f, 12f);
            storePanelRect.offsetMax = new Vector2(-12f, -storePanelTop);
            var storePanelImg = storePanel.AddComponent<Image>();
            storePanelImg.color = new Color(0.06f, 0.07f, 0.12f, 0.96f);
            if (panelBackgroundSprite != null) { storePanelImg.sprite = panelBackgroundSprite; storePanelImg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }

            float storeY = 0f;
            CreateSectionLabel(storePanel.transform, "Store", "Store", ref storeY);
            gemsText = CreateTMP(storePanel.transform, "Gems", "Your contributed gems: 0", 14, ref storeY);
            storeY -= 4f;
            const float StoreBlockShiftUpPx = 14f; // Tighter header: tabs + scroll start higher in the store panel
            storeY += StoreBlockShiftUpPx;

            // Scroll + content for store
            var scrollViewGo = new GameObject("StoreScrollView");
            scrollViewGo.transform.SetParent(storePanel.transform, false);
            var scrollViewRect = scrollViewGo.AddComponent<RectTransform>();
            scrollViewRect.anchorMin = new Vector2(0f, 0f);
            scrollViewRect.anchorMax = new Vector2(1f, 1f);
            scrollViewRect.offsetMin = new Vector2(12f, 12f);
            scrollViewRect.offsetMax = new Vector2(-28f, storeY); // Leave room for scrollbar
            storeScrollRect = scrollViewGo.AddComponent<ScrollRect>();
            storeScrollRect.horizontal = false;
            storeScrollRect.vertical = true;
            storeScrollRect.movementType = ScrollRect.MovementType.Clamped;
            storeScrollRect.scrollSensitivity = 20f;
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollViewGo.transform, false);
            var viewportRect = viewport.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();
            var viewportImg = viewport.AddComponent<Image>();
            viewportImg.color = new Color(1f, 1f, 1f, 0.01f);
            viewportImg.raycastTarget = true; // Required for ScrollRect to receive drag events
            viewport.AddComponent<ScrollRectForwarder>();
            storeScrollRect.viewport = viewportRect;
            storeContentRoot = new GameObject("StoreContent").AddComponent<RectTransform>();
            storeContentRoot.SetParent(viewport.transform, false);
            // Horizontal stretch: width = viewportWidth + sizeDelta.x — keep x=0 so content matches viewport (not 2× wide).
            storeContentRoot.anchorMin = new Vector2(0f, 1f);
            storeContentRoot.anchorMax = new Vector2(1f, 1f);
            storeContentRoot.pivot = new Vector2(0f, 1f);
            storeContentRoot.anchoredPosition = Vector2.zero;
            storeContentRoot.sizeDelta = new Vector2(0f, 800f); // height updated below; width follows viewport
            var contentBg = storeContentRoot.gameObject.AddComponent<Image>();
            contentBg.color = new Color(0f, 0f, 0f, 0.01f);
            contentBg.raycastTarget = true;
            storeContentRoot.gameObject.AddComponent<ScrollRectForwarder>();
            var contentSizeFitter = storeContentRoot.gameObject.AddComponent<ContentSizeFitter>();
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            // Disable so we control content height explicitly (ContentSizeFitter can prevent scrolling)
            contentSizeFitter.enabled = false;
            var contentVlg = storeContentRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            contentVlg.childAlignment = TextAnchor.UpperCenter;
            contentVlg.childControlWidth = true;
            contentVlg.childControlHeight = true;
            contentVlg.childForceExpandWidth = true;
            contentVlg.childForceExpandHeight = false;
            contentVlg.spacing = 0f;
            storeScrollRect.content = storeContentRoot;

            // Cards tab content — vertical layout (moon dock + legacy orbit panel)
            cardsTabContent = new GameObject("CardsTabContent");
            cardsTabContent.transform.SetParent(storeContentRoot, false);
            var cardsContentRect = cardsTabContent.AddComponent<RectTransform>();
            cardsContentRect.anchorMin = new Vector2(0f, 1f);
            cardsContentRect.anchorMax = new Vector2(1f, 1f);
            cardsContentRect.pivot = new Vector2(0.5f, 1f);
            cardsContentRect.offsetMin = Vector2.zero;
            cardsContentRect.offsetMax = Vector2.zero;
            var cardsTabVlg = cardsTabContent.AddComponent<VerticalLayoutGroup>();
            cardsTabVlg.spacing = 6f;
            cardsTabVlg.padding = new RectOffset(12, 12, 8, 8);
            cardsTabVlg.childAlignment = TextAnchor.UpperCenter;
            cardsTabVlg.childControlWidth = true;
            cardsTabVlg.childControlHeight = true;
            cardsTabVlg.childForceExpandWidth = true;
            cardsTabVlg.childForceExpandHeight = false;

            CreateMoonDockSectionHeader(
                cardsTabContent.transform,
                OrbitDockSidebarPanelUI.SectionTitleUpgradeCards,
                "Spin for three cards — pick one to equip in an empty slot.",
                OrbitDockSidebarPanelUI.UpgradeCardsAccent);

            var spinBlockGo = new GameObject("CardSpinBlock");
            spinBlockGo.transform.SetParent(cardsTabContent.transform, false);
            var spinBlockLe = spinBlockGo.AddComponent<LayoutElement>();
            spinBlockLe.flexibleWidth = 1f;
            spinBlockLe.flexibleHeight = 0f;
            spinBlockLe.preferredHeight = 36f + 6f + MoonDockUpgradeSpinCardHeight;
            spinBlockLe.minHeight = spinBlockLe.preferredHeight;
            var spinBlockVlg = spinBlockGo.AddComponent<VerticalLayoutGroup>();
            spinBlockVlg.spacing = 6f;
            spinBlockVlg.padding = new RectOffset(0, 0, 0, 0);
            spinBlockVlg.childAlignment = TextAnchor.UpperLeft;
            spinBlockVlg.childControlWidth = true;
            spinBlockVlg.childControlHeight = true;
            spinBlockVlg.childForceExpandWidth = false;
            spinBlockVlg.childForceExpandHeight = false;

            var spinBtnGo = new GameObject("CardSpinButton");
            spinBtnGo.transform.SetParent(spinBlockGo.transform, false);
            _cardSpinButtonLayout = spinBtnGo.AddComponent<LayoutElement>();
            _cardSpinButtonLayout.flexibleWidth = 0f;
            _cardSpinButtonLayout.preferredHeight = 36f;
            _cardSpinButtonLayout.minHeight = 34f;
            var spinImg = spinBtnGo.AddComponent<Image>();
            cardSpinButtonImage = spinImg;
            spinImg.color = MoonDockSpinButtonIdle;
            if (buttonSprite != null) { spinImg.sprite = buttonSprite; spinImg.type = Image.Type.Sliced; }
            var spinOutline = spinBtnGo.AddComponent<Outline>();
            spinOutline.effectColor = new Color(
                OrbitDockSidebarPanelUI.UpgradeCardsAccent.r,
                OrbitDockSidebarPanelUI.UpgradeCardsAccent.g,
                OrbitDockSidebarPanelUI.UpgradeCardsAccent.b,
                0.72f);
            spinOutline.effectDistance = new Vector2(1f, -1f);
            cardSpinButton = spinBtnGo.AddComponent<Button>();
            var spinBtnColors = cardSpinButton.colors;
            spinBtnColors.normalColor = Color.white;
            spinBtnColors.highlightedColor = new Color(1.08f, 1.08f, 1.1f, 1f);
            spinBtnColors.pressedColor = new Color(0.92f, 0.94f, 0.98f, 1f);
            spinBtnColors.disabledColor = new Color(0.75f, 0.78f, 0.84f, 0.9f);
            cardSpinButton.colors = spinBtnColors;
            cardSpinButton.onClick.AddListener(OnCardSpinClick);
            var spinLabelGo = new GameObject("Text");
            spinLabelGo.transform.SetParent(spinBtnGo.transform, false);
            var spinLabelRect = spinLabelGo.AddComponent<RectTransform>();
            spinLabelRect.anchorMin = Vector2.zero;
            spinLabelRect.anchorMax = Vector2.one;
            spinLabelRect.offsetMin = new Vector2(12f, 4f);
            spinLabelRect.offsetMax = new Vector2(-12f, -4f);
            cardSpinButtonLabel = spinLabelGo.AddComponent<TextMeshProUGUI>();
            cardSpinButtonLabel.text = "Spin";
            cardSpinButtonLabel.fontSize = 14;
            cardSpinButtonLabel.fontStyle = FontStyles.Bold;
            cardSpinButtonLabel.alignment = TextAlignmentOptions.Center;
            cardSpinButtonLabel.color = Color.white;
            if (fontAsset != null) cardSpinButtonLabel.font = fontAsset;

            const int maxStoreCards = 3;
            var cardSpinRowGo = new GameObject("CardSpinRow");
            cardSpinRowGo.transform.SetParent(spinBlockGo.transform, false);
            var cardSpinRowLe = cardSpinRowGo.AddComponent<LayoutElement>();
            cardSpinRowLe.flexibleWidth = 0f;
            cardSpinRowLe.preferredHeight = MoonDockUpgradeSpinCardHeight;
            cardSpinRowLe.minHeight = MoonDockUpgradeSpinCardHeight;
            _cardSpinRowLayout = cardSpinRowGo.AddComponent<HorizontalLayoutGroup>();
            _cardSpinRowLayout.spacing = 6f;
            _cardSpinRowLayout.padding = new RectOffset(0, 0, 0, 0);
            _cardSpinRowLayout.childAlignment = TextAnchor.UpperLeft;
            _cardSpinRowLayout.childControlWidth = true;
            _cardSpinRowLayout.childControlHeight = true;
            _cardSpinRowLayout.childForceExpandWidth = false;
            _cardSpinRowLayout.childForceExpandHeight = false;
            cardRoots = new GameObject[maxStoreCards];
            cardBgImages = new Image[maxStoreCards];
            cardTitleTexts = new TextMeshProUGUI[maxStoreCards];
            cardLevelTexts = new TextMeshProUGUI[maxStoreCards];
            cardDescTexts = new TextMeshProUGUI[maxStoreCards];
            cardButtons = new Button[maxStoreCards];
            cardEntries = new CardData[maxStoreCards];
            cardRarityFrameImages = new Image[maxStoreCards];
            cardIconImages = new Image[maxStoreCards];
            cardRarityLabels = new TextMeshProUGUI[maxStoreCards];
            for (int i = 0; i < maxStoreCards; i++)
            {
                CreateUpgradeSpinOfferCard(cardSpinRowGo.transform, i, out cardRoots[i], out cardRarityFrameImages[i], out cardBgImages[i], out cardIconImages[i], out cardTitleTexts[i], out cardLevelTexts[i], out cardRarityLabels[i], out cardDescTexts[i], out cardButtons[i]);
                if (cardRoots[i] != null)
                    cardRoots[i].AddComponent<ScrollRectForwarder>();
                int idx = i;
                cardButtons[i].onClick.AddListener(() => OnTakeSpinOffer(idx));
            }
            _upgradeSpinRowUsesTallLayout = true;

            var cardsLayoutEl = cardsTabContent.AddComponent<LayoutElement>();
            cardsLayoutEl.flexibleHeight = 0f;
            cardsLayoutEl.flexibleWidth = 1f;
            var cardsTabFitter = cardsTabContent.AddComponent<ContentSizeFitter>();
            cardsTabFitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            cardsTabFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            ApplyMoonDockUpgradeCardsSectionHeight();

            // Ships tab content — fixed list of ship slots (like Cards tab), populated when tab is shown
            shipsTabContent = new GameObject("ShipsTabContent");
            shipsTabContent.transform.SetParent(storeContentRoot, false);
            var shipsContentRect = shipsTabContent.AddComponent<RectTransform>();
            shipsContentRect.anchorMin = new Vector2(0f, 1f);
            shipsContentRect.anchorMax = new Vector2(1f, 1f);
            shipsContentRect.pivot = new Vector2(0.5f, 1f);
            shipsContentRect.offsetMin = Vector2.zero;
            shipsContentRect.offsetMax = Vector2.zero;
            chassisButtons = new Button[MaxShipCards];
            chassisLabels = new TextMeshProUGUI[MaxShipCards];
            shipUnlockEntries = new ShipUnlockEntry[MaxShipCards];
            EnsureShipUpgradeTreeInstance(shipsTabContent.transform);

            shipsRowsContainer = null;

            float shipsContentHeight = Mathf.Max(820f, MaxShipCards * 40f + 60f);
            _shipsContentHeight = shipsContentHeight;
            var shipsLayoutEl = shipsTabContent.AddComponent<LayoutElement>();
            shipsLayoutEl.preferredHeight = shipsContentHeight;
            shipsLayoutEl.flexibleWidth = 1f;

            storeContentRoot.sizeDelta = new Vector2(0f, Mathf.Max(_cardsContentHeight, shipsContentHeight, 600f));

            // Vertical scrollbar for store + scroll up/down buttons
            const float scrollBtnHeight = 28f;
            var scrollbarGo = new GameObject("StoreScrollbar");
            scrollbarGo.transform.SetParent(storePanel.transform, false);
            var scrollbarRect = scrollbarGo.AddComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 1f);
            scrollbarRect.anchoredPosition = Vector2.zero;
            scrollbarRect.offsetMin = new Vector2(-20f, 12f + scrollBtnHeight);
            scrollbarRect.offsetMax = new Vector2(-12f, storeY - scrollBtnHeight);
            var scrollbar = scrollbarGo.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            var scrollbarBg = scrollbarGo.AddComponent<Image>();
            scrollbarBg.color = new Color(0.1f, 0.12f, 0.18f, 0.8f);
            var scrollbarHandleArea = new GameObject("Sliding Area");
            scrollbarHandleArea.transform.SetParent(scrollbarGo.transform, false);
            var handleAreaRect = scrollbarHandleArea.AddComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = new Vector2(2f, 2f);
            handleAreaRect.offsetMax = new Vector2(-2f, -2f);
            var scrollbarHandle = new GameObject("Handle");
            scrollbarHandle.transform.SetParent(scrollbarHandleArea.transform, false);
            var handleRect = scrollbarHandle.AddComponent<RectTransform>();
            handleRect.anchorMin = new Vector2(0f, 0f);
            handleRect.anchorMax = new Vector2(1f, 1f);
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;
            var handleImg = scrollbarHandle.AddComponent<Image>();
            handleImg.color = new Color(0.35f, 0.45f, 0.65f, 0.95f);
            if (buttonSprite != null) { handleImg.sprite = buttonSprite; handleImg.type = Image.Type.Sliced; }
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImg;
            storeScrollRect.verticalScrollbar = scrollbar;
            storeScrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            // Scroll Up / Scroll Down buttons (reliable scroll without wheel/drag)
            Button storeScrollUpBtn = CreateStoreScrollButton(storePanel.transform, "▲", true, storeY, scrollBtnHeight);
            Button storeScrollDownBtn = CreateStoreScrollButton(storePanel.transform, "▼", false, storeY, scrollBtnHeight);
            if (storeScrollUpBtn != null) storeScrollUpBtn.onClick.AddListener(OnStoreScrollUp);
            if (storeScrollDownBtn != null) storeScrollDownBtn.onClick.AddListener(OnStoreScrollDown);

            RefreshStoreTabVisibility();
            RefreshShipsTab();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);
            if (canvas != null) Canvas.ForceUpdateCanvases();

            EnsureMoonDockChromeExists();
        }

        private void RefreshStoreTabVisibility()
        {
            if (cardsTabContent == null || shipsTabContent == null) return;

            if (_moonDockLayoutActive)
            {
                _lastActiveStoreTab = activeStoreTab;
                cardsTabContent.SetActive(activeStoreTab == 0);
                shipsTabContent.SetActive(activeStoreTab == 1);
                if (activeStoreTab == 1)
                    EnsureShipsTabPopulated();
                return;
            }

            bool tabChanged = _lastActiveStoreTab != activeStoreTab;
            _lastActiveStoreTab = activeStoreTab;

            cardsTabContent.SetActive(activeStoreTab == 0);
            shipsTabContent.SetActive(activeStoreTab == 1);

            if (activeStoreTab == 1)
                EnsureShipsTabPopulated();

            if (storeScrollRect != null && storeScrollRect.content != null)
            {
                if (tabChanged)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(storeScrollRect.content);
                    Canvas.ForceUpdateCanvases();
                    RectTransform content = storeScrollRect.content;
                    RectTransform viewport = storeScrollRect.viewport;
                    if (viewport != null && content != null)
                    {
                        float viewportHeight = viewport.rect.height;
                        float contentHeight = activeStoreTab == 0 ? _cardsContentHeight : _shipsContentHeight;
                        float minContentHeight = viewportHeight + 50f;
                        content.sizeDelta = new Vector2(0f, Mathf.Max(contentHeight, minContentHeight));
                    }
                    if (activeStoreTab == 0)
                        storeScrollRect.verticalNormalizedPosition = 1f;
                    else
                        ScrollStoreToCurrentShipTreeNode();
                }
            }
        }

        private void EnsureShipsTabPopulated()
        {
            EnsurePanelExists();
            RefreshShipsTab(scrollToActiveShipNode: false);
        }

        private Button CreateShipSlotButton(Transform parent, ref float y)
        {
            var go = new GameObject("ShipSlot");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-24f, 36f);
            y -= 40f;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.15f, 0.28f, 0.5f, 0.95f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            var btn = go.AddComponent<Button>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 4f);
            textRect.offsetMax = new Vector2(-8f, -4f);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = 14;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.color = Color.white;
            tmp.overflowMode = TextOverflowModes.Truncate;
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
            go.SetActive(false);
            go.AddComponent<ScrollRectForwarder>();
            return btn;
        }

        private GameObject CreateUpgradeShipRow(Transform parent, ref float y)
        {
            var go = new GameObject("UpgradeShipRow");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-24f, 36f);
            y -= 40f;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.45f, 0.35f, 0.95f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            var btn = go.AddComponent<Button>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 4f);
            textRect.offsetMax = new Vector2(-8f, -4f);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = 14;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
            go.SetActive(false);
            go.AddComponent<ScrollRectForwarder>();
            return go;
        }

        private float GetOrbitStationPanelWidth()
        {
            var rt = transform as RectTransform;
            if (rt == null) return Mathf.Max(PanelWidth, SlotPanelWidthConst);
            return Mathf.Max(1f, rt.rect.width);
        }

        /// <summary>Width available for laying out the ship tree (row / ships tab / orbit, widest reliable source).</summary>
        private float GetShipTreeLayoutBasisWidth()
        {
            if (_moonDockLayoutActive && _moonDockShipTreeHorizontal)
            {
                if (shipUpgradeTree != null)
                {
                    var treeRt = (RectTransform)shipUpgradeTree.transform;
                    LayoutRebuilder.ForceRebuildLayoutImmediate(treeRt);
                    Canvas.ForceUpdateCanvases();
                    if (treeRt.rect.width > 80f)
                        return treeRt.rect.width;
                }

                if (moonDockCenterShipsHost != null && moonDockCenterShipsHost.gameObject.activeInHierarchy)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(moonDockCenterShipsHost);
                    Canvas.ForceUpdateCanvases();
                    float w = moonDockCenterShipsHost.rect.width;
                    if (w > 80f)
                        return Mathf.Max(120f, w - 24f);
                }
            }
            if (shipTreeCenterRow != null && shipTreeCenterRow.rect.width > 8f)
                return shipTreeCenterRow.rect.width;
            if (shipsTabContent != null)
            {
                var srt = shipsTabContent.GetComponent<RectTransform>();
                if (srt != null && srt.rect.width > 8f)
                    return srt.rect.width;
            }
            return Mathf.Max(160f, GetOrbitStationPanelWidth() - 52f);
        }

        /// <summary>Tree canvas width and per-node width from basis ÷ 6 columns + gaps (orbit menu scales → tree reflows).</summary>
        private void ComputeShipTreeGeometry(out float treeCanvasWidth, out float nodeWidth, out float innerLayoutWidth)
        {
            int cols = ShipTreeMaxColumns;
            var rowRt = shipTreeCenterRow != null ? shipTreeCenterRow : null;
            float rowHlgPad = 0f;
            if (rowRt != null)
            {
                var hlg = rowRt.GetComponent<HorizontalLayoutGroup>();
                if (hlg != null)
                    rowHlgPad = hlg.padding.left + hlg.padding.right;
            }
            float basis = GetShipTreeLayoutBasisWidth();
            float usable = Mathf.Max(120f, basis - rowHlgPad);
            nodeWidth = (usable - (cols - 1) * ShipTreeColGap) / cols;
            nodeWidth = Mathf.Max(52f, nodeWidth);
            innerLayoutWidth = cols * nodeWidth + (cols - 1) * ShipTreeColGap;
            treeCanvasWidth = innerLayoutWidth + 2f * ShipTreeCanvasInnerMargin;
        }

        /// <summary>Sets tree canvas width to exactly 6 columns + gaps (+ inner margin); centered in the row, not stretched.</summary>
        private void ApplyShipTreeCanvasWidth()
        {
            if (_moonDockShipTreeHorizontal) return;
            if (shipTreeCanvas == null || shipsTabContent == null) return;
            var parentRt = shipsTabContent.GetComponent<RectTransform>();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);
            if (shipTreeCenterRow != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(shipTreeCenterRow);
            ComputeShipTreeGeometry(out float treeW, out _, out _);
            float h = shipTreeCanvas.sizeDelta.y;
            shipTreeCanvas.sizeDelta = new Vector2(treeW, h);
            var le = shipTreeCanvas.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.preferredWidth = treeW;
                le.minWidth = treeW;
                le.flexibleWidth = 0f;
            }
            if (shipTreeCenterRow != null)
            {
                shipTreeCenterRow.sizeDelta = new Vector2(0f, Mathf.Max(h, 400f));
                var rowLe = shipTreeCenterRow.GetComponent<LayoutElement>();
                if (rowLe != null) rowLe.preferredHeight = Mathf.Max(h, 400f);
            }
        }

        private void CheckShipTreeLayoutBasisChanged()
        {
            if (shipTreeCanvas == null || shipsTabContent == null) return;
            float b = GetShipTreeLayoutBasisWidth();
            int bucket = Mathf.RoundToInt(b / 32f);
            if (bucket == _cachedShipTreeWidthBucket)
                return;
            _cachedShipTreeWidthBucket = bucket;
            _cachedShipTreeBasisWidth = b;
            if (UpgradeSystem.Instance != null && currentShip != null && currentPlanet != null && CardShopSystem.Instance != null)
                RefreshShipsTab(false);
        }

        /// <summary>Height of the tree canvas only (matches <see cref="BuildShipUpgradeTreeVisualFull"/>).</summary>
        private static float ComputeShipTreeCanvasContentHeight()
        {
            const int maxLevel = 7;
            float margin = 8f;
            return Mathf.Max(160f, margin * 2f + (maxLevel - 1) * ShipTreeLevelSpacing + ShipTreeNodeHeight);
        }

        /// <summary>
        /// Ships tab was created with a fixed ~820px height; the tree is taller — sync LayoutElement + scroll content
        /// so the store ScrollRect can reach the bottom.
        /// </summary>
        private void UpdateShipsTabContentHeight()
        {
            if (shipsTabContent == null) return;
            if (_moonDockShipTreeHorizontal && _moonDockLayoutActive)
            {
                var shipsLayoutElQuick = shipsTabContent.GetComponent<LayoutElement>();
                if (shipsLayoutElQuick != null)
                {
                    shipsLayoutElQuick.flexibleHeight = 1f;
                    shipsLayoutElQuick.minHeight = 280f;
                }
                return;
            }
            var shipsRt = shipsTabContent.GetComponent<RectTransform>();
            LayoutRebuilder.ForceRebuildLayoutImmediate(shipsRt);
            if (shipUpgradeTree != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)shipUpgradeTree.transform);
            else if (shipTreeCenterRow != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(shipTreeCenterRow);
            if (shipTreeCanvas != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(shipTreeCanvas);
            Canvas.ForceUpdateCanvases();

            float treeH = 0f;
            if (shipUpgradeTree != null)
                treeH = ((RectTransform)shipUpgradeTree.transform).rect.height;
            if (treeH < 1f && shipTreeCanvas != null)
                treeH = shipTreeCanvas.sizeDelta.y;
            if (treeH < 1f)
                treeH = ComputeShipTreeCanvasContentHeight();

            // Section title + hint + tree block
            const float analyticHeaderAndGap = 88f;
            const float analyticBottomSlack = 40f;
            float analytic = analyticHeaderAndGap + treeH + analyticBottomSlack;

            // Measured: union axis-aligned bounds of each top-level child in ships-tab space (includes connectors under canvas)
            float measured = MeasureShipsTabUnionHeight(shipsRt, shipTreeCanvas);
            if (measured > 0.5f)
                measured += 72f; // padding below last pixel (scrollbar thumb, layout rounding)

            float preferred = Mathf.Max(analytic, measured, 400f);

            var shipsLayoutEl = shipsTabContent.GetComponent<LayoutElement>();
            if (shipsLayoutEl != null)
            {
                shipsLayoutEl.preferredHeight = preferred;
                shipsLayoutEl.minHeight = preferred;
            }
            _shipsContentHeight = preferred;

            if (storeContentRoot == null) return;
            float h = Mathf.Max(_cardsContentHeight, _shipsContentHeight, 600f);
            if (storeScrollRect != null && storeScrollRect.viewport != null)
            {
                float vh = storeScrollRect.viewport.rect.height;
                if (vh > 1f)
                    h = Mathf.Max(h, vh + 50f);
            }
            storeContentRoot.sizeDelta = new Vector2(0f, h);
        }

        /// <summary>
        /// Unions bounds of each direct child of the ships tab, plus the tree canvas again so connector lines
        /// that extend past the row/canvas RectTransform are included.
        /// </summary>
        private static float MeasureShipsTabUnionHeight(RectTransform shipsRt, RectTransform shipTreeCanvas)
        {
            if (shipsRt == null) return 0f;
            bool first = true;
            Bounds combined = default;
            void Encapsulate(RectTransform child)
            {
                if (child == null || !child.gameObject.activeInHierarchy) return;
                Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(shipsRt, child);
                if (first)
                {
                    combined = b;
                    first = false;
                }
                else
                    combined.Encapsulate(b);
            }
            for (int i = 0; i < shipsRt.childCount; i++)
                Encapsulate(shipsRt.GetChild(i) as RectTransform);
            Encapsulate(shipTreeCanvas);
            return first ? 0f : combined.size.y;
        }

        /// <summary>Scrolls the store viewport so the current ship's tree node is visible (centers when possible).</summary>
        private void ScrollStoreToCurrentShipTreeNode()
        {
            if (_moonDockLayoutActive && _moonDockShipTreeHorizontal) return;
            if (activeStoreTab != 1 || storeScrollRect == null || storeContentRoot == null || storeScrollRect.viewport == null)
                return;
            if (currentShip == null || shipUpgradeTree == null) return;

            ShipUpgradeTreeNodeUI target = shipUpgradeTree.CurrentShipDisplayNode;
            if (target?.Rect == null)
            {
                int curL = currentShip.ShipLevel;
                int curB = currentShip.BranchIndex;
                for (int i = 0; i < shipUpgradeTree.Nodes.Count; i++)
                {
                    var v = shipUpgradeTree.Nodes[i];
                    if (v != null && !v.IsCurrentShipDisplay && v.Level == curL && v.BranchIndex == curB)
                    {
                        target = v;
                        break;
                    }
                }
            }
            if (target?.Rect == null) return;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);

            RectTransform viewport = storeScrollRect.viewport;
            RectTransform content = storeContentRoot;
            RectTransform node = target.Rect;

            for (int iter = 0; iter < 6; iter++)
            {
                Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, node);
                Rect vr = viewport.rect;
                float dy = 0f;
                if (b.max.y > vr.yMax) dy = vr.yMax - b.max.y;
                else if (b.min.y < vr.yMin) dy = vr.yMin - b.min.y;
                else break;

                Vector2 ap = content.anchoredPosition;
                ap.y += dy;
                float maxScroll = Mathf.Max(0f, content.rect.height - viewport.rect.height);
                ap.y = Mathf.Clamp(ap.y, -maxScroll, 0f);
                content.anchoredPosition = ap;
            }

            storeScrollRect.velocity = Vector2.zero;
        }

        /// <param name="scrollToActiveShipNode">When true, scrolls the store viewport to the current ship node (e.g. opening the panel on Ships tab). Avoid true on periodic refreshes so manual scrolling is not overridden.</param>
        private void RefreshShipsTab(bool scrollToActiveShipNode = false)
        {
            if (shipUpgradeTree == null)
                return;

            ApplyShipUpgradeTreeContainerLayout();
            if (shipsTabContent != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(shipsTabContent.GetComponent<RectTransform>());
            if (moonDockCenterShipsHost != null && _moonDockLayoutActive)
                LayoutRebuilder.ForceRebuildLayoutImmediate(moonDockCenterShipsHost);

            if (!IsTreeDataAvailable())
            {
                _shipTreeStructureKey = "";
                shipUpgradeTree.Clear();
                if (shipUpgradeTree.Hint != null)
                    shipUpgradeTree.Hint.text = "Upgrade tree unavailable.";
                UpdateShipsTabContentHeight();
                return;
            }

            string expectedStructureKey = _moonDockShipTreeHorizontal ? MoonDockShipTreeStructureKey : ShipTreeStructureKey;
            shipUpgradeTree.RebuildIfNeeded(_moonDockShipTreeHorizontal, expectedStructureKey);
            _shipTreeStructureKey = expectedStructureKey;

            UpdateShipsTabContentHeight();
            ApplyMoonDockShipTreeRowLayout();
            if (storeContentRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);
            Canvas.ForceUpdateCanvases();
            if (scrollToActiveShipNode)
                ScrollStoreToCurrentShipTreeNode();
        }

        private void ApplyMoonDockShipTreeRowLayout()
        {
            if (shipTreeCenterRow == null) return;
            var hlg = shipTreeCenterRow.GetComponent<HorizontalLayoutGroup>();
            if (hlg == null) return;
            if (_moonDockLayoutActive && _moonDockShipTreeHorizontal)
            {
                shipTreeCenterRow.anchorMin = Vector2.zero;
                shipTreeCenterRow.anchorMax = Vector2.one;
                shipTreeCenterRow.pivot = new Vector2(0.5f, 0.5f);
                shipTreeCenterRow.offsetMin = Vector2.zero;
                shipTreeCenterRow.offsetMax = Vector2.zero;
                shipTreeCenterRow.anchoredPosition = Vector2.zero;
                hlg.childAlignment = TextAnchor.MiddleCenter;
                hlg.padding = new RectOffset(0, 0, 0, 0);
                hlg.childForceExpandWidth = true;
                hlg.childControlWidth = true;
                return;
            }

            // Orbit menu tree should stay centered in the parent panel regardless of aspect ratio.
            shipTreeCenterRow.anchorMin = new Vector2(0f, 0.5f);
            shipTreeCenterRow.anchorMax = new Vector2(1f, 0.5f);
            shipTreeCenterRow.pivot = new Vector2(0.5f, 0.5f);
            shipTreeCenterRow.anchoredPosition = Vector2.zero;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.padding = new RectOffset(0, 0, 0, 0);
        }

        private string GetShipDisplayName(ShipUpgradeNode node, int level, int branchIndex)
        {
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (currentShip != null && storePlanet != null && CardShopSystem.Instance != null)
            {
                string treeName = CardShopSystem.Instance.GetUpgradeTreeShipNameForUpgradeSlot(currentShip, storePlanet.PlanetId, level, branchIndex);
                if (!string.IsNullOrEmpty(treeName))
                    return treeName.Trim();
            }
            if (node != null)
            {
                if (!string.IsNullOrEmpty(node.shipName)) return node.shipName.Trim();
                if (node.shipData != null && !string.IsNullOrEmpty(node.shipData.shipName)) return node.shipData.shipName.Trim();
            }
            return "Unassigned";
        }

        private Sprite ResolveShipTreePreviewSprite(int level, int branchIndex)
        {
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (currentShip == null || storePlanet == null || CardShopSystem.Instance == null) return null;
            TeamManager.Team team = currentShip.ShipTeam;
            return CardShopSystem.Instance.GetMenuPreviewSpriteForUpgradeSlot(
                currentShip, storePlanet.PlanetId, level, branchIndex, team);
        }

        private Sprite ResolveCurrentShipPreviewSprite()
        {
            if (currentShip == null || CardShopSystem.Instance == null) return null;
            return CardShopSystem.Instance.GetMenuPreviewSpriteForChassisId(currentShip.CurrentChassisId, currentShip.ShipTeam);
        }

        private string GetCurrentShipDisplayName()
        {
            if (currentShip != null && CardShopSystem.Instance != null && !string.IsNullOrEmpty(currentShip.CurrentChassisId))
            {
                string treeName = CardShopSystem.Instance.GetUpgradeTreeShipNameForChassisId(currentShip.CurrentChassisId);
                if (!string.IsNullOrEmpty(treeName))
                    return treeName.Trim();
                ShipChassisDefinition ch = CardShopSystem.Instance.GetChassisDefinitionByChassisId(currentShip.CurrentChassisId);
                if (ch != null && !string.IsNullOrEmpty(ch.displayName))
                    return ch.displayName.Trim();
            }
            if (currentShip != null && currentShip.CurrentShipData != null && !string.IsNullOrEmpty(currentShip.CurrentShipData.shipName))
                return currentShip.CurrentShipData.shipName.Trim();
            return "Your Ship";
        }

        private void CreateSectionLabel(Transform parent, string name, string text, ref float y)
        {
            CreateSectionLabelWithRef(parent, name, text, ref y);
        }

        private TextMeshProUGUI CreateSectionLabelWithRef(Transform parent, string name, string text, ref float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-24f, 24f);
            y -= 28f;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 18;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = new Color(0.7f, 0.85f, 1f, 1f);
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.enableWordWrapping = false;
            if (fontAsset != null) tmp.font = fontAsset;
            return tmp;
        }

        private void CreateRowLabel(Transform parent, string text, ref float y)
        {
            var go = new GameObject("Row_" + text);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-24f, 18f);
            y -= 22f;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 14;
            tmp.color = new Color(0.85f, 0.9f, 1f, 0.95f);
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
        }

        /// <summary>Title + muted subtitle for cards/ships moon-dock sections (stacked from top).</summary>
        private void CreateSectionHeaderPair(Transform parent, string title, string subtitle, ref float y)
        {
            var titleGo = new GameObject("SectionHeaderTitle");
            titleGo.transform.SetParent(parent, false);
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, y);
            titleRt.sizeDelta = new Vector2(-24f, 34f);
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = title;
            titleTmp.fontSize = 22f;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = new Color(0.94f, 0.96f, 1f, 1f);
            titleTmp.alignment = TextAlignmentOptions.Left;
            titleTmp.raycastTarget = false;
            if (fontAsset != null) titleTmp.font = fontAsset;
            y -= 32f;

            var subGo = new GameObject("SectionHeaderSub");
            subGo.transform.SetParent(parent, false);
            var subRt = subGo.AddComponent<RectTransform>();
            subRt.anchorMin = new Vector2(0f, 1f);
            subRt.anchorMax = new Vector2(1f, 1f);
            subRt.pivot = new Vector2(0.5f, 1f);
            subRt.anchoredPosition = new Vector2(0f, y);
            subRt.sizeDelta = new Vector2(-24f, 44f);
            var subTmp = subGo.AddComponent<TextMeshProUGUI>();
            subTmp.text = subtitle;
            subTmp.fontSize = 13f;
            subTmp.color = new Color(0.78f, 0.86f, 0.96f, 0.98f);
            subTmp.alignment = TextAlignmentOptions.TopLeft;
            subTmp.enableWordWrapping = true;
            subTmp.raycastTarget = false;
            if (fontAsset != null) subTmp.font = fontAsset;
            y -= 46f;
        }

        /// <summary>Creates a roomy slot card: level bubble (top-right), title (top-center), description (bottom-center), bg + highlighted border by slot type; small delete control (top-left).</summary>
        private void CreateSlotBoxForGrid(Transform gridParent, float cellW, float cellH, int index, out GameObject boxRoot, out Image bgImage, out Image borderImage, out TextMeshProUGUI levelText, out TextMeshProUGUI titleText, out TextMeshProUGUI descText, out Button deleteButton)
        {
            boxRoot = new GameObject("SlotBox_" + (index + 1));
            boxRoot.transform.SetParent(gridParent, false);
            var rect = boxRoot.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(cellW, cellH);

            bgImage = boxRoot.AddComponent<Image>();
            bgImage.color = new Color(0.18f, 0.22f, 0.32f, 0.95f);
            if (buttonSprite != null) { bgImage.sprite = buttonSprite; bgImage.type = Image.Type.Sliced; }
            bgImage.raycastTarget = false;

            var borderGo = new GameObject("Border");
            borderGo.transform.SetParent(boxRoot.transform, false);
            var borderRect = borderGo.AddComponent<RectTransform>();
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = new Vector2(-2f, -2f);
            borderRect.offsetMax = new Vector2(2f, 2f);
            borderImage = borderGo.AddComponent<Image>();
            borderImage.color = new Color(0.4f, 0.5f, 0.7f, 0.9f);
            borderImage.raycastTarget = false;
            borderImage.enabled = true;

            var levelBubbleGo = new GameObject("LevelBubble");
            levelBubbleGo.transform.SetParent(boxRoot.transform, false);
            var levelBubbleRect = levelBubbleGo.AddComponent<RectTransform>();
            levelBubbleRect.anchorMin = new Vector2(1f, 1f);
            levelBubbleRect.anchorMax = new Vector2(1f, 1f);
            levelBubbleRect.pivot = new Vector2(1f, 1f);
            levelBubbleRect.anchoredPosition = new Vector2(-4f, -4f);
            levelBubbleRect.sizeDelta = new Vector2(22f, 22f);
            var bubbleBg = levelBubbleGo.AddComponent<Image>();
            bubbleBg.color = new Color(0.15f, 0.2f, 0.35f, 0.95f);
            var levelTextGo = new GameObject("LevelText");
            levelTextGo.transform.SetParent(levelBubbleGo.transform, false);
            var levelTextRect = levelTextGo.AddComponent<RectTransform>();
            levelTextRect.anchorMin = Vector2.zero;
            levelTextRect.anchorMax = Vector2.one;
            levelTextRect.offsetMin = Vector2.zero;
            levelTextRect.offsetMax = Vector2.zero;
            levelText = levelTextGo.AddComponent<TextMeshProUGUI>();
            levelText.text = "—";
            levelText.fontSize = 11;
            levelText.alignment = TextAlignmentOptions.Center;
            levelText.color = new Color(0.9f, 0.95f, 1f, 1f);
            if (fontAsset != null) levelText.font = fontAsset;
            levelText.raycastTarget = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(boxRoot.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(14f, -6f);
            titleRect.sizeDelta = new Vector2(-64f, -24f);
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.text = "Empty";
            titleText.fontSize = 12;
            titleText.alignment = TextAlignmentOptions.Top;
            titleText.enableAutoSizing = true;
            titleText.fontSizeMin = 8;
            titleText.fontSizeMax = 13;
            titleText.color = new Color(0.95f, 0.97f, 1f, 0.98f);
            titleText.overflowMode = TextOverflowModes.Ellipsis;
            if (fontAsset != null) titleText.font = fontAsset;

            var descGo = new GameObject("Desc");
            descGo.transform.SetParent(boxRoot.transform, false);
            var descRect = descGo.AddComponent<RectTransform>();
            descRect.anchorMin = new Vector2(0f, 0f);
            descRect.anchorMax = new Vector2(1f, 0.5f);
            descRect.offsetMin = new Vector2(6f, 4f);
            descRect.offsetMax = new Vector2(-6f, 4f);
            descText = descGo.AddComponent<TextMeshProUGUI>();
            descText.text = "";
            descText.fontSize = 9;
            descText.alignment = TextAlignmentOptions.Bottom;
            descText.enableWordWrapping = true;
            descText.enableAutoSizing = true;
            descText.fontSizeMin = 6;
            descText.fontSizeMax = 10;
            descText.color = new Color(0.75f, 0.82f, 0.9f, 0.92f);
            descText.overflowMode = TextOverflowModes.Ellipsis;
            if (fontAsset != null) descText.font = fontAsset;

            var delGo = new GameObject("Delete");
            delGo.transform.SetParent(boxRoot.transform, false);
            var delRt = delGo.AddComponent<RectTransform>();
            delRt.anchorMin = new Vector2(0f, 1f);
            delRt.anchorMax = new Vector2(0f, 1f);
            delRt.pivot = new Vector2(0f, 1f);
            delRt.anchoredPosition = new Vector2(4f, -4f);
            delRt.sizeDelta = new Vector2(22f, 22f);
            var delImg = delGo.AddComponent<Image>();
            delImg.color = new Color(0.42f, 0.18f, 0.2f, 0.96f);
            if (buttonSprite != null) { delImg.sprite = buttonSprite; delImg.type = Image.Type.Sliced; }
            deleteButton = delGo.AddComponent<Button>();
            var delTxtGo = new GameObject("Text");
            delTxtGo.transform.SetParent(delGo.transform, false);
            var delTxtRect = delTxtGo.AddComponent<RectTransform>();
            delTxtRect.anchorMin = Vector2.zero;
            delTxtRect.anchorMax = Vector2.one;
            delTxtRect.offsetMin = Vector2.zero;
            delTxtRect.offsetMax = Vector2.zero;
            var delTmp = delTxtGo.AddComponent<TextMeshProUGUI>();
            delTmp.text = "×";
            delTmp.fontSize = 16;
            delTmp.alignment = TextAlignmentOptions.Center;
            delTmp.color = new Color(1f, 0.92f, 0.92f, 1f);
            if (fontAsset != null) delTmp.font = fontAsset;
            delTmp.raycastTarget = false;
            deleteButton.gameObject.SetActive(false);
        }

        private void SetEquipmentSlotLayoutMode(bool richLayout)
        {
            if (equipmentGridRoot == null)
                return;

            if (_equipmentSlotRichLayoutActive == richLayout && equipmentBoxes != null && equipmentBoxes[0] != null)
                return;

            _equipmentSlotRichLayoutActive = richLayout;
            _sidebarEquipmentSlotUi = richLayout ? new SidebarEquipmentSlotUi[MaxSlotRows] : null;

            for (int i = 0; i < MaxSlotRows; i++)
            {
                if (equipmentBoxes != null && equipmentBoxes[i] != null)
                    Destroy(equipmentBoxes[i]);
                if (equipmentBoxes != null)
                    equipmentBoxes[i] = null;
            }

            equipmentBoxes = new GameObject[MaxSlotRows];
            equipmentBgImages = new Image[MaxSlotRows];
            equipmentBorderImages = new Image[MaxSlotRows];
            equipmentChargeTexts = new TextMeshProUGUI[MaxSlotRows];
            equipmentTitleTexts = new TextMeshProUGUI[MaxSlotRows];
            equipmentDescTexts = new TextMeshProUGUI[MaxSlotRows];
            equipmentDeleteButtons = new Button[MaxSlotRows];

            for (int i = 0; i < MaxSlotRows; i++)
            {
                int idx = i;
                if (richLayout)
                {
                    CreateSidebarEquipmentSlotCard(
                        equipmentGridRoot.transform,
                        i,
                        out equipmentBoxes[i],
                        out equipmentBgImages[i],
                        out equipmentTitleTexts[i],
                        out equipmentDescTexts[i],
                        out equipmentDeleteButtons[i],
                        out _sidebarEquipmentSlotUi[i]);
                }
                else
                {
                    CreateSlotBoxForGrid(
                        equipmentGridRoot.transform,
                        SlotCardWidth,
                        SlotCardHeight,
                        i,
                        out equipmentBoxes[i],
                        out equipmentBgImages[i],
                        out equipmentBorderImages[i],
                        out equipmentChargeTexts[i],
                        out equipmentTitleTexts[i],
                        out equipmentDescTexts[i],
                        out equipmentDeleteButtons[i]);
                }

                if (equipmentDeleteButtons[i] != null)
                    equipmentDeleteButtons[i].onClick.AddListener(() => ShowEquipmentRemoveConfirm(idx));
            }
        }

        private void CreateSidebarEquipmentSlotCard(
            Transform gridParent,
            int index,
            out GameObject boxRoot,
            out Image bgImage,
            out TextMeshProUGUI titleText,
            out TextMeshProUGUI descText,
            out Button deleteButton,
            out SidebarEquipmentSlotUi slotUi)
        {
            slotUi = new SidebarEquipmentSlotUi();
            float trackWidth = Mathf.Max(40f, SidebarSlotCardWidth - 22f);

            boxRoot = new GameObject("EquipmentSlot_" + (index + 1));
            boxRoot.transform.SetParent(gridParent, false);
            var cardLe = boxRoot.AddComponent<LayoutElement>();
            cardLe.flexibleWidth = 0f;
            cardLe.flexibleHeight = 0f;
            cardLe.preferredWidth = SidebarSlotCardWidth;
            cardLe.minWidth = SidebarSlotCardWidth;
            cardLe.preferredHeight = SidebarEquipmentSlotCardHeight;
            cardLe.minHeight = SidebarEquipmentSlotCardHeight;

            bgImage = boxRoot.AddComponent<Image>();
            bgImage.color = MoonDockEquipmentCardBg;
            bgImage.raycastTarget = false;
            var cardOutline = boxRoot.AddComponent<Outline>();
            cardOutline.effectColor = MoonDockStoreCardFrameColor;
            cardOutline.effectDistance = new Vector2(1f, 1f);

            var cardVlg = boxRoot.AddComponent<VerticalLayoutGroup>();
            cardVlg.spacing = 3f;
            cardVlg.padding = new RectOffset(4, 4, 5, 4);
            cardVlg.childAlignment = TextAnchor.UpperCenter;
            cardVlg.childControlWidth = true;
            cardVlg.childControlHeight = true;
            cardVlg.childForceExpandWidth = true;
            cardVlg.childForceExpandHeight = false;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(boxRoot.transform, false);
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.preferredHeight = 4f;
            accentLe.minHeight = 4f;
            slotUi.accentImage = accentGo.AddComponent<Image>();
            slotUi.accentImage.color = SidebarEquipmentEmptyAccent;
            slotUi.accentImage.raycastTarget = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(boxRoot.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 14f;
            titleLe.minHeight = 12f;
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.text = "Empty";
            titleText.fontSize = 10f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = Color.white;
            titleText.overflowMode = TextOverflowModes.Ellipsis;
            titleText.raycastTarget = false;
            if (fontAsset != null) titleText.font = fontAsset;

            slotUi.iconRoot = new GameObject("Icon");
            slotUi.iconRoot.transform.SetParent(boxRoot.transform, false);
            var iconLe = slotUi.iconRoot.AddComponent<LayoutElement>();
            iconLe.flexibleHeight = 0f;
            iconLe.minHeight = SidebarEquipmentIconMinHeight;
            iconLe.preferredHeight = SidebarEquipmentIconHeight;
            slotUi.iconImage = slotUi.iconRoot.AddComponent<Image>();
            slotUi.iconImage.color = new Color(1f, 1f, 1f, 0f);
            slotUi.iconImage.preserveAspect = true;
            slotUi.iconImage.raycastTarget = false;

            var glyphGo = new GameObject("Glyph");
            glyphGo.transform.SetParent(slotUi.iconRoot.transform, false);
            var glyphRt = glyphGo.AddComponent<RectTransform>();
            glyphRt.anchorMin = Vector2.zero;
            glyphRt.anchorMax = Vector2.one;
            glyphRt.offsetMin = Vector2.zero;
            glyphRt.offsetMax = Vector2.zero;
            slotUi.iconGlyph = glyphGo.AddComponent<TextMeshProUGUI>();
            slotUi.iconGlyph.fontSize = 34f;
            slotUi.iconGlyph.alignment = TextAlignmentOptions.Center;
            slotUi.iconGlyph.color = new Color(1f, 1f, 1f, 0.95f);
            slotUi.iconGlyph.raycastTarget = false;
            if (fontAsset != null) slotUi.iconGlyph.font = fontAsset;

            slotUi.statsFooter = new GameObject("StatsFooter");
            slotUi.statsFooter.transform.SetParent(boxRoot.transform, false);
            var statsFooterLe = slotUi.statsFooter.AddComponent<LayoutElement>();
            statsFooterLe.flexibleHeight = 0f;
            statsFooterLe.minHeight = SidebarEquipmentStatsFooterHeight;
            statsFooterLe.preferredHeight = SidebarEquipmentStatsFooterHeight;
            var statsFooterBg = slotUi.statsFooter.AddComponent<Image>();
            statsFooterBg.color = MoonDockEquipmentStatsFooterBg;
            statsFooterBg.raycastTarget = false;
            var statsFooterVlg = slotUi.statsFooter.AddComponent<VerticalLayoutGroup>();
            statsFooterVlg.spacing = 2f;
            statsFooterVlg.padding = new RectOffset(4, 4, 4, 4);
            statsFooterVlg.childAlignment = TextAnchor.UpperCenter;
            statsFooterVlg.childControlWidth = true;
            statsFooterVlg.childControlHeight = true;
            statsFooterVlg.childForceExpandWidth = true;
            statsFooterVlg.childForceExpandHeight = false;

            var descriptionGo = new GameObject("Description");
            descriptionGo.transform.SetParent(slotUi.statsFooter.transform, false);
            var descriptionLe = descriptionGo.AddComponent<LayoutElement>();
            descriptionLe.flexibleHeight = 0f;
            descriptionLe.minHeight = SidebarEquipmentAbilityAreaHeight;
            descriptionLe.preferredHeight = SidebarEquipmentAbilityAreaHeight;
            descText = descriptionGo.AddComponent<TextMeshProUGUI>();
            descText.text = string.Empty;
            descText.fontSize = SidebarEquipmentAbilityFontSize;
            descText.alignment = TextAlignmentOptions.Top;
            descText.color = new Color(0.92f, 0.95f, 1f, 0.92f);
            descText.enableWordWrapping = true;
            descText.richText = true;
            descText.overflowMode = TextOverflowModes.Overflow;
            descText.maxVisibleLines = 6;
            descText.raycastTarget = false;
            if (fontAsset != null) descText.font = fontAsset;

            slotUi.powerBar = ShipUpgradeTreePowerBarUI.CreateInTrack(
                slotUi.statsFooter.transform,
                MoonDockEquipmentPowerBarTrackBg,
                MoonDockEquipmentPowerBarHeight,
                MoonDockEquipmentPowerBarPairGap,
                trackWidth);

            var sublineGo = new GameObject("Subline");
            sublineGo.transform.SetParent(boxRoot.transform, false);
            var sublineLe = sublineGo.AddComponent<LayoutElement>();
            sublineLe.preferredHeight = 11f;
            sublineLe.minHeight = 10f;
            slotUi.sublineText = sublineGo.AddComponent<TextMeshProUGUI>();
            slotUi.sublineText.fontSize = 8.5f;
            slotUi.sublineText.fontStyle = FontStyles.Bold;
            slotUi.sublineText.alignment = TextAlignmentOptions.Center;
            slotUi.sublineText.color = new Color(1f, 1f, 1f, 0.82f);
            slotUi.sublineText.overflowMode = TextOverflowModes.Ellipsis;
            slotUi.sublineText.raycastTarget = false;
            if (fontAsset != null) slotUi.sublineText.font = fontAsset;

            slotUi.placementPanel = new GameObject("Placement");
            slotUi.placementPanel.transform.SetParent(boxRoot.transform, false);
            var placementLe = slotUi.placementPanel.AddComponent<LayoutElement>();
            placementLe.preferredHeight = 112f;
            placementLe.minHeight = 112f;
            var placementVlg = slotUi.placementPanel.AddComponent<VerticalLayoutGroup>();
            placementVlg.spacing = 3f;
            placementVlg.padding = new RectOffset(2, 2, 0, 0);
            placementVlg.childAlignment = TextAnchor.UpperCenter;
            placementVlg.childControlWidth = true;
            placementVlg.childControlHeight = true;
            placementVlg.childForceExpandWidth = true;
            placementVlg.childForceExpandHeight = false;

            CreateSidebarEquipmentPlacementMoveRow(slotUi.placementPanel.transform, "Move X", index, moveAxis: 0);
            CreateSidebarEquipmentPlacementMoveRow(slotUi.placementPanel.transform, "Move Y", index, moveAxis: 1);
            CreateSidebarEquipmentPlacementMoveRow(slotUi.placementPanel.transform, "Move Z", index, moveAxis: 2);
            CreateSidebarEquipmentPlacementRotateRow(slotUi.placementPanel.transform, "Turn X", index, rotateAxis: 0);
            CreateSidebarEquipmentPlacementRotateRow(slotUi.placementPanel.transform, "Turn Y", index, rotateAxis: 1);
            CreateSidebarEquipmentPlacementRotateRow(slotUi.placementPanel.transform, "Turn Z", index, rotateAxis: 2);

            var readoutGo = new GameObject("PlacementReadout");
            readoutGo.transform.SetParent(slotUi.placementPanel.transform, false);
            var readoutLe = readoutGo.AddComponent<LayoutElement>();
            readoutLe.preferredHeight = 12f;
            readoutLe.minHeight = 11f;
            slotUi.placementReadout = readoutGo.AddComponent<TextMeshProUGUI>();
            slotUi.placementReadout.fontSize = 7.5f;
            slotUi.placementReadout.alignment = TextAlignmentOptions.Center;
            slotUi.placementReadout.color = new Color(0.72f, 0.8f, 0.92f, 0.88f);
            slotUi.placementReadout.raycastTarget = false;
            if (fontAsset != null) slotUi.placementReadout.font = fontAsset;

            slotUi.placementPanel.SetActive(false);

            var delGo = new GameObject("Delete");
            delGo.transform.SetParent(boxRoot.transform, false);
            var delRt = delGo.AddComponent<RectTransform>();
            delRt.anchorMin = new Vector2(0f, 1f);
            delRt.anchorMax = new Vector2(0f, 1f);
            delRt.pivot = new Vector2(0f, 1f);
            delRt.anchoredPosition = new Vector2(4f, -8f);
            delRt.sizeDelta = new Vector2(22f, 22f);
            delGo.AddComponent<LayoutElement>().ignoreLayout = true;
            var delImg = delGo.AddComponent<Image>();
            delImg.color = new Color(0.42f, 0.18f, 0.2f, 0.96f);
            if (buttonSprite != null) { delImg.sprite = buttonSprite; delImg.type = Image.Type.Sliced; }
            deleteButton = delGo.AddComponent<Button>();
            var delTxtGo = new GameObject("Text");
            delTxtGo.transform.SetParent(delGo.transform, false);
            var delTxtRect = delTxtGo.AddComponent<RectTransform>();
            delTxtRect.anchorMin = Vector2.zero;
            delTxtRect.anchorMax = Vector2.one;
            delTxtRect.offsetMin = Vector2.zero;
            delTxtRect.offsetMax = Vector2.zero;
            var delTmp = delTxtGo.AddComponent<TextMeshProUGUI>();
            delTmp.text = "×";
            delTmp.fontSize = 16;
            delTmp.alignment = TextAlignmentOptions.Center;
            delTmp.color = new Color(1f, 0.92f, 0.92f, 1f);
            if (fontAsset != null) delTmp.font = fontAsset;
            delTmp.raycastTarget = false;
            deleteButton.gameObject.SetActive(false);
        }

        private void CreateSidebarEquipmentPlacementMoveRow(Transform parent, string label, int slotIndex, int moveAxis)
        {
            CreateSidebarEquipmentPlacementAxisRow(
                parent,
                label,
                slotIndex,
                axisIndex: moveAxis,
                negativeLabel: "−",
                positiveLabel: "+",
                onStep: (slot, axis, direction) =>
                {
                    Vector3 delta = Vector3.zero;
                    delta[axis] = direction * EquipmentPlacementNudgeStep;
                    NudgeSidebarEquipmentPlacement(slot, delta, Vector3.zero);
                });
        }

        private void CreateSidebarEquipmentPlacementRotateRow(Transform parent, string label, int slotIndex, int rotateAxis)
        {
            CreateSidebarEquipmentPlacementAxisRow(
                parent,
                label,
                slotIndex,
                axisIndex: rotateAxis,
                negativeLabel: "↺",
                positiveLabel: "↻",
                onStep: (slot, axis, direction) =>
                {
                    Vector3 deltaEuler = Vector3.zero;
                    deltaEuler[axis] = direction * EquipmentRotationSnapStep;
                    NudgeSidebarEquipmentPlacement(slot, Vector3.zero, deltaEuler);
                });
        }

        private void CreateSidebarEquipmentPlacementAxisRow(
            Transform parent,
            string label,
            int slotIndex,
            int axisIndex,
            string negativeLabel,
            string positiveLabel,
            System.Action<int, int, int> onStep)
        {
            var rowGo = new GameObject(label.Replace(' ', '_') + "Row");
            rowGo.transform.SetParent(parent, false);
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.preferredHeight = 16f;
            rowLe.minHeight = 15f;
            var rowHlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowHlg.spacing = 3f;
            rowHlg.childAlignment = TextAnchor.MiddleCenter;
            rowHlg.childControlWidth = true;
            rowHlg.childControlHeight = true;
            rowHlg.childForceExpandWidth = false;
            rowHlg.childForceExpandHeight = true;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var labelLe = labelGo.AddComponent<LayoutElement>();
            labelLe.preferredWidth = 42f;
            labelLe.minWidth = 42f;
            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text = label;
            labelTmp.fontSize = 7.5f;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.alignment = TextAlignmentOptions.MidlineLeft;
            labelTmp.color = new Color(0.75f, 0.82f, 0.95f, 0.92f);
            labelTmp.raycastTarget = false;
            if (fontAsset != null) labelTmp.font = fontAsset;

            CreateSidebarEquipmentPlacementStepButton(rowGo.transform, negativeLabel, 18f, () => onStep?.Invoke(slotIndex, axisIndex, -1));
            CreateSidebarEquipmentPlacementStepButton(rowGo.transform, positiveLabel, 18f, () => onStep?.Invoke(slotIndex, axisIndex, 1));
        }

        private void CreateSidebarEquipmentPlacementStepButton(Transform parent, string text, float width, UnityEngine.Events.UnityAction onClick)
        {
            var btnGo = new GameObject("Step_" + text);
            btnGo.transform.SetParent(parent, false);
            var btnLe = btnGo.AddComponent<LayoutElement>();
            btnLe.preferredWidth = width;
            btnLe.minWidth = width;
            btnLe.preferredHeight = 16f;
            btnLe.minHeight = 16f;
            var btnImg = btnGo.AddComponent<Image>();
            btnImg.color = new Color(0.12f, 0.16f, 0.24f, 0.96f);
            if (buttonSprite != null)
            {
                btnImg.sprite = buttonSprite;
                btnImg.type = Image.Type.Sliced;
            }
            var btn = btnGo.AddComponent<Button>();
            btn.onClick.AddListener(onClick);

            var txtGo = new GameObject("Text");
            txtGo.transform.SetParent(btnGo.transform, false);
            var txtRt = txtGo.AddComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = Vector2.zero;
            txtRt.offsetMax = Vector2.zero;
            var tmp = txtGo.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 9f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.92f, 0.96f, 1f, 0.96f);
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
        }

        private static string FormatEquipmentPlacementCompact(Vector3 pos, Vector3 euler) =>
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Pos {0:0.##}, {1:0.##}, {2:0.##}   Rot {3:0.#}°, {4:0.#}°, {5:0.#}°",
                pos.x, pos.y, pos.z, euler.x, euler.y, euler.z);

        private void NudgeSidebarEquipmentPlacement(int slotIndex, Vector3 deltaPosition, Vector3 deltaEuler)
        {
            if (currentShip == null || slotIndex < 0)
                return;

            var equipment = currentShip.EquippedEquipment;
            if (equipment == null || slotIndex >= equipment.Count)
                return;

            EquippedEquipmentEntry entry = equipment[slotIndex];
            if (!entry.IsShipComponent)
                return;

            Vector3 pos = entry.LocalPosition + deltaPosition;
            Vector3 rot = EquippedComponentPlacementUtility.SnapEulerAngles(entry.LocalEulerAngles + deltaEuler);

            currentShip.UpdateEquippedComponentPlacementServerRpc(
                slotIndex,
                pos.x, pos.y, pos.z,
                rot.x, rot.y, rot.z);
        }

        private void SetUpgradeCardSlotLayoutMode(bool richLayout)
        {
            if (slotGridRoot == null)
                return;

            if (_upgradeCardSlotRichLayoutActive == richLayout && slotBoxes != null && slotBoxes[0] != null)
                return;

            _upgradeCardSlotRichLayoutActive = richLayout;
            _sidebarUpgradeCardSlotUi = richLayout ? new SidebarUpgradeCardSlotUi[MaxSlotRows] : null;

            for (int i = 0; i < MaxSlotRows; i++)
            {
                if (slotBoxes != null && slotBoxes[i] != null)
                    Destroy(slotBoxes[i]);
                if (slotBoxes != null)
                    slotBoxes[i] = null;
            }

            slotBoxes = new GameObject[MaxSlotRows];
            slotBgImages = new Image[MaxSlotRows];
            slotBorderImages = new Image[MaxSlotRows];
            slotLevelTexts = new TextMeshProUGUI[MaxSlotRows];
            slotTitleTexts = new TextMeshProUGUI[MaxSlotRows];
            slotDescTexts = new TextMeshProUGUI[MaxSlotRows];
            slotDeleteButtons = new Button[MaxSlotRows];

            for (int i = 0; i < MaxSlotRows; i++)
            {
                int idx = i;
                if (richLayout)
                {
                    CreateSidebarUpgradeCardSlotCard(
                        slotGridRoot.transform,
                        i,
                        out slotBoxes[i],
                        out slotBgImages[i],
                        out slotTitleTexts[i],
                        out slotDescTexts[i],
                        out slotDeleteButtons[i],
                        out _sidebarUpgradeCardSlotUi[i]);
                }
                else
                {
                    CreateSlotBoxForGrid(
                        slotGridRoot.transform,
                        SlotCardWidth,
                        SlotCardHeight,
                        i,
                        out slotBoxes[i],
                        out slotBgImages[i],
                        out slotBorderImages[i],
                        out slotLevelTexts[i],
                        out slotTitleTexts[i],
                        out slotDescTexts[i],
                        out slotDeleteButtons[i]);
                }

                if (slotDeleteButtons[i] != null)
                    slotDeleteButtons[i].onClick.AddListener(() => ShowCardRemoveConfirm(idx));
            }
        }

        private void CreateSidebarUpgradeCardSlotCard(
            Transform gridParent,
            int index,
            out GameObject boxRoot,
            out Image bgImage,
            out TextMeshProUGUI titleText,
            out TextMeshProUGUI descText,
            out Button deleteButton,
            out SidebarUpgradeCardSlotUi slotUi)
        {
            slotUi = new SidebarUpgradeCardSlotUi();

            boxRoot = new GameObject("UpgradeCardSlot_" + (index + 1));
            boxRoot.transform.SetParent(gridParent, false);
            var cardLe = boxRoot.AddComponent<LayoutElement>();
            cardLe.flexibleWidth = 0f;
            cardLe.flexibleHeight = 0f;
            cardLe.preferredWidth = SidebarSlotCardWidth;
            cardLe.minWidth = SidebarSlotCardWidth;
            cardLe.preferredHeight = SidebarUpgradeCardSlotHeight;
            cardLe.minHeight = SidebarUpgradeCardSlotHeight;

            bgImage = boxRoot.AddComponent<Image>();
            bgImage.color = MoonDockEquipmentCardBg;
            bgImage.raycastTarget = false;
            var cardOutline = boxRoot.AddComponent<Outline>();
            cardOutline.effectColor = MoonDockStoreCardFrameColor;
            cardOutline.effectDistance = new Vector2(1f, 1f);

            var cardVlg = boxRoot.AddComponent<VerticalLayoutGroup>();
            cardVlg.spacing = 3f;
            cardVlg.padding = new RectOffset(4, 4, 5, 4);
            cardVlg.childAlignment = TextAnchor.UpperCenter;
            cardVlg.childControlWidth = true;
            cardVlg.childControlHeight = true;
            cardVlg.childForceExpandWidth = true;
            cardVlg.childForceExpandHeight = false;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(boxRoot.transform, false);
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.preferredHeight = 4f;
            accentLe.minHeight = 4f;
            slotUi.accentImage = accentGo.AddComponent<Image>();
            slotUi.accentImage.color = SidebarUpgradeCardEmptyAccent;
            slotUi.accentImage.raycastTarget = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(boxRoot.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 14f;
            titleLe.minHeight = 12f;
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.text = "Empty";
            titleText.fontSize = 10f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = Color.white;
            titleText.overflowMode = TextOverflowModes.Ellipsis;
            titleText.raycastTarget = false;
            if (fontAsset != null) titleText.font = fontAsset;

            slotUi.iconRoot = new GameObject("Icon");
            slotUi.iconRoot.transform.SetParent(boxRoot.transform, false);
            var iconLe = slotUi.iconRoot.AddComponent<LayoutElement>();
            iconLe.flexibleHeight = 0f;
            iconLe.minHeight = SidebarUpgradeCardIconMinHeight;
            iconLe.preferredHeight = SidebarUpgradeCardIconHeight;
            slotUi.iconImage = slotUi.iconRoot.AddComponent<Image>();
            slotUi.iconImage.color = new Color(1f, 1f, 1f, 0f);
            slotUi.iconImage.preserveAspect = true;
            slotUi.iconImage.raycastTarget = false;

            var glyphGo = new GameObject("Glyph");
            glyphGo.transform.SetParent(slotUi.iconRoot.transform, false);
            var glyphRt = glyphGo.AddComponent<RectTransform>();
            glyphRt.anchorMin = Vector2.zero;
            glyphRt.anchorMax = Vector2.one;
            glyphRt.offsetMin = Vector2.zero;
            glyphRt.offsetMax = Vector2.zero;
            slotUi.iconGlyph = glyphGo.AddComponent<TextMeshProUGUI>();
            slotUi.iconGlyph.fontSize = 32f;
            slotUi.iconGlyph.alignment = TextAlignmentOptions.Center;
            slotUi.iconGlyph.color = new Color(1f, 1f, 1f, 0.95f);
            slotUi.iconGlyph.raycastTarget = false;
            if (fontAsset != null) slotUi.iconGlyph.font = fontAsset;

            slotUi.descFooter = new GameObject("DescFooter");
            slotUi.descFooter.transform.SetParent(boxRoot.transform, false);
            var descFooterLe = slotUi.descFooter.AddComponent<LayoutElement>();
            descFooterLe.flexibleHeight = 0f;
            descFooterLe.minHeight = SidebarUpgradeCardDescHeight;
            descFooterLe.preferredHeight = SidebarUpgradeCardDescHeight;
            var descFooterBg = slotUi.descFooter.AddComponent<Image>();
            descFooterBg.color = MoonDockEquipmentStatsFooterBg;
            descFooterBg.raycastTarget = false;
            var descFooterVlg = slotUi.descFooter.AddComponent<VerticalLayoutGroup>();
            descFooterVlg.padding = new RectOffset(4, 4, 4, 4);
            descFooterVlg.childAlignment = TextAnchor.UpperCenter;
            descFooterVlg.childControlWidth = true;
            descFooterVlg.childControlHeight = true;
            descFooterVlg.childForceExpandWidth = true;
            descFooterVlg.childForceExpandHeight = true;

            var descriptionGo = new GameObject("Description");
            descriptionGo.transform.SetParent(slotUi.descFooter.transform, false);
            var descriptionLe = descriptionGo.AddComponent<LayoutElement>();
            descriptionLe.flexibleHeight = 1f;
            descriptionLe.minHeight = SidebarUpgradeCardDescHeight - 8f;
            descText = descriptionGo.AddComponent<TextMeshProUGUI>();
            descText.text = string.Empty;
            descText.fontSize = SidebarUpgradeCardDescFontSize;
            descText.alignment = TextAlignmentOptions.Top;
            descText.color = new Color(0.92f, 0.95f, 1f, 0.92f);
            descText.enableWordWrapping = true;
            descText.overflowMode = TextOverflowModes.Ellipsis;
            descText.maxVisibleLines = 5;
            descText.raycastTarget = false;
            if (fontAsset != null) descText.font = fontAsset;

            var sublineGo = new GameObject("Subline");
            sublineGo.transform.SetParent(boxRoot.transform, false);
            var sublineLe = sublineGo.AddComponent<LayoutElement>();
            sublineLe.preferredHeight = 11f;
            sublineLe.minHeight = 10f;
            slotUi.sublineText = sublineGo.AddComponent<TextMeshProUGUI>();
            slotUi.sublineText.fontSize = 8.5f;
            slotUi.sublineText.fontStyle = FontStyles.Bold;
            slotUi.sublineText.alignment = TextAlignmentOptions.Center;
            slotUi.sublineText.color = new Color(1f, 1f, 1f, 0.82f);
            slotUi.sublineText.overflowMode = TextOverflowModes.Ellipsis;
            slotUi.sublineText.raycastTarget = false;
            if (fontAsset != null) slotUi.sublineText.font = fontAsset;

            var delGo = new GameObject("Delete");
            delGo.transform.SetParent(boxRoot.transform, false);
            var delRt = delGo.AddComponent<RectTransform>();
            delRt.anchorMin = new Vector2(0f, 1f);
            delRt.anchorMax = new Vector2(0f, 1f);
            delRt.pivot = new Vector2(0f, 1f);
            delRt.anchoredPosition = new Vector2(4f, -8f);
            delRt.sizeDelta = new Vector2(22f, 22f);
            delGo.AddComponent<LayoutElement>().ignoreLayout = true;
            var delImg = delGo.AddComponent<Image>();
            delImg.color = new Color(0.42f, 0.18f, 0.2f, 0.96f);
            if (buttonSprite != null) { delImg.sprite = buttonSprite; delImg.type = Image.Type.Sliced; }
            deleteButton = delGo.AddComponent<Button>();
            var delTxtGo = new GameObject("Text");
            delTxtGo.transform.SetParent(delGo.transform, false);
            var delTxtRect = delTxtGo.AddComponent<RectTransform>();
            delTxtRect.anchorMin = Vector2.zero;
            delTxtRect.anchorMax = Vector2.one;
            delTxtRect.offsetMin = Vector2.zero;
            delTxtRect.offsetMax = Vector2.zero;
            var delTmp = delTxtGo.AddComponent<TextMeshProUGUI>();
            delTmp.text = "×";
            delTmp.fontSize = 16;
            delTmp.alignment = TextAlignmentOptions.Center;
            delTmp.color = new Color(1f, 0.92f, 0.92f, 1f);
            if (fontAsset != null) delTmp.font = fontAsset;
            delTmp.raycastTarget = false;
            deleteButton.gameObject.SetActive(false);
        }

        private void CreateSlotBox(Transform parent, int index, ref float y, out GameObject boxRoot, out Image iconImage, out TextMeshProUGUI levelText)
        {
            boxRoot = new GameObject("SlotBox_" + (index + 1));
            boxRoot.transform.SetParent(parent, false);
            var rect = boxRoot.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-24f, SlotCardHeight);
            y -= SlotCardHeight + SlotCellSpacing;

            var bg = boxRoot.AddComponent<Image>();
            bg.color = new Color(0.12f, 0.18f, 0.28f, 0.95f);
            if (buttonSprite != null) { bg.sprite = buttonSprite; bg.type = Image.Type.Sliced; }

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(boxRoot.transform, false);
            var iconRect = iconGo.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0f);
            iconRect.anchorMax = new Vector2(0f, 0f);
            iconRect.pivot = new Vector2(0f, 0f);
            iconRect.anchoredPosition = new Vector2(6f, 6f);
            iconRect.sizeDelta = new Vector2(SlotCardHeight - 24f, SlotCardHeight - 22f);
            iconImage = iconGo.AddComponent<Image>();
            iconImage.color = new Color(1f, 1f, 1f, 0.9f);
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            var levelGo = new GameObject("Level");
            levelGo.transform.SetParent(boxRoot.transform, false);
            var levelRect = levelGo.AddComponent<RectTransform>();
            levelRect.anchorMin = new Vector2(1f, 0f);
            levelRect.anchorMax = new Vector2(1f, 0f);
            levelRect.pivot = new Vector2(1f, 0f);
            levelRect.anchoredPosition = new Vector2(-4f, 4f);
            levelRect.sizeDelta = new Vector2(32f, 16f);
            levelText = levelGo.AddComponent<TextMeshProUGUI>();
            levelText.text = "—";
            levelText.fontSize = 11;
            levelText.alignment = TextAlignmentOptions.BottomRight;
            levelText.color = new Color(0.85f, 0.9f, 1f, 0.95f);
            if (fontAsset != null) levelText.font = fontAsset;
        }

        private TextMeshProUGUI CreateTMP(Transform parent, string name, string text, int fontSize, ref float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-24f, 22f);
            y -= 26f;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = Color.white;
            if (fontAsset != null) tmp.font = fontAsset;
            return tmp;
        }

        private static Color GetSlotTypeColor(SlotType slotType)
        {
            switch (slotType)
            {
                case SlotType.Weapon: return new Color(0.32f, 0.14f, 0.12f, 0.95f);
                case SlotType.Ship:   return new Color(0.12f, 0.2f, 0.32f, 0.95f);
                case SlotType.Cargo:  return new Color(0.12f, 0.28f, 0.18f, 0.95f);
                default:              return new Color(0.18f, 0.2f, 0.26f, 0.95f);
            }
        }

        private static Color GetSlotTypeBorderColor(SlotType slotType)
        {
            switch (slotType)
            {
                case SlotType.Weapon: return new Color(0.6f, 0.35f, 0.3f, 0.95f);
                case SlotType.Ship:   return new Color(0.35f, 0.5f, 0.75f, 0.95f);
                case SlotType.Cargo:  return new Color(0.35f, 0.6f, 0.45f, 0.95f);
                default:              return new Color(0.45f, 0.5f, 0.6f, 0.95f);
            }
        }

        private static string GetSlotTypeIconChar(SlotType slotType)
        {
            switch (slotType)
            {
                case SlotType.Weapon: return "⚔";
                case SlotType.Ship:   return "◆";
                case SlotType.Cargo:  return "●";
                default:              return "?";
            }
        }

        private static Color GetCardRarityFrameColor(int rarity)
        {
            if (rarity <= 1) return new Color(0.55f, 0.65f, 0.78f, 1f);
            if (rarity == 2) return new Color(0.3f, 0.9f, 0.55f, 1f);
            if (rarity == 3) return new Color(0.35f, 0.7f, 1f, 1f);
            if (rarity == 4) return new Color(0.85f, 0.45f, 1f, 1f);
            return new Color(1f, 0.85f, 0.28f, 1f);
        }

        private static void ApplySpaceCardOutline(TextMeshProUGUI tmp, float width = 0.2f)
        {
            if (tmp == null || tmp.font == null)
                return;
            tmp.outlineWidth = width;
            tmp.outlineColor = new Color(0.02f, 0.05f, 0.12f, 0.92f);
        }

        private static string GetCardRarityLabel(int rarity)
        {
            if (rarity <= 1) return "Common";
            if (rarity == 2) return "Uncommon";
            if (rarity == 3) return "Rare";
            if (rarity == 4) return "Epic";
            return "Legendary";
        }

        private static string GetCardSlotTypeLabel(SlotType slotType)
        {
            switch (slotType)
            {
                case SlotType.Weapon: return "Weapon";
                case SlotType.Ship: return "Hull";
                case SlotType.Cargo: return "Cargo";
                default: return "Module";
            }
        }

        /// <summary>Shared moon-dock item tile used by equipment store cards and upgrade spin offers.</summary>
        private void CreateMoonDockItemTile(
            Transform parent,
            string tileName,
            Color tileColor,
            Color accentColor,
            string actionLabel,
            out GameObject root,
            out Image accentImage,
            out Image bgImage,
            out Image iconImage,
            out TextMeshProUGUI iconGlyphText,
            out TextMeshProUGUI titleText,
            out TextMeshProUGUI sublineText,
            out Button actionButton,
            out Image actionButtonImage)
        {
            root = new GameObject(tileName);
            root.transform.SetParent(parent, false);
            var cardLe = root.AddComponent<LayoutElement>();
            cardLe.flexibleWidth = 0f;
            cardLe.minWidth = MoonDockStoreTileMinWidth;
            cardLe.preferredWidth = MoonDockStoreTileMinWidth;
            cardLe.preferredHeight = MoonDockStoreCardHeight;
            cardLe.minHeight = MoonDockStoreCardHeight;
            cardLe.flexibleHeight = 0f;

            bgImage = root.AddComponent<Image>();
            bgImage.color = tileColor;
            bgImage.raycastTarget = false;
            var cardOutline = root.AddComponent<Outline>();
            cardOutline.effectColor = MoonDockStoreCardFrameColor;
            cardOutline.effectDistance = new Vector2(1f, 1f);

            var innerShadeGo = new GameObject("InnerShade");
            innerShadeGo.transform.SetParent(root.transform, false);
            var innerShadeRt = innerShadeGo.AddComponent<RectTransform>();
            innerShadeRt.anchorMin = Vector2.zero;
            innerShadeRt.anchorMax = Vector2.one;
            innerShadeRt.offsetMin = new Vector2(3f, 3f);
            innerShadeRt.offsetMax = new Vector2(-3f, -3f);
            var innerShadeImg = innerShadeGo.AddComponent<Image>();
            innerShadeImg.color = MoonDockStoreCardInnerShade;
            innerShadeImg.raycastTarget = false;
            innerShadeGo.AddComponent<LayoutElement>().ignoreLayout = true;

            var cardVlg = root.AddComponent<VerticalLayoutGroup>();
            cardVlg.spacing = 2f;
            cardVlg.padding = new RectOffset(4, 4, 5, 4);
            cardVlg.childAlignment = TextAnchor.UpperCenter;
            cardVlg.childControlWidth = true;
            cardVlg.childControlHeight = true;
            cardVlg.childForceExpandWidth = true;
            cardVlg.childForceExpandHeight = false;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(root.transform, false);
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.preferredHeight = 3f;
            accentLe.minHeight = 3f;
            accentImage = accentGo.AddComponent<Image>();
            accentImage.color = accentColor;
            accentImage.raycastTarget = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(root.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 22f;
            titleLe.minHeight = 18f;
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.fontSize = 11f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = Color.white;
            titleText.enableWordWrapping = true;
            titleText.overflowMode = TextOverflowModes.Ellipsis;
            titleText.raycastTarget = false;
            if (fontAsset != null) titleText.font = fontAsset;

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(root.transform, false);
            var iconLe = iconGo.AddComponent<LayoutElement>();
            iconLe.flexibleHeight = 0f;
            iconLe.minHeight = 28f;
            iconLe.preferredHeight = 36f;
            iconImage = iconGo.AddComponent<Image>();
            iconImage.color = new Color(1f, 1f, 1f, 0f);
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            var glyphGo = new GameObject("Glyph");
            glyphGo.transform.SetParent(iconGo.transform, false);
            var glyphRt = glyphGo.AddComponent<RectTransform>();
            glyphRt.anchorMin = Vector2.zero;
            glyphRt.anchorMax = Vector2.one;
            glyphRt.offsetMin = Vector2.zero;
            glyphRt.offsetMax = Vector2.zero;
            iconGlyphText = glyphGo.AddComponent<TextMeshProUGUI>();
            iconGlyphText.fontSize = 26f;
            iconGlyphText.alignment = TextAlignmentOptions.Center;
            iconGlyphText.color = new Color(1f, 1f, 1f, 0.95f);
            iconGlyphText.raycastTarget = false;
            if (fontAsset != null) iconGlyphText.font = fontAsset;

            var sublineGo = new GameObject("Subline");
            sublineGo.transform.SetParent(root.transform, false);
            var sublineLe = sublineGo.AddComponent<LayoutElement>();
            sublineLe.preferredHeight = 14f;
            sublineLe.minHeight = 12f;
            sublineText = sublineGo.AddComponent<TextMeshProUGUI>();
            sublineText.fontSize = 10f;
            sublineText.fontStyle = FontStyles.Bold;
            sublineText.alignment = TextAlignmentOptions.Center;
            sublineText.color = new Color(1f, 1f, 1f, 0.88f);
            sublineText.overflowMode = TextOverflowModes.Ellipsis;
            sublineText.raycastTarget = false;
            if (fontAsset != null) sublineText.font = fontAsset;

            var actionGo = new GameObject("Action");
            actionGo.transform.SetParent(root.transform, false);
            var actionLe = actionGo.AddComponent<LayoutElement>();
            actionLe.preferredHeight = 24f;
            actionLe.minHeight = 22f;
            actionButtonImage = actionGo.AddComponent<Image>();
            actionButtonImage.color = MoonDockItemTileButtonIdle;
            if (buttonSprite != null)
            {
                actionButtonImage.sprite = buttonSprite;
                actionButtonImage.type = Image.Type.Sliced;
            }
            actionButton = actionGo.AddComponent<Button>();
            actionButton.targetGraphic = actionButtonImage;
            var actionLabelGo = new GameObject("Label");
            actionLabelGo.transform.SetParent(actionGo.transform, false);
            var actionLabelRt = actionLabelGo.AddComponent<RectTransform>();
            actionLabelRt.anchorMin = Vector2.zero;
            actionLabelRt.anchorMax = Vector2.one;
            actionLabelRt.offsetMin = new Vector2(2f, 1f);
            actionLabelRt.offsetMax = new Vector2(-2f, -1f);
            var actionLabelTmp = actionLabelGo.AddComponent<TextMeshProUGUI>();
            actionLabelTmp.text = actionLabel;
            actionLabelTmp.fontSize = 11f;
            actionLabelTmp.fontStyle = FontStyles.Bold;
            actionLabelTmp.alignment = TextAlignmentOptions.Center;
            actionLabelTmp.color = Color.white;
            actionLabelTmp.raycastTarget = false;
            if (fontAsset != null) actionLabelTmp.font = fontAsset;
        }

        private static Color GetSlotTypeAccentColor(SlotType slotType)
        {
            switch (slotType)
            {
                case SlotType.Weapon: return new Color(0.92f, 0.38f, 0.28f, 1f);
                case SlotType.Ship: return OrbitDockSidebarPanelUI.UpgradeCardsAccent;
                case SlotType.Cargo: return new Color(0.34f, 0.82f, 0.52f, 1f);
                default: return OrbitDockSidebarPanelUI.UpgradeCardsAccent;
            }
        }

        /// <summary>Upgrade spin-offer tile with icon, description, and choose action.</summary>
        private void CreateUpgradeSpinOfferCard(
            Transform parent,
            int index,
            out GameObject root,
            out Image accentImage,
            out Image bgImage,
            out Image iconImage,
            out TextMeshProUGUI titleText,
            out TextMeshProUGUI levelText,
            out TextMeshProUGUI metaLabel,
            out TextMeshProUGUI descText,
            out Button takeButton)
        {
            root = new GameObject("SpinOffer_" + (index + 1));
            root.transform.SetParent(parent, false);
            var cardLe = root.AddComponent<LayoutElement>();
            cardLe.flexibleWidth = 0f;
            cardLe.flexibleHeight = 0f;
            cardLe.minWidth = MoonDockStoreTileMinWidth;
            cardLe.preferredWidth = MoonDockStoreTileMinWidth;
            cardLe.preferredHeight = MoonDockUpgradeSpinCardHeight;
            cardLe.minHeight = MoonDockUpgradeSpinCardHeight;

            bgImage = root.AddComponent<Image>();
            bgImage.color = MoonDockEquipmentCardBg;
            bgImage.raycastTarget = false;
            var cardOutline = root.AddComponent<Outline>();
            cardOutline.effectColor = MoonDockStoreCardFrameColor;
            cardOutline.effectDistance = new Vector2(1f, 1f);

            var cardVlg = root.AddComponent<VerticalLayoutGroup>();
            cardVlg.spacing = 3f;
            cardVlg.padding = new RectOffset(4, 4, 5, 4);
            cardVlg.childAlignment = TextAnchor.UpperCenter;
            cardVlg.childControlWidth = true;
            cardVlg.childControlHeight = true;
            cardVlg.childForceExpandWidth = true;
            cardVlg.childForceExpandHeight = false;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(root.transform, false);
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.preferredHeight = 4f;
            accentLe.minHeight = 4f;
            accentImage = accentGo.AddComponent<Image>();
            accentImage.color = OrbitDockSidebarPanelUI.UpgradeCardsAccent;
            accentImage.raycastTarget = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(root.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 14f;
            titleLe.minHeight = 12f;
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.text = $"Offer {index + 1}";
            titleText.fontSize = 10f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = Color.white;
            titleText.overflowMode = TextOverflowModes.Ellipsis;
            titleText.raycastTarget = false;
            if (fontAsset != null) titleText.font = fontAsset;

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(root.transform, false);
            var iconLe = iconGo.AddComponent<LayoutElement>();
            iconLe.flexibleHeight = 0f;
            iconLe.minHeight = 36f;
            iconLe.preferredHeight = MoonDockUpgradeSpinIconHeight;
            iconImage = iconGo.AddComponent<Image>();
            iconImage.color = new Color(1f, 1f, 1f, 0f);
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            var glyphGo = new GameObject("Glyph");
            glyphGo.transform.SetParent(iconGo.transform, false);
            var glyphRt = glyphGo.AddComponent<RectTransform>();
            glyphRt.anchorMin = Vector2.zero;
            glyphRt.anchorMax = Vector2.one;
            glyphRt.offsetMin = Vector2.zero;
            glyphRt.offsetMax = Vector2.zero;
            var iconGlyphText = glyphGo.AddComponent<TextMeshProUGUI>();
            iconGlyphText.text = "?";
            iconGlyphText.fontSize = 30f;
            iconGlyphText.alignment = TextAlignmentOptions.Center;
            iconGlyphText.color = new Color(1f, 1f, 1f, 0.95f);
            iconGlyphText.raycastTarget = false;
            if (fontAsset != null) iconGlyphText.font = fontAsset;

            var descFooterGo = new GameObject("DescFooter");
            descFooterGo.transform.SetParent(root.transform, false);
            var descFooterLe = descFooterGo.AddComponent<LayoutElement>();
            descFooterLe.flexibleHeight = 0f;
            descFooterLe.minHeight = MoonDockUpgradeSpinDescHeight;
            descFooterLe.preferredHeight = MoonDockUpgradeSpinDescHeight;
            var descFooterBg = descFooterGo.AddComponent<Image>();
            descFooterBg.color = MoonDockEquipmentStatsFooterBg;
            descFooterBg.raycastTarget = false;
            var descFooterVlg = descFooterGo.AddComponent<VerticalLayoutGroup>();
            descFooterVlg.padding = new RectOffset(4, 4, 4, 4);
            descFooterVlg.childAlignment = TextAnchor.UpperCenter;
            descFooterVlg.childControlWidth = true;
            descFooterVlg.childControlHeight = true;
            descFooterVlg.childForceExpandWidth = true;
            descFooterVlg.childForceExpandHeight = true;

            var descriptionGo = new GameObject("Description");
            descriptionGo.transform.SetParent(descFooterGo.transform, false);
            var descriptionLe = descriptionGo.AddComponent<LayoutElement>();
            descriptionLe.flexibleHeight = 1f;
            descriptionLe.minHeight = MoonDockUpgradeSpinDescHeight - 8f;
            descText = descriptionGo.AddComponent<TextMeshProUGUI>();
            descText.text = "Spin to reveal card abilities.";
            descText.fontSize = MoonDockUpgradeSpinDescFontSize;
            descText.alignment = TextAlignmentOptions.Top;
            descText.color = new Color(0.92f, 0.95f, 1f, 0.92f);
            descText.enableWordWrapping = true;
            descText.overflowMode = TextOverflowModes.Ellipsis;
            descText.maxVisibleLines = 4;
            descText.raycastTarget = false;
            if (fontAsset != null) descText.font = fontAsset;

            var sublineGo = new GameObject("Subline");
            sublineGo.transform.SetParent(root.transform, false);
            var sublineLe = sublineGo.AddComponent<LayoutElement>();
            sublineLe.preferredHeight = 11f;
            sublineLe.minHeight = 10f;
            metaLabel = sublineGo.AddComponent<TextMeshProUGUI>();
            metaLabel.text = "Spin to reveal";
            metaLabel.fontSize = 8.5f;
            metaLabel.fontStyle = FontStyles.Bold;
            metaLabel.alignment = TextAlignmentOptions.Center;
            metaLabel.color = new Color(1f, 1f, 1f, 0.82f);
            metaLabel.overflowMode = TextOverflowModes.Ellipsis;
            metaLabel.raycastTarget = false;
            if (fontAsset != null) metaLabel.font = fontAsset;

            var actionGo = new GameObject("Action");
            actionGo.transform.SetParent(root.transform, false);
            var actionLe = actionGo.AddComponent<LayoutElement>();
            actionLe.preferredHeight = 22f;
            actionLe.minHeight = 20f;
            var actionButtonImage = actionGo.AddComponent<Image>();
            actionButtonImage.color = MoonDockItemTileButtonIdle;
            if (buttonSprite != null)
            {
                actionButtonImage.sprite = buttonSprite;
                actionButtonImage.type = Image.Type.Sliced;
            }
            takeButton = actionGo.AddComponent<Button>();
            takeButton.targetGraphic = actionButtonImage;
            var actionLabelGo = new GameObject("Label");
            actionLabelGo.transform.SetParent(actionGo.transform, false);
            var actionLabelRt = actionLabelGo.AddComponent<RectTransform>();
            actionLabelRt.anchorMin = Vector2.zero;
            actionLabelRt.anchorMax = Vector2.one;
            actionLabelRt.offsetMin = new Vector2(2f, 1f);
            actionLabelRt.offsetMax = new Vector2(-2f, -1f);
            var actionLabelTmp = actionLabelGo.AddComponent<TextMeshProUGUI>();
            actionLabelTmp.text = "Choose";
            actionLabelTmp.fontSize = 10f;
            actionLabelTmp.fontStyle = FontStyles.Bold;
            actionLabelTmp.alignment = TextAlignmentOptions.Center;
            actionLabelTmp.color = Color.white;
            actionLabelTmp.raycastTarget = false;
            if (fontAsset != null) actionLabelTmp.font = fontAsset;

            var levelHidden = new GameObject("LevelUnused");
            levelHidden.transform.SetParent(root.transform, false);
            levelHidden.SetActive(false);
            levelText = levelHidden.AddComponent<TextMeshProUGUI>();
        }

        private static void CreateMoonDockSectionHeader(Transform parent, string title, string subtitle, Color accent)
        {
            var blockGo = new GameObject("SectionHeader_" + title.Replace(" ", ""));
            blockGo.transform.SetParent(parent, false);
            var blockLe = blockGo.AddComponent<LayoutElement>();
            blockLe.preferredHeight = MoonDockSectionHeaderHeight;
            blockLe.minHeight = MoonDockSectionHeaderHeight;
            blockLe.flexibleHeight = 0f;

            var row = blockGo.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 10f;
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(blockGo.transform, false);
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.preferredWidth = 4f;
            accentLe.minWidth = 4f;
            accentLe.preferredHeight = MoonDockSectionHeaderHeight;
            accentLe.minHeight = MoonDockSectionHeaderHeight;
            accentLe.flexibleHeight = 0f;
            var accentImg = accentGo.AddComponent<Image>();
            accentImg.color = accent;
            accentImg.raycastTarget = false;

            var textColGo = new GameObject("TextCol");
            textColGo.transform.SetParent(blockGo.transform, false);
            var textColLe = textColGo.AddComponent<LayoutElement>();
            textColLe.flexibleWidth = 1f;
            textColLe.preferredHeight = MoonDockSectionHeaderTitleHeight + MoonDockSectionHeaderTextSpacing + MoonDockSectionHeaderSubtitleHeight;
            textColLe.minHeight = textColLe.preferredHeight;
            textColLe.flexibleHeight = 0f;
            var textVlg = textColGo.AddComponent<VerticalLayoutGroup>();
            textVlg.spacing = MoonDockSectionHeaderTextSpacing;
            textVlg.childAlignment = TextAnchor.UpperLeft;
            textVlg.childControlWidth = true;
            textVlg.childControlHeight = true;
            textVlg.childForceExpandWidth = true;
            textVlg.childForceExpandHeight = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(textColGo.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = MoonDockSectionHeaderTitleHeight;
            titleLe.minHeight = MoonDockSectionHeaderTitleHeight;
            titleLe.flexibleHeight = 0f;
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = title;
            titleTmp.fontSize = 20f;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.Left;
            titleTmp.color = new Color(0.94f, 0.96f, 1f, 1f);
            titleTmp.raycastTarget = false;

            if (!string.IsNullOrEmpty(subtitle))
            {
                var subGo = new GameObject("Subtitle");
                subGo.transform.SetParent(textColGo.transform, false);
                var subLe = subGo.AddComponent<LayoutElement>();
                subLe.preferredHeight = MoonDockSectionHeaderSubtitleHeight;
                subLe.minHeight = MoonDockSectionHeaderSubtitleHeight;
                subLe.flexibleHeight = 0f;
                var subTmp = subGo.AddComponent<TextMeshProUGUI>();
                subTmp.text = subtitle;
                subTmp.fontSize = 12f;
                subTmp.lineSpacing = -4f;
                subTmp.alignment = TextAlignmentOptions.TopLeft;
                subTmp.color = new Color(0.72f, 0.8f, 0.94f, 0.95f);
                subTmp.enableWordWrapping = true;
                subTmp.overflowMode = TextOverflowModes.Ellipsis;
                subTmp.maxVisibleLines = 2;
                subTmp.raycastTarget = false;
            }
        }

        private static void ApplyMoonDockSectionHeaderMetrics(Transform headerRoot)
        {
            if (headerRoot == null)
                return;

            var blockLe = headerRoot.GetComponent<LayoutElement>();
            if (blockLe != null)
            {
                blockLe.preferredHeight = MoonDockSectionHeaderHeight;
                blockLe.minHeight = MoonDockSectionHeaderHeight;
                blockLe.flexibleHeight = 0f;
            }

            var row = headerRoot.GetComponent<HorizontalLayoutGroup>();
            if (row != null)
                row.childForceExpandHeight = false;

            var accent = headerRoot.Find("Accent");
            if (accent != null)
            {
                var accentLe = accent.GetComponent<LayoutElement>();
                if (accentLe != null)
                {
                    accentLe.preferredHeight = MoonDockSectionHeaderHeight;
                    accentLe.minHeight = MoonDockSectionHeaderHeight;
                    accentLe.flexibleHeight = 0f;
                }
            }

            var textCol = headerRoot.Find("TextCol");
            if (textCol == null)
                return;

            var textColLe = textCol.GetComponent<LayoutElement>();
            if (textColLe != null)
            {
                textColLe.preferredHeight = MoonDockSectionHeaderTitleHeight + MoonDockSectionHeaderTextSpacing + MoonDockSectionHeaderSubtitleHeight;
                textColLe.minHeight = textColLe.preferredHeight;
                textColLe.flexibleHeight = 0f;
            }

            var title = textCol.Find("Title");
            if (title != null)
            {
                var titleLe = title.GetComponent<LayoutElement>();
                if (titleLe != null)
                {
                    titleLe.preferredHeight = MoonDockSectionHeaderTitleHeight;
                    titleLe.minHeight = MoonDockSectionHeaderTitleHeight;
                    titleLe.flexibleHeight = 0f;
                }
            }

            var subtitle = textCol.Find("Subtitle");
            if (subtitle != null)
            {
                var subLe = subtitle.GetComponent<LayoutElement>();
                if (subLe != null)
                {
                    subLe.preferredHeight = MoonDockSectionHeaderSubtitleHeight;
                    subLe.minHeight = MoonDockSectionHeaderSubtitleHeight;
                    subLe.flexibleHeight = 0f;
                }

                var subTmp = subtitle.GetComponent<TextMeshProUGUI>();
                if (subTmp != null)
                {
                    subTmp.lineSpacing = -4f;
                    subTmp.alignment = TextAlignmentOptions.TopLeft;
                    subTmp.enableWordWrapping = true;
                    subTmp.overflowMode = TextOverflowModes.Ellipsis;
                    subTmp.maxVisibleLines = 2;
                }
            }
        }

        private Button CreateActionButton(Transform parent, string label, ref float y, float width = 360f)
        {
            var go = new GameObject("Button");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-24f, 32f);
            y -= 36f;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.15f, 0.28f, 0.5f, 0.95f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            var btn = go.AddComponent<Button>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 4f);
            textRect.offsetMax = new Vector2(-8f, -4f);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
            return btn;
        }

        private static TextMeshProUGUI GetTileIconGlyph(GameObject tileRoot)
        {
            if (tileRoot == null) return null;
            Transform glyph = tileRoot.transform.Find("Icon/Glyph");
            return glyph != null ? glyph.GetComponent<TextMeshProUGUI>() : null;
        }

        private static void SetTileIconGlyphVisible(GameObject tileRoot, bool visible, string glyphText = "")
        {
            TextMeshProUGUI glyph = GetTileIconGlyph(tileRoot);
            if (glyph == null) return;
            glyph.gameObject.SetActive(visible);
            if (visible && !string.IsNullOrEmpty(glyphText))
                glyph.text = glyphText;
        }

        private void RefreshStoreLabels()
        {
            // Server is source of truth (Show() was zeroing contributedGems while pendingGemsRequest blocked syncing).
            contributedGems = lastReceivedGems;
            if (gemsText != null)
            {
                gemsText.text = $"Your contributed gems: {contributedGems:F0}";
                gemsText.gameObject.SetActive(!_moonDockLayoutActive);
            }
            RefreshSidebar();

            if (cardRoots == null || cardButtons == null || currentShip == null || currentPlanet == null) return;
            if (CardShopSystem.Instance == null)
            {
                for (int i = 0; i < cardRoots.Length; i++)
                {
                    if (cardRoots[i] != null) cardRoots[i].SetActive(false);
                }
                if (cardSpinButton != null) cardSpinButton.gameObject.SetActive(false);
                if (!_moonDockLayoutActive || _moonDockCenterView == MoonDockCenterView.Ships)
                    RefreshShipTreeVisualStateOnly();
                return;
            }
            int homeLevel = currentHomePlanet != null ? currentHomePlanet.HomePlanetLevel : 1;
            bool isHomeStore = currentPlanet is HomePlanet;
            bool hasEmptySlot = currentShip.HasEmptySlot;
            int shipLevel = currentShip.ShipLevel;
            int spinTier = CardShopSystem.GetSpinCardTier(shipLevel, homeLevel);
            float spinCost = CardShopSystem.Instance.GetCardSpinCost(spinTier);
            int poolCount = CardShopSystem.Instance.GetCardPoolCountForSpin(currentShip, spinTier, homeLevel, isHomeStore, currentPlanet.PlanetId, currentShip.ShipTeam);

            if (cardSpinButton != null)
            {
                cardSpinButton.gameObject.SetActive(true);
                cardSpinButton.interactable = poolCount > 0 && contributedGems >= spinCost && hasEmptySlot;
                if (cardSpinButtonImage != null)
                {
                    cardSpinButtonImage.color = cardSpinButton.interactable
                        ? OrbitDockSidebarPanelUI.UpgradeCardsAccent
                        : MoonDockSpinButtonDisabled;
                }
            }
            if (cardSpinButtonLabel != null)
                cardSpinButtonLabel.text = hasEmptySlot
                    ? $"Spin — {spinCost:F0} g"
                    : "No card slot";

            for (int i = 0; i < cardRoots.Length; i++)
            {
                string offerId = CardShopSystem.Instance.GetClientSpinOfferCardId(i);
                CardData card = !string.IsNullOrEmpty(offerId) ? CardShopSystem.Instance.GetCardByIdForShip(currentShip, offerId) : null;
                cardEntries[i] = card;
                if (cardRoots[i] != null)
                    cardRoots[i].SetActive(true);

                if (card == null)
                {
                    if (cardTitleTexts[i] != null)
                    {
                        cardTitleTexts[i].fontSize = 10f;
                        cardTitleTexts[i].color = new Color(0.75f, 0.82f, 0.95f, 0.9f);
                        cardTitleTexts[i].text = $"Offer {i + 1}";
                    }
                    if (cardDescTexts != null && i < cardDescTexts.Length && cardDescTexts[i] != null)
                        cardDescTexts[i].text = "Spin to reveal card abilities.";
                    if (cardRarityLabels != null && i < cardRarityLabels.Length && cardRarityLabels[i] != null)
                    {
                        cardRarityLabels[i].fontSize = 8.5f;
                        cardRarityLabels[i].text = "Awaiting spin";
                    }
                    if (cardRarityFrameImages != null && i < cardRarityFrameImages.Length && cardRarityFrameImages[i] != null)
                        cardRarityFrameImages[i].color = OrbitDockSidebarPanelUI.UpgradeCardsAccent;
                    if (cardIconImages != null && i < cardIconImages.Length && cardIconImages[i] != null)
                    {
                        cardIconImages[i].sprite = null;
                        cardIconImages[i].color = new Color(1f, 1f, 1f, 0f);
                    }
                    SetTileIconGlyphVisible(cardRoots[i], true, "?");
                    if (cardBgImages != null && i < cardBgImages.Length && cardBgImages[i] != null)
                        cardBgImages[i].color = MoonDockEquipmentCardBg;
                    if (cardButtons[i] != null)
                    {
                        cardButtons[i].interactable = false;
                        var takeImgEmpty = cardButtons[i].GetComponent<Image>();
                        if (takeImgEmpty != null)
                            takeImgEmpty.color = MoonDockItemTileButtonDisabled;
                        var tl = cardButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                        if (tl != null)
                        {
                            tl.text = "Choose";
                            tl.color = new Color(0.85f, 0.88f, 0.92f, 0.85f);
                        }
                    }
                    continue;
                }

                if (cardTitleTexts[i] != null)
                {
                    cardTitleTexts[i].fontSize = 10f;
                    cardTitleTexts[i].color = Color.white;
                    cardTitleTexts[i].text = card.GetDisplayNameOrDefault();
                }
                int rar = Mathf.Clamp((int)card.rarity, 1, 5);
                int cl = Mathf.Max(1, card.cardLevel);
                if (cardRarityFrameImages != null && i < cardRarityFrameImages.Length && cardRarityFrameImages[i] != null)
                    cardRarityFrameImages[i].color = GetSlotTypeAccentColor(card.slotType);
                if (cardBgImages != null && i < cardBgImages.Length && cardBgImages[i] != null)
                    cardBgImages[i].color = MoonDockEquipmentCardBg;
                if (cardDescTexts != null && i < cardDescTexts.Length && cardDescTexts[i] != null)
                {
                    cardDescTexts[i].text = !string.IsNullOrEmpty(card.description)
                        ? card.description
                        : "No description.";
                }
                if (cardIconImages != null && i < cardIconImages.Length && cardIconImages[i] != null)
                {
                    if (card.icon != null)
                    {
                        cardIconImages[i].sprite = card.icon;
                        cardIconImages[i].color = Color.white;
                        cardIconImages[i].preserveAspect = true;
                        SetTileIconGlyphVisible(cardRoots[i], false);
                    }
                    else
                    {
                        cardIconImages[i].sprite = null;
                        cardIconImages[i].color = new Color(1f, 1f, 1f, 0f);
                        SetTileIconGlyphVisible(cardRoots[i], true, GetCardSlotTypeLabel(card.slotType).Substring(0, 1));
                    }
                }
                if (cardRarityLabels != null && i < cardRarityLabels.Length && cardRarityLabels[i] != null)
                {
                    cardRarityLabels[i].fontSize = 8.5f;
                    cardRarityLabels[i].text = $"Lv {cl} · {GetCardSlotTypeLabel(card.slotType)} · {GetCardRarityLabel(rar)}";
                    cardRarityLabels[i].color = new Color(1f, 1f, 1f, 0.88f);
                }
                int cardLvl = Mathf.Max(1, card.cardLevel);
                bool levelOk = cardLvl <= shipLevel;
                if (cardButtons[i] != null)
                {
                    bool canChoose = hasEmptySlot && levelOk && !string.IsNullOrEmpty(offerId);
                    cardButtons[i].interactable = canChoose;
                    var takeImgFilled = cardButtons[i].GetComponent<Image>();
                    if (takeImgFilled != null)
                        takeImgFilled.color = canChoose
                            ? OrbitDockSidebarPanelUI.UpgradeCardsAccent
                            : MoonDockItemTileButtonDisabled;
                    var takeLabel = cardButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                    if (takeLabel != null)
                    {
                        takeLabel.color = Color.white;
                        if (!hasEmptySlot) takeLabel.text = "No slot";
                        else if (!levelOk) takeLabel.text = $"Need Lv.{cardLvl}";
                        else takeLabel.text = "Choose";
                    }
                }
            }

            if (!_moonDockLayoutActive || _moonDockCenterView == MoonDockCenterView.Ships)
                RefreshShipTreeVisualStateOnly();
        }

        private void RefreshSidebar()
        {
            if (!_moonDockLayoutActive || orbitDockSidebar == null)
                return;

            float bankBalance = contributedGems;
            if (OrbitStationEcsContext.HomePlanetId > 0 &&
                EcsGameBridge.TryGetContributedGems(OrbitStationEcsContext.HomePlanetId, out float liveBank))
            {
                bankBalance = liveBank;
                if (Mathf.Abs(liveBank - lastReceivedGems) > 0.01f)
                    OnContributedGemsReceived(liveBank);
            }

            float shipGems = 0f;
            if (EcsGameBridge.TryGetLocalShipState(out var shipState))
                shipGems = shipState.CurrentGems;

            float planetGems = 0f;
            int planetLevel = 1;
            int storePlanetId = OrbitStationEcsContext.StorePlanetId;
            if (storePlanetId <= 0 && currentPlanet != null)
                storePlanetId = currentPlanet.PlanetId;
            if (storePlanetId > 0 && EcsGameBridge.TryGetPlanetStateByPlanetId(storePlanetId, out var planetState))
            {
                planetGems = planetState.CurrentGems;
                planetLevel = Mathf.Max(1, planetState.PlanetLevel);
            }

            orbitDockSidebar.RefreshDepositStatus(shipGems, bankBalance, planetGems, planetLevel);
            ApplyAutoDepositPreferenceToShip();
            orbitDockSidebar.RefreshAutoDepositToggle(GetSavedAutoDepositGems());
            float maxPower = shipUpgradeTree != null ? shipUpgradeTree.GetMaxDisplayPower() : 0.001f;
            orbitDockSidebar.RefreshCurrentShip(PopulateTreeNode, maxPower);
            RefreshMoonDockStore();
        }

        private static bool GetSavedAutoDepositGems()
        {
            return PlayerPrefs.GetInt(OrbitDockSidebarPanelUI.AutoDepositGemsPrefsKey,
                OrbitDockSidebarPanelUI.AutoDepositGemsDefaultEnabled) != 0;
        }

        private void OnAutoDepositToggleChanged(bool enabled)
        {
            ApplyAutoDepositToShip(enabled);
        }

        private void ApplyAutoDepositPreferenceToShip()
        {
            ApplyAutoDepositToShip(GetSavedAutoDepositGems());
        }

        private void ApplyAutoDepositToShip(bool wantDeposit)
        {
            if (currentShip != null)
                currentShip.SetWantToDepositGemsServerRpc(wantDeposit);
            else
                MoonOrbitRpcClient.SetWantDepositGems(wantDeposit);
        }

        private void RefreshMoonDockStore()
        {
            if (!_moonDockLayoutActive || moonDockStoreSection == null)
                return;

            EnsureMoonDockStoreSection();
            Planet storePlanet = GetShipUpgradeStorePlanet();
            ShipFamilyDefinition family = null;
            if (CardShopSystem.Instance != null && storePlanet != null)
                family = CardShopSystem.Instance.GetShipFamilyForStorePlanet(storePlanet.PlanetId, currentShip);
            else if (CardShopSystem.Instance != null && currentShip != null)
                family = CardShopSystem.Instance.GetShipFamilyForShip(currentShip);
            string familyKey = family != null ? family.familyId : string.Empty;
            bool needsEquipmentRebuild = !string.Equals(familyKey, _moonDockStoreBuiltForFamilyKey, StringComparison.Ordinal)
                || _moonDockEquipmentCardLayoutVersionBuilt != MoonDockEquipmentCardLayoutVersion;
            if (!needsEquipmentRebuild && family?.components != null && family.components.Count > 0)
            {
                for (int c = 0; c < _moonDockStoreCards.Count; c++)
                {
                    MoonDockStoreCardBinding existing = _moonDockStoreCards[c];
                    if (existing != null && existing.isComponent && existing.powerBar == null)
                    {
                        needsEquipmentRebuild = true;
                        break;
                    }
                }
            }

            if (needsEquipmentRebuild)
                RebuildMoonDockEquipmentStore(family);

            int shipLevel = currentShip != null ? currentShip.ShipLevel : 1;
            for (int i = 0; i < _moonDockStoreCards.Count; i++)
            {
                MoonDockStoreCardBinding card = _moonDockStoreCards[i];
                if (card == null) continue;

                bool canBuy;
                float price;
                string subline;
                if (card.isComponent)
                {
                    ShipFamilyComponentEntry componentEntry = null;
                    if (family != null)
                        family.TryGetComponentEntry(card.componentId, out componentEntry);
                    price = componentEntry != null
                        ? ShipComponentStoreData.GetComponentGemPrice(componentEntry, shipLevel)
                        : 999f;
                    bool owned = currentShip != null && currentShip.HasComponentEquipped(card.componentId);
                    canBuy = currentShip != null && !owned && contributedGems >= price && currentShip.HasEmptyEquipmentSlot;
                    float power = componentEntry != null
                        ? ShipComponentStoreData.GetComponentPowerScore(componentEntry, shipLevel, family)
                        : 0f;
                    subline = FormatMoonDockEquipmentSubline(shipLevel, power, owned);
                    if (card.descriptionText != null && componentEntry != null)
                        ApplyEquipmentCardAbilityDescription(card.descriptionText, componentEntry, shipLevel, family);
                    if (card.powerBar != null && componentEntry != null)
                    {
                        ShipFamilyPowerScoreBreakdown breakdown = ShipComponentStoreData.GetPowerBreakdown(componentEntry, shipLevel, family);
                        float maxPower = GetMoonDockComponentMaxDisplayPower(family, shipLevel);
                        float trackW = Mathf.Max(40f, MoonDockStoreTileMinWidth - 14f);
                        card.powerBar.ConfigureLayoutScale(1f, 1f);
                        card.powerBar.ApplyEquipmentBreakdown(breakdown, maxPower, trackW);
                    }
                    ApplyMoonDockEquipmentTileIcon(
                        card.iconImage,
                        card.iconGlyph,
                        ShipComponentStoreData.GetMenuPreviewSprite(family, componentEntry, currentShip != null ? currentShip.ShipTeam : TeamManager.Team.None),
                        componentEntry != null ? ShipComponentStoreData.GetIconGlyph(componentEntry) : "?");
                }
                else
                {
                    price = StoreItemData.GetPrice(card.supportItem);
                    int count = CountSupportItem(currentShip, card.supportItem);
                    canBuy = currentShip != null && contributedGems >= price && currentShip.HasEmptyEquipmentSlot;
                    subline = count > 0 ? $"\u00d7{count} owned" : StoreItemData.GetDescription(card.supportItem);
                }

                if (card.sublineText != null)
                    card.sublineText.text = subline;
                if (card.buyLabel != null)
                    card.buyLabel.text = $"{price:F0}g";
                if (card.buyButton != null)
                {
                    card.buyButton.interactable = canBuy;
                    card.buyButton.onClick.RemoveAllListeners();
                    if (card.isComponent)
                    {
                        string capturedId = card.componentId;
                        card.buyButton.onClick.AddListener(() => OnBuyComponent(capturedId));
                    }
                    else
                    {
                        StoreItemType capturedItem = card.supportItem;
                        card.buyButton.onClick.AddListener(() => OnBuySupportItem(capturedItem));
                    }
                }
                if (card.buyImage != null)
                {
                    card.buyImage.color = canBuy
                        ? OrbitDockSidebarPanelUI.EquipmentAccent
                        : MoonDockItemTileButtonDisabled;
                }
                if (card.bgImage != null)
                {
                    if (card.isComponent)
                    {
                        float alpha = canBuy ? 0.98f : 0.72f;
                        card.bgImage.color = new Color(
                            MoonDockEquipmentCardBg.r,
                            MoonDockEquipmentCardBg.g,
                            MoonDockEquipmentCardBg.b,
                            alpha);
                    }
                    else
                    {
                        var c = card.bgImage.color;
                        c.a = canBuy ? 0.92f : 0.58f;
                        card.bgImage.color = c;
                    }
                }
            }

            ApplyMoonDockCardGridWidth();
        }

        private void RebuildMoonDockEquipmentStore(ShipFamilyDefinition family)
        {
            if (_moonDockStoreGridContent == null)
                return;

            _moonDockStoreCards.Clear();
            for (int c = _moonDockStoreGridContent.childCount - 1; c >= 0; c--)
                Destroy(_moonDockStoreGridContent.GetChild(c).gameObject);

            int shipLevel = currentShip != null ? currentShip.ShipLevel : 1;
            if (family?.components != null)
            {
                var sorted = new List<ShipFamilyComponentEntry>();
                for (int i = 0; i < family.components.Count; i++)
                {
                    if (family.components[i] != null)
                        sorted.Add(family.components[i]);
                }
                sorted.Sort((a, b) =>
                {
                    string nameA = ShipComponentStoreData.GetDisplayName(a);
                    string nameB = ShipComponentStoreData.GetDisplayName(b);
                    int cmp = string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0)
                        return cmp;
                    return string.Compare(a?.componentId, b?.componentId, StringComparison.OrdinalIgnoreCase);
                });

                for (int i = 0; i < sorted.Count; i++)
                    CreateMoonDockEquipmentStoreCard(_moonDockStoreGridContent, sorted[i], shipLevel);
            }

            foreach (StoreItemType item in Enum.GetValues(typeof(StoreItemType)))
            {
                if (StoreItemData.IsShipComponent(item))
                    continue;
                CreateMoonDockSupportStoreCard(_moonDockStoreGridContent, item);
            }

            _moonDockStoreBuiltForFamilyKey = family != null ? family.familyId : string.Empty;
            _moonDockEquipmentCardLayoutVersionBuilt = MoonDockEquipmentCardLayoutVersion;
            ApplyMoonDockCardGridWidth();
        }

        private float GetMoonDockItemTileWidth()
        {
            if (_moonDockStoreScrollViewport != null && _moonDockStoreScrollViewport.rect.width > 1f)
            {
                float pad = _moonDockStoreGrid != null
                    ? _moonDockStoreGrid.padding.left + _moonDockStoreGrid.padding.right
                    : 0f;
                return ComputeMoonDockStoreTileWidth(_moonDockStoreScrollViewport.rect.width - pad, MoonDockStoreTileSpacing);
            }

            if (cardsTabContent != null)
            {
                var rt = cardsTabContent.GetComponent<RectTransform>();
                var vlg = cardsTabContent.GetComponent<VerticalLayoutGroup>();
                float pad = vlg != null ? vlg.padding.left + vlg.padding.right : 0f;
                if (rt != null && rt.rect.width > pad + 1f)
                    return ComputeMoonDockStoreTileWidth(rt.rect.width - pad, MoonDockStoreTileSpacing);
            }

            return MoonDockStoreTileMinWidth;
        }

        private static float ComputeMoonDockStoreTileWidth(float rowInnerWidth, float spacing)
        {
            float totalSpacing = spacing * (MoonDockStoreTilesPerRow - 1);
            return Mathf.Max(MoonDockStoreTileMinWidth, (rowInnerWidth - totalSpacing) / MoonDockStoreTilesPerRow);
        }

        private static void ApplyMoonDockTileLayoutToRow(RectTransform row, float tileWidth, float tileHeight = MoonDockStoreCardHeight)
        {
            if (row == null || tileWidth <= 1f) return;
            for (int i = 0; i < row.childCount; i++)
            {
                var child = row.GetChild(i);
                if (child == null) continue;
                var le = child.GetComponent<LayoutElement>();
                if (le == null) le = child.gameObject.AddComponent<LayoutElement>();
                le.flexibleWidth = 0f;
                le.flexibleHeight = 0f;
                le.preferredWidth = tileWidth;
                le.minWidth = tileWidth;
                le.preferredHeight = tileHeight;
                le.minHeight = tileHeight;
            }
        }

        private static float ComputeMoonDockSpinBandWidth(float tileWidth, int tileCount = 3)
        {
            if (tileCount <= 0) return tileWidth;
            return tileCount * tileWidth + MoonDockStoreTileSpacing * (tileCount - 1);
        }

        private void EnsureMoonDockStoreSection()
        {
            if (moonDockStoreSection != null || moonDockCenterCardsHost == null)
                return;

            moonDockStoreSection = new GameObject("MoonDockStoreSection");
            moonDockStoreSection.transform.SetParent(moonDockCenterCardsHost, false);
            var sectionVlg = moonDockStoreSection.AddComponent<VerticalLayoutGroup>();
            sectionVlg.spacing = 4f;
            sectionVlg.padding = new RectOffset(0, 0, 0, 0);
            sectionVlg.childAlignment = TextAnchor.UpperCenter;
            sectionVlg.childControlWidth = true;
            sectionVlg.childControlHeight = true;
            sectionVlg.childForceExpandWidth = true;
            sectionVlg.childForceExpandHeight = true;
            var sectionLe = moonDockStoreSection.AddComponent<LayoutElement>();
            sectionLe.flexibleHeight = 1f;
            sectionLe.flexibleWidth = 1f;

            CreateMoonDockSectionHeader(
                moonDockStoreSection.transform,
                OrbitDockSidebarPanelUI.SectionTitleEquipment,
                "Buy ship components and support gear — fills equipment slots.",
                OrbitDockSidebarPanelUI.EquipmentAccent);

            var scrollGo = new GameObject("EquipmentStoreScroll");
            scrollGo.transform.SetParent(moonDockStoreSection.transform, false);
            var scrollLe = scrollGo.AddComponent<LayoutElement>();
            scrollLe.flexibleHeight = 1f;
            scrollLe.minHeight = MoonDockEquipmentScrollMinHeight;
            scrollLe.flexibleWidth = 1f;
            var scrollRect = scrollGo.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;

            var viewportGo = new GameObject("Viewport");
            viewportGo.transform.SetParent(scrollGo.transform, false);
            _moonDockStoreScrollViewport = viewportGo.AddComponent<RectTransform>();
            _moonDockStoreScrollViewport.anchorMin = Vector2.zero;
            _moonDockStoreScrollViewport.anchorMax = Vector2.one;
            _moonDockStoreScrollViewport.offsetMin = Vector2.zero;
            _moonDockStoreScrollViewport.offsetMax = Vector2.zero;
            viewportGo.AddComponent<RectMask2D>();
            var viewportImg = viewportGo.AddComponent<Image>();
            viewportImg.color = new Color(1f, 1f, 1f, 0.02f);
            scrollRect.viewport = _moonDockStoreScrollViewport;

            var contentGo = new GameObject("EquipmentGrid");
            contentGo.transform.SetParent(viewportGo.transform, false);
            _moonDockStoreGridContent = contentGo.AddComponent<RectTransform>();
            _moonDockStoreGridContent.anchorMin = new Vector2(0f, 1f);
            _moonDockStoreGridContent.anchorMax = new Vector2(1f, 1f);
            _moonDockStoreGridContent.pivot = new Vector2(0.5f, 1f);
            _moonDockStoreGridContent.anchoredPosition = Vector2.zero;
            _moonDockStoreGrid = contentGo.AddComponent<GridLayoutGroup>();
            _moonDockStoreGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            _moonDockStoreGrid.constraintCount = MoonDockStoreTilesPerRow;
            _moonDockStoreGrid.spacing = new Vector2(MoonDockStoreTileSpacing, MoonDockStoreTileSpacing);
            _moonDockStoreGrid.padding = new RectOffset(0, 0, 0, 8);
            _moonDockStoreGrid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            _moonDockStoreGrid.startAxis = GridLayoutGroup.Axis.Horizontal;
            _moonDockStoreGrid.childAlignment = TextAnchor.UpperLeft;
            var contentFitter = contentGo.AddComponent<ContentSizeFitter>();
            contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = _moonDockStoreGridContent;
        }

        private void ApplyMoonDockUpgradeCardsSectionHeight()
        {
            if (cardsTabContent == null)
                return;

            const float sectionSpacing = 6f;
            const float spinButtonHeight = 36f;
            const float spinBlockSpacing = 6f;
            const float spinRowHeight = MoonDockUpgradeSpinCardHeight;
            float verticalPadding = _moonDockLayoutActive ? 12f : 16f;
            float contentHeight = MoonDockSectionHeaderHeight + sectionSpacing + spinButtonHeight + spinBlockSpacing + spinRowHeight + verticalPadding;

            var cardsLe = cardsTabContent.GetComponent<LayoutElement>();
            if (cardsLe != null)
            {
                cardsLe.flexibleHeight = 0f;
                cardsLe.minHeight = contentHeight;
                cardsLe.preferredHeight = contentHeight;
            }

            _cardsContentHeight = contentHeight;
        }

        private void CreateMoonDockEquipmentStoreCard(Transform parent, ShipFamilyComponentEntry entry, int shipLevel)
        {
            if (entry == null) return;

            Color accentColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(
                ShipComponentStoreData.GetAbilityColorStatIndex(entry), 0.92f);
            ShipFamilyDefinition family = CardShopSystem.Instance != null && currentShip != null
                ? CardShopSystem.Instance.GetShipFamilyForShip(currentShip)
                : null;
            float price = ShipComponentStoreData.GetComponentGemPrice(entry, shipLevel);
            float power = ShipComponentStoreData.GetComponentPowerScore(entry, shipLevel, family);

            CreateMoonDockEquipmentItemTile(
                parent,
                "Component_" + entry.componentId,
                MoonDockEquipmentCardBg,
                accentColor,
                $"{price:F0}g",
                componentStatLayout: true,
                out GameObject root,
                out _,
                out Image cardBg,
                out Image iconImg,
                out TextMeshProUGUI iconGlyph,
                out TextMeshProUGUI titleTmp,
                out TextMeshProUGUI descriptionTmp,
                out TextMeshProUGUI sublineTmp,
                out ShipUpgradeTreePowerBarUI powerBar,
                out Button buyBtn,
                out Image buyImg);

            if (titleTmp != null)
                titleTmp.text = ShipComponentStoreData.GetDisplayName(entry);
            if (descriptionTmp != null)
                ApplyEquipmentCardAbilityDescription(descriptionTmp, entry, shipLevel, family);
            if (sublineTmp != null)
                sublineTmp.text = FormatMoonDockEquipmentSubline(shipLevel, power, owned: false);

            ApplyMoonDockEquipmentTileIcon(
                iconImg,
                iconGlyph,
                ShipComponentStoreData.GetMenuPreviewSprite(family, entry, currentShip != null ? currentShip.ShipTeam : TeamManager.Team.None),
                ShipComponentStoreData.GetIconGlyph(entry));

            if (powerBar != null)
            {
                float maxPower = GetMoonDockComponentMaxDisplayPower(family, shipLevel);
                float trackW = Mathf.Max(40f, MoonDockStoreTileMinWidth - 14f);
                powerBar.ConfigureLayoutScale(1f, 1f);
                powerBar.ApplyEquipmentBreakdown(ShipComponentStoreData.GetPowerBreakdown(entry, shipLevel, family), maxPower, trackW);
            }

            _moonDockStoreCards.Add(new MoonDockStoreCardBinding
            {
                isComponent = true,
                componentId = entry.componentId,
                root = root,
                bgImage = cardBg,
                iconImage = iconImg,
                iconGlyph = iconGlyph,
                titleText = titleTmp,
                descriptionText = descriptionTmp,
                sublineText = sublineTmp,
                powerBar = powerBar,
                buyButton = buyBtn,
                buyImage = buyImg,
                buyLabel = buyBtn != null ? buyBtn.GetComponentInChildren<TextMeshProUGUI>() : null
            });
        }

        private void CreateMoonDockSupportStoreCard(Transform parent, StoreItemType itemType)
        {
            Color cardColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(
                StoreItemData.GetAbilityColorStatIndex(itemType), 0.92f);

            CreateMoonDockEquipmentItemTile(
                parent,
                "Support_" + itemType,
                cardColor,
                OrbitDockSidebarPanelUI.EquipmentAccent,
                $"{StoreItemData.GetPrice(itemType):F0}g",
                componentStatLayout: false,
                out GameObject root,
                out _,
                out Image cardBg,
                out Image iconImg,
                out TextMeshProUGUI iconGlyph,
                out TextMeshProUGUI titleTmp,
                out TextMeshProUGUI descriptionTmp,
                out TextMeshProUGUI sublineTmp,
                out _,
                out Button buyBtn,
                out Image buyImg);

            if (titleTmp != null)
                titleTmp.text = StoreItemData.GetShortDisplayName(itemType);
            if (descriptionTmp != null)
                descriptionTmp.text = StoreItemData.GetDescription(itemType);
            ApplyMoonDockEquipmentTileIcon(iconImg, iconGlyph, null, StoreItemData.GetIconGlyph(itemType));

            _moonDockStoreCards.Add(new MoonDockStoreCardBinding
            {
                isComponent = false,
                supportItem = itemType,
                root = root,
                bgImage = cardBg,
                iconImage = iconImg,
                iconGlyph = iconGlyph,
                titleText = titleTmp,
                descriptionText = descriptionTmp,
                sublineText = sublineTmp,
                buyButton = buyBtn,
                buyImage = buyImg,
                buyLabel = buyBtn != null ? buyBtn.GetComponentInChildren<TextMeshProUGUI>() : null
            });
        }

        private static void ApplyMoonDockEquipmentTileIcon(Image iconImage, TextMeshProUGUI iconGlyph, Sprite sprite, string fallbackGlyph)
        {
            bool hasSprite = sprite != null;
            if (iconImage != null)
            {
                iconImage.sprite = sprite;
                iconImage.color = hasSprite ? Color.white : new Color(1f, 1f, 1f, 0f);
                iconImage.preserveAspect = true;
            }

            if (iconGlyph != null)
            {
                iconGlyph.gameObject.SetActive(!hasSprite);
                if (!hasSprite && !string.IsNullOrEmpty(fallbackGlyph))
                    iconGlyph.text = fallbackGlyph;
            }
        }

        private static string FormatMoonDockEquipmentSubline(int shipLevel, float power, bool owned)
        {
            int level = Mathf.Max(1, shipLevel);
            if (owned)
                return $"Equipped · Lv {level}";
            return $"Lv {level} · PWR {power:F0}";
        }

        private static string BuildMoonDockComponentStatRichText(ShipFamilyComponentEntry entry, int shipLevel, ShipFamilyDefinition family, int maxLines = 8)
        {
            if (entry == null)
                return string.Empty;

            ShipFamilyPowerScoreBreakdown breakdown = ShipComponentStoreData.GetPowerBreakdown(entry, shipLevel, family);
            ShipComponentAbilityStats effective = ShipComponentStoreData.GetEffectiveStatsForDisplay(entry, shipLevel, family);
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(entry.componentId);
            var sb = new StringBuilder(128);
            int count = 0;
            for (int i = 0; i < ShipFamilyPowerScoreBreakdown.DisplayStatCount; i++)
            {
                float value = breakdown.GetDisplayStatValue(i);
                if (Mathf.Abs(value) < 0.05f)
                    continue;
                if (count >= maxLines)
                    break;

                AppendMoonDockComponentStatLine(sb, ref count, maxLines, value, ShipAbilityCategoryColors.PowerBreakdownStatFullLabels[i], i);
            }

            if (partType == "Weapon")
                AppendMoonDockComponentStatLine(sb, ref count, maxLines, effective.fireRate, "Fire Rate", 1);
            AppendMoonDockComponentStatLine(sb, ref count, maxLines, effective.rammingPower, "Ramming Power", 0);

            return count == 0 ? "<color=#888888>—</color>" : sb.ToString();
        }

        private static void AppendMoonDockComponentStatLine(
            StringBuilder sb,
            ref int count,
            int maxLines,
            float value,
            string label,
            int colorStatIndex)
        {
            if (count >= maxLines || Mathf.Abs(value) < 0.05f)
                return;

            if (count > 0)
                sb.Append('\n');

            Color statColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(colorStatIndex);
            sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(statColor)).Append('>');
            sb.Append('+').Append(FormatMoonDockComponentStatValue(value)).Append(' ');
            sb.Append(label);
            sb.Append("</color>");
            count++;
        }

        private static void ApplyEquipmentCardAbilityDescription(
            TextMeshProUGUI descriptionTmp,
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null)
        {
            if (descriptionTmp == null)
                return;

            descriptionTmp.text = entry != null
                ? BuildMoonDockComponentStatRichText(entry, shipLevel, family)
                : string.Empty;
            descriptionTmp.ForceMeshUpdate(true);

            RectTransform descriptionRt = descriptionTmp.rectTransform;
            if (descriptionRt != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(descriptionRt);
                Transform layoutRoot = descriptionRt.parent;
                if (layoutRoot is RectTransform parentRt)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);
            }
        }

        private static string FormatMoonDockComponentStatValue(float value)
        {
            if (Mathf.Abs(value - Mathf.Round(value)) < 0.05f)
                return Mathf.RoundToInt(value).ToString();
            return value.ToString("0.#");
        }

        private float GetMoonDockComponentMaxDisplayPower(ShipFamilyDefinition family, int shipLevel)
        {
            float max = 0.001f;
            if (family?.components == null)
                return max;

            for (int i = 0; i < family.components.Count; i++)
            {
                ShipFamilyComponentEntry entry = family.components[i];
                if (entry == null)
                    continue;
                float total = ShipUpgradeTreePowerBarUI.GetEquipmentBarDisplayTotal(
                    ShipComponentStoreData.GetPowerBreakdown(entry, shipLevel, family));
                if (total > max)
                    max = total;
            }

            return max;
        }

        private void CreateMoonDockEquipmentItemTile(
            Transform parent,
            string tileName,
            Color tileColor,
            Color accentColor,
            string actionLabel,
            bool componentStatLayout,
            out GameObject root,
            out Image accentImage,
            out Image bgImage,
            out Image iconImage,
            out TextMeshProUGUI iconGlyphText,
            out TextMeshProUGUI titleText,
            out TextMeshProUGUI descriptionText,
            out TextMeshProUGUI sublineText,
            out ShipUpgradeTreePowerBarUI powerBar,
            out Button actionButton,
            out Image actionButtonImage)
        {
            powerBar = null;
            float cardHeight = componentStatLayout ? MoonDockEquipmentCardHeight : MoonDockStoreCardHeight;
            float iconHeight = componentStatLayout ? MoonDockEquipmentIconHeight : 28f;
            float iconMinHeight = componentStatLayout ? MoonDockEquipmentIconMinHeight : 24f;

            root = new GameObject(tileName);
            root.transform.SetParent(parent, false);
            var cardLe = root.AddComponent<LayoutElement>();
            cardLe.flexibleWidth = 0f;
            cardLe.flexibleHeight = 0f;
            cardLe.minWidth = MoonDockStoreTileMinWidth;
            cardLe.preferredWidth = MoonDockStoreTileMinWidth;
            cardLe.preferredHeight = cardHeight;
            cardLe.minHeight = cardHeight;

            bgImage = root.AddComponent<Image>();
            bgImage.color = tileColor;
            bgImage.raycastTarget = false;
            var cardOutline = root.AddComponent<Outline>();
            cardOutline.effectColor = MoonDockStoreCardFrameColor;
            cardOutline.effectDistance = new Vector2(1f, 1f);

            if (!componentStatLayout)
            {
                var innerShadeGo = new GameObject("InnerShade");
                innerShadeGo.transform.SetParent(root.transform, false);
                var innerShadeRt = innerShadeGo.AddComponent<RectTransform>();
                innerShadeRt.anchorMin = Vector2.zero;
                innerShadeRt.anchorMax = Vector2.one;
                innerShadeRt.offsetMin = new Vector2(3f, 3f);
                innerShadeRt.offsetMax = new Vector2(-3f, -3f);
                var innerShadeImg = innerShadeGo.AddComponent<Image>();
                innerShadeImg.color = MoonDockStoreCardInnerShade;
                innerShadeImg.raycastTarget = false;
                innerShadeGo.AddComponent<LayoutElement>().ignoreLayout = true;
            }

            var cardVlg = root.AddComponent<VerticalLayoutGroup>();
            cardVlg.spacing = componentStatLayout ? 3f : 2f;
            cardVlg.padding = new RectOffset(4, 4, 5, 4);
            cardVlg.childAlignment = TextAnchor.UpperCenter;
            cardVlg.childControlWidth = true;
            cardVlg.childControlHeight = true;
            cardVlg.childForceExpandWidth = true;
            cardVlg.childForceExpandHeight = componentStatLayout;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(root.transform, false);
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.preferredHeight = componentStatLayout ? 4f : 3f;
            accentLe.minHeight = componentStatLayout ? 4f : 3f;
            accentImage = accentGo.AddComponent<Image>();
            accentImage.color = accentColor;
            accentImage.raycastTarget = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(root.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = componentStatLayout ? 16f : 18f;
            titleLe.minHeight = componentStatLayout ? 14f : 16f;
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.fontSize = componentStatLayout ? 9.5f : 10f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = Color.white;
            titleText.overflowMode = TextOverflowModes.Ellipsis;
            titleText.raycastTarget = false;
            if (fontAsset != null) titleText.font = fontAsset;

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(root.transform, false);
            var iconLe = iconGo.AddComponent<LayoutElement>();
            iconLe.flexibleHeight = componentStatLayout ? 1f : 0f;
            iconLe.minHeight = iconMinHeight;
            iconLe.preferredHeight = iconHeight;
            iconImage = iconGo.AddComponent<Image>();
            iconImage.color = new Color(1f, 1f, 1f, 0f);
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            var glyphGo = new GameObject("Glyph");
            glyphGo.transform.SetParent(iconGo.transform, false);
            var glyphRt = glyphGo.AddComponent<RectTransform>();
            glyphRt.anchorMin = Vector2.zero;
            glyphRt.anchorMax = Vector2.one;
            glyphRt.offsetMin = Vector2.zero;
            glyphRt.offsetMax = Vector2.zero;
            iconGlyphText = glyphGo.AddComponent<TextMeshProUGUI>();
            iconGlyphText.fontSize = componentStatLayout ? 42f : 22f;
            iconGlyphText.alignment = TextAlignmentOptions.Center;
            iconGlyphText.color = new Color(1f, 1f, 1f, 0.95f);
            iconGlyphText.raycastTarget = false;
            if (fontAsset != null) iconGlyphText.font = fontAsset;

            Transform statsParent = root.transform;
            if (componentStatLayout)
            {
                var statsFooterGo = new GameObject("StatsFooter");
                statsFooterGo.transform.SetParent(root.transform, false);
                var statsFooterLe = statsFooterGo.AddComponent<LayoutElement>();
                statsFooterLe.flexibleHeight = 0f;
                statsFooterLe.minHeight = MoonDockEquipmentStatsFooterHeight;
                statsFooterLe.preferredHeight = MoonDockEquipmentStatsFooterHeight;
                var statsFooterBg = statsFooterGo.AddComponent<Image>();
                statsFooterBg.color = MoonDockEquipmentStatsFooterBg;
                statsFooterBg.raycastTarget = false;
                var statsFooterVlg = statsFooterGo.AddComponent<VerticalLayoutGroup>();
                statsFooterVlg.spacing = 2f;
                statsFooterVlg.padding = new RectOffset(4, 4, 4, 4);
                statsFooterVlg.childAlignment = TextAnchor.UpperCenter;
                statsFooterVlg.childControlWidth = true;
                statsFooterVlg.childControlHeight = true;
                statsFooterVlg.childForceExpandWidth = true;
                statsFooterVlg.childForceExpandHeight = false;
                statsParent = statsFooterGo.transform;
            }

            var descriptionGo = new GameObject("Description");
            descriptionGo.transform.SetParent(statsParent, false);
            var descriptionLe = descriptionGo.AddComponent<LayoutElement>();
            if (componentStatLayout)
            {
                descriptionLe.flexibleHeight = 0f;
                descriptionLe.minHeight = MoonDockEquipmentAbilityAreaHeight;
                descriptionLe.preferredHeight = MoonDockEquipmentAbilityAreaHeight;
            }
            else
            {
                descriptionLe.flexibleHeight = 1f;
                descriptionLe.minHeight = 36f;
                descriptionLe.preferredHeight = 44f;
            }
            descriptionText = descriptionGo.AddComponent<TextMeshProUGUI>();
            descriptionText.fontSize = componentStatLayout ? MoonDockEquipmentAbilityFontSize : 9f;
            descriptionText.alignment = componentStatLayout ? TextAlignmentOptions.Top : TextAlignmentOptions.Top;
            descriptionText.color = new Color(0.92f, 0.95f, 1f, 0.92f);
            descriptionText.enableWordWrapping = true;
            descriptionText.richText = componentStatLayout;
            descriptionText.overflowMode = componentStatLayout ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            descriptionText.maxVisibleLines = componentStatLayout ? 8 : 4;
            descriptionText.raycastTarget = false;
            if (fontAsset != null) descriptionText.font = fontAsset;

            if (componentStatLayout)
            {
                powerBar = ShipUpgradeTreePowerBarUI.CreateInTrack(
                    statsParent,
                    MoonDockEquipmentPowerBarTrackBg,
                    MoonDockEquipmentPowerBarHeight,
                    MoonDockEquipmentPowerBarPairGap,
                    Mathf.Max(40f, MoonDockStoreTileMinWidth - 14f));
            }

            var sublineGo = new GameObject("Subline");
            sublineGo.transform.SetParent(root.transform, false);
            var sublineLe = sublineGo.AddComponent<LayoutElement>();
            sublineLe.preferredHeight = componentStatLayout ? 12f : 12f;
            sublineLe.minHeight = 10f;
            sublineText = sublineGo.AddComponent<TextMeshProUGUI>();
            sublineText.fontSize = componentStatLayout ? 9f : 9f;
            sublineText.fontStyle = FontStyles.Bold;
            sublineText.alignment = TextAlignmentOptions.Center;
            sublineText.color = new Color(1f, 1f, 1f, 0.82f);
            sublineText.overflowMode = TextOverflowModes.Ellipsis;
            sublineText.raycastTarget = false;
            if (fontAsset != null) sublineText.font = fontAsset;

            var actionGo = new GameObject("Action");
            actionGo.transform.SetParent(root.transform, false);
            var actionLe = actionGo.AddComponent<LayoutElement>();
            actionLe.preferredHeight = 22f;
            actionLe.minHeight = 20f;
            actionButtonImage = actionGo.AddComponent<Image>();
            actionButtonImage.color = MoonDockItemTileButtonIdle;
            if (buttonSprite != null)
            {
                actionButtonImage.sprite = buttonSprite;
                actionButtonImage.type = Image.Type.Sliced;
            }
            actionButton = actionGo.AddComponent<Button>();
            actionButton.targetGraphic = actionButtonImage;
            var actionLabelGo = new GameObject("Label");
            actionLabelGo.transform.SetParent(actionGo.transform, false);
            var actionLabelRt = actionLabelGo.AddComponent<RectTransform>();
            actionLabelRt.anchorMin = Vector2.zero;
            actionLabelRt.anchorMax = Vector2.one;
            actionLabelRt.offsetMin = new Vector2(2f, 1f);
            actionLabelRt.offsetMax = new Vector2(-2f, -1f);
            var actionLabelTmp = actionLabelGo.AddComponent<TextMeshProUGUI>();
            actionLabelTmp.text = actionLabel;
            actionLabelTmp.fontSize = 10f;
            actionLabelTmp.fontStyle = FontStyles.Bold;
            actionLabelTmp.alignment = TextAlignmentOptions.Center;
            actionLabelTmp.color = Color.white;
            actionLabelTmp.raycastTarget = false;
            if (fontAsset != null) actionLabelTmp.font = fontAsset;
        }

        private static Color GetEquipmentSlotColor(StoreItemType itemType)
        {
            if (StoreItemData.IsShipComponent(itemType))
                return new Color(0.18f, 0.28f, 0.42f, 0.95f);
            return ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(
                StoreItemData.GetAbilityColorStatIndex(itemType));
        }

        private static Color GetEquipmentSlotColor(ShipFamilyComponentEntry entry)
        {
            return ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(
                ShipComponentStoreData.GetAbilityColorStatIndex(entry));
        }

        private static Color GetEquipmentSlotBorderColor(StoreItemType itemType)
        {
            Color c = GetEquipmentSlotColor(itemType);
            return new Color(Mathf.Min(1f, c.r + 0.12f), Mathf.Min(1f, c.g + 0.12f), Mathf.Min(1f, c.b + 0.12f), 0.95f);
        }

        private static Color GetEquipmentSlotBorderColor(ShipFamilyComponentEntry entry)
        {
            Color c = GetEquipmentSlotColor(entry);
            return new Color(Mathf.Min(1f, c.r + 0.12f), Mathf.Min(1f, c.g + 0.12f), Mathf.Min(1f, c.b + 0.12f), 0.95f);
        }

        private static int CountSupportItem(Starship ship, StoreItemType item)
        {
            if (ship == null)
                return 0;

            int total = 0;
            var equipment = ship.EquippedEquipment;
            for (int i = 0; i < equipment.Count; i++)
            {
                if (equipment[i].ItemType != item)
                    continue;
                total += StoreItemData.IsDrone(item) ? 1 : Mathf.Max(1, equipment[i].remainingCharges);
            }
            return total;
        }

        private void OnBuySupportItem(StoreItemType item)
        {
            if (currentShip == null || currentHomePlanet == null || HomePlanetStoreSystem.Instance == null)
                return;
            if (StoreItemData.IsShipComponent(item))
                return;
            if (OrbitStationEcsContext.UseEcsStoreRpc)
            {
                HomePlanetStoreSystem.Instance.PurchaseItemServerRpc(0, 0, item);
                pendingGemsRequest = true;
                HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
                return;
            }
            var homeNo = currentHomePlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (homeNo == null || !homeNo.IsSpawned)
                return;
            HomePlanetStoreSystem.Instance.PurchaseItemServerRpc(homeNo.NetworkObjectId, currentShip.NetworkObjectId, item);
            pendingGemsRequest = true;
            HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private void OnBuyComponent(string componentId)
        {
            if (currentShip == null || currentHomePlanet == null || HomePlanetStoreSystem.Instance == null)
                return;
            if (string.IsNullOrWhiteSpace(componentId))
                return;
            var homeNo = currentHomePlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (homeNo == null || !homeNo.IsSpawned)
                return;
            HomePlanetStoreSystem.Instance.PurchaseComponentServerRpc(homeNo.NetworkObjectId, currentShip.NetworkObjectId, componentId);
            pendingGemsRequest = true;
            HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private void ApplySidebarSlotGridLayout(int slotCount)
        {
            if (slotGridRoot == null)
                return;

            var gridLayout = slotGridRoot.GetComponent<GridLayoutGroup>();
            if (gridLayout == null)
                return;

            int rows = Mathf.Max(1, Mathf.Min(MaxSlotRows, slotCount));
            float cardHeight = _upgradeCardSlotRichLayoutActive ? SidebarUpgradeCardSlotHeight : SidebarSlotCardHeight;
            float slotGridTotalH = rows * cardHeight + (rows - 1) * SidebarSlotCellSpacing;
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = SidebarSlotColumns;
            gridLayout.cellSize = new Vector2(SidebarSlotCardWidth, cardHeight);
            gridLayout.spacing = new Vector2(SidebarSlotCellSpacing, SidebarSlotCellSpacing);

            if (slotPanelRect != null)
            {
                float slotPanelHeight = 8f + slotGridTotalH + 8f;
                var slotLe = slotPanel.GetComponent<LayoutElement>();
                if (slotLe == null)
                    slotLe = slotPanel.AddComponent<LayoutElement>();
                slotLe.preferredHeight = slotPanelHeight;
                slotLe.minHeight = slotPanelHeight;
                slotLe.flexibleHeight = 0f;
                slotLe.flexibleWidth = 1f;
            }

            if (slotGridRect != null)
            {
                slotGridRect.anchorMin = Vector2.zero;
                slotGridRect.anchorMax = Vector2.one;
                slotGridRect.pivot = new Vector2(0.5f, 0.5f);
                slotGridRect.offsetMin = Vector2.zero;
                slotGridRect.offsetMax = Vector2.zero;
                slotGridRect.anchoredPosition = Vector2.zero;
            }

            if (orbitDockSidebar != null && orbitDockSidebar.LoadoutHost != null)
            {
                var loadoutLe = orbitDockSidebar.LoadoutHost.GetComponent<LayoutElement>();
                if (loadoutLe != null)
                {
                    float hostH = 8f + slotGridTotalH + 8f;
                    loadoutLe.preferredHeight = hostH;
                    loadoutLe.minHeight = hostH;
                }
            }
        }

        private void BuildShipsTabByLevel(List<ShipUnlockEntry> unlocked, float contributedGems)
        {
            if (shipsRowsContainer == null)
            {
                EnsurePanelExists();
                if (shipsRowsContainer == null) return;
            }
            for (int i = 0; i < chassisButtons.Length; i++)
            {
                chassisButtons[i] = null;
                chassisLabels[i] = null;
                shipUnlockEntries[i] = null;
            }
            for (int c = shipsRowsContainer.childCount - 1; c >= 0; c--)
            {
                var child = shipsRowsContainer.GetChild(c);
                if (child != null && child.gameObject != null) Destroy(child.gameObject);
            }
            if (unlocked == null || unlocked.Count == 0)
            {
                AddNoShipsPlaceholderRow();
                return;
            }

            var byTier = new Dictionary<int, List<ShipUnlockEntry>>();
            foreach (var entry in unlocked)
            {
                if (entry?.chassis == null) continue;
                int tier = Mathf.Max(1, entry.minHomePlanetLevel);
                if (!byTier.ContainsKey(tier)) byTier[tier] = new List<ShipUnlockEntry>();
                byTier[tier].Add(entry);
            }
            var tiersDesc = new List<int>(byTier.Keys);
            tiersDesc.Sort((a, b) => b.CompareTo(a));

            int globalIndex = 0;
            foreach (int tier in tiersDesc)
            {
                var entries = byTier[tier];
                if (entries == null || entries.Count == 0) continue;
                GameObject rowGo = new GameObject("ShipRow_Lv" + tier);
                rowGo.transform.SetParent(shipsRowsContainer, false);
                var rowRect = rowGo.AddComponent<RectTransform>();
                rowRect.sizeDelta = new Vector2(0f, ShipCardHeight);
                var rowLE = rowGo.AddComponent<LayoutElement>();
                rowLE.preferredHeight = ShipCardHeight;
                rowLE.minHeight = ShipCardHeight;
                var rowLayout = rowGo.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = 8f;
                rowLayout.childAlignment = TextAnchor.MiddleCenter;
                rowLayout.childControlWidth = false;
                rowLayout.childControlHeight = true;
                rowLayout.childForceExpandWidth = false;
                rowLayout.childForceExpandHeight = true;

                foreach (var entry in entries)
                {
                    if (globalIndex >= MaxShipCards) break;
                    CreateShipCard(rowGo.transform, entry, globalIndex, contributedGems);
                    shipUnlockEntries[globalIndex] = entry;
                    globalIndex++;
                }
            }
            float totalHeight = tiersDesc.Count * (ShipCardHeight + ShipRowSpacing) + 60f;
            var shipsRowsRect = shipsRowsContainer as RectTransform;
            if (shipsRowsRect != null)
            {
                shipsRowsRect.sizeDelta = new Vector2(shipsRowsRect.sizeDelta.x, Mathf.Max(400f, totalHeight));
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(shipsRowsRect);
            }
            if (storeContentRoot != null)
            {
                float contentH = storeContentRoot.sizeDelta.y;
                if (totalHeight + 80f > contentH)
                    storeContentRoot.sizeDelta = new Vector2(0f, totalHeight + 80f);
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);
            }
        }

        private void AddNoShipsPlaceholderRow()
        {
            if (shipsRowsContainer == null) return;
            GameObject rowGo = new GameObject("ShipRow_Empty");
            rowGo.transform.SetParent(shipsRowsContainer, false);
            var rowRect = rowGo.AddComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0f, ShipCardHeight);
            var rowLE = rowGo.AddComponent<LayoutElement>();
            rowLE.preferredHeight = ShipCardHeight;
            var tmp = rowGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "No ships available. Dock at your home planet or level up to unlock more.";
            tmp.fontSize = 14;
            tmp.color = new Color(0.85f, 0.9f, 1f, 0.95f);
            tmp.enableWordWrapping = true;
            if (fontAsset != null) tmp.font = fontAsset;
            var shipsRowsRect = shipsRowsContainer as RectTransform;
            if (shipsRowsRect != null)
            {
                shipsRowsRect.sizeDelta = new Vector2(shipsRowsRect.sizeDelta.x, Mathf.Max(400f, ShipCardHeight + 60f));
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(shipsRowsRect);
            }
            if (storeContentRoot != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);
        }

        private void CreateShipCard(Transform rowParent, ShipUnlockEntry entry, int index, float contributedGems)
        {
            ShipChassisDefinition chassis = entry?.chassis;
            if (chassis == null) return;
            int tierLevel = Mathf.Max(1, entry.minHomePlanetLevel);
            int cost = entry.gemCost > 0f
                ? Mathf.RoundToInt(entry.gemCost)
                : (CardShopSystem.Instance != null && chassis != null
                    ? CardShopSystem.Instance.GetPurchaseGemCostForChassisId(chassis.chassisId, tierLevel)
                    : 0);
            string family = string.IsNullOrEmpty(chassis.shipFamily) ? "Ship" : chassis.shipFamily;
            bool canAfford = contributedGems >= cost;

            GameObject cardGo = new GameObject("ShipCard_" + index);
            cardGo.transform.SetParent(rowParent, false);
            var cardRect = cardGo.AddComponent<RectTransform>();
            cardRect.sizeDelta = new Vector2(ShipCardWidth, ShipCardHeight);
            var cardLE = cardGo.AddComponent<LayoutElement>();
            cardLE.preferredWidth = ShipCardWidth;
            cardLE.preferredHeight = ShipCardHeight;
            var cardImg = cardGo.AddComponent<Image>();
            cardImg.color = new Color(0.15f, 0.28f, 0.5f, 0.95f);
            if (buttonSprite != null) { cardImg.sprite = buttonSprite; cardImg.type = Image.Type.Sliced; }
            var cardBtn = cardGo.AddComponent<Button>();

            var horz = cardGo.AddComponent<HorizontalLayoutGroup>();
            horz.spacing = 6f;
            horz.padding = new RectOffset(4, 4, 4, 4);
            horz.childAlignment = TextAnchor.MiddleLeft;
            horz.childControlWidth = true;
            horz.childControlHeight = true;
            horz.childForceExpandWidth = false;
            horz.childForceExpandHeight = true;

            var previewGo = new GameObject("Preview");
            previewGo.transform.SetParent(cardGo.transform, false);
            var previewRect = previewGo.AddComponent<RectTransform>();
            previewRect.sizeDelta = new Vector2(ShipCardPreviewSize, ShipCardPreviewSize);
            var previewLE = previewGo.AddComponent<LayoutElement>();
            previewLE.preferredWidth = ShipCardPreviewSize;
            previewLE.preferredHeight = ShipCardPreviewSize;
            var previewImg = previewGo.AddComponent<Image>();
            previewImg.color = new Color(0.08f, 0.1f, 0.18f, 0.95f);
            if (CardShopSystem.Instance != null)
            {
                TeamManager.Team team = currentShip != null ? currentShip.ShipTeam : TeamManager.Team.None;
                Sprite previewSprite = CardShopSystem.Instance.GetMenuPreviewSpriteForChassisId(chassis.chassisId, team);
                if (previewSprite != null)
                {
                    previewImg.sprite = previewSprite;
                    previewImg.preserveAspect = true;
                }
            }

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(cardGo.transform, false);
            var contentLE = contentGo.AddComponent<LayoutElement>();
            contentLE.flexibleWidth = 1f;
            contentLE.preferredHeight = ShipCardHeight - 8f;
            var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
            contentVlg.spacing = 4f;
            contentVlg.childAlignment = TextAnchor.UpperLeft;
            contentVlg.childControlWidth = true;
            contentVlg.childControlHeight = true;
            contentVlg.childForceExpandWidth = true;
            contentVlg.childForceExpandHeight = false;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(contentGo.transform, false);
            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text = $"Lv.{tierLevel} • {chassis.displayName} ({family}) — {cost:F0}g";
            labelTmp.fontSize = 11;
            labelTmp.enableWordWrapping = true;
            labelTmp.overflowMode = TextOverflowModes.Ellipsis;
            labelTmp.color = Color.white;
            labelTmp.raycastTarget = false;
            if (fontAsset != null) labelTmp.font = fontAsset;
            var labelLE = labelGo.AddComponent<LayoutElement>();
            labelLE.flexibleHeight = 1f;

            var buyGo = new GameObject("BuyButton");
            buyGo.transform.SetParent(contentGo.transform, false);
            var buyRect = buyGo.AddComponent<RectTransform>();
            buyRect.sizeDelta = new Vector2(0f, 24f);
            var buyImg = buyGo.AddComponent<Image>();
            buyImg.color = canAfford ? new Color(0.2f, 0.4f, 0.65f, 0.95f) : new Color(0.2f, 0.2f, 0.25f, 0.95f);
            if (buttonSprite != null) { buyImg.sprite = buttonSprite; buyImg.type = Image.Type.Sliced; }
            var buyBtn = buyGo.AddComponent<Button>();
            buyBtn.interactable = canAfford;
            var buyLabelGo = new GameObject("Text");
            buyLabelGo.transform.SetParent(buyGo.transform, false);
            var buyLabelRect = buyLabelGo.AddComponent<RectTransform>();
            buyLabelRect.anchorMin = Vector2.zero;
            buyLabelRect.anchorMax = Vector2.one;
            buyLabelRect.offsetMin = Vector2.zero;
            buyLabelRect.offsetMax = Vector2.zero;
            var buyLabel = buyLabelGo.AddComponent<TextMeshProUGUI>();
            buyLabel.text = $"Buy {cost:F0}g";
            buyLabel.fontSize = 11;
            buyLabel.alignment = TextAlignmentOptions.Center;
            buyLabel.color = Color.white;
            buyLabel.raycastTarget = false;
            if (fontAsset != null) buyLabel.font = fontAsset;

            chassisButtons[index] = cardBtn;
            chassisLabels[index] = labelTmp;
            cardGo.AddComponent<ScrollRectForwarder>();
            int idx = index;
            cardBtn.onClick.AddListener(() => OnBuyChassis(idx));
            buyBtn.onClick.AddListener(() => OnBuyChassis(idx));
        }

        private void EnsureCardRemoveConfirmModal()
        {
            if (cardRemoveConfirmRoot != null) return;
            var canvas = GetComponentInParent<Canvas>();
            Transform parent = canvas != null ? canvas.transform : transform;
            cardRemoveConfirmRoot = new GameObject("CardRemoveConfirm");
            cardRemoveConfirmRoot.transform.SetParent(parent, false);
            var rootRt = cardRemoveConfirmRoot.AddComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;
            var backdrop = new GameObject("Backdrop");
            backdrop.transform.SetParent(cardRemoveConfirmRoot.transform, false);
            var bdRt = backdrop.AddComponent<RectTransform>();
            bdRt.anchorMin = Vector2.zero;
            bdRt.anchorMax = Vector2.one;
            bdRt.offsetMin = Vector2.zero;
            bdRt.offsetMax = Vector2.zero;
            var bdImg = backdrop.AddComponent<Image>();
            bdImg.color = new Color(0.02f, 0.04f, 0.08f, 0.72f);
            var bdBtn = backdrop.AddComponent<Button>();
            bdBtn.targetGraphic = bdImg;
            bdBtn.onClick.AddListener(OnCardRemoveConfirmCancel);

            var panel = new GameObject("Panel");
            panel.transform.SetParent(cardRemoveConfirmRoot.transform, false);
            var panelRt = panel.AddComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(360f, 168f);
            var panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0.1f, 0.12f, 0.18f, 0.98f);
            if (panelBackgroundSprite != null) { panelImg.sprite = panelBackgroundSprite; panelImg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panel.transform, false);
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -12f);
            titleRt.sizeDelta = new Vector2(-32f, 28f);
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "Remove card?";
            titleTmp.fontSize = 18;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.color = new Color(0.95f, 0.97f, 1f, 1f);
            if (fontAsset != null) titleTmp.font = fontAsset;

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(panel.transform, false);
            var bodyRt = bodyGo.AddComponent<RectTransform>();
            bodyRt.anchorMin = new Vector2(0f, 0f);
            bodyRt.anchorMax = new Vector2(1f, 1f);
            bodyRt.offsetMin = new Vector2(20f, 56f);
            bodyRt.offsetMax = new Vector2(-20f, -44f);
            cardRemoveConfirmBodyText = bodyGo.AddComponent<TextMeshProUGUI>();
            cardRemoveConfirmBodyText.text = "";
            cardRemoveConfirmBodyText.fontSize = 14;
            cardRemoveConfirmBodyText.alignment = TextAlignmentOptions.Top;
            cardRemoveConfirmBodyText.enableWordWrapping = true;
            cardRemoveConfirmBodyText.color = new Color(0.82f, 0.88f, 0.96f, 0.98f);
            if (fontAsset != null) cardRemoveConfirmBodyText.font = fontAsset;

            var row = new GameObject("Buttons");
            row.transform.SetParent(panel.transform, false);
            var rowRt = row.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0f, 0f);
            rowRt.anchorMax = new Vector2(1f, 0f);
            rowRt.pivot = new Vector2(0.5f, 0f);
            rowRt.anchoredPosition = new Vector2(0f, 14f);
            rowRt.sizeDelta = new Vector2(-32f, 36f);
            var rowH = row.AddComponent<HorizontalLayoutGroup>();
            rowH.spacing = 12f;
            rowH.childAlignment = TextAnchor.MiddleCenter;
            rowH.childControlWidth = true;
            rowH.childControlHeight = true;
            rowH.childForceExpandWidth = true;
            rowH.childForceExpandHeight = true;

            var cancelGo = new GameObject("Cancel");
            cancelGo.transform.SetParent(row.transform, false);
            var cancelRt = cancelGo.AddComponent<RectTransform>();
            cancelRt.sizeDelta = new Vector2(120f, 36f);
            var cancelImg = cancelGo.AddComponent<Image>();
            cancelImg.color = new Color(0.22f, 0.24f, 0.32f, 0.98f);
            if (buttonSprite != null) { cancelImg.sprite = buttonSprite; cancelImg.type = Image.Type.Sliced; }
            var cancelBtn = cancelGo.AddComponent<Button>();
            cancelBtn.onClick.AddListener(OnCardRemoveConfirmCancel);
            var cancelTxtGo = new GameObject("Text");
            cancelTxtGo.transform.SetParent(cancelGo.transform, false);
            var cancelTxtRt = cancelTxtGo.AddComponent<RectTransform>();
            cancelTxtRt.anchorMin = Vector2.zero;
            cancelTxtRt.anchorMax = Vector2.one;
            cancelTxtRt.offsetMin = new Vector2(8f, 4f);
            cancelTxtRt.offsetMax = new Vector2(-8f, -4f);
            var cancelTmp = cancelTxtGo.AddComponent<TextMeshProUGUI>();
            cancelTmp.text = "Cancel";
            cancelTmp.fontSize = 14;
            cancelTmp.alignment = TextAlignmentOptions.Center;
            cancelTmp.color = Color.white;
            if (fontAsset != null) cancelTmp.font = fontAsset;
            cancelTmp.raycastTarget = false;

            var removeGo = new GameObject("Remove");
            removeGo.transform.SetParent(row.transform, false);
            var removeRt = removeGo.AddComponent<RectTransform>();
            removeRt.sizeDelta = new Vector2(120f, 36f);
            var removeImg = removeGo.AddComponent<Image>();
            removeImg.color = new Color(0.5f, 0.22f, 0.22f, 0.98f);
            if (buttonSprite != null) { removeImg.sprite = buttonSprite; removeImg.type = Image.Type.Sliced; }
            var removeBtn = removeGo.AddComponent<Button>();
            removeBtn.onClick.AddListener(OnCardRemoveConfirmYes);
            var removeTxtGo = new GameObject("Text");
            removeTxtGo.transform.SetParent(removeGo.transform, false);
            var removeTxtRt = removeTxtGo.AddComponent<RectTransform>();
            removeTxtRt.anchorMin = Vector2.zero;
            removeTxtRt.anchorMax = Vector2.one;
            removeTxtRt.offsetMin = new Vector2(8f, 4f);
            removeTxtRt.offsetMax = new Vector2(-8f, -4f);
            var removeTmp = removeTxtGo.AddComponent<TextMeshProUGUI>();
            removeTmp.text = "Remove";
            removeTmp.fontSize = 14;
            removeTmp.alignment = TextAlignmentOptions.Center;
            removeTmp.color = Color.white;
            if (fontAsset != null) removeTmp.font = fontAsset;
            removeTmp.raycastTarget = false;

            cardRemoveConfirmRoot.SetActive(false);
        }

        private void ShowCardRemoveConfirm(int slotIndex)
        {
            if (currentShip == null) return;
            var cards = currentShip.EquippedCards;
            if (cards == null || slotIndex < 0 || slotIndex >= cards.Count) return;
            var c = cards[slotIndex];
            if (c == null) return;
            _pendingRemoveSlotIndex = slotIndex;
            EnsureCardRemoveConfirmModal();
            if (cardRemoveConfirmBodyText != null)
                cardRemoveConfirmBodyText.text = $"Remove \"{c.displayName}\" from your loadout?";
            cardRemoveConfirmRoot.SetActive(true);
            cardRemoveConfirmRoot.transform.SetAsLastSibling();
        }

        private void OnCardRemoveConfirmYes()
        {
            if (currentShip != null && _pendingRemoveSlotIndex >= 0)
                currentShip.RemoveCardServerRpc(_pendingRemoveSlotIndex);
            HideCardRemoveConfirm();
        }

        private void OnCardRemoveConfirmCancel()
        {
            HideCardRemoveConfirm();
        }

        private void HideCardRemoveConfirm()
        {
            _pendingRemoveSlotIndex = -1;
            if (cardRemoveConfirmRoot != null)
                cardRemoveConfirmRoot.SetActive(false);
        }

        private void EnsureEquipmentRemoveConfirmModal()
        {
            if (equipmentRemoveConfirmRoot != null) return;
            equipmentRemoveConfirmRoot = new GameObject("EquipmentRemoveConfirm");
            equipmentRemoveConfirmRoot.transform.SetParent(transform, false);
            var rootRt = equipmentRemoveConfirmRoot.AddComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = rootRt.offsetMax = Vector2.zero;
            var dim = equipmentRemoveConfirmRoot.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.55f);
            dim.raycastTarget = true;

            var box = new GameObject("Box");
            box.transform.SetParent(equipmentRemoveConfirmRoot.transform, false);
            var boxRt = box.AddComponent<RectTransform>();
            boxRt.anchorMin = boxRt.anchorMax = new Vector2(0.5f, 0.5f);
            boxRt.sizeDelta = new Vector2(340f, 140f);
            var boxImg = box.AddComponent<Image>();
            boxImg.color = new Color(0.1f, 0.12f, 0.18f, 0.98f);
            if (panelBackgroundSprite != null) { boxImg.sprite = panelBackgroundSprite; boxImg.type = Image.Type.Sliced; }

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(box.transform, false);
            var bodyRt = bodyGo.AddComponent<RectTransform>();
            bodyRt.anchorMin = new Vector2(0f, 0.35f);
            bodyRt.anchorMax = new Vector2(1f, 1f);
            bodyRt.offsetMin = new Vector2(16f, 8f);
            bodyRt.offsetMax = new Vector2(-16f, -12f);
            equipmentRemoveConfirmBodyText = bodyGo.AddComponent<TextMeshProUGUI>();
            equipmentRemoveConfirmBodyText.text = "Remove equipment?";
            equipmentRemoveConfirmBodyText.fontSize = 14;
            equipmentRemoveConfirmBodyText.alignment = TextAlignmentOptions.Center;
            equipmentRemoveConfirmBodyText.color = Color.white;
            if (fontAsset != null) equipmentRemoveConfirmBodyText.font = fontAsset;

            var row = new GameObject("Buttons");
            row.transform.SetParent(box.transform, false);
            var rowRt = row.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0f, 0f);
            rowRt.anchorMax = new Vector2(1f, 0.35f);
            rowRt.offsetMin = new Vector2(16f, 12f);
            rowRt.offsetMax = new Vector2(-16f, -8f);
            var rowH = row.AddComponent<HorizontalLayoutGroup>();
            rowH.spacing = 12f;
            rowH.childAlignment = TextAnchor.MiddleCenter;
            rowH.childControlWidth = rowH.childControlHeight = true;
            rowH.childForceExpandWidth = rowH.childForceExpandHeight = true;

            var cancelBtn = CreateModalButton(row.transform, "Cancel", new Color(0.22f, 0.24f, 0.32f, 0.98f), OnEquipmentRemoveConfirmCancel);
            var removeBtn = CreateModalButton(row.transform, "Remove", new Color(0.5f, 0.22f, 0.22f, 0.98f), OnEquipmentRemoveConfirmYes);
            cancelBtn.name = "Cancel";
            removeBtn.name = "Remove";

            equipmentRemoveConfirmRoot.SetActive(false);
        }

        private Button CreateModalButton(Transform parent, string label, Color bgColor, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = bgColor;
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(onClick);
            var txtGo = new GameObject("Text");
            txtGo.transform.SetParent(go.transform, false);
            var tr = txtGo.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(8f, 4f);
            tr.offsetMax = new Vector2(-8f, -4f);
            var tmp = txtGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            if (fontAsset != null) tmp.font = fontAsset;
            tmp.raycastTarget = false;
            return btn;
        }

        private void ShowEquipmentRemoveConfirm(int slotIndex)
        {
            if (currentShip == null) return;
            var equipment = currentShip.EquippedEquipment;
            if (equipment == null || slotIndex < 0 || slotIndex >= equipment.Count) return;
            StoreItemType itemType = equipment[slotIndex].ItemType;
            _pendingRemoveEquipmentSlotIndex = slotIndex;
            EnsureEquipmentRemoveConfirmModal();
            if (equipmentRemoveConfirmBodyText != null)
            {
                string itemLabel;
                if (equipment[slotIndex].IsShipComponent)
                {
                    itemLabel = equipment[slotIndex].ComponentId;
                    if (CardShopSystem.Instance != null &&
                        CardShopSystem.Instance.GetShipFamilyForShip(currentShip) is ShipFamilyDefinition family &&
                        family.TryGetComponentEntry(equipment[slotIndex].ComponentId, out ShipFamilyComponentEntry componentEntry))
                    {
                        itemLabel = ShipComponentStoreData.GetDisplayName(componentEntry);
                    }
                    else
                    {
                        itemLabel = ShipComponentStoreData.FormatComponentId(itemLabel);
                    }
                }
                else
                {
                    itemLabel = StoreItemData.GetDisplayName(itemType);
                }
                equipmentRemoveConfirmBodyText.text = $"Remove \"{itemLabel}\" from equipment?";
            }
            equipmentRemoveConfirmRoot.SetActive(true);
            equipmentRemoveConfirmRoot.transform.SetAsLastSibling();
        }

        private void OnEquipmentRemoveConfirmYes()
        {
            if (currentShip != null && _pendingRemoveEquipmentSlotIndex >= 0)
                currentShip.RemoveEquipmentServerRpc(_pendingRemoveEquipmentSlotIndex);
            HideEquipmentRemoveConfirm();
        }

        private void OnEquipmentRemoveConfirmCancel()
        {
            HideEquipmentRemoveConfirm();
        }

        private void HideEquipmentRemoveConfirm()
        {
            _pendingRemoveEquipmentSlotIndex = -1;
            if (equipmentRemoveConfirmRoot != null)
                equipmentRemoveConfirmRoot.SetActive(false);
        }

        private void RefreshSlots()
        {
            if (currentShip == null || slotBoxes == null) return;

            int slotCount = currentShip.SlotCount;

            // Resize card slot panel and grid to match ship's slot count (level 2 = 2 slots, level 3 = 3 slots, etc.)
            if (!_moonDockLayoutActive && slotPanelRect != null && slotGridRect != null)
            {
                int effectiveSlotRows = Mathf.Max(1, Mathf.Min(MaxSlotRows / SlotGridColumns, Mathf.CeilToInt((float)slotCount / SlotGridColumns)));
                float slotGridTotalH = effectiveSlotRows * SlotCardHeight + (effectiveSlotRows - 1) * SlotCellSpacing;
                float slotPanelHeight = SlotPanelHeaderHeight + 8f + slotGridTotalH + 12f;
                slotPanelRect.offsetMin = new Vector2(12f, -slotPanelHeight);
                slotPanelRect.offsetMax = new Vector2(-12f, 0f);
                slotGridRect.sizeDelta = new Vector2(-24f, slotGridTotalH);
            }
            else if (_moonDockLayoutActive && slotPanelRect != null && slotGridRect != null)
            {
                ApplySidebarSlotGridLayout(slotCount);
            }
            if (loadoutSectionLabel != null)
                loadoutSectionLabel.text = OrbitDockSidebarPanelUI.SectionTitleUpgradeCards;
            var cards = currentShip.EquippedCards;
            for (int i = 0; i < slotBoxes.Length; i++)
            {
                if (slotBoxes[i] == null) continue;
                bool visible = i < slotCount;
                slotBoxes[i].SetActive(visible);
                if (!visible) continue;
                CardData card = (cards != null && i < cards.Count) ? cards[i] : null;

                SidebarUpgradeCardSlotUi slotUi = _upgradeCardSlotRichLayoutActive && _sidebarUpgradeCardSlotUi != null && i < _sidebarUpgradeCardSlotUi.Length
                    ? _sidebarUpgradeCardSlotUi[i]
                    : null;

                if (_upgradeCardSlotRichLayoutActive)
                {
                    RefreshSidebarUpgradeCardSlotRich(i, card, slotUi);
                    continue;
                }

                if (slotTitleTexts != null && i < slotTitleTexts.Length && slotTitleTexts[i] != null)
                    slotTitleTexts[i].text = card != null ? card.displayName : "Empty";
                if (slotDescTexts != null && i < slotDescTexts.Length && slotDescTexts[i] != null)
                    slotDescTexts[i].text = card != null && !string.IsNullOrEmpty(card.description) ? card.description : "";
                if (slotLevelTexts != null && i < slotLevelTexts.Length && slotLevelTexts[i] != null)
                {
                    slotLevelTexts[i].text = card != null ? Mathf.Max(1, card.cardLevel).ToString() : "—";
                    var bubble = slotLevelTexts[i].transform.parent;
                    if (bubble != null) bubble.gameObject.SetActive(card != null);
                }
                if (slotBgImages != null && i < slotBgImages.Length && slotBgImages[i] != null)
                    slotBgImages[i].color = card != null ? GetSlotTypeColor(card.slotType) : new Color(0.18f, 0.22f, 0.32f, 0.95f);
                if (slotBorderImages != null && i < slotBorderImages.Length && slotBorderImages[i] != null)
                {
                    slotBorderImages[i].enabled = true;
                    slotBorderImages[i].color = card != null ? GetSlotTypeBorderColor(card.slotType) : new Color(0.35f, 0.4f, 0.5f, 0.8f);
                }
                if (slotDeleteButtons != null && i < slotDeleteButtons.Length && slotDeleteButtons[i] != null)
                {
                    slotDeleteButtons[i].gameObject.SetActive(card != null);
                    slotDeleteButtons[i].interactable = card != null;
                }
            }
        }

        private void RefreshSidebarUpgradeCardSlotRich(int index, CardData card, SidebarUpgradeCardSlotUi slotUi)
        {
            if (slotBgImages != null && index < slotBgImages.Length && slotBgImages[index] != null)
                slotBgImages[index].color = MoonDockEquipmentCardBg;

            Color accentColor = card != null ? GetSlotTypeAccentColor(card.slotType) : SidebarUpgradeCardEmptyAccent;
            if (slotUi?.accentImage != null)
                slotUi.accentImage.color = accentColor;

            if (slotTitleTexts != null && index < slotTitleTexts.Length && slotTitleTexts[index] != null)
                slotTitleTexts[index].text = card != null ? card.GetDisplayNameOrDefault() : "Empty";

            if (slotDescTexts != null && index < slotDescTexts.Length && slotDescTexts[index] != null)
            {
                if (card != null && !string.IsNullOrEmpty(card.description))
                    slotDescTexts[index].text = card.description;
                else
                    slotDescTexts[index].text = card != null ? "No description." : "Spin cards from the store below.";
            }

            if (slotUi?.sublineText != null)
            {
                if (card != null)
                {
                    int cl = Mathf.Max(1, card.cardLevel);
                    int rar = Mathf.Clamp((int)card.rarity, 1, 5);
                    slotUi.sublineText.text = $"Equipped · Lv {cl} · {GetCardRarityLabel(rar)}";
                    slotUi.sublineText.gameObject.SetActive(true);
                }
                else
                    slotUi.sublineText.gameObject.SetActive(false);
            }

            if (slotUi != null)
            {
                if (card != null)
                {
                    if (card.icon != null && slotUi.iconImage != null)
                    {
                        slotUi.iconImage.sprite = card.icon;
                        slotUi.iconImage.color = Color.white;
                        if (slotUi.iconGlyph != null)
                            slotUi.iconGlyph.gameObject.SetActive(false);
                    }
                    else
                    {
                        if (slotUi.iconImage != null)
                        {
                            slotUi.iconImage.sprite = null;
                            slotUi.iconImage.color = new Color(1f, 1f, 1f, 0f);
                        }
                        if (slotUi.iconGlyph != null)
                        {
                            slotUi.iconGlyph.gameObject.SetActive(true);
                            slotUi.iconGlyph.text = GetSlotTypeIconChar(card.slotType);
                        }
                    }

                    if (slotUi.iconRoot != null)
                        slotUi.iconRoot.SetActive(true);
                }
                else if (slotUi.iconRoot != null)
                    slotUi.iconRoot.SetActive(false);
            }

            if (slotDeleteButtons != null && index < slotDeleteButtons.Length && slotDeleteButtons[index] != null)
            {
                slotDeleteButtons[index].gameObject.SetActive(card != null);
                slotDeleteButtons[index].interactable = card != null;
            }
        }

        private void RebuildUpgradeSpinOfferRowIfNeeded()
        {
            if (_upgradeSpinRowUsesTallLayout || _cardSpinRowLayout == null)
                return;

            Transform row = _cardSpinRowLayout.transform;
            for (int i = row.childCount - 1; i >= 0; i--)
                Destroy(row.GetChild(i).gameObject);

            const int maxStoreCards = 3;
            cardRoots = new GameObject[maxStoreCards];
            cardBgImages = new Image[maxStoreCards];
            cardTitleTexts = new TextMeshProUGUI[maxStoreCards];
            cardLevelTexts = new TextMeshProUGUI[maxStoreCards];
            cardDescTexts = new TextMeshProUGUI[maxStoreCards];
            cardButtons = new Button[maxStoreCards];
            cardEntries = new CardData[maxStoreCards];
            cardRarityFrameImages = new Image[maxStoreCards];
            cardIconImages = new Image[maxStoreCards];
            cardRarityLabels = new TextMeshProUGUI[maxStoreCards];

            for (int i = 0; i < maxStoreCards; i++)
            {
                CreateUpgradeSpinOfferCard(row, i, out cardRoots[i], out cardRarityFrameImages[i], out cardBgImages[i], out cardIconImages[i], out cardTitleTexts[i], out cardLevelTexts[i], out cardRarityLabels[i], out cardDescTexts[i], out cardButtons[i]);
                if (cardRoots[i] != null)
                    cardRoots[i].AddComponent<ScrollRectForwarder>();
                int idx = i;
                cardButtons[i].onClick.AddListener(() => OnTakeSpinOffer(idx));
            }

            var spinBlockLe = row.parent != null ? row.parent.GetComponent<LayoutElement>() : null;
            if (spinBlockLe != null)
            {
                spinBlockLe.preferredHeight = 36f + 6f + MoonDockUpgradeSpinCardHeight;
                spinBlockLe.minHeight = spinBlockLe.preferredHeight;
            }

            var spinRowLe = row.GetComponent<LayoutElement>();
            if (spinRowLe != null)
            {
                spinRowLe.preferredHeight = MoonDockUpgradeSpinCardHeight;
                spinRowLe.minHeight = MoonDockUpgradeSpinCardHeight;
            }

            _upgradeSpinRowUsesTallLayout = true;
            ApplyMoonDockUpgradeCardsSectionHeight();
        }

        private void RefreshEquipmentSlots()
        {
            if (currentShip == null || equipmentBoxes == null) return;

            int slotCount = currentShip.EquipmentSlotCount;

            if (_moonDockLayoutActive && equipmentPanelRect != null && equipmentGridRect != null)
            {
                ApplySidebarEquipmentGridLayout(slotCount);
            }

            if (equipmentSectionLabel != null)
                equipmentSectionLabel.text = OrbitDockSidebarPanelUI.SectionTitleEquipment;

            var equipment = currentShip.EquippedEquipment;
            ShipFamilyDefinition shipFamily = CardShopSystem.Instance != null
                ? CardShopSystem.Instance.GetShipFamilyForShip(currentShip)
                : null;
            int shipLevel = currentShip.ShipLevel;
            float maxComponentPower = shipFamily != null
                ? GetMoonDockComponentMaxDisplayPower(shipFamily, shipLevel)
                : 0.001f;
            float sidebarPowerTrackWidth = Mathf.Max(40f, SidebarSlotCardWidth - 22f);

            for (int i = 0; i < equipmentBoxes.Length; i++)
            {
                if (equipmentBoxes[i] == null) continue;
                bool visible = i < slotCount;
                equipmentBoxes[i].SetActive(visible);
                if (!visible) continue;

                EquippedEquipmentEntry entry = (equipment != null && i < equipment.Count) ? equipment[i] : default;
                bool filled = equipment != null && i < equipment.Count;
                StoreItemType itemType = filled ? entry.ItemType : default;
                ShipFamilyComponentEntry componentEntry = null;
                if (filled && entry.IsShipComponent && shipFamily != null)
                    shipFamily.TryGetComponentEntry(entry.ComponentId, out componentEntry);

                SidebarEquipmentSlotUi slotUi = _equipmentSlotRichLayoutActive && _sidebarEquipmentSlotUi != null && i < _sidebarEquipmentSlotUi.Length
                    ? _sidebarEquipmentSlotUi[i]
                    : null;

                if (_equipmentSlotRichLayoutActive)
                {
                    RefreshSidebarEquipmentSlotRich(
                        i,
                        filled,
                        entry,
                        itemType,
                        componentEntry,
                        shipFamily,
                        shipLevel,
                        maxComponentPower,
                        sidebarPowerTrackWidth,
                        slotUi);
                    continue;
                }

                if (equipmentTitleTexts != null && i < equipmentTitleTexts.Length && equipmentTitleTexts[i] != null)
                {
                    if (!filled)
                        equipmentTitleTexts[i].text = "Empty";
                    else if (entry.IsShipComponent && componentEntry != null)
                        equipmentTitleTexts[i].text = ShipComponentStoreData.GetDisplayName(componentEntry);
                    else if (entry.IsShipComponent)
                        equipmentTitleTexts[i].text = ShipComponentStoreData.FormatComponentId(entry.ComponentId);
                    else
                        equipmentTitleTexts[i].text = StoreItemData.GetShortDisplayName(itemType);
                }
                if (equipmentDescTexts != null && i < equipmentDescTexts.Length && equipmentDescTexts[i] != null)
                {
                    if (!filled)
                        equipmentDescTexts[i].text = "Buy from Store tab";
                    else if (entry.IsShipComponent && componentEntry != null)
                        equipmentDescTexts[i].text = ShipComponentStoreData.GetStatsDescription(componentEntry, shipLevel, shipFamily, 2);
                    else
                        equipmentDescTexts[i].text = StoreItemData.GetDescription(itemType);
                }
                if (equipmentChargeTexts != null && i < equipmentChargeTexts.Length && equipmentChargeTexts[i] != null)
                {
                    if (filled && entry.IsShipComponent && componentEntry != null)
                        equipmentChargeTexts[i].text = ShipComponentStoreData.GetIconGlyph(componentEntry);
                    else if (filled && !StoreItemData.IsDrone(itemType))
                        equipmentChargeTexts[i].text = entry.remainingCharges.ToString();
                    else if (filled)
                        equipmentChargeTexts[i].text = StoreItemData.GetIconGlyph(itemType);
                    else
                        equipmentChargeTexts[i].text = "—";
                    var bubble = equipmentChargeTexts[i].transform.parent;
                    if (bubble != null) bubble.gameObject.SetActive(filled);
                }
                if (equipmentBgImages != null && i < equipmentBgImages.Length && equipmentBgImages[i] != null)
                {
                    if (!filled)
                        equipmentBgImages[i].color = new Color(0.18f, 0.22f, 0.32f, 0.95f);
                    else if (entry.IsShipComponent && componentEntry != null)
                        equipmentBgImages[i].color = GetEquipmentSlotColor(componentEntry);
                    else
                        equipmentBgImages[i].color = GetEquipmentSlotColor(itemType);
                }
                if (equipmentBorderImages != null && i < equipmentBorderImages.Length && equipmentBorderImages[i] != null)
                {
                    equipmentBorderImages[i].enabled = true;
                    if (!filled)
                        equipmentBorderImages[i].color = new Color(0.35f, 0.4f, 0.5f, 0.8f);
                    else if (entry.IsShipComponent && componentEntry != null)
                        equipmentBorderImages[i].color = GetEquipmentSlotBorderColor(componentEntry);
                    else
                        equipmentBorderImages[i].color = GetEquipmentSlotBorderColor(itemType);
                }
                if (equipmentDeleteButtons != null && i < equipmentDeleteButtons.Length && equipmentDeleteButtons[i] != null)
                {
                    equipmentDeleteButtons[i].gameObject.SetActive(filled);
                    equipmentDeleteButtons[i].interactable = filled;
                }
            }

            UpdateLegacyOrbitStorePanelTop(currentShip.SlotCount, slotCount);
        }

        private void RefreshSidebarEquipmentSlotRich(
            int index,
            bool filled,
            EquippedEquipmentEntry entry,
            StoreItemType itemType,
            ShipFamilyComponentEntry componentEntry,
            ShipFamilyDefinition family,
            int shipLevel,
            float maxComponentPower,
            float trackWidth,
            SidebarEquipmentSlotUi slotUi)
        {
            if (equipmentBgImages != null && index < equipmentBgImages.Length && equipmentBgImages[index] != null)
                equipmentBgImages[index].color = MoonDockEquipmentCardBg;

            Color accentColor = SidebarEquipmentEmptyAccent;
            if (filled)
            {
                if (entry.IsShipComponent && componentEntry != null)
                {
                    accentColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(
                        ShipComponentStoreData.GetAbilityColorStatIndex(componentEntry));
                }
                else
                {
                    accentColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(
                        StoreItemData.GetAbilityColorStatIndex(itemType));
                }
            }

            if (slotUi?.accentImage != null)
                slotUi.accentImage.color = accentColor;

            if (equipmentTitleTexts != null && index < equipmentTitleTexts.Length && equipmentTitleTexts[index] != null)
            {
                if (!filled)
                    equipmentTitleTexts[index].text = "Empty";
                else if (entry.IsShipComponent && componentEntry != null)
                    equipmentTitleTexts[index].text = ShipComponentStoreData.GetDisplayName(componentEntry);
                else if (entry.IsShipComponent)
                    equipmentTitleTexts[index].text = ShipComponentStoreData.FormatComponentId(entry.ComponentId);
                else
                    equipmentTitleTexts[index].text = StoreItemData.GetShortDisplayName(itemType);
            }

            if (equipmentDescTexts != null && index < equipmentDescTexts.Length && equipmentDescTexts[index] != null)
            {
                if (!filled)
                    equipmentDescTexts[index].text = "<color=#888888>Buy from equipment store</color>";
                else if (entry.IsShipComponent && componentEntry != null)
                {
                    equipmentDescTexts[index].richText = true;
                    ApplyEquipmentCardAbilityDescription(equipmentDescTexts[index], componentEntry, shipLevel, family);
                }
                else
                {
                    equipmentDescTexts[index].richText = false;
                    equipmentDescTexts[index].text = StoreItemData.GetDescription(itemType);
                }
            }

            if (slotUi?.sublineText != null)
            {
                if (filled && entry.IsShipComponent && componentEntry != null)
                {
                    float power = ShipComponentStoreData.GetComponentPowerScore(componentEntry, shipLevel, family);
                    slotUi.sublineText.text = FormatMoonDockEquipmentSubline(shipLevel, power, owned: true);
                    slotUi.sublineText.gameObject.SetActive(true);
                }
                else if (filled && StoreItemData.IsDrone(itemType))
                {
                    int maxHp = StoreItemData.GetDroneMaxHp(itemType);
                    slotUi.sublineText.text = $"{entry.remainingCharges}/{maxHp} HP";
                    slotUi.sublineText.gameObject.SetActive(true);
                }
                else if (filled && !entry.IsShipComponent)
                {
                    slotUi.sublineText.text = $"\u00d7{entry.remainingCharges} charges";
                    slotUi.sublineText.gameObject.SetActive(true);
                }
                else
                    slotUi.sublineText.gameObject.SetActive(false);
            }

            bool showComponentPowerBar = filled && entry.IsShipComponent && componentEntry != null;
            if (slotUi?.powerBar != null)
            {
                slotUi.powerBar.gameObject.SetActive(showComponentPowerBar);
                if (showComponentPowerBar)
                {
                    slotUi.powerBar.ConfigureLayoutScale(1f, 1f);
                    slotUi.powerBar.ApplyEquipmentBreakdown(
                        ShipComponentStoreData.GetPowerBreakdown(componentEntry, shipLevel, family),
                        maxComponentPower,
                        trackWidth);
                }
            }

            if (slotUi != null)
            {
                if (filled && entry.IsShipComponent && componentEntry != null)
                {
                    ApplyMoonDockEquipmentTileIcon(
                        slotUi.iconImage,
                        slotUi.iconGlyph,
                        ShipComponentStoreData.GetMenuPreviewSprite(
                            family,
                            componentEntry,
                            currentShip != null ? currentShip.ShipTeam : TeamManager.Team.None),
                        ShipComponentStoreData.GetIconGlyph(componentEntry));
                    if (slotUi.iconRoot != null)
                        slotUi.iconRoot.SetActive(true);
                }
                else if (filled)
                {
                    ApplyMoonDockEquipmentTileIcon(
                        slotUi.iconImage,
                        slotUi.iconGlyph,
                        null,
                        StoreItemData.GetIconGlyph(itemType));
                    if (slotUi.iconRoot != null)
                        slotUi.iconRoot.SetActive(true);
                }
                else if (slotUi.iconRoot != null)
                    slotUi.iconRoot.SetActive(false);
            }

            if (equipmentDeleteButtons != null && index < equipmentDeleteButtons.Length && equipmentDeleteButtons[index] != null)
            {
                equipmentDeleteButtons[index].gameObject.SetActive(filled);
                equipmentDeleteButtons[index].interactable = filled;
            }

            bool showPlacement = filled && entry.IsShipComponent && componentEntry != null;
            if (slotUi?.placementPanel != null)
                slotUi.placementPanel.SetActive(showPlacement);

            if (showPlacement && slotUi != null)
            {
                if (slotUi.placementReadout != null)
                    slotUi.placementReadout.text = FormatEquipmentPlacementCompact(entry.LocalPosition, entry.LocalEulerAngles);
            }
        }

        private void UpdateLegacyOrbitStorePanelTop(int cardSlotCount, int equipmentSlotCount)
        {
            if (_moonDockLayoutActive || slotPanelRect == null || equipmentPanelRect == null || storePanelRect == null)
                return;

            int cardRows = Mathf.Max(1, Mathf.Min(MaxSlotRows / SlotGridColumns, Mathf.CeilToInt((float)cardSlotCount / SlotGridColumns)));
            float cardGridTotalH = cardRows * SlotCardHeight + (cardRows - 1) * SlotCellSpacing;
            float cardPanelHeight = SlotPanelHeaderHeight + 8f + cardGridTotalH + 12f;

            int equipmentRows = Mathf.Max(1, Mathf.Min(MaxSlotRows / SlotGridColumns, Mathf.CeilToInt((float)equipmentSlotCount / SlotGridColumns)));
            float equipmentGridTotalH = equipmentRows * SlotCardHeight + (equipmentRows - 1) * SlotCellSpacing;
            float equipmentPanelHeight = SlotPanelHeaderHeight + 8f + equipmentGridTotalH + 12f;

            slotPanelRect.offsetMin = new Vector2(12f, -(cardPanelHeight));
            slotPanelRect.offsetMax = new Vector2(-12f, 0f);
            if (slotGridRect != null)
                slotGridRect.sizeDelta = new Vector2(-24f, cardGridTotalH);

            equipmentPanelRect.offsetMin = new Vector2(12f, -(cardPanelHeight + 8f + equipmentPanelHeight));
            equipmentPanelRect.offsetMax = new Vector2(-12f, -(cardPanelHeight + 8f));
            if (equipmentGridRect != null)
                equipmentGridRect.sizeDelta = new Vector2(-24f, equipmentGridTotalH);

            float storePanelTop = cardPanelHeight + equipmentPanelHeight + 16f;
            storePanelRect.offsetMax = new Vector2(-12f, -storePanelTop);
        }

        private void ApplySidebarEquipmentGridLayout(int slotCount)
        {
            if (equipmentGridRoot == null)
                return;

            var gridLayout = equipmentGridRoot.GetComponent<GridLayoutGroup>();
            if (gridLayout == null)
                return;

            int rows = Mathf.Max(1, Mathf.Min(MaxSlotRows, slotCount));
            float cardHeight = _equipmentSlotRichLayoutActive ? SidebarEquipmentSlotCardHeight : SidebarSlotCardHeight;
            float equipmentGridTotalH = rows * cardHeight + (rows - 1) * SidebarSlotCellSpacing;
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = SidebarSlotColumns;
            gridLayout.cellSize = new Vector2(SidebarSlotCardWidth, cardHeight);
            gridLayout.spacing = new Vector2(SidebarSlotCellSpacing, SidebarSlotCellSpacing);

            if (equipmentPanelRect != null)
            {
                float equipmentPanelHeight = 8f + equipmentGridTotalH + 8f;
                var equipmentLe = equipmentPanel.GetComponent<LayoutElement>();
                if (equipmentLe == null)
                    equipmentLe = equipmentPanel.AddComponent<LayoutElement>();
                equipmentLe.preferredHeight = equipmentPanelHeight;
                equipmentLe.minHeight = equipmentPanelHeight;
                equipmentLe.flexibleHeight = 0f;
                equipmentLe.flexibleWidth = 1f;
            }

            if (equipmentGridRect != null)
            {
                equipmentGridRect.anchorMin = Vector2.zero;
                equipmentGridRect.anchorMax = Vector2.one;
                equipmentGridRect.pivot = new Vector2(0.5f, 0.5f);
                equipmentGridRect.offsetMin = Vector2.zero;
                equipmentGridRect.offsetMax = Vector2.zero;
                equipmentGridRect.anchoredPosition = Vector2.zero;
            }

            if (orbitDockSidebar != null && orbitDockSidebar.EquipmentHost != null)
            {
                var equipmentHostLe = orbitDockSidebar.EquipmentHost.GetComponent<LayoutElement>();
                if (equipmentHostLe != null)
                {
                    float hostH = 8f + equipmentGridTotalH + 8f;
                    equipmentHostLe.preferredHeight = hostH;
                    equipmentHostLe.minHeight = hostH;
                }
            }
        }

        private void OnCardSpinClick()
        {
            if (currentShip == null || currentPlanet == null || CardShopSystem.Instance == null) return;
            var planetNo = currentPlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (planetNo == null || !planetNo.IsSpawned) return;
            var shipNo = currentShip.GetComponent<Unity.Netcode.NetworkObject>();
            if (shipNo == null || !shipNo.IsSpawned) return;
            CardShopSystem.Instance.CardSpinServerRpc(planetNo.NetworkObjectId, shipNo.NetworkObjectId);
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null) HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private void OnTakeSpinOffer(int index)
        {
            if (currentShip == null || currentPlanet == null || CardShopSystem.Instance == null) return;
            if (cardEntries == null || index < 0 || index >= cardEntries.Length) return;
            CardData card = cardEntries[index];
            if (card == null) return;
            var planetNo = currentPlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (planetNo == null || !planetNo.IsSpawned) return;
            var shipNo = currentShip.GetComponent<Unity.Netcode.NetworkObject>();
            if (shipNo == null || !shipNo.IsSpawned) return;
            CardShopSystem.Instance.PurchaseCardServerRpc(planetNo.NetworkObjectId, shipNo.NetworkObjectId, card.GetStableCardId());
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null) HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private bool _moonDockReparentDone;

        private void EnsureMoonDockChromeExists()
        {
            if (_moonDockChromeReady && moonDockCenterBackdrop != null)
            {
                EnsureMoonDockCloseButtonExists();
                return;
            }

            moonDockCenterBackdrop = new GameObject("MoonDockCenterBackdrop");
            moonDockCenterBackdrop.transform.SetParent(transform, false);
            var bdRt = moonDockCenterBackdrop.AddComponent<RectTransform>();
            bdRt.anchorMin = Vector2.zero;
            bdRt.anchorMax = Vector2.one;
            bdRt.offsetMin = bdRt.offsetMax = Vector2.zero;
            var bdImg = moonDockCenterBackdrop.AddComponent<Image>();
            bdImg.color = new Color(0f, 0f, 0f, 0.45f);
            bdImg.raycastTarget = true;

            moonDockSplitRow = new GameObject("MoonDockSplitRow").AddComponent<RectTransform>();
            moonDockSplitRow.SetParent(moonDockCenterBackdrop.transform, false);
            moonDockSplitRow.anchorMin = new Vector2(0.04f, 0.06f);
            moonDockSplitRow.anchorMax = new Vector2(0.96f, 0.94f);
            moonDockSplitRow.offsetMin = moonDockSplitRow.offsetMax = Vector2.zero;
            var splitHlg = moonDockSplitRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            splitHlg.spacing = 10f;
            splitHlg.padding = new RectOffset(0, 0, 0, 0);
            splitHlg.childAlignment = TextAnchor.UpperLeft;
            splitHlg.childControlWidth = true;
            splitHlg.childControlHeight = true;
            splitHlg.childForceExpandWidth = false;
            splitHlg.childForceExpandHeight = true;

            moonDockSidebarHost = new GameObject("MoonDockSidebarHost").AddComponent<RectTransform>();
            moonDockSidebarHost.SetParent(moonDockSplitRow, false);
            var sidebarHostLe = moonDockSidebarHost.gameObject.AddComponent<LayoutElement>();
            sidebarHostLe.preferredWidth = OrbitDockSidebarPanelUI.PanelWidth;
            sidebarHostLe.minWidth = OrbitDockSidebarPanelUI.PanelWidth;
            sidebarHostLe.flexibleWidth = 0f;
            sidebarHostLe.flexibleHeight = 1f;

            var sidebarGo = new GameObject("OrbitDockSidebarPanel");
            sidebarGo.transform.SetParent(moonDockSidebarHost, false);
            var sidebarRt = sidebarGo.AddComponent<RectTransform>();
            sidebarRt.anchorMin = Vector2.zero;
            sidebarRt.anchorMax = Vector2.one;
            sidebarRt.offsetMin = Vector2.zero;
            sidebarRt.offsetMax = Vector2.zero;
            orbitDockSidebar = sidebarGo.AddComponent<OrbitDockSidebarPanelUI>();
            orbitDockSidebar.ConfigureVisuals(panelBackgroundSprite, buttonSprite, fontAsset);
            orbitDockSidebar.BindStation(this);
            orbitDockSidebar.BindNavigation(OnSidebarNavSelected);
            orbitDockSidebar.BindAutoDeposit(OnAutoDepositToggleChanged);
            orbitDockSidebar.EnsureBuilt();

            moonDockMainHost = new GameObject("MoonDockMainHost").AddComponent<RectTransform>();
            moonDockMainHost.SetParent(moonDockSplitRow, false);
            var mainHostLe = moonDockMainHost.gameObject.AddComponent<LayoutElement>();
            mainHostLe.flexibleWidth = 1f;
            mainHostLe.minWidth = 280f;
            mainHostLe.flexibleHeight = 1f;

            var cardsScrollGo = new GameObject("MoonDockCardsScroll");
            cardsScrollGo.transform.SetParent(moonDockMainHost, false);
            var cardsScrollRt = cardsScrollGo.AddComponent<RectTransform>();
            cardsScrollRt.anchorMin = Vector2.zero;
            cardsScrollRt.anchorMax = Vector2.one;
            cardsScrollRt.offsetMin = cardsScrollRt.offsetMax = Vector2.zero;
            var cardsScrollLe = cardsScrollGo.AddComponent<LayoutElement>();
            cardsScrollLe.flexibleWidth = 1f;
            cardsScrollLe.flexibleHeight = 1f;
            moonDockCardsScroll = cardsScrollGo.AddComponent<ScrollRect>();
            moonDockCardsScroll.horizontal = false;
            moonDockCardsScroll.vertical = true;
            moonDockCardsScroll.movementType = ScrollRect.MovementType.Clamped;
            var cardsVp = new GameObject("Viewport");
            cardsVp.transform.SetParent(cardsScrollGo.transform, false);
            var cardsVpRt = cardsVp.AddComponent<RectTransform>();
            cardsVpRt.anchorMin = Vector2.zero;
            cardsVpRt.anchorMax = Vector2.one;
            cardsVpRt.offsetMin = Vector2.zero;
            cardsVpRt.offsetMax = Vector2.zero;
            cardsVp.AddComponent<RectMask2D>();
            var cardsVpImg = cardsVp.AddComponent<Image>();
            cardsVpImg.color = new Color(1f, 1f, 1f, 0.02f);
            moonDockCardsScroll.viewport = cardsVpRt;

            moonDockCenterCardsHost = new GameObject("MoonDockCardsHost").AddComponent<RectTransform>();
            moonDockCenterCardsHost.SetParent(cardsVp.transform, false);
            moonDockCenterCardsHost.anchorMin = new Vector2(0f, 1f);
            moonDockCenterCardsHost.anchorMax = new Vector2(1f, 1f);
            moonDockCenterCardsHost.pivot = new Vector2(0.5f, 1f);
            moonDockCenterCardsHost.anchoredPosition = Vector2.zero;
            moonDockCenterCardsHost.sizeDelta = new Vector2(0f, 1200f);
            var cardsHostBg = moonDockCenterCardsHost.gameObject.AddComponent<Image>();
            cardsHostBg.color = new Color(0.07f, 0.08f, 0.12f, 0.96f);
            if (panelBackgroundSprite != null) { cardsHostBg.sprite = panelBackgroundSprite; cardsHostBg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }
            var cardsVlg = moonDockCenterCardsHost.gameObject.AddComponent<VerticalLayoutGroup>();
            cardsVlg.spacing = 10f;
            cardsVlg.childAlignment = TextAnchor.UpperCenter;
            cardsVlg.childControlWidth = true;
            cardsVlg.childControlHeight = true;
            cardsVlg.childForceExpandWidth = true;
            cardsVlg.childForceExpandHeight = false;
            cardsVlg.padding = new RectOffset(20, 20, 16, 20);
            var cardsFitter = moonDockCenterCardsHost.gameObject.AddComponent<ContentSizeFitter>();
            cardsFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            cardsFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            moonDockCardsScroll.content = moonDockCenterCardsHost;

            moonDockCenterShipsHost = new GameObject("MoonDockShipsHost").AddComponent<RectTransform>();
            moonDockCenterShipsHost.SetParent(moonDockMainHost, false);
            var shipsHostRt = moonDockCenterShipsHost;
            shipsHostRt.anchorMin = Vector2.zero;
            shipsHostRt.anchorMax = Vector2.one;
            shipsHostRt.offsetMin = shipsHostRt.offsetMax = Vector2.zero;
            var shipsHostLe = moonDockCenterShipsHost.gameObject.AddComponent<LayoutElement>();
            shipsHostLe.flexibleWidth = 1f;
            shipsHostLe.flexibleHeight = 1f;
            var shipsHostBg = moonDockCenterShipsHost.gameObject.AddComponent<Image>();
            shipsHostBg.color = new Color(0.06f, 0.07f, 0.11f, 0.97f);
            shipsHostBg.raycastTarget = false;
            if (panelBackgroundSprite != null) { shipsHostBg.sprite = panelBackgroundSprite; shipsHostBg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }
            var shipsHostVlg = moonDockCenterShipsHost.gameObject.AddComponent<VerticalLayoutGroup>();
            shipsHostVlg.childAlignment = TextAnchor.UpperCenter;
            shipsHostVlg.childControlWidth = true;
            shipsHostVlg.childControlHeight = true;
            shipsHostVlg.childForceExpandWidth = true;
            shipsHostVlg.childForceExpandHeight = true;
            shipsHostVlg.padding = new RectOffset(12, 12, 10, 22);
            shipsHostVlg.spacing = 4f;

            EnsureMoonDockCloseButtonExists();

            _moonDockChromeReady = true;
            moonDockCenterBackdrop.SetActive(false);
        }

        private void EnsureMoonDockCloseButtonExists()
        {
            if (moonDockCenterBackdrop == null || moonDockCloseButton != null) return;

            var go = new GameObject("MoonDockClose");
            go.transform.SetParent(moonDockCenterBackdrop.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-14f, -12f);
            rt.sizeDelta = new Vector2(44f, 44f);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.12f, 0.14f, 0.2f, 0.94f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            moonDockCloseButton = go.AddComponent<Button>();
            var colors = moonDockCloseButton.colors;
            colors.highlightedColor = new Color(1.05f, 1.05f, 1.08f, 1f);
            moonDockCloseButton.colors = colors;
            moonDockCloseButton.onClick.AddListener(CloseMoonDockMenu);
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var tr = textGo.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "\u00d7";
            tmp.fontSize = 28f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.92f, 0.94f, 0.98f, 1f);
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
        }

        private void EnterMoonDockLayout()
        {
            EnsureMoonDockChromeExists();
            if (moonDockCenterCardsHost == null || moonDockCenterShipsHost == null) return;

            _moonDockLayoutActive = true;

            var myRect = transform as RectTransform;
            if (myRect != null)
            {
                myRect.anchorMin = Vector2.zero;
                myRect.anchorMax = Vector2.one;
                myRect.pivot = new Vector2(0.5f, 0.5f);
                myRect.offsetMin = Vector2.zero;
                myRect.offsetMax = Vector2.zero;
            }

            if (rootPanel != null) rootPanel.SetActive(false);

            ReparentMoonDockContent();

            _moonDockCenterView = MoonDockCenterView.None;
            _moonDockShipTreeHorizontal = false;
            if (moonDockCenterBackdrop != null) moonDockCenterBackdrop.SetActive(false);
            ApplyShipTreeHudObscuring(false);
        }

        private void ExitMoonDockLayout()
        {
            if (!_moonDockLayoutActive) return;

            ApplyShipTreeHudObscuring(false);

            _moonDockLayoutActive = false;
            _moonDockShipTreeHorizontal = false;
            _moonDockCenterView = MoonDockCenterView.None;
            _shipTreeStructureKey = "";

            if (moonDockCenterBackdrop != null) moonDockCenterBackdrop.SetActive(false);

            RestoreMoonDockParents();

            if (currentShip != null)
            {
                RefreshSlots();
                RefreshEquipmentSlots();
            }

            var myRect = transform as RectTransform;
            if (myRect != null)
            {
                myRect.anchorMin = new Vector2(0f, 1f);
                myRect.anchorMax = new Vector2(0f, 1f);
                myRect.pivot = new Vector2(0f, 1f);
                myRect.anchoredPosition = new Vector2(LeftMargin, -TopOffsetBelowShipStats);
                myRect.sizeDelta = new Vector2(Mathf.Max(PanelWidth, SlotPanelWidthConst), 860f);
            }
        }

        private void ReparentMoonDockContent()
        {
            if (_moonDockReparentDone || slotPanel == null || equipmentPanel == null || moonDockCenterCardsHost == null || storeContentRoot == null || cardsTabContent == null || shipsTabContent == null || orbitDockSidebar == null)
                return;

            if (shipUpgradeTreePrefab == null)
                shipUpgradeTreePrefab = Resources.Load<ShipUpgradeTreeUI>("ShipUpgradeTree");
            if (shipUpgradeTreePrefab != null)
            {
                orbitDockSidebar.EnsureCurrentShipNode(
                    shipUpgradeTreePrefab.NodePrefab,
                    shipUpgradeTreePrefab.NodeBackgroundSprite);
            }

            _moonDockSavedSlotPanelParent = slotPanel.transform.parent;
            _moonDockSavedSlotPanelSibling = slotPanel.transform.GetSiblingIndex();
            slotPanel.transform.SetParent(orbitDockSidebar.LoadoutHost, false);
            var slotPanelRt = slotPanel.GetComponent<RectTransform>();
            slotPanelRt.anchorMin = Vector2.zero;
            slotPanelRt.anchorMax = Vector2.one;
            slotPanelRt.offsetMin = Vector2.zero;
            slotPanelRt.offsetMax = Vector2.zero;
            slotPanelRt.pivot = new Vector2(0.5f, 0.5f);
            if (loadoutSectionLabel != null)
                loadoutSectionLabel.gameObject.SetActive(false);

            _moonDockSavedEquipmentPanelParent = equipmentPanel.transform.parent;
            _moonDockSavedEquipmentPanelSibling = equipmentPanel.transform.GetSiblingIndex();
            equipmentPanel.transform.SetParent(orbitDockSidebar.EquipmentHost, false);
            var equipmentPanelRt = equipmentPanel.GetComponent<RectTransform>();
            equipmentPanelRt.anchorMin = Vector2.zero;
            equipmentPanelRt.anchorMax = Vector2.one;
            equipmentPanelRt.offsetMin = Vector2.zero;
            equipmentPanelRt.offsetMax = Vector2.zero;
            equipmentPanelRt.pivot = new Vector2(0.5f, 0.5f);
            if (equipmentSectionLabel != null)
                equipmentSectionLabel.gameObject.SetActive(false);

            var equipmentPanelImg = equipmentPanel.GetComponent<Image>();
            if (equipmentPanelImg != null)
                equipmentPanelImg.color = new Color(0f, 0f, 0f, 0f);

            var slotPanelImg = slotPanel.GetComponent<Image>();
            if (slotPanelImg != null)
                slotPanelImg.color = new Color(0f, 0f, 0f, 0f);

            _moonDockSavedCardsTabParent = cardsTabContent.transform.parent;
            _moonDockSavedCardsTabSibling = cardsTabContent.transform.GetSiblingIndex();
            cardsTabContent.transform.SetParent(moonDockCenterCardsHost, false);
            cardsTabContent.transform.SetAsFirstSibling();

            EnsureMoonDockStoreSection();

            if (_moonDockCardsToEquipmentDivider == null && moonDockCenterCardsHost != null)
            {
                _moonDockCardsToEquipmentDivider = new GameObject("CardsToEquipmentDivider");
                _moonDockCardsToEquipmentDivider.transform.SetParent(moonDockCenterCardsHost, false);
                var divLe = _moonDockCardsToEquipmentDivider.AddComponent<LayoutElement>();
                divLe.preferredHeight = 0f;
                divLe.minHeight = 0f;
                divLe.flexibleHeight = 0f;
                _moonDockCardsToEquipmentDivider.SetActive(false);
            }
            else if (_moonDockCardsToEquipmentDivider != null)
                _moonDockCardsToEquipmentDivider.SetActive(false);

            if (_moonDockStoreToCardsDivider != null)
                _moonDockStoreToCardsDivider.SetActive(false);

            _moonDockSavedShipsTabParent = shipsTabContent.transform.parent;
            _moonDockSavedShipsTabSibling = shipsTabContent.transform.GetSiblingIndex();
            shipsTabContent.transform.SetParent(moonDockCenterShipsHost, false);

            if (storePanel != null)
            {
                var storeLabel = storePanel.transform.Find("Store");
                if (storeLabel != null) storeLabel.gameObject.SetActive(false);
                var scroll = storePanel.transform.Find("StoreScrollView");
                if (scroll != null) scroll.gameObject.SetActive(false);
                var sb = storePanel.transform.Find("StoreScrollbar");
                if (sb != null) sb.gameObject.SetActive(false);
                foreach (Transform c in storePanel.transform)
                {
                    if (c.name.StartsWith("StoreScroll_", StringComparison.Ordinal)) c.gameObject.SetActive(false);
                }
            }

            var slotLe = slotPanel.GetComponent<LayoutElement>();
            if (slotLe == null) slotLe = slotPanel.AddComponent<LayoutElement>();
            slotLe.flexibleHeight = 0f;
            slotLe.flexibleWidth = 1f;

            var equipmentLe = equipmentPanel.GetComponent<LayoutElement>();
            if (equipmentLe == null) equipmentLe = equipmentPanel.AddComponent<LayoutElement>();
            equipmentLe.flexibleHeight = 0f;
            equipmentLe.flexibleWidth = 1f;

            var cardsLe = cardsTabContent.GetComponent<LayoutElement>();
            if (cardsLe == null) cardsLe = cardsTabContent.AddComponent<LayoutElement>();
            cardsLe.flexibleHeight = 0f;
            ApplyMoonDockUpgradeCardsSectionHeight();

            var shipsTabLe = shipsTabContent.GetComponent<LayoutElement>();
            if (shipsTabLe == null) shipsTabLe = shipsTabContent.AddComponent<LayoutElement>();
            shipsTabLe.flexibleWidth = 1f;
            shipsTabLe.flexibleHeight = 1f;
            shipsTabLe.minHeight = 320f;

            var shipsRt = shipsTabContent.GetComponent<RectTransform>();
            shipsRt.anchorMin = Vector2.zero;
            shipsRt.anchorMax = Vector2.one;
            shipsRt.pivot = new Vector2(0.5f, 0.5f);
            shipsRt.offsetMin = Vector2.zero;
            shipsRt.offsetMax = Vector2.zero;
            shipsRt.sizeDelta = Vector2.zero;

            _moonDockReparentDone = true;
            RebuildUpgradeSpinOfferRowIfNeeded();
            SetUpgradeCardSlotLayoutMode(true);
            SetEquipmentSlotLayoutMode(true);
            RefreshSlots();
            RefreshEquipmentSlots();
            ApplyMoonDockCardsHostLayout();
        }

        private void ApplyMoonDockCardsHostLayout()
        {
            if (!_moonDockLayoutActive || moonDockCenterCardsHost == null)
                return;

            moonDockCenterCardsHost.anchorMin = Vector2.zero;
            moonDockCenterCardsHost.anchorMax = Vector2.one;
            moonDockCenterCardsHost.pivot = new Vector2(0.5f, 0.5f);
            moonDockCenterCardsHost.offsetMin = Vector2.zero;
            moonDockCenterCardsHost.offsetMax = Vector2.zero;
            moonDockCenterCardsHost.sizeDelta = Vector2.zero;

            var cardsFitter = moonDockCenterCardsHost.GetComponent<ContentSizeFitter>();
            if (cardsFitter != null)
                cardsFitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            var cardsVlg = moonDockCenterCardsHost.GetComponent<VerticalLayoutGroup>();
            if (cardsVlg != null)
            {
                cardsVlg.spacing = MoonDockCardsToEquipmentGap;
                cardsVlg.childForceExpandHeight = false;
            }

            if (cardsTabContent != null)
            {
                var cardsTabVlg = cardsTabContent.GetComponent<VerticalLayoutGroup>();
                if (cardsTabVlg != null)
                    cardsTabVlg.padding = new RectOffset(12, 12, 8, 4);

                var cardsLe = cardsTabContent.GetComponent<LayoutElement>();
                if (cardsLe == null)
                    cardsLe = cardsTabContent.AddComponent<LayoutElement>();
                cardsLe.flexibleHeight = 0f;
                ApplyMoonDockUpgradeCardsSectionHeight();

                var upgradeHeader = cardsTabContent.transform.Find("SectionHeader_UpgradeCards");
                ApplyMoonDockSectionHeaderMetrics(upgradeHeader);
            }

            if (_moonDockCardsToEquipmentDivider != null)
                _moonDockCardsToEquipmentDivider.SetActive(false);

            if (moonDockStoreSection != null)
            {
                var sectionLe = moonDockStoreSection.GetComponent<LayoutElement>();
                if (sectionLe == null)
                    sectionLe = moonDockStoreSection.AddComponent<LayoutElement>();
                sectionLe.flexibleHeight = 1f;
                sectionLe.flexibleWidth = 1f;

                var sectionVlg = moonDockStoreSection.GetComponent<VerticalLayoutGroup>();
                if (sectionVlg != null)
                {
                    sectionVlg.spacing = MoonDockEquipmentHeaderToGridGap;
                    sectionVlg.padding = new RectOffset(12, 12, 0, 0);
                    sectionVlg.childForceExpandHeight = false;
                }

                var equipmentHeader = moonDockStoreSection.transform.Find("SectionHeader_Equipment");
                ApplyMoonDockSectionHeaderMetrics(equipmentHeader);

                Transform scrollTransform = moonDockStoreSection.transform.Find("EquipmentStoreScroll");
                if (scrollTransform != null)
                {
                    var scrollLe = scrollTransform.GetComponent<LayoutElement>();
                    if (scrollLe != null)
                    {
                        scrollLe.flexibleHeight = 1f;
                        scrollLe.flexibleWidth = 1f;
                        scrollLe.minHeight = MoonDockEquipmentScrollMinHeight;
                    }
                }
            }

            if (_moonDockStoreGridContent != null)
                _moonDockStoreGridContent.sizeDelta = new Vector2(0f, 0f);

            if (moonDockCardsScroll != null)
            {
                moonDockCardsScroll.verticalNormalizedPosition = 1f;
                moonDockCardsScroll.enabled = true;
            }
        }

        private void RestoreMoonDockParents()
        {
            if (!_moonDockReparentDone) return;

            if (cardsTabContent != null)
            {
                var cle = cardsTabContent.GetComponent<LayoutElement>();
                if (cle != null)
                {
                    cle.flexibleHeight = 1f;
                    cle.minHeight = 240f;
                }
            }

            if (slotPanel != null && _moonDockSavedSlotPanelParent != null)
            {
                slotPanel.transform.SetParent(_moonDockSavedSlotPanelParent, false);
                slotPanel.transform.SetSiblingIndex(_moonDockSavedSlotPanelSibling);
                if (loadoutSectionLabel != null)
                    loadoutSectionLabel.gameObject.SetActive(true);
                var slotPanelImg = slotPanel.GetComponent<Image>();
                if (slotPanelImg != null)
                    slotPanelImg.color = new Color(0.08f, 0.1f, 0.16f, 0.94f);
            }
            if (equipmentPanel != null && _moonDockSavedEquipmentPanelParent != null)
            {
                equipmentPanel.transform.SetParent(_moonDockSavedEquipmentPanelParent, false);
                equipmentPanel.transform.SetSiblingIndex(_moonDockSavedEquipmentPanelSibling);
                if (equipmentSectionLabel != null)
                    equipmentSectionLabel.gameObject.SetActive(true);
                var equipmentPanelImg = equipmentPanel.GetComponent<Image>();
                if (equipmentPanelImg != null)
                    equipmentPanelImg.color = new Color(0.08f, 0.1f, 0.16f, 0.94f);
            }
            SetEquipmentSlotLayoutMode(false);
            SetUpgradeCardSlotLayoutMode(false);
            RefreshEquipmentSlots();
            RefreshSlots();
            if (cardsTabContent != null && _moonDockSavedCardsTabParent != null)
            {
                cardsTabContent.transform.SetParent(_moonDockSavedCardsTabParent, false);
                cardsTabContent.transform.SetSiblingIndex(_moonDockSavedCardsTabSibling);
                var cardsTabVlg = cardsTabContent.GetComponent<VerticalLayoutGroup>();
                if (cardsTabVlg != null)
                    cardsTabVlg.padding = new RectOffset(12, 12, 8, 8);
            }

            if (moonDockCenterCardsHost != null)
            {
                var cardsVlg = moonDockCenterCardsHost.GetComponent<VerticalLayoutGroup>();
                if (cardsVlg != null)
                {
                    cardsVlg.spacing = 10f;
                    cardsVlg.childForceExpandHeight = false;
                }
            }

            if (moonDockStoreSection != null)
            {
                var sectionVlg = moonDockStoreSection.GetComponent<VerticalLayoutGroup>();
                if (sectionVlg != null)
                {
                    sectionVlg.spacing = 4f;
                    sectionVlg.padding = new RectOffset(0, 0, 0, 0);
                }
            }
            if (shipsTabContent != null && _moonDockSavedShipsTabParent != null)
            {
                shipsTabContent.transform.SetParent(_moonDockSavedShipsTabParent, false);
                shipsTabContent.transform.SetSiblingIndex(_moonDockSavedShipsTabSibling);
            }

            if (storePanel != null)
            {
                var storeLabel = storePanel.transform.Find("Store");
                if (storeLabel != null) storeLabel.gameObject.SetActive(true);
                var scroll = storePanel.transform.Find("StoreScrollView");
                if (scroll != null) scroll.gameObject.SetActive(true);
                var sb = storePanel.transform.Find("StoreScrollbar");
                if (sb != null) sb.gameObject.SetActive(true);
                foreach (Transform c in storePanel.transform)
                {
                    if (c.name.StartsWith("StoreScroll_", StringComparison.Ordinal)) c.gameObject.SetActive(true);
                }
            }

            _moonDockReparentDone = false;
        }

        private static void ApplyShipTreeHudObscuring(bool obscuring)
        {
            HUDController.SetShipUpgradeTreeObscuresHud(obscuring);
        }

        private void SetMoonDockCenterView(MoonDockCenterView view)
        {
            _moonDockCenterView = view;
            if (moonDockCenterBackdrop == null) return;

            bool show = view != MoonDockCenterView.None;
            moonDockCenterBackdrop.SetActive(show);
            if (!show)
            {
                ApplyShipTreeHudObscuring(false);
                return;
            }

            bool storePanel = view == MoonDockCenterView.Store;
            ApplyShipTreeHudObscuring(show);
            if (orbitDockSidebar != null)
            {
                orbitDockSidebar.SetActiveNav(storePanel
                    ? OrbitDockSidebarPanelUI.NavTarget.Store
                    : OrbitDockSidebarPanelUI.NavTarget.Upgrades);
            }
            if (moonDockCardsScroll != null) moonDockCardsScroll.gameObject.SetActive(storePanel);
            if (moonDockCenterShipsHost != null) moonDockCenterShipsHost.gameObject.SetActive(!storePanel);

            if (storePanel)
            {
                activeStoreTab = 0;
                _moonDockShipTreeHorizontal = false;
                RefreshStoreTabVisibility();
                ApplyMoonDockCardsHostLayout();
                ApplyMoonDockCardGridWidth();
                RefreshSlots();
                RefreshEquipmentSlots();
                RefreshStoreLabels();
                RefreshSidebar();
            }
            else
            {
                activeStoreTab = 1;
                _moonDockShipTreeHorizontal = true;
                _shipTreeStructureKey = "";
                RefreshStoreTabVisibility();
                RefreshShipsTab(scrollToActiveShipNode: false);
                RefreshSidebar();
            }

            if (storePanel && moonDockCardsScroll != null) moonDockCardsScroll.verticalNormalizedPosition = 1f;
            if (moonDockCenterBackdrop != null) moonDockCenterBackdrop.transform.SetAsLastSibling();
            if (moonDockCloseButton != null) moonDockCloseButton.transform.SetAsLastSibling();
            ApplyMoonDockShipTreeRowLayout();
            Canvas.ForceUpdateCanvases();
        }

        private void RebuildMoonDockLayoutsAfterShow()
        {
            ApplyMoonDockCardsHostLayout();
            if (moonDockCenterCardsHost != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(moonDockCenterCardsHost);
            if (slotGridRoot != null)
            {
                var slotRect = slotGridRoot.transform as RectTransform;
                if (slotRect != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(slotRect);
            }
            Canvas.ForceUpdateCanvases();
            ApplyMoonDockCardGridWidth();
        }

        private void ApplyMoonDockCardGridWidth()
        {
            ApplyMoonDockUpgradeCardsSectionHeight();
            ApplyMoonDockCardsHostLayout();
            if (moonDockCenterCardsHost != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(moonDockCenterCardsHost);
            else if (storeContentRoot != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);
            Canvas.ForceUpdateCanvases();

            float tileWidth = GetMoonDockItemTileWidth();
            float spinBandWidth = ComputeMoonDockSpinBandWidth(tileWidth);
            if (_moonDockStoreGrid != null)
            {
                _moonDockStoreGrid.cellSize = new Vector2(tileWidth, MoonDockEquipmentCardHeight);
                if (_moonDockStoreGridContent != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_moonDockStoreGridContent);
            }
            if (_cardSpinRowLayout != null)
            {
                var spinRowRt = _cardSpinRowLayout.GetComponent<RectTransform>();
                ApplyMoonDockTileLayoutToRow(spinRowRt, tileWidth, MoonDockUpgradeSpinCardHeight);
                var spinRowLe = spinRowRt.GetComponent<LayoutElement>();
                if (spinRowLe == null) spinRowLe = spinRowRt.gameObject.AddComponent<LayoutElement>();
                spinRowLe.flexibleWidth = 0f;
                spinRowLe.flexibleHeight = 0f;
                spinRowLe.preferredWidth = spinBandWidth;
                spinRowLe.minWidth = spinBandWidth;
                spinRowLe.preferredHeight = MoonDockUpgradeSpinCardHeight;
                spinRowLe.minHeight = MoonDockUpgradeSpinCardHeight;
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(spinRowRt);
            }
            if (_cardSpinButtonLayout != null)
            {
                _cardSpinButtonLayout.flexibleWidth = 0f;
                _cardSpinButtonLayout.preferredWidth = spinBandWidth;
                _cardSpinButtonLayout.minWidth = spinBandWidth;
            }
        }

        private Planet GetShipUpgradeStorePlanet()
        {
            if (currentShip == null) return null;
            if (currentPlanet != null)
            {
                bool isHome = currentPlanet is HomePlanet hp && hp.AssignedTeam == currentShip.ShipTeam;
                bool isCaptured = !isHome && currentPlanet.TeamOwnership == currentShip.ShipTeam;
                if (isHome || isCaptured)
                    return currentPlanet;
            }
            if (currentHomePlanet != null && currentHomePlanet.AssignedTeam == currentShip.ShipTeam)
                return currentHomePlanet;
            return currentPlanet;
        }

        private void OnBuyChassis(int index)
        {
            if (currentShip == null || currentHomePlanet == null || currentPlanet == null || CardShopSystem.Instance == null) return;
            if (shipUnlockEntries == null || index < 0 || index >= shipUnlockEntries.Length) return;
            ShipUnlockEntry entry = shipUnlockEntries[index];
            if (entry?.chassis == null) return;
            var planetNo = currentPlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (planetNo == null || !planetNo.IsSpawned) return;
            int tierLevel = Mathf.Max(1, entry.minHomePlanetLevel);
            CardShopSystem.Instance.PurchaseChassisServerRpc(planetNo.NetworkObjectId, currentShip.NetworkObjectId, entry.chassis.chassisId, tierLevel);
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null) HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }
    }
}
