using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Core;
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
    public class OrbitStationUI : MonoBehaviour
    {
        [Header("Shift Sci-Fi UI (optional)")]
        [Tooltip("Assign Shift UI panel/sprite for sci-fi look.")]
        [SerializeField] private Sprite panelBackgroundSprite;
        [SerializeField] private Sprite buttonSprite;
        [Tooltip("Spin cards: defaults to Resources/SpinCardShiftVisuals if unset.")]
        [SerializeField] private SpinCardShiftVisuals spinCardShiftVisuals;
        [Tooltip("e.g. Rajdhani from Shift UI/Fonts.")]
        [SerializeField] private TMP_FontAsset fontAsset;

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

        private GameObject rootPanel;
        private GameObject slotPanel;
        private GameObject storePanel;
        private ScrollRect storeScrollRect;
        private RectTransform storeContentRoot;
        private GameObject cardsTabContent;
        private GameObject shipsTabContent;
        private Button tabCardsButton;
        private Button tabShipsButton;
        private int activeStoreTab = 0; // 0 = Cards, 1 = Ships
        private const int MaxStoreShips = 80;
        private const int MaxShipCards = 20;
        private const float ShipCardPreviewSize = 80f;
        private const float ShipCardWidth = 140f;
        private const float ShipCardHeight = 88f;
        private const float ShipRowSpacing = 8f;
        private const int ShipPreviewRenderSize = 128;
        private Transform shipPreviewsRoot;
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
        private RectTransform shipTreeCenterRow;
        private RectTransform shipTreeCanvas;
        private TextMeshProUGUI shipTreeHintText;
        private readonly List<GameObject> shipTreeVisuals = new List<GameObject>();
        private readonly List<ShipTreeNodeView> shipTreeNodes = new List<ShipTreeNodeView>();
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
        private const string ShipTreeStructureKey = "full8_fixed_vlayout_powerbreakdown_v4_basis6col_centered";
        private const string MoonDockShipTreeStructureKey = "moon_hlayout_compact_v1";
        private float _cachedShipTreeBasisWidth = -999f;
        private HorizontalLayoutGroup _cardSpinRowLayout;
        private Button cardSpinButton;
        private TextMeshProUGUI cardSpinButtonLabel;
        private Image[] cardRarityFrameImages;
        private Image[] cardIconImages;
        private TextMeshProUGUI[] cardRarityLabels;

        private bool _moonDockLayoutActive;
        private bool _moonDockChromeReady;
        private bool _moonDockShipTreeHorizontal;
        private enum MoonDockCenterView { None, Cards, Ships }
        private MoonDockCenterView _moonDockCenterView = MoonDockCenterView.None;

        private GameObject moonDockRightRail;
        private Button moonDockBtnCards;
        private Button moonDockBtnShips;
        private GameObject moonDockCenterBackdrop;
        private RectTransform moonDockCenterCardsHost;
        private ScrollRect moonDockCardsScroll;
        private RectTransform moonDockCenterShipsHost;
        private Button moonDockCloseButton;
        private Transform _moonDockSavedSlotPanelParent;
        private int _moonDockSavedSlotPanelSibling;
        private Transform _moonDockSavedGemsParent;
        private int _moonDockSavedGemsSibling;
        private Transform _moonDockSavedCardsTabParent;
        private int _moonDockSavedCardsTabSibling;
        private Transform _moonDockSavedShipsTabParent;
        private int _moonDockSavedShipsTabSibling;
        /// <summary>Visual gap + divider between loadout slots and card shop in moon dock (destroyed on restore).</summary>
        private GameObject _moonDockLoadoutCardsGap;
        /// <summary>Extra empty space below the divider so the card grid sits clearly below loadout (destroyed on restore).</summary>
        private GameObject _moonDockCardsBelowDividerSpacer;

        private const float MoonHorizontalShipTreeNodeHeight = 118f;
        /// <summary>Preview square size for moon horizontal nodes; kept at legacy 144×0.56 so row height can shrink without shrinking the image.</summary>
        private const float MoonHorizontalShipPreviewImageBox = 144f * 0.56f;
        /// <summary>Vertical tree compact styling threshold (legacy ~moon row + padding); independent of <see cref="MoonHorizontalShipTreeNodeHeight"/>.</summary>
        private const float ShipTreeCompactLayoutMaxHeight = 148f;
        private const float MoonHorizontalBranchGapY = 8f;
        private const float MoonHorizontalLevelColGap = 12f;
        private static readonly Color ShipTreeConnectorDim = new Color(0.45f, 0.62f, 0.85f, 0.55f);
        private static readonly Color ShipTreeConnectorPath = new Color(0.35f, 0.98f, 0.62f, 0.92f);
        private static readonly string[] ShipTreePowerCategoryLetters = { "O", "D", "E", "M", "C" };
        private readonly List<int> _shipTreeNextTargets = new List<int>(4);
        private StoreItemType[] itemTypes;
        private Button[] itemButtons;
        private TextMeshProUGUI[] itemLabels;

        private class ShipTreeNodeView
        {
            public int Level;
            public int BranchIndex;
            public ShipUpgradeNode Node;
            public Button Button;
            public TextMeshProUGUI LevelNumberText;
            public TextMeshProUGUI ShipNameText;
            public Image PreviewImage;
            public TextMeshProUGUI PriceText;
            public RectTransform Rect;
            /// <summary>Node button width (for scaling the power bar track vs max power in tree).</summary>
            public float NodeButtonWidth;
            /// <summary>Moon horizontal layout: power bar uses this width (left column), not full node width.</summary>
            public float PowerBarTrackWidth;
            public RectTransform PowerBarRow;
            public Image[] PowerBarSegments;
            public TextMeshProUGUI[] PowerStatLabels;
            public TextMeshProUGUI[] PowerStatValues;
        }

        private float _cardsContentHeight;
        private float _shipsContentHeight;

        private Vector2 _storeScrollLastMousePos;
        private bool _storeScrollDragging;
        /// <summary>Track which tab was active so we only reset scroll position when switching tabs, not when using the scrollbar.</summary>
        private int _lastActiveStoreTab = -1;

        public static OrbitStationUI GetOrCreate()
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<OrbitStationUI>();
            if (existing != null) return existing;

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
            return uiObj.AddComponent<OrbitStationUI>();
        }

        public static void OnContributedGemsReceived(float gems)
        {
            lastReceivedGems = gems;
            pendingGemsRequest = false;
            var ui = UnityEngine.Object.FindFirstObjectByType<OrbitStationUI>();
            if (ui != null) ui.RefreshFromReceivedGems();
        }

        /// <summary>Called from world-space gem moon UI (Cards / Ships next to reservoir count).</summary>
        public void OpenGemMoonDockPanelFromWorld(bool shipsPanel)
        {
            if (!_moonDockLayoutActive) return;
            SetMoonDockCenterView(shipsPanel ? MoonDockCenterView.Ships : MoonDockCenterView.Cards);
        }

        private float storeRefreshAccum;
        private const float StoreRefreshInterval = 0.35f;
        private float contributedGemsRequestAccum;
        private const float ContributedGemsRequestInterval = 1f; // Request contributed gems periodically so deposits show up

        private void Awake()
        {
            EnsurePanelExists();
            if (rootPanel != null) rootPanel.SetActive(false);
        }

        private void OnEnable()
        {
            CardShopSystem.ClientSpinOfferConsumed += OnClientSpinOfferConsumed;
        }

        private void OnDisable()
        {
            CardShopSystem.ClientSpinOfferConsumed -= OnClientSpinOfferConsumed;
        }

        private void OnClientSpinOfferConsumed()
        {
            RefreshStoreLabels();
            RefreshSlots();
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
        }

        private void Update()
        {
            if (currentShip == null || currentPlanet == null) return;
            if (!_moonDockLayoutActive && (rootPanel == null || !rootPanel.activeSelf)) return;
            storeRefreshAccum += Time.deltaTime;
            contributedGemsRequestAccum += Time.deltaTime;
            if (storeRefreshAccum >= StoreRefreshInterval)
            {
                storeRefreshAccum = 0f;
                RefreshStoreLabels();
                RefreshSlots();
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
                CheckShipTreeLayoutBasisChanged();
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
            currentShip = ship;
            currentPlanet = planet;
            if (ship != null && (currentHomePlanet == null || Time.time - _lastHomePlanetLookupTime >= HomePlanetLookupInterval))
            {
                _lastHomePlanetLookupTime = Time.time;
                foreach (var h in HomePlanet.AllHomePlanets)
                {
                    if (h != null && h.AssignedTeam == ship.ShipTeam) { currentHomePlanet = h; break; }
                }
            }
            contributedGems = 0f;
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
            ExitMoonDockLayout();
            currentShip = null;
            currentPlanet = null;
            currentHomePlanet = null; // Clear so next Show does fresh lookup
            if (rootPanel != null) rootPanel.SetActive(false);
        }

        public void RefreshFromReceivedGems()
        {
            contributedGems = lastReceivedGems;
            RefreshStoreLabels();
        }

        private void RefreshAll()
        {
            RefreshStoreLabels();
            RefreshSlots();
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
            shipPreviewsRoot = null;

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
            myRect.sizeDelta = new Vector2(Mathf.Max(PanelWidth, SlotPanelWidthConst), 720f);

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
            loadoutSectionLabel = CreateSectionLabelWithRef(slotPanel.transform, "Loadout", "Ship Loadout — tap ✕ on a card to remove it", ref slotY);
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

            // —— Store Panel (tabs: Cards | Ships) ——
            float storePanelTop = slotPanelHeight + 8f;
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

            // Tab strip
            var tabStripGo = new GameObject("TabStrip");
            tabStripGo.transform.SetParent(storePanel.transform, false);
            var tabStripRect = tabStripGo.AddComponent<RectTransform>();
            tabStripRect.anchorMin = new Vector2(0f, 1f);
            tabStripRect.anchorMax = new Vector2(1f, 1f);
            tabStripRect.pivot = new Vector2(0.5f, 1f);
            tabStripRect.anchoredPosition = new Vector2(0f, storeY);
            tabStripRect.sizeDelta = new Vector2(-24f, 36f);
            storeY -= 40f;
            var tabStripLayout = tabStripGo.AddComponent<HorizontalLayoutGroup>();
            tabStripLayout.spacing = 12f;
            tabStripLayout.childAlignment = TextAnchor.MiddleLeft;
            tabStripLayout.childControlWidth = true;
            tabStripLayout.childControlHeight = true;
            tabStripLayout.childForceExpandWidth = false;
            tabStripLayout.childForceExpandHeight = true;

            tabCardsButton = CreateTabButton(tabStripGo.transform, "Cards");
            tabShipsButton = CreateTabButton(tabStripGo.transform, "Ships");
            tabCardsButton.onClick.AddListener(() => { activeStoreTab = 0; RefreshStoreTabVisibility(); });
            tabShipsButton.onClick.AddListener(() => { activeStoreTab = 1; RefreshStoreTabVisibility(); });

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

            // Cards tab content
            cardsTabContent = new GameObject("CardsTabContent");
            cardsTabContent.transform.SetParent(storeContentRoot, false);
            var cardsContentRect = cardsTabContent.AddComponent<RectTransform>();
            cardsContentRect.anchorMin = new Vector2(0f, 1f);
            cardsContentRect.anchorMax = new Vector2(1f, 1f);
            cardsContentRect.pivot = new Vector2(0.5f, 1f);
            cardsContentRect.offsetMin = Vector2.zero;
            cardsContentRect.offsetMax = Vector2.zero;
            float y = 0f;
            CreateSectionHeaderPair(cardsTabContent.transform, "Loadout cards", "Spend gems on a spin. You’ll get three random cards — pick one to equip. (Needs an empty slot.)", ref y);
            var spinBtnGo = new GameObject("CardSpinButton");
            spinBtnGo.transform.SetParent(cardsTabContent.transform, false);
            var spinBtnRect = spinBtnGo.AddComponent<RectTransform>();
            spinBtnRect.anchorMin = new Vector2(0f, 1f);
            spinBtnRect.anchorMax = new Vector2(1f, 1f);
            spinBtnRect.pivot = new Vector2(0.5f, 1f);
            spinBtnRect.anchoredPosition = new Vector2(0f, y);
            spinBtnRect.sizeDelta = new Vector2(-24f, 44f);
            y -= 50f;
            var spinImg = spinBtnGo.AddComponent<Image>();
            cardSpinButtonImage = spinImg;
            var shiftSpin = GetSpinCardShiftVisuals();
            if (shiftSpin != null && shiftSpin.chooseButtonSliced != null)
            {
                spinImg.sprite = shiftSpin.chooseButtonSliced;
                spinImg.type = Image.Type.Sliced;
                spinImg.color = new Color(0.39f, 0.78f, 1f, 0.92f);
            }
            else
            {
                spinImg.color = new Color(0.15f, 0.42f, 0.72f, 1f);
                if (buttonSprite != null) { spinImg.sprite = buttonSprite; spinImg.type = Image.Type.Sliced; }
            }
            cardSpinButton = spinBtnGo.AddComponent<Button>();
            cardSpinButton.onClick.AddListener(OnCardSpinClick);
            var spinLabelGo = new GameObject("Text");
            spinLabelGo.transform.SetParent(spinBtnGo.transform, false);
            var spinLabelRect = spinLabelGo.AddComponent<RectTransform>();
            spinLabelRect.anchorMin = Vector2.zero;
            spinLabelRect.anchorMax = Vector2.one;
            spinLabelRect.offsetMin = new Vector2(12f, 6f);
            spinLabelRect.offsetMax = new Vector2(-12f, -6f);
            cardSpinButtonLabel = spinLabelGo.AddComponent<TextMeshProUGUI>();
            cardSpinButtonLabel.text = "Spin";
            cardSpinButtonLabel.fontSize = 16;
            cardSpinButtonLabel.fontStyle = FontStyles.Bold;
            cardSpinButtonLabel.alignment = TextAlignmentOptions.Center;
            cardSpinButtonLabel.color = Color.white;
            if (fontAsset != null) cardSpinButtonLabel.font = fontAsset;
            const int maxStoreCards = 3;
            var cardSpinRowGo = new GameObject("CardSpinRow");
            cardSpinRowGo.transform.SetParent(cardsTabContent.transform, false);
            var cardSpinRowRect = cardSpinRowGo.AddComponent<RectTransform>();
            cardSpinRowRect.anchorMin = new Vector2(0f, 1f);
            cardSpinRowRect.anchorMax = new Vector2(1f, 1f);
            cardSpinRowRect.pivot = new Vector2(0.5f, 1f);
            cardSpinRowRect.anchoredPosition = new Vector2(0f, y);
            float spinRowHeight = 356f;
            cardSpinRowRect.sizeDelta = new Vector2(-20f, spinRowHeight);
            y -= spinRowHeight + 14f;
            _cardSpinRowLayout = cardSpinRowGo.AddComponent<HorizontalLayoutGroup>();
            _cardSpinRowLayout.spacing = 18f;
            _cardSpinRowLayout.padding = new RectOffset(8, 8, 10, 10);
            _cardSpinRowLayout.childAlignment = TextAnchor.UpperCenter;
            _cardSpinRowLayout.childControlWidth = true;
            _cardSpinRowLayout.childControlHeight = true;
            _cardSpinRowLayout.childForceExpandWidth = true;
            _cardSpinRowLayout.childForceExpandHeight = true;
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
                CreateSpinOfferCard(cardSpinRowGo.transform, i, out cardRoots[i], out cardRarityFrameImages[i], out cardBgImages[i], out cardIconImages[i], out cardTitleTexts[i], out cardLevelTexts[i], out cardRarityLabels[i], out cardDescTexts[i], out cardButtons[i]);
                if (cardRoots[i] != null)
                    cardRoots[i].AddComponent<ScrollRectForwarder>();
                int idx = i;
                cardButtons[i].onClick.AddListener(() => OnTakeSpinOffer(idx));
            }
            float cardsContentHeight = -y + 24f;
            _cardsContentHeight = cardsContentHeight;
            var cardsLayoutEl = cardsTabContent.AddComponent<LayoutElement>();
            cardsLayoutEl.preferredHeight = cardsContentHeight;
            cardsLayoutEl.flexibleWidth = 1f;

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
            float shipY = 0f;
            CreateSectionHeaderPair(shipsTabContent.transform, "Ship upgrade tree", "Each tier splits into branches. Stats on the left — ship preview and price on the right.", ref shipY);
            shipY -= 6f;

            var hintGo = new GameObject("ShipTreeHint");
            hintGo.transform.SetParent(shipsTabContent.transform, false);
            var hintRect = hintGo.AddComponent<RectTransform>();
            hintRect.anchorMin = new Vector2(0f, 1f);
            hintRect.anchorMax = new Vector2(1f, 1f);
            hintRect.pivot = new Vector2(0.5f, 1f);
            hintRect.anchoredPosition = new Vector2(0f, shipY);
            hintRect.sizeDelta = new Vector2(-24f, 52f);
            shipTreeHintText = hintGo.AddComponent<TextMeshProUGUI>();
            shipTreeHintText.text = "";
            shipTreeHintText.fontSize = 13f;
            shipTreeHintText.color = new Color(0.78f, 0.88f, 0.98f, 0.96f);
            shipTreeHintText.alignment = TextAlignmentOptions.TopLeft;
            shipTreeHintText.enableWordWrapping = true;
            shipTreeHintText.raycastTarget = false;
            if (fontAsset != null) shipTreeHintText.font = fontAsset;
            shipY -= 58f;

            var rowGo = new GameObject("ShipTreeCenterRow");
            rowGo.transform.SetParent(shipsTabContent.transform, false);
            shipTreeCenterRow = rowGo.AddComponent<RectTransform>();
            shipTreeCenterRow.anchorMin = new Vector2(0f, 0.5f);
            shipTreeCenterRow.anchorMax = new Vector2(1f, 0.5f);
            shipTreeCenterRow.pivot = new Vector2(0.5f, 0.5f);
            shipTreeCenterRow.anchoredPosition = Vector2.zero;
            shipTreeCenterRow.sizeDelta = new Vector2(0f, 560f);
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.preferredHeight = 560f;
            rowLe.flexibleWidth = 1f;
            rowLe.minHeight = 400f;
            var rowHlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowHlg.childAlignment = TextAnchor.UpperLeft;
            rowHlg.childControlWidth = true;
            rowHlg.childControlHeight = true;
            rowHlg.childForceExpandWidth = false;
            rowHlg.childForceExpandHeight = true;
            rowHlg.spacing = 0f;
            rowHlg.padding = new RectOffset(8, 12, 0, 0);

            var treeGo = new GameObject("ShipUpgradeTreeCanvas");
            treeGo.transform.SetParent(rowGo.transform, false);
            shipTreeCanvas = treeGo.AddComponent<RectTransform>();
            shipTreeCanvas.anchorMin = new Vector2(0.5f, 0.5f);
            shipTreeCanvas.anchorMax = new Vector2(0.5f, 0.5f);
            shipTreeCanvas.pivot = new Vector2(0.5f, 0.5f);
            shipTreeCanvas.anchoredPosition = Vector2.zero;
            float treeInitW = Mathf.Max(200f, Mathf.Max(PanelWidth, SlotPanelWidthConst) - 56f);
            shipTreeCanvas.sizeDelta = new Vector2(treeInitW, 560f);
            var treeLe = treeGo.AddComponent<LayoutElement>();
            treeLe.preferredWidth = treeInitW;
            treeLe.flexibleWidth = 0f;
            treeLe.minWidth = 100f;
            var treeBg = treeGo.AddComponent<Image>();
            treeBg.color = new Color(0f, 0f, 0f, 0f);
            treeBg.raycastTarget = true; // Keep hit target for scroll forwarding; no visible panel behind the tree
            treeGo.AddComponent<ScrollRectForwarder>();
            shipY -= 568f;

            shipsRowsContainer = null;
            shipPreviewsRoot = new GameObject("ShipPreviewsRoot").transform;
            shipPreviewsRoot.SetParent(transform, false);
            shipPreviewsRoot.localPosition = Vector3.zero;
            shipPreviewsRoot.localRotation = Quaternion.identity;
            shipPreviewsRoot.localScale = Vector3.one;

            float shipsContentHeight = Mathf.Max(820f, MaxShipCards * 40f + 60f);
            _shipsContentHeight = shipsContentHeight;
            var shipsLayoutEl = shipsTabContent.AddComponent<LayoutElement>();
            shipsLayoutEl.preferredHeight = shipsContentHeight;
            shipsLayoutEl.flexibleWidth = 1f;

            storeContentRoot.sizeDelta = new Vector2(0f, Mathf.Max(cardsContentHeight, shipsContentHeight, 600f));

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

            itemTypes = (StoreItemType[])System.Enum.GetValues(typeof(StoreItemType));
            itemButtons = new Button[itemTypes.Length];
            itemLabels = new TextMeshProUGUI[itemTypes.Length];
            RefreshStoreTabVisibility();
            RefreshShipsTab();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);
            if (canvas != null) Canvas.ForceUpdateCanvases();

            EnsureMoonDockChromeExists();
        }

        private Button CreateTabButton(Transform parent, string label)
        {
            var go = new GameObject("Tab_" + label);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(120f, 32f);
            var layoutEl = go.AddComponent<UnityEngine.UI.LayoutElement>();
            layoutEl.minWidth = 100f;
            layoutEl.preferredWidth = 120f;
            layoutEl.flexibleWidth = 0f;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.28f, 0.4f, 0.95f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            var btn = go.AddComponent<Button>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(4f, 2f);
            textRect.offsetMax = new Vector2(-4f, -2f);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
            return btn;
        }

        private void RefreshStoreTabVisibility()
        {
            if (cardsTabContent == null || shipsTabContent == null || tabCardsButton == null || tabShipsButton == null) return;

            if (_moonDockLayoutActive)
            {
                _lastActiveStoreTab = activeStoreTab;
                cardsTabContent.SetActive(activeStoreTab == 0);
                shipsTabContent.SetActive(activeStoreTab == 1);
                if (shipPreviewsRoot != null)
                    shipPreviewsRoot.gameObject.SetActive(activeStoreTab == 1);
                if (activeStoreTab == 1)
                    EnsureShipsTabPopulated();
                var cardsImgM = tabCardsButton.GetComponent<Image>();
                var shipsImgM = tabShipsButton.GetComponent<Image>();
                if (cardsImgM != null) cardsImgM.color = activeStoreTab == 0 ? new Color(0.25f, 0.4f, 0.6f, 0.98f) : new Color(0.2f, 0.28f, 0.4f, 0.95f);
                if (shipsImgM != null) shipsImgM.color = activeStoreTab == 1 ? new Color(0.25f, 0.4f, 0.6f, 0.98f) : new Color(0.2f, 0.28f, 0.4f, 0.95f);
                return;
            }

            bool tabChanged = _lastActiveStoreTab != activeStoreTab;
            _lastActiveStoreTab = activeStoreTab;

            cardsTabContent.SetActive(activeStoreTab == 0);
            shipsTabContent.SetActive(activeStoreTab == 1);
            if (shipPreviewsRoot != null)
                shipPreviewsRoot.gameObject.SetActive(activeStoreTab == 1);

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
            var cardsImg = tabCardsButton.GetComponent<Image>();
            var shipsImg = tabShipsButton.GetComponent<Image>();
            if (cardsImg != null) cardsImg.color = activeStoreTab == 0 ? new Color(0.25f, 0.4f, 0.6f, 0.98f) : new Color(0.2f, 0.28f, 0.4f, 0.95f);
            if (shipsImg != null) shipsImg.color = activeStoreTab == 1 ? new Color(0.25f, 0.4f, 0.6f, 0.98f) : new Color(0.2f, 0.28f, 0.4f, 0.95f);
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
            if (_moonDockLayoutActive && _moonDockShipTreeHorizontal && moonDockCenterShipsHost != null && moonDockCenterShipsHost.gameObject.activeInHierarchy)
            {
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(moonDockCenterShipsHost);
                Canvas.ForceUpdateCanvases();
                float w = moonDockCenterShipsHost.rect.width;
                if (w > 80f) return Mathf.Max(120f, w - 48f);
                var trRoot = transform as RectTransform;
                if (trRoot != null && trRoot.rect.width > 80f)
                    return Mathf.Max(400f, trRoot.rect.width * 0.88f - 48f);
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
            LayoutRebuilder.ForceRebuildLayoutImmediate(shipsTabContent.GetComponent<RectTransform>());
            float b = GetShipTreeLayoutBasisWidth();
            if (Mathf.Abs(b - _cachedShipTreeBasisWidth) < 0.5f) return;
            _cachedShipTreeBasisWidth = b;
            _shipTreeStructureKey = "";
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
                float moonTreeH = shipTreeCanvas != null ? shipTreeCanvas.sizeDelta.y : 0f;
                if (moonTreeH < 1f) moonTreeH = ComputeShipTreeCanvasContentHeight();
                // Header: title + subtitle + status block + tree row; bottom slack for connectors / last row
                const float header = 132f;
                float moonPreferred = header + moonTreeH + 48f;
                var shipsLayoutElQuick = shipsTabContent.GetComponent<LayoutElement>();
                if (shipsLayoutElQuick != null)
                {
                    shipsLayoutElQuick.preferredHeight = moonPreferred;
                    shipsLayoutElQuick.minHeight = moonPreferred;
                }
                _shipsContentHeight = moonPreferred;
                return;
            }
            var shipsRt = shipsTabContent.GetComponent<RectTransform>();
            LayoutRebuilder.ForceRebuildLayoutImmediate(shipsRt);
            if (shipTreeCenterRow != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(shipTreeCenterRow);
            if (shipTreeCanvas != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(shipTreeCanvas);
            Canvas.ForceUpdateCanvases();

            float treeH = shipTreeCanvas != null ? shipTreeCanvas.sizeDelta.y : 0f;
            if (treeH < 1f)
                treeH = ComputeShipTreeCanvasContentHeight();

            // Analytic floor: title row (22) + gap (4) + hint row (24) + tree row (treeH) + extra for connectors / rounding
            const float analyticHeaderAndGap = 50f;
            const float analyticBottomSlack = 96f;
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
            if (currentShip == null || shipTreeNodes == null || shipTreeNodes.Count == 0) return;

            ShipTreeNodeView target = null;
            int curL = currentShip.ShipLevel;
            int curB = currentShip.BranchIndex;
            for (int i = 0; i < shipTreeNodes.Count; i++)
            {
                var v = shipTreeNodes[i];
                if (v != null && v.Level == curL && v.BranchIndex == curB)
                {
                    target = v;
                    break;
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
            if (shipTreeCanvas == null) return;
            ApplyShipTreeCanvasWidth();

            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            if (tree == null || currentShip == null || GetShipUpgradeStorePlanet() == null || CardShopSystem.Instance == null)
            {
                _shipTreeStructureKey = "";
                ClearShipTreeVisuals();
                if (shipTreeHintText != null) shipTreeHintText.text = "Upgrade tree unavailable.";
                UpdateShipsTabContentHeight();
                return;
            }

            string expectedStructureKey = _moonDockShipTreeHorizontal ? MoonDockShipTreeStructureKey : ShipTreeStructureKey;
            if (shipTreeNodes.Count == 0 || _shipTreeStructureKey != expectedStructureKey)
            {
                BuildShipUpgradeTreeVisualFull();
                _shipTreeStructureKey = expectedStructureKey;
            }
            else
                UpdateShipUpgradeTreeVisualState();

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
                // Moon dock horizontal tree is intentionally left-aligned.
                shipTreeCenterRow.anchorMin = new Vector2(0f, 1f);
                shipTreeCenterRow.anchorMax = new Vector2(1f, 1f);
                shipTreeCenterRow.pivot = new Vector2(0.5f, 1f);
                shipTreeCenterRow.anchoredPosition = Vector2.zero;
                hlg.childAlignment = TextAnchor.UpperLeft;
                hlg.padding = new RectOffset(12, 12, 0, 0);
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
            if (level <= 1)
                return CardShopSystem.Instance.GetMenuPreviewSpriteForChassisId(currentShip.CurrentChassisId, team);
            return CardShopSystem.Instance.GetMenuPreviewSpriteForUpgradeSlot(currentShip, storePlanet.PlanetId, level, branchIndex, team);
        }

        private string GetStarterShipDisplayName()
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
            return "Starter";
        }

        private void UpdateShipUpgradeTreeVisualState()
        {
            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            if (tree == null || currentShip == null || shipTreeNodes.Count == 0) return;
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (storePlanet == null) return;

            int homeLevel = currentHomePlanet != null ? Mathf.Max(1, currentHomePlanet.HomePlanetLevel) : 1;
            int currentLevel = currentShip.ShipLevel;
            int currentBranch = currentShip.BranchIndex;
            int nextLevel = currentLevel + 1;
            float nextCost = tree.GetGemCostForLevel(nextLevel);
            var available = tree.GetAvailableUpgrades(currentLevel, currentBranch);
            bool canBuyAny = CardShopSystem.Instance != null
                && CardShopSystem.Instance.CanPurchaseShipLevelUpgrade(currentShip, storePlanet, out _, out _, out _);

            if (shipTreeHintText != null)
            {
                if (canBuyAny && available != null && available.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("Next: ").Append(nextCost.ToString("F0")).Append("g  ·  ");
                    UpgradeTree.GetNextLevelBranchTargets(currentLevel, currentBranch, _shipTreeNextTargets);
                    for (int i = 0; i < _shipTreeNextTargets.Count; i++)
                    {
                        int bi = _shipTreeNextTargets[i];
                        ShipUpgradeNode hintNode = tree.GetNodeForBranch(nextLevel, bi);
                        string nm = GetShipDisplayName(hintNode, nextLevel, bi);
                        if (i > 0) sb.Append(" · ");
                        sb.Append(nm);
                    }
                    shipTreeHintText.text = sb.ToString();
                }
                else if (nextLevel <= 7 && homeLevel < nextLevel)
                    shipTreeHintText.text = $"Locked — raise home planet to level {nextLevel}.";
                else if (canBuyAny)
                    shipTreeHintText.text = $"Next tier costs {nextCost:F0}g.";
                else
                    shipTreeHintText.text = "Green: your path. Blue: affordable choices.";
            }

            for (int v = 0; v < shipTreeNodes.Count; v++)
            {
                var view = shipTreeNodes[v];
                if (view == null || view.Button == null) continue;

                bool isCurrent = view.Level == currentLevel && view.BranchIndex == currentBranch;
                bool tierBlockedByPlanet = view.Level > homeLevel;
                bool isNextChoice = false;
                if (view.Level == nextLevel)
                {
                    UpgradeTree.GetNextLevelBranchTargets(currentLevel, currentBranch, _shipTreeNextTargets);
                    isNextChoice = _shipTreeNextTargets.Contains(view.BranchIndex);
                }

                // Purchasable if this store's family defines a hull at this ladder slot and/or legacy UpgradeTree has ShipData (server prefers ladder when both exist).
                bool ladderOk = CardShopSystem.Instance != null
                    && !string.IsNullOrEmpty(CardShopSystem.Instance.GetChassisIdForUpgradeLadderSlot(currentShip, storePlanet.PlanetId, view.Level, view.BranchIndex));
                bool canApplyPurchase = ladderOk || (view.Node != null && view.Node.shipData != null);
                view.Button.interactable = isNextChoice && canBuyAny && contributedGems >= nextCost && !tierBlockedByPlanet && canApplyPurchase;
                var img = view.Button.GetComponent<Image>();
                if (img != null)
                {
                    if (isCurrent) img.color = new Color(0.26f, 0.62f, 0.36f, 0.98f);
                    else if (tierBlockedByPlanet) img.color = new Color(0.1f, 0.11f, 0.14f, 0.92f);
                    else if (isNextChoice) img.color = new Color(0.25f, 0.48f, 0.78f, 0.98f);
                    else img.color = new Color(0.19f, 0.23f, 0.31f, 0.94f);
                }

                if (view.PreviewImage != null)
                {
                    Sprite sp = ResolveShipTreePreviewSprite(view.Level, view.BranchIndex);
                    view.PreviewImage.sprite = sp;
                    view.PreviewImage.color = sp != null ? Color.white : new Color(0.07f, 0.09f, 0.12f, 0.95f);
                }

                if (view.LevelNumberText != null)
                {
                    if (view.PowerBarTrackWidth > 0.01f)
                        view.LevelNumberText.text = view.Level == 1 ? "Lv 1" : $"Lv {view.Level}";
                    else
                        view.LevelNumberText.text = view.Level == 1 ? "1" : view.Level.ToString();
                }
                if (view.ShipNameText != null)
                {
                    if (view.Level == 1)
                        view.ShipNameText.text = GetStarterShipDisplayName();
                    else
                        view.ShipNameText.text = GetShipDisplayName(view.Node, view.Level, view.BranchIndex);
                }
                if (view.PriceText != null)
                {
                    if (view.Level == 1)
                        view.PriceText.text = "—";
                    else
                        view.PriceText.text = $"{tree.GetGemCostForLevel(view.Level):F0}g";
                }

                float maxPowerTree = ComputeMaxPowerScoreAcrossTree();
                ApplyPowerBreakdownToNodeView(view, GetPowerBreakdownForTreeNode(view.Level, view.BranchIndex), maxPowerTree);
            }
        }

        private void BuildShipUpgradeTreeVisualFull()
        {
            ClearShipTreeVisuals();
            if (shipTreeCanvas == null)
                return;

            if (_moonDockShipTreeHorizontal)
            {
                BuildShipUpgradeTreeVisualFullHorizontal();
                return;
            }

            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            if (tree == null || currentShip == null || GetShipUpgradeStorePlanet() == null || CardShopSystem.Instance == null)
            {
                if (shipTreeHintText != null) shipTreeHintText.text = "Upgrade tree unavailable.";
                return;
            }

            // Vertical tree: center the canvas in the row (moon horizontal sets left/top anchors instead).
            if (shipTreeCanvas != null)
            {
                shipTreeCanvas.anchorMin = new Vector2(0.5f, 0.5f);
                shipTreeCanvas.anchorMax = new Vector2(0.5f, 0.5f);
                shipTreeCanvas.pivot = new Vector2(0.5f, 0.5f);
                shipTreeCanvas.anchoredPosition = Vector2.zero;
            }

            ApplyShipTreeCanvasWidth();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(shipsTabContent.GetComponent<RectTransform>());
            if (shipTreeCenterRow != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(shipTreeCenterRow);
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(shipTreeCanvas);
            Canvas.ForceUpdateCanvases();

            const int maxLevel = 7;
            float margin = ShipTreeCanvasInnerMargin;
            ComputeShipTreeGeometry(out _, out float nodeW, out float innerW);
            innerW = Mathf.Max(120f, innerW);
            nodeW = Mathf.Max(52f, nodeW);
            float nodeHeight = ShipTreeNodeHeight;
            float contentHeight = Mathf.Max(160f, margin * 2f + (maxLevel - 1) * ShipTreeLevelSpacing + nodeHeight);
            shipTreeCanvas.sizeDelta = new Vector2(shipTreeCanvas.sizeDelta.x, contentHeight);
            if (shipTreeCenterRow != null)
            {
                shipTreeCenterRow.sizeDelta = new Vector2(0f, contentHeight);
                var rowLe = shipTreeCenterRow.GetComponent<LayoutElement>();
                if (rowLe != null) rowLe.preferredHeight = contentHeight;
            }

            float maxPowerTree = ComputeMaxPowerScoreAcrossTree();

            var nodesByLevel = new Dictionary<int, List<ShipTreeNodeView>>();
            for (int level = 1; level <= maxLevel; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                var views = new List<ShipTreeNodeView>(count);
                float useW = nodeW;
                float rowW = count * useW + (count - 1) * ShipTreeColGap;
                float startX = margin + (innerW - rowW) * 0.5f;
                float y = margin + (level - 1) * ShipTreeLevelSpacing;
                for (int b = 0; b < count; b++)
                {
                    ShipUpgradeNode node = level == 1 ? null : tree.GetNodeForBranch(level, b);
                    var view = CreateShipTreeNode(level, b, node, useW, nodeHeight, maxPowerTree);
                    views.Add(view);
                    float x = startX + useW * 0.5f + b * (useW + ShipTreeColGap);
                    view.Rect.anchorMin = new Vector2(0f, 0f);
                    view.Rect.anchorMax = new Vector2(0f, 0f);
                    view.Rect.pivot = new Vector2(0.5f, 0.5f);
                    view.Rect.anchoredPosition = new Vector2(x, y);
                }
                nodesByLevel[level] = views;
            }

            for (int level = 2; level <= maxLevel; level++)
            {
                if (!nodesByLevel.TryGetValue(level, out var levelViews)) continue;
                if (!nodesByLevel.TryGetValue(level - 1, out var previousViews)) continue;
                foreach (var prevView in previousViews)
                {
                    int p = prevView.BranchIndex;
                    foreach (var nextView in levelViews)
                    {
                        int j = nextView.BranchIndex;
                        if (UpgradeTree.IsValidUpgradeStep(level - 1, p, level, j))
                            DrawTreeConnector(prevView.Rect.anchoredPosition, nextView.Rect.anchoredPosition);
                    }
                }
            }

            UpdateShipUpgradeTreeVisualState();
        }

        /// <summary>Edges (fromLevel, fromBranch, toLevel, toBranch) on the player&apos;s current upgrade path (reconstructed backward from current tier).</summary>
        private bool TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> edges)
        {
            edges = new HashSet<(int, int, int, int)>();
            if (currentShip == null) return false;
            int L = currentShip.ShipLevel;
            int B = currentShip.BranchIndex;
            if (L < 1) return false;
            while (L > 1)
            {
                int prevL = L - 1;
                int parentB = -1;
                int countP = UpgradeTree.GetShipCountForLevel(prevL);
                for (int p = 0; p < countP; p++)
                {
                    if (UpgradeTree.IsValidUpgradeStep(prevL, p, L, B))
                    {
                        parentB = p;
                        break;
                    }
                }
                if (parentB < 0)
                {
                    edges.Clear();
                    return false;
                }
                edges.Add((prevL, parentB, L, B));
                L = prevL;
                B = parentB;
            }
            return true;
        }

        private void ClearShipTreeVisuals()
        {
            for (int i = 0; i < shipTreeVisuals.Count; i++)
            {
                if (shipTreeVisuals[i] != null) Destroy(shipTreeVisuals[i]);
            }
            shipTreeVisuals.Clear();
            shipTreeNodes.Clear();
        }

        private ShipTreeNodeView CreateShipTreeNode(int level, int branchIndex, ShipUpgradeNode node, float width, float height, float maxPowerAcrossTree)
        {
            var go = new GameObject($"ShipTreeNode_{level}_{branchIndex}");
            go.transform.SetParent(shipTreeCanvas, false);
            var rect = go.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.1f, 0.14f, 0.22f, 0.98f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }

            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 6, 8);
            vlg.spacing = 4;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.fadeDuration = 0f;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.04f, 1.06f, 1.08f, 1f);
            colors.pressedColor = new Color(0.88f, 0.9f, 0.94f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.55f, 0.55f, 0.58f, 0.72f);
            btn.colors = colors;
            int targetBranch = branchIndex;
            btn.onClick.AddListener(() => OnUpgradeTreeNodeClicked(level, targetBranch));

            var headerGo = new GameObject("Header");
            headerGo.transform.SetParent(go.transform, false);
            var headerHlg = headerGo.AddComponent<HorizontalLayoutGroup>();
            headerHlg.padding = new RectOffset(0, 0, 0, 0);
            headerHlg.spacing = 4;
            headerHlg.childAlignment = TextAnchor.UpperLeft;
            headerHlg.childControlWidth = true;
            headerHlg.childControlHeight = true;
            headerHlg.childForceExpandWidth = false;
            headerHlg.childForceExpandHeight = true;
            var headerLe = headerGo.AddComponent<LayoutElement>();
            headerLe.minHeight = 26f;
            headerLe.preferredHeight = 36f;
            headerLe.flexibleHeight = 0f;

            var levelGo = new GameObject("Level");
            levelGo.transform.SetParent(headerGo.transform, false);
            var levelTmp = levelGo.AddComponent<TextMeshProUGUI>();
            levelTmp.text = level.ToString();
            levelTmp.fontSize = 15;
            levelTmp.fontStyle = FontStyles.Bold;
            levelTmp.alignment = TextAlignmentOptions.TopLeft;
            levelTmp.enableWordWrapping = false;
            levelTmp.color = new Color(0.55f, 0.78f, 1f, 1f);
            levelTmp.raycastTarget = false;
            if (fontAsset != null) levelTmp.font = fontAsset;
            var levelLe = levelGo.AddComponent<LayoutElement>();
            levelLe.preferredWidth = 26f;
            levelLe.minWidth = 22f;
            levelLe.flexibleWidth = 0f;

            var nameGo = new GameObject("ShipName");
            nameGo.transform.SetParent(headerGo.transform, false);
            var nameTmp = nameGo.AddComponent<TextMeshProUGUI>();
            nameTmp.fontSize = 14;
            nameTmp.lineSpacing = -2f;
            nameTmp.alignment = TextAlignmentOptions.TopLeft;
            nameTmp.enableWordWrapping = true;
            nameTmp.overflowMode = TextOverflowModes.Ellipsis;
            nameTmp.color = new Color(0.95f, 0.97f, 1f, 1f);
            nameTmp.raycastTarget = false;
            if (fontAsset != null) nameTmp.font = fontAsset;
            var nameLe = nameGo.AddComponent<LayoutElement>();
            nameLe.flexibleWidth = 1f;
            nameLe.minWidth = 52f;
            nameLe.minHeight = 18f;
            nameLe.preferredHeight = 36f;
            nameLe.flexibleHeight = 0f;

            var previewGo = new GameObject("Preview");
            previewGo.transform.SetParent(go.transform, false);
            var previewImg = previewGo.AddComponent<Image>();
            previewImg.preserveAspect = true;
            previewImg.raycastTarget = false;
            previewImg.maskable = true;
            var previewLe = previewGo.AddComponent<LayoutElement>();
            previewLe.minHeight = 44f;
            previewLe.preferredHeight = 52f;
            previewLe.flexibleHeight = 1f;
            previewLe.flexibleWidth = 1f;

            var barRowGo = new GameObject("PowerBar");
            barRowGo.transform.SetParent(go.transform, false);
            var barRowRect = barRowGo.GetComponent<RectTransform>();
            var barRowLe = barRowGo.AddComponent<LayoutElement>();
            barRowLe.preferredHeight = 10f;
            barRowLe.flexibleHeight = 0f;
            barRowLe.minHeight = 10f;
            barRowLe.flexibleWidth = 0f;
            var barHlg = barRowGo.AddComponent<HorizontalLayoutGroup>();
            barHlg.padding = new RectOffset(0, 0, 0, 0);
            barHlg.spacing = 1;
            barHlg.childAlignment = TextAnchor.MiddleCenter;
            barHlg.childControlWidth = true;
            barHlg.childControlHeight = true;
            barHlg.childForceExpandWidth = false;
            barHlg.childForceExpandHeight = true;
            var powerBarSegments = new Image[5];
            for (int pi = 0; pi < 5; pi++)
            {
                var segGo = new GameObject("Seg_" + pi);
                segGo.transform.SetParent(barRowGo.transform, false);
                var segImg = segGo.AddComponent<Image>();
                segImg.color = ShipAbilityCategoryColors.PowerBreakdownOdEmc[pi];
                segImg.raycastTarget = false;
                var segLe = segGo.AddComponent<LayoutElement>();
                segLe.flexibleWidth = 1f;
                segLe.minWidth = 3f;
                segLe.preferredHeight = 10f;
                powerBarSegments[pi] = segImg;
            }

            var statsRowGo = new GameObject("PowerStats");
            statsRowGo.transform.SetParent(go.transform, false);
            var statsRowLe = statsRowGo.AddComponent<LayoutElement>();
            statsRowLe.preferredHeight = 34f;
            statsRowLe.flexibleHeight = 0f;
            statsRowLe.minHeight = 34f;
            var statsHlg = statsRowGo.AddComponent<HorizontalLayoutGroup>();
            statsHlg.padding = new RectOffset(0, 0, 0, 0);
            statsHlg.spacing = 0;
            statsHlg.childAlignment = TextAnchor.UpperCenter;
            statsHlg.childControlWidth = true;
            statsHlg.childControlHeight = true;
            statsHlg.childForceExpandWidth = true;
            statsHlg.childForceExpandHeight = true;
            var powerLabels = new TextMeshProUGUI[5];
            var powerValues = new TextMeshProUGUI[5];
            for (int pi = 0; pi < 5; pi++)
            {
                var cellGo = new GameObject("Stat_" + pi);
                cellGo.transform.SetParent(statsRowGo.transform, false);
                var cellVlg = cellGo.AddComponent<VerticalLayoutGroup>();
                cellVlg.spacing = 0;
                cellVlg.childAlignment = TextAnchor.UpperCenter;
                cellVlg.childControlWidth = true;
                cellVlg.childControlHeight = true;
                cellVlg.childForceExpandWidth = true;
                cellVlg.childForceExpandHeight = false;
                var cellLe = cellGo.AddComponent<LayoutElement>();
                cellLe.flexibleWidth = 1f;
                cellLe.minWidth = 18f;

                var letterGo = new GameObject("L");
                letterGo.transform.SetParent(cellGo.transform, false);
                var letterTmp = letterGo.AddComponent<TextMeshProUGUI>();
                letterTmp.text = ShipTreePowerCategoryLetters[pi];
                letterTmp.fontSize = 6.5f;
                letterTmp.alignment = TextAlignmentOptions.Center;
                letterTmp.enableWordWrapping = false;
                letterTmp.color = new Color(0.55f, 0.62f, 0.72f, 1f);
                letterTmp.raycastTarget = false;
                if (fontAsset != null) letterTmp.font = fontAsset;
                var letterLe = letterGo.AddComponent<LayoutElement>();
                letterLe.preferredHeight = 10f;
                letterLe.flexibleHeight = 0f;

                var valGo = new GameObject("V");
                valGo.transform.SetParent(cellGo.transform, false);
                var valTmp = valGo.AddComponent<TextMeshProUGUI>();
                valTmp.text = "—";
                valTmp.fontSize = 8f;
                valTmp.fontStyle = FontStyles.Bold;
                valTmp.alignment = TextAlignmentOptions.Center;
                valTmp.enableWordWrapping = false;
                valTmp.color = ShipAbilityCategoryColors.PowerBreakdownOdEmc[pi];
                valTmp.raycastTarget = false;
                if (fontAsset != null) valTmp.font = fontAsset;
                var valLe = valGo.AddComponent<LayoutElement>();
                valLe.preferredHeight = 14f;
                valLe.flexibleHeight = 0f;

                powerLabels[pi] = letterTmp;
                powerValues[pi] = valTmp;
            }

            var priceGo = new GameObject("Price");
            priceGo.transform.SetParent(go.transform, false);
            var priceTmp = priceGo.AddComponent<TextMeshProUGUI>();
            priceTmp.fontSize = 10;
            priceTmp.alignment = TextAlignmentOptions.Center;
            priceTmp.enableWordWrapping = false;
            priceTmp.color = new Color(0.55f, 0.88f, 0.72f, 1f);
            priceTmp.raycastTarget = false;
            if (fontAsset != null) priceTmp.font = fontAsset;
            var priceLe = priceGo.AddComponent<LayoutElement>();
            priceLe.preferredHeight = 18f;
            priceLe.flexibleHeight = 0f;

            Sprite initialPreview = ResolveShipTreePreviewSprite(level, branchIndex);
            previewImg.sprite = initialPreview;
            previewImg.color = initialPreview != null ? Color.white : new Color(0.07f, 0.09f, 0.12f, 0.95f);

            if (height <= ShipTreeCompactLayoutMaxHeight)
            {
                levelTmp.fontSize = 14f;
                nameTmp.fontSize = 13f;
                nameTmp.lineSpacing = -3f;
                headerLe.preferredHeight = 30f;
                headerLe.minHeight = 24f;
                previewLe.minHeight = 22f;
                previewLe.preferredHeight = 26f;
                previewLe.flexibleHeight = 0f;
                barRowLe.preferredHeight = 7f;
                barRowLe.minHeight = 7f;
                statsRowLe.preferredHeight = 20f;
                statsRowLe.minHeight = 20f;
                priceLe.preferredHeight = 13f;
                priceTmp.fontSize = 8f;
                vlg.padding = new RectOffset(5, 5, 3, 4);
                vlg.spacing = 2f;
                for (int pi = 0; pi < 5; pi++)
                {
                    if (powerLabels[pi] != null) powerLabels[pi].fontSize = 5.5f;
                    if (powerValues[pi] != null) powerValues[pi].fontSize = 6.5f;
                }
            }

            shipTreeVisuals.Add(go);
            var view = new ShipTreeNodeView
            {
                Level = level,
                BranchIndex = branchIndex,
                Node = node,
                Button = btn,
                LevelNumberText = levelTmp,
                ShipNameText = nameTmp,
                PreviewImage = previewImg,
                PriceText = priceTmp,
                Rect = rect,
                NodeButtonWidth = width,
                PowerBarTrackWidth = 0f,
                PowerBarRow = barRowRect,
                PowerBarSegments = powerBarSegments,
                PowerStatLabels = powerLabels,
                PowerStatValues = powerValues
            };
            shipTreeNodes.Add(view);
            ApplyPowerBreakdownToNodeView(view, GetPowerBreakdownForTreeNode(level, branchIndex), maxPowerAcrossTree);
            return view;
        }

        /// <summary>Moon horizontal tree: left = level, name (2 lines), power bar + stats; right = preview top-right + price below.</summary>
        private ShipTreeNodeView CreateShipTreeNodeMoonHorizontal(int level, int branchIndex, ShipUpgradeNode node, float width, float height, float maxPowerAcrossTree)
        {
            float previewColW = Mathf.Clamp(width * 0.43f, 64f, 108f);
            float powerTrackW = Mathf.Max(48f, width - previewColW - 26f);

            var go = new GameObject($"ShipTreeNode_H_{level}_{branchIndex}");
            go.transform.SetParent(shipTreeCanvas, false);
            var rect = go.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.1f, 0.14f, 0.22f, 0.98f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }

            var rootHlg = go.AddComponent<HorizontalLayoutGroup>();
            rootHlg.padding = new RectOffset(4, 6, 4, 4);
            rootHlg.spacing = 5;
            rootHlg.childAlignment = TextAnchor.UpperLeft;
            rootHlg.childControlWidth = true;
            rootHlg.childControlHeight = true;
            rootHlg.childForceExpandWidth = false;
            rootHlg.childForceExpandHeight = true;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.fadeDuration = 0f;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.04f, 1.06f, 1.08f, 1f);
            colors.pressedColor = new Color(0.88f, 0.9f, 0.94f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.55f, 0.55f, 0.58f, 0.72f);
            btn.colors = colors;
            int targetBranch = branchIndex;
            btn.onClick.AddListener(() => OnUpgradeTreeNodeClicked(level, targetBranch));

            var leftGo = new GameObject("LeftColumn");
            leftGo.transform.SetParent(go.transform, false);
            var leftVlg = leftGo.AddComponent<VerticalLayoutGroup>();
            leftVlg.padding = new RectOffset(0, 0, 0, 0);
            leftVlg.spacing = 2;
            leftVlg.childAlignment = TextAnchor.UpperLeft;
            leftVlg.childControlWidth = true;
            leftVlg.childControlHeight = true;
            leftVlg.childForceExpandWidth = true;
            leftVlg.childForceExpandHeight = false;
            var leftLe = leftGo.AddComponent<LayoutElement>();
            leftLe.flexibleWidth = 1f;
            leftLe.flexibleHeight = 1f;
            leftLe.minWidth = 40f;

            var levelLineGo = new GameObject("LevelLine");
            levelLineGo.transform.SetParent(leftGo.transform, false);
            var levelTmp = levelLineGo.AddComponent<TextMeshProUGUI>();
            levelTmp.text = level == 1 ? "Lv 1" : $"Lv {level}";
            levelTmp.fontSize = 14;
            levelTmp.fontStyle = FontStyles.Bold;
            levelTmp.alignment = TextAlignmentOptions.TopLeft;
            levelTmp.enableWordWrapping = false;
            levelTmp.color = new Color(0.55f, 0.78f, 1f, 1f);
            levelTmp.raycastTarget = false;
            if (fontAsset != null) levelTmp.font = fontAsset;
            var levelLineLe = levelLineGo.AddComponent<LayoutElement>();
            levelLineLe.preferredHeight = 17f;
            levelLineLe.flexibleHeight = 0f;

            var nameLineGo = new GameObject("ShipName");
            nameLineGo.transform.SetParent(leftGo.transform, false);
            nameLineGo.AddComponent<RectMask2D>();
            var nameTmp = nameLineGo.AddComponent<TextMeshProUGUI>();
            nameTmp.fontSize = 16;
            nameTmp.lineSpacing = -6f;
            nameTmp.alignment = TextAlignmentOptions.TopLeft;
            nameTmp.enableWordWrapping = true;
            nameTmp.overflowMode = TextOverflowModes.Overflow;
            nameTmp.color = new Color(0.95f, 0.97f, 1f, 1f);
            nameTmp.raycastTarget = false;
            if (fontAsset != null) nameTmp.font = fontAsset;
            var nameLineLe = nameLineGo.AddComponent<LayoutElement>();
            nameLineLe.preferredHeight = 40f;
            nameLineLe.minHeight = 34f;
            nameLineLe.flexibleHeight = 0f;
            nameLineLe.flexibleWidth = 1f;

            var barRowGo = new GameObject("PowerBar");
            barRowGo.transform.SetParent(leftGo.transform, false);
            var barRowRect = barRowGo.GetComponent<RectTransform>();
            var barRowLe = barRowGo.AddComponent<LayoutElement>();
            barRowLe.preferredHeight = 8f;
            barRowLe.minHeight = 8f;
            barRowLe.flexibleHeight = 0f;
            var barHlg = barRowGo.AddComponent<HorizontalLayoutGroup>();
            barHlg.spacing = 1;
            barHlg.childAlignment = TextAnchor.MiddleLeft;
            barHlg.childControlWidth = true;
            barHlg.childControlHeight = true;
            barHlg.childForceExpandWidth = false;
            barHlg.childForceExpandHeight = true;
            var powerBarSegments = new Image[5];
            for (int pi = 0; pi < 5; pi++)
            {
                var segGo = new GameObject("Seg_" + pi);
                segGo.transform.SetParent(barRowGo.transform, false);
                var segImg = segGo.AddComponent<Image>();
                segImg.color = ShipAbilityCategoryColors.PowerBreakdownOdEmc[pi];
                segImg.raycastTarget = false;
                var segLe = segGo.AddComponent<LayoutElement>();
                segLe.flexibleWidth = 1f;
                segLe.minWidth = 3f;
                segLe.preferredHeight = 8f;
                powerBarSegments[pi] = segImg;
            }

            var statsRowGo = new GameObject("PowerStats");
            statsRowGo.transform.SetParent(leftGo.transform, false);
            var statsRowLe = statsRowGo.AddComponent<LayoutElement>();
            statsRowLe.preferredHeight = 22f;
            statsRowLe.minHeight = 20f;
            statsRowLe.flexibleHeight = 0f;
            var statsHlg = statsRowGo.AddComponent<HorizontalLayoutGroup>();
            statsHlg.spacing = 0;
            statsHlg.childAlignment = TextAnchor.UpperCenter;
            statsHlg.childControlWidth = true;
            statsHlg.childControlHeight = true;
            statsHlg.childForceExpandWidth = true;
            statsHlg.childForceExpandHeight = true;
            var powerLabels = new TextMeshProUGUI[5];
            var powerValues = new TextMeshProUGUI[5];
            for (int pi = 0; pi < 5; pi++)
            {
                var cellGo = new GameObject("Stat_" + pi);
                cellGo.transform.SetParent(statsRowGo.transform, false);
                var cellVlg = cellGo.AddComponent<VerticalLayoutGroup>();
                cellVlg.spacing = 0;
                cellVlg.childAlignment = TextAnchor.UpperCenter;
                cellVlg.childControlWidth = true;
                cellVlg.childControlHeight = true;
                cellVlg.childForceExpandWidth = true;
                cellVlg.childForceExpandHeight = false;
                var cellLe = cellGo.AddComponent<LayoutElement>();
                cellLe.flexibleWidth = 1f;
                cellLe.minWidth = 14f;

                var letterGo = new GameObject("L");
                letterGo.transform.SetParent(cellGo.transform, false);
                var letterTmp = letterGo.AddComponent<TextMeshProUGUI>();
                letterTmp.text = ShipTreePowerCategoryLetters[pi];
                letterTmp.fontSize = 6.5f;
                letterTmp.alignment = TextAlignmentOptions.Center;
                letterTmp.enableWordWrapping = false;
                letterTmp.color = new Color(0.55f, 0.62f, 0.72f, 1f);
                letterTmp.raycastTarget = false;
                if (fontAsset != null) letterTmp.font = fontAsset;
                var letterLe = letterGo.AddComponent<LayoutElement>();
                letterLe.preferredHeight = 9f;
                letterLe.flexibleHeight = 0f;

                var valGo = new GameObject("V");
                valGo.transform.SetParent(cellGo.transform, false);
                var valTmp = valGo.AddComponent<TextMeshProUGUI>();
                valTmp.text = "—";
                valTmp.fontSize = 8f;
                valTmp.fontStyle = FontStyles.Bold;
                valTmp.alignment = TextAlignmentOptions.Center;
                valTmp.enableWordWrapping = false;
                valTmp.color = ShipAbilityCategoryColors.PowerBreakdownOdEmc[pi];
                valTmp.raycastTarget = false;
                if (fontAsset != null) valTmp.font = fontAsset;
                var valLe = valGo.AddComponent<LayoutElement>();
                valLe.preferredHeight = 12f;
                valLe.flexibleHeight = 0f;

                powerLabels[pi] = letterTmp;
                powerValues[pi] = valTmp;
            }

            var leftFillGo = new GameObject("LeftFill");
            leftFillGo.transform.SetParent(leftGo.transform, false);
            var leftFillLe = leftFillGo.AddComponent<LayoutElement>();
            leftFillLe.flexibleHeight = 1f;
            leftFillLe.minHeight = 0f;

            TextMeshProUGUI priceTmp = null;

            var previewColGo = new GameObject("PreviewColumn");
            previewColGo.transform.SetParent(go.transform, false);
            var previewColLe = previewColGo.AddComponent<LayoutElement>();
            previewColLe.preferredWidth = previewColW;
            previewColLe.minWidth = previewColW;
            previewColLe.flexibleWidth = 0f;
            previewColLe.flexibleHeight = 1f;
            var previewColVlg = previewColGo.AddComponent<VerticalLayoutGroup>();
            previewColVlg.padding = new RectOffset(0, 0, 0, 0);
            previewColVlg.spacing = 3;
            previewColVlg.childAlignment = TextAnchor.UpperRight;
            previewColVlg.childControlWidth = true;
            previewColVlg.childControlHeight = true;
            previewColVlg.childForceExpandWidth = true;
            previewColVlg.childForceExpandHeight = false;

            var previewImageArea = new GameObject("PreviewImageArea");
            previewImageArea.transform.SetParent(previewColGo.transform, false);
            var previewImageAreaLe = previewImageArea.AddComponent<LayoutElement>();
            previewImageAreaLe.flexibleHeight = 1f;
            previewImageAreaLe.minHeight = MoonHorizontalShipPreviewImageBox;
            var previewAreaHlg = previewImageArea.AddComponent<HorizontalLayoutGroup>();
            previewAreaHlg.childAlignment = TextAnchor.UpperRight;
            previewAreaHlg.padding = new RectOffset(0, 0, 0, 0);
            previewAreaHlg.spacing = 0;
            previewAreaHlg.childControlWidth = false;
            previewAreaHlg.childControlHeight = true;
            previewAreaHlg.childForceExpandWidth = false;
            previewAreaHlg.childForceExpandHeight = true;

            var previewAreaSpacer = new GameObject("Spacer");
            previewAreaSpacer.transform.SetParent(previewImageArea.transform, false);
            var previewAreaSpacerLe = previewAreaSpacer.AddComponent<LayoutElement>();
            previewAreaSpacerLe.flexibleWidth = 1f;
            previewAreaSpacerLe.minWidth = 0f;

            float imgBox = Mathf.Min(previewColW - 2f, MoonHorizontalShipPreviewImageBox);
            imgBox = Mathf.Max(56f, imgBox);

            var previewHolder = new GameObject("Preview");
            previewHolder.transform.SetParent(previewImageArea.transform, false);
            var previewHolderRt = previewHolder.AddComponent<RectTransform>();
            previewHolderRt.sizeDelta = new Vector2(imgBox, imgBox);
            var previewHolderLe = previewHolder.AddComponent<LayoutElement>();
            previewHolderLe.preferredWidth = imgBox;
            previewHolderLe.preferredHeight = imgBox;
            previewHolderLe.minWidth = imgBox;
            previewHolderLe.minHeight = imgBox;
            previewHolderLe.flexibleWidth = 0f;
            previewHolderLe.flexibleHeight = 0f;
            var previewImg = previewHolder.AddComponent<Image>();
            previewImg.preserveAspect = true;
            previewImg.raycastTarget = false;
            previewImg.maskable = true;

            var priceGo = new GameObject("Price");
            priceGo.transform.SetParent(previewColGo.transform, false);
            priceTmp = priceGo.AddComponent<TextMeshProUGUI>();
            priceTmp.fontSize = 12;
            priceTmp.alignment = TextAlignmentOptions.BottomRight;
            priceTmp.enableWordWrapping = false;
            priceTmp.color = new Color(0.55f, 0.88f, 0.72f, 1f);
            priceTmp.raycastTarget = false;
            if (fontAsset != null) priceTmp.font = fontAsset;
            var priceLe = priceGo.AddComponent<LayoutElement>();
            priceLe.preferredHeight = 19f;
            priceLe.minHeight = 16f;
            priceLe.flexibleHeight = 0f;

            Sprite initialPreview = ResolveShipTreePreviewSprite(level, branchIndex);
            previewImg.sprite = initialPreview;
            previewImg.color = initialPreview != null ? Color.white : new Color(0.07f, 0.09f, 0.12f, 0.95f);

            shipTreeVisuals.Add(go);
            var view = new ShipTreeNodeView
            {
                Level = level,
                BranchIndex = branchIndex,
                Node = node,
                Button = btn,
                LevelNumberText = levelTmp,
                ShipNameText = nameTmp,
                PreviewImage = previewImg,
                PriceText = priceTmp,
                Rect = rect,
                NodeButtonWidth = width,
                PowerBarTrackWidth = powerTrackW,
                PowerBarRow = barRowRect,
                PowerBarSegments = powerBarSegments,
                PowerStatLabels = powerLabels,
                PowerStatValues = powerValues
            };
            shipTreeNodes.Add(view);
            ApplyPowerBreakdownToNodeView(view, GetPowerBreakdownForTreeNode(level, branchIndex), maxPowerAcrossTree);
            return view;
        }

        private ShipFamilyPowerScoreBreakdown GetPowerBreakdownForTreeNode(int level, int branchIndex)
        {
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (currentShip == null || storePlanet == null || CardShopSystem.Instance == null)
                return default;
            if (level <= 1)
                return CardShopSystem.Instance.GetPowerScoreBreakdownForChassisId(currentShip.CurrentChassisId);
            return CardShopSystem.Instance.GetPowerScoreBreakdownForUpgradeSlot(currentShip, storePlanet.PlanetId, level, branchIndex);
        }

        /// <summary>Strongest ship’s total power (O+D+E+M+C) in the visible tree — used as the width scale for stacked segments.</summary>
        private float ComputeMaxPowerScoreAcrossTree()
        {
            float max = 0f;
            for (int level = 1; level <= 7; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                for (int b = 0; b < count; b++)
                {
                    var bd = GetPowerBreakdownForTreeNode(level, b);
                    float t = bd.Total;
                    if (t > max) max = t;
                }
            }
            return Mathf.Max(max, 0.001f);
        }

        /// <summary>
        /// Stacked bar: segment i width = nodeWidth × (stat[i] / strongestShipTotal). Sum of segments = nodeWidth × (thisShipTotal / strongest) — absolute stats, not within-ship %.
        /// </summary>
        private static void ApplyPowerBreakdownToNodeView(ShipTreeNodeView view, ShipFamilyPowerScoreBreakdown b, float strongestShipTotalPower)
        {
            if (view == null) return;
            float[] vals = { b.offense, b.defense, b.energy, b.mobility, b.capacity };
            float total = b.Total;
            bool hasData = total > 0.01f;
            float nodeW = view.PowerBarTrackWidth > 0.01f
                ? view.PowerBarTrackWidth
                : (view.NodeButtonWidth > 1f ? view.NodeButtonWidth : (view.Rect != null ? view.Rect.sizeDelta.x : 100f));
            float maxDen = Mathf.Max(strongestShipTotalPower, 0.001f);

            float gapPx = 1f;
            if (view.PowerBarRow != null)
            {
                var barHlg = view.PowerBarRow.GetComponent<HorizontalLayoutGroup>();
                if (barHlg != null) gapPx = barHlg.spacing;
            }

            float sumSegW = 0f;
            for (int i = 0; i < 5; i++)
            {
                if (view.PowerStatValues != null && i < view.PowerStatValues.Length && view.PowerStatValues[i] != null)
                {
                    view.PowerStatValues[i].text = hasData ? vals[i].ToString("F0") : "—";
                    view.PowerStatValues[i].color = hasData ? ShipAbilityCategoryColors.PowerBreakdownOdEmc[i] : new Color(0.45f, 0.48f, 0.55f, 0.85f);
                }
                if (view.PowerBarSegments != null && i < view.PowerBarSegments.Length && view.PowerBarSegments[i] != null)
                {
                    var seg = view.PowerBarSegments[i];
                    var le = seg.GetComponent<LayoutElement>();
                    float segW;
                    if (hasData)
                    {
                        segW = nodeW * vals[i] / maxDen;
                        if (vals[i] > 0.01f && segW > 0f && segW < 1f)
                            segW = 1f;
                    }
                    else
                        segW = Mathf.Max(2f, nodeW * 0.18f);

                    if (le != null)
                    {
                        le.preferredWidth = segW;
                        le.flexibleWidth = 0f;
                        le.minWidth = 0f;
                    }
                    sumSegW += segW;
                    seg.color = hasData
                        ? ShipAbilityCategoryColors.PowerBreakdownOdEmc[i]
                        : new Color(0.22f, 0.25f, 0.3f, 0.55f);
                }
            }

            if (view.PowerBarRow != null)
            {
                var barLe = view.PowerBarRow.GetComponent<LayoutElement>();
                if (barLe != null)
                {
                    barLe.preferredWidth = sumSegW + 4f * gapPx;
                    barLe.flexibleWidth = 0f;
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(view.PowerBarRow);
            }
        }

        private void DrawTreeConnector(Vector2 from, Vector2 to)
        {
            DrawTreeConnector(from, to, ShipTreeConnectorDim, 2f);
        }

        private void DrawTreeConnector(Vector2 from, Vector2 to, Color color, float thickness)
        {
            var go = new GameObject("ShipTreeConnector");
            go.transform.SetParent(shipTreeCanvas, false);
            var rect = go.AddComponent<RectTransform>();
            Vector2 delta = to - from;
            float length = delta.magnitude;
            rect.sizeDelta = new Vector2(length, Mathf.Max(1.5f, thickness));
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = from;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            go.transform.SetAsFirstSibling();
            shipTreeVisuals.Add(go);
        }

        /// <summary>Moon horizontal tree: anchor lines at the preview column (under the ship image), not the card center.</summary>
        private static Vector2 GetMoonHorizontalTreeConnectorAnchor(ShipTreeNodeView view)
        {
            float w = view.NodeButtonWidth > 1f ? view.NodeButtonWidth : (view.Rect != null ? view.Rect.rect.width : 100f);
            float previewColW = Mathf.Clamp(w * 0.43f, 64f, 108f);
            const float padR = 6f;
            float cx = view.Rect.anchoredPosition.x + w * 0.5f - padR - previewColW * 0.5f;
            float cy = view.Rect.anchoredPosition.y;
            return new Vector2(cx, cy);
        }

        private void OnUpgradeTreeNodeClicked(int nodeLevel, int targetBranchIndex)
        {
            Planet storePlanet = GetShipUpgradeStorePlanet();
            if (currentShip == null || storePlanet == null || CardShopSystem.Instance == null) return;
            if (nodeLevel != currentShip.ShipLevel + 1) return;
            var planetNo = storePlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (planetNo == null || !planetNo.IsSpawned) return;
            CardShopSystem.Instance.PurchaseShipLevelUpgradeServerRpc(planetNo.NetworkObjectId, currentShip.NetworkObjectId, targetBranchIndex);
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null) HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
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
            if (tmp == null) return;
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

        /// <summary>Spin offer card using Shift Sci-Fi UI frames/panels when <see cref="SpinCardShiftVisuals"/> is available.</summary>
        private void CreateSpinOfferCard(Transform parent, int index, out GameObject root, out Image rarityFrame, out Image bgImage, out Image iconImage, out TextMeshProUGUI titleText, out TextMeshProUGUI levelText, out TextMeshProUGUI rarityLabel, out TextMeshProUGUI descText, out Button takeButton)
        {
            SpinCardShiftVisuals shift = GetSpinCardShiftVisuals();
            bool useShift = shift != null && shift.outerFrameSliced != null;
            const float cardH = 356f;
            root = new GameObject("SpinOffer_" + (index + 1));
            root.transform.SetParent(parent, false);
            var le = root.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minWidth = 128f;
            le.preferredHeight = cardH;
            le.minHeight = cardH;

            rarityFrame = root.AddComponent<Image>();
            rarityFrame.raycastTarget = false;
            ApplySpinCardImageSprite(rarityFrame, useShift ? shift.outerFrameSliced : null, Image.Type.Sliced);
            rarityFrame.color = useShift
                ? new Color(0.42f, 0.72f, 0.95f, 0.92f)
                : GetCardRarityFrameColor(1);

            if (useShift && shift.innerGlowSliced != null)
            {
                var glowGo = new GameObject("InnerGlow");
                glowGo.transform.SetParent(root.transform, false);
                var glowRt = glowGo.AddComponent<RectTransform>();
                glowRt.SetAsFirstSibling();
                glowRt.anchorMin = Vector2.zero;
                glowRt.anchorMax = Vector2.one;
                glowRt.offsetMin = new Vector2(5f, 5f);
                glowRt.offsetMax = new Vector2(-5f, -5f);
                var glowImg = glowGo.AddComponent<Image>();
                glowImg.sprite = shift.innerGlowSliced;
                glowImg.type = Image.Type.Simple;
                glowImg.color = new Color(0.4f, 0.75f, 1f, 0.14f);
                glowImg.raycastTarget = false;
            }

            var inner = new GameObject("Inner");
            inner.transform.SetParent(root.transform, false);
            var innerRt = inner.transform as RectTransform;
            if (innerRt != null)
            {
                innerRt.anchorMin = Vector2.zero;
                innerRt.anchorMax = Vector2.one;
                innerRt.offsetMin = new Vector2(useShift ? 10f : 6f, useShift ? 10f : 6f);
                innerRt.offsetMax = new Vector2(useShift ? -10f : -6f, useShift ? -10f : -6f);
            }
            bgImage = inner.AddComponent<Image>();
            bgImage.raycastTarget = false;
            ApplySpinCardImageSprite(bgImage, useShift ? shift.innerPanelSliced : null, Image.Type.Simple);
            bgImage.color = useShift
                ? new Color(0.06f, 0.09f, 0.14f, 0.98f)
                : new Color(0.03f, 0.06f, 0.12f, 1f);

            var innerVlg = inner.AddComponent<VerticalLayoutGroup>();
            innerVlg.padding = new RectOffset(12, 12, 14, 12);
            innerVlg.spacing = useShift ? 10 : 8;
            innerVlg.childAlignment = TextAnchor.UpperCenter;
            innerVlg.childControlWidth = true;
            innerVlg.childControlHeight = true;
            innerVlg.childForceExpandWidth = true;
            innerVlg.childForceExpandHeight = false;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(inner.transform, false);
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.preferredHeight = useShift ? 4f : 3f;
            accentLe.minHeight = useShift ? 4f : 3f;
            var accentImg = accentGo.AddComponent<Image>();
            accentImg.raycastTarget = false;
            if (useShift && shift.accentLineSliced != null)
            {
                accentImg.sprite = shift.accentLineSliced;
                accentImg.type = Image.Type.Sliced;
                accentImg.color = new Color(0.45f, 0.82f, 1f, 0.45f);
            }
            else
            {
                accentImg.color = new Color(0.35f, 0.75f, 1f, 0.35f);
            }

            var iconDock = new GameObject("IconDock");
            iconDock.transform.SetParent(inner.transform, false);
            var dockLe = iconDock.AddComponent<LayoutElement>();
            dockLe.preferredWidth = 82f;
            dockLe.preferredHeight = 82f;
            dockLe.minWidth = 82f;
            dockLe.minHeight = 82f;
            var dockBg = iconDock.AddComponent<Image>();
            dockBg.raycastTarget = false;
            ApplySpinCardImageSprite(dockBg, useShift ? shift.iconDockSliced : null, Image.Type.Sliced);
            dockBg.color = useShift
                ? new Color(0.12f, 0.2f, 0.32f, 0.95f)
                : new Color(0.06f, 0.12f, 0.22f, 1f);

            var iconInner = new GameObject("Icon");
            iconInner.transform.SetParent(iconDock.transform, false);
            var iconInnerRt = iconInner.transform as RectTransform;
            if (iconInnerRt != null)
            {
                iconInnerRt.anchorMin = new Vector2(0.5f, 0.5f);
                iconInnerRt.anchorMax = new Vector2(0.5f, 0.5f);
                iconInnerRt.sizeDelta = new Vector2(68f, 68f);
                iconInnerRt.anchoredPosition = Vector2.zero;
            }
            iconImage = iconInner.AddComponent<Image>();
            iconImage.color = new Color(0.25f, 0.6f, 0.95f, 0.55f);
            iconImage.raycastTarget = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(inner.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 44f;
            titleLe.minHeight = 40f;
            titleLe.flexibleHeight = 0f;
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.text = " ";
            titleText.fontSize = 14;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.enableWordWrapping = true;
            titleText.enableAutoSizing = true;
            titleText.fontSizeMin = 11;
            titleText.fontSizeMax = 15;
            titleText.color = new Color(0.96f, 0.98f, 1f, 1f);
            if (fontAsset != null) titleText.font = fontAsset;
            titleText.raycastTarget = false;
            ApplySpaceCardOutline(titleText, 0.22f);

            var metaGo = new GameObject("Meta");
            metaGo.transform.SetParent(inner.transform, false);
            metaGo.AddComponent<LayoutElement>().preferredHeight = 18f;
            rarityLabel = metaGo.AddComponent<TextMeshProUGUI>();
            rarityLabel.text = "";
            rarityLabel.fontSize = 11;
            rarityLabel.fontStyle = FontStyles.Bold;
            rarityLabel.alignment = TextAlignmentOptions.Center;
            rarityLabel.color = new Color(0.55f, 0.88f, 1f, 1f);
            if (fontAsset != null) rarityLabel.font = fontAsset;
            ApplySpaceCardOutline(rarityLabel, 0.18f);

            var descPanel = new GameObject("DescPanel");
            descPanel.transform.SetParent(inner.transform, false);
            var descPanelLe = descPanel.AddComponent<LayoutElement>();
            descPanelLe.flexibleHeight = 1f;
            descPanelLe.minHeight = 72f;
            var descBg = descPanel.AddComponent<Image>();
            descBg.raycastTarget = false;
            ApplySpinCardImageSprite(descBg, useShift ? shift.innerPanelSliced : null, Image.Type.Simple);
            descBg.color = useShift
                ? new Color(0.04f, 0.07f, 0.11f, 0.94f)
                : new Color(0.05f, 0.09f, 0.16f, 0.98f);
            var descVlg = descPanel.AddComponent<VerticalLayoutGroup>();
            descVlg.padding = new RectOffset(useShift ? 10 : 8, useShift ? 10 : 8, useShift ? 10 : 8, useShift ? 10 : 8);
            descVlg.childAlignment = TextAnchor.UpperLeft;
            descVlg.childControlWidth = true;
            descVlg.childControlHeight = true;
            descVlg.childForceExpandWidth = true;
            descVlg.childForceExpandHeight = true;

            var descGo = new GameObject("Desc");
            descGo.transform.SetParent(descPanel.transform, false);
            var descGoLe = descGo.AddComponent<LayoutElement>();
            descGoLe.flexibleHeight = 1f;
            descGoLe.minHeight = 56f;
            descText = descGo.AddComponent<TextMeshProUGUI>();
            descText.text = "";
            descText.fontSize = 11;
            descText.alignment = TextAlignmentOptions.TopLeft;
            descText.enableWordWrapping = true;
            descText.enableAutoSizing = true;
            descText.fontSizeMin = 9;
            descText.fontSizeMax = 12;
            descText.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            descText.overflowMode = TextOverflowModes.Ellipsis;
            if (fontAsset != null) descText.font = fontAsset;
            descText.raycastTarget = false;
            ApplySpaceCardOutline(descText, 0.16f);

            var takeGo = new GameObject("ChooseButton");
            takeGo.transform.SetParent(inner.transform, false);
            var takeBtnLe = takeGo.AddComponent<LayoutElement>();
            takeBtnLe.preferredHeight = 38f;
            takeBtnLe.minHeight = 38f;
            var takeImg = takeGo.AddComponent<Image>();
            ApplySpinCardImageSprite(takeImg, useShift ? shift.chooseButtonSliced : null, Image.Type.Sliced);
            takeImg.color = useShift
                ? new Color(0.38f, 0.78f, 1f, 0.9f)
                : new Color(0.12f, 0.55f, 0.42f, 1f);
            takeButton = takeGo.AddComponent<Button>();
            var takeLabelGo = new GameObject("Text");
            takeLabelGo.transform.SetParent(takeGo.transform, false);
            var takeLabelRect = takeLabelGo.AddComponent<RectTransform>();
            takeLabelRect.anchorMin = Vector2.zero;
            takeLabelRect.anchorMax = Vector2.one;
            takeLabelRect.offsetMin = Vector2.zero;
            takeLabelRect.offsetMax = Vector2.zero;
            var takeLabel = takeLabelGo.AddComponent<TextMeshProUGUI>();
            takeLabel.text = "Choose";
            takeLabel.fontSize = 13;
            takeLabel.fontStyle = FontStyles.Bold;
            takeLabel.alignment = TextAlignmentOptions.Center;
            takeLabel.color = new Color(0.98f, 1f, 1f, 1f);
            takeLabel.raycastTarget = false;
            if (fontAsset != null) takeLabel.font = fontAsset;
            ApplySpaceCardOutline(takeLabel, 0.12f);

            var levelHidden = new GameObject("LevelUnused");
            levelHidden.transform.SetParent(root.transform, false);
            levelHidden.SetActive(false);
            levelText = levelHidden.AddComponent<TextMeshProUGUI>();
            levelText.text = "";
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

        private void RefreshStoreLabels()
        {
            // Server is source of truth (Show() was zeroing contributedGems while pendingGemsRequest blocked syncing).
            contributedGems = lastReceivedGems;
            if (gemsText != null) gemsText.text = $"Your contributed gems: {contributedGems:F0}";

            if (cardRoots == null || cardButtons == null || currentShip == null || currentPlanet == null) return;
            if (CardShopSystem.Instance == null)
            {
                for (int i = 0; i < cardRoots.Length; i++)
                {
                    if (cardRoots[i] != null) cardRoots[i].SetActive(false);
                }
                if (cardSpinButton != null) cardSpinButton.gameObject.SetActive(false);
                if (!_moonDockLayoutActive || _moonDockCenterView == MoonDockCenterView.Ships)
                    RefreshShipsTab();
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
                cardSpinButton.interactable = poolCount > 0 && contributedGems >= spinCost;
                if (cardSpinButtonImage != null)
                {
                    var ca = cardSpinButtonImage.color;
                    ca.a = cardSpinButton.interactable ? 1f : 0.45f;
                    cardSpinButtonImage.color = ca;
                }
            }
            if (cardSpinButtonLabel != null)
                cardSpinButtonLabel.text = $"Spin — {spinCost:F0} g";

            for (int i = 0; i < cardRoots.Length; i++)
            {
                string offerId = CardShopSystem.Instance.GetClientSpinOfferCardId(i);
                CardData card = !string.IsNullOrEmpty(offerId) ? CardShopSystem.Instance.GetCardByIdForShip(currentShip, offerId) : null;
                cardEntries[i] = card;
                if (cardRoots[i] != null)
                    cardRoots[i].SetActive(true);

                if (card == null)
                {
                    SpinCardShiftVisuals shEmpty = GetSpinCardShiftVisuals();
                    bool shiftEmpty = shEmpty != null && shEmpty.outerFrameSliced != null;
                    if (cardTitleTexts[i] != null)
                    {
                        cardTitleTexts[i].enableAutoSizing = false;
                        cardTitleTexts[i].fontSize = 30f;
                        cardTitleTexts[i].color = shiftEmpty
                            ? new Color(0.45f, 0.62f, 0.85f, 0.88f)
                            : new Color(0.5f, 0.65f, 0.88f, 0.95f);
                        cardTitleTexts[i].text = (i + 1).ToString();
                        ApplySpaceCardOutline(cardTitleTexts[i], 0.2f);
                    }
                    if (cardDescTexts[i] != null)
                    {
                        cardDescTexts[i].fontSize = 11f;
                        cardDescTexts[i].color = shiftEmpty
                            ? new Color(0.65f, 0.78f, 0.94f, 0.92f)
                            : new Color(0.72f, 0.8f, 0.92f, 1f);
                        cardDescTexts[i].text = "Appears after a spin";
                        ApplySpaceCardOutline(cardDescTexts[i], 0.15f);
                    }
                    if (cardRarityLabels != null && i < cardRarityLabels.Length && cardRarityLabels[i] != null)
                    {
                        cardRarityLabels[i].text = "";
                        cardRarityLabels[i].color = new Color(0.55f, 0.88f, 1f, 1f);
                    }
                    if (cardRarityFrameImages != null && i < cardRarityFrameImages.Length && cardRarityFrameImages[i] != null)
                        cardRarityFrameImages[i].color = shiftEmpty
                            ? new Color(0.36f, 0.5f, 0.66f, 0.5f)
                            : new Color(0.4f, 0.48f, 0.58f, 0.75f);
                    if (cardIconImages != null && i < cardIconImages.Length && cardIconImages[i] != null)
                    {
                        cardIconImages[i].sprite = null;
                        cardIconImages[i].color = shiftEmpty
                            ? new Color(0.2f, 0.38f, 0.58f, 0.42f)
                            : new Color(0.22f, 0.35f, 0.52f, 0.55f);
                    }
                    if (cardBgImages != null && i < cardBgImages.Length && cardBgImages[i] != null)
                        cardBgImages[i].color = shiftEmpty
                            ? new Color(0.05f, 0.08f, 0.12f, 0.94f)
                            : new Color(0.04f, 0.07f, 0.13f, 1f);
                    if (cardButtons[i] != null)
                    {
                        cardButtons[i].interactable = false;
                        var takeImgEmpty = cardButtons[i].GetComponent<Image>();
                        if (takeImgEmpty != null && shEmpty != null && shEmpty.chooseButtonSliced != null)
                        {
                            takeImgEmpty.sprite = shEmpty.chooseButtonSliced;
                            takeImgEmpty.type = Image.Type.Sliced;
                            takeImgEmpty.color = new Color(0.15f, 0.17f, 0.22f, 0.62f);
                        }
                        var tl = cardButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                        if (tl != null)
                        {
                            tl.text = "Choose";
                            tl.color = new Color(0.85f, 0.88f, 0.92f, 0.85f);
                            ApplySpaceCardOutline(tl, 0.12f);
                        }
                    }
                    continue;
                }

                if (cardTitleTexts[i] != null)
                {
                    cardTitleTexts[i].enableAutoSizing = true;
                    cardTitleTexts[i].fontSize = 14f;
                    cardTitleTexts[i].color = new Color(0.98f, 0.99f, 1f, 1f);
                    cardTitleTexts[i].text = card.displayName;
                    ApplySpaceCardOutline(cardTitleTexts[i], 0.22f);
                }
                int rar = Mathf.Clamp((int)card.rarity, 1, 5);
                int cl = Mathf.Max(1, card.cardLevel);
                SpinCardShiftVisuals shCard = GetSpinCardShiftVisuals();
                bool shiftCard = shCard != null && shCard.outerFrameSliced != null;
                if (cardRarityFrameImages != null && i < cardRarityFrameImages.Length && cardRarityFrameImages[i] != null)
                {
                    Color rc = GetCardRarityFrameColor(rar);
                    cardRarityFrameImages[i].color = shiftCard
                        ? Color.Lerp(new Color(0.42f, 0.72f, 0.95f, 0.92f), rc, 0.55f)
                        : rc;
                }
                if (cardBgImages != null && i < cardBgImages.Length && cardBgImages[i] != null)
                {
                    cardBgImages[i].color = shiftCard
                        ? Color.Lerp(new Color(0.06f, 0.09f, 0.14f, 0.98f), GetSlotTypeColor(card.slotType), 0.2f)
                        : Color.Lerp(new Color(0.04f, 0.07f, 0.13f, 1f), GetSlotTypeColor(card.slotType), 0.22f);
                }
                if (cardIconImages != null && i < cardIconImages.Length && cardIconImages[i] != null)
                {
                    if (card.icon != null)
                    {
                        cardIconImages[i].sprite = card.icon;
                        cardIconImages[i].color = Color.white;
                        cardIconImages[i].preserveAspect = true;
                    }
                    else
                    {
                        cardIconImages[i].sprite = null;
                        cardIconImages[i].color = Color.Lerp(GetSlotTypeColor(card.slotType), new Color(0.12f, 0.18f, 0.28f, 1f), 0.4f);
                    }
                }
                if (cardRarityLabels != null && i < cardRarityLabels.Length && cardRarityLabels[i] != null)
                {
                    cardRarityLabels[i].text = $"Lv.{cl} · {GetCardSlotTypeLabel(card.slotType)} · {GetCardRarityLabel(rar)}";
                    cardRarityLabels[i].color = new Color(0.5f, 0.9f, 1f, 1f);
                    ApplySpaceCardOutline(cardRarityLabels[i], 0.18f);
                }
                if (cardDescTexts[i] != null)
                {
                    cardDescTexts[i].text = string.IsNullOrEmpty(card.description) ? "" : card.description;
                    cardDescTexts[i].color = new Color(0.9f, 0.93f, 0.98f, 1f);
                    ApplySpaceCardOutline(cardDescTexts[i], 0.16f);
                }
                int cardLvl = Mathf.Max(1, card.cardLevel);
                bool levelOk = cardLvl <= shipLevel;
                if (cardButtons[i] != null)
                {
                    bool canChoose = hasEmptySlot && levelOk && !string.IsNullOrEmpty(offerId);
                    cardButtons[i].interactable = canChoose;
                    var takeImgFilled = cardButtons[i].GetComponent<Image>();
                    if (takeImgFilled != null && shCard != null && shCard.chooseButtonSliced != null)
                    {
                        takeImgFilled.sprite = shCard.chooseButtonSliced;
                        takeImgFilled.type = Image.Type.Sliced;
                        takeImgFilled.color = canChoose
                            ? new Color(0.36f, 0.78f, 1f, 0.95f)
                            : new Color(0.22f, 0.25f, 0.32f, 0.88f);
                    }
                    var takeLabel = cardButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                    if (takeLabel != null)
                    {
                        takeLabel.color = new Color(1f, 1f, 1f, 1f);
                        if (!hasEmptySlot) takeLabel.text = "No slot";
                        else if (!levelOk) takeLabel.text = $"Need Lv.{cardLvl}";
                        else takeLabel.text = "Choose";
                        ApplySpaceCardOutline(takeLabel, 0.14f);
                    }
                }
            }

            if (!_moonDockLayoutActive || _moonDockCenterView == MoonDockCenterView.Ships)
                RefreshShipsTab();
        }

        private void BuildShipsTabByLevel(List<ShipUnlockEntry> unlocked, float contributedGems)
        {
            if (shipsRowsContainer == null || shipPreviewsRoot == null)
            {
                EnsurePanelExists();
                if (shipsRowsContainer == null || shipPreviewsRoot == null) return;
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
            for (int c = shipPreviewsRoot.childCount - 1; c >= 0; c--)
            {
                Transform t = shipPreviewsRoot.GetChild(c);
                if (t == null || !t) continue;
                GameObject go = t.gameObject;
                var cam = go.GetComponentInChildren<UnityEngine.Camera>();
                if (cam != null && cam.targetTexture != null)
                {
                    cam.targetTexture.Release();
                    cam.targetTexture = null;
                }
                Destroy(go);
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
            float cost = entry.gemCost > 0f ? entry.gemCost : ShipUnlockTable.GetTierCost(tierLevel);
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
            var rawImg = previewGo.AddComponent<RawImage>();
            rawImg.color = new Color(0.08f, 0.1f, 0.18f, 0.95f);

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

            GameObject prefab = CardShopSystem.Instance != null ? CardShopSystem.Instance.GetShipPrefabForChassisId(chassis.chassisId) : null;
            if (prefab != null)
                SetupShipPreview(prefab, rawImg, previewRect);
        }

        private void SetupShipPreview(GameObject shipPrefab, RawImage targetImage, RectTransform previewRect)
        {
            if (shipPreviewsRoot == null || targetImage == null) return;
            RenderTexture rt = new RenderTexture(ShipPreviewRenderSize, ShipPreviewRenderSize, 16);
            rt.name = "ShipPreviewRT";
            targetImage.texture = rt;

            GameObject previewRootGo = new GameObject("ShipPreview");
            previewRootGo.transform.SetParent(shipPreviewsRoot, false);
            previewRootGo.transform.localPosition = Vector3.zero;
            previewRootGo.transform.localRotation = Quaternion.identity;
            previewRootGo.transform.localScale = Vector3.one;

            var camGo = new GameObject("PreviewCamera");
            camGo.transform.SetParent(previewRootGo.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 8f, 0f);
            camGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 4f;
            cam.targetTexture = rt;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.1f, 0.18f, 1f);
            cam.cullingMask = 1 << 0;
            cam.enabled = true;

            GameObject instance = Instantiate(shipPrefab);
            instance.transform.SetParent(previewRootGo.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one * 0.35f;
            var no = instance.GetComponent<Unity.Netcode.NetworkObject>();
            if (no != null) no.enabled = false;
            var ship = instance.GetComponent<TitanOrbit.Entities.Starship>();
            if (ship != null) ship.enabled = false;
            foreach (var rb in instance.GetComponentsInChildren<Rigidbody>(true))
                rb.isKinematic = true;

            var lightGo = new GameObject("PreviewLight");
            lightGo.transform.SetParent(previewRootGo.transform, false);
            lightGo.transform.localPosition = new Vector3(2f, 6f, 2f);
            lightGo.transform.LookAt(previewRootGo.transform.position);
            var lightComp = lightGo.AddComponent<Light>();
            lightComp.type = LightType.Directional;
            lightComp.intensity = 0.9f;
            lightComp.cullingMask = 1 << 0;

            var rotator = previewRootGo.AddComponent<ShipPreviewRotateToMouse>();
            rotator.SetPreviewRect(previewRect);
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

        private void RefreshSlots()
        {
            if (currentShip == null || slotBoxes == null) return;

            int slotCount = currentShip.SlotCount;

            // Resize slot panel and grid to match ship's slot count (level 2 = 2 slots, level 3 = 3 slots, etc.)
            if (!_moonDockLayoutActive && slotPanelRect != null && slotGridRect != null && storePanelRect != null)
            {
                int effectiveSlotRows = Mathf.Max(1, Mathf.Min(MaxSlotRows / SlotGridColumns, Mathf.CeilToInt((float)slotCount / SlotGridColumns)));
                float slotGridTotalH = effectiveSlotRows * SlotCardHeight + (effectiveSlotRows - 1) * SlotCellSpacing;
                float slotPanelHeight = SlotPanelHeaderHeight + 8f + slotGridTotalH + 12f;
                slotPanelRect.offsetMin = new Vector2(12f, -slotPanelHeight);
                slotPanelRect.offsetMax = new Vector2(-12f, 0f);
                slotGridRect.sizeDelta = new Vector2(-24f, slotGridTotalH);
                float storePanelTop = slotPanelHeight + 8f;
                storePanelRect.offsetMax = new Vector2(-12f, -storePanelTop);
            }
            else if (_moonDockLayoutActive && slotPanelRect != null && slotGridRect != null)
            {
                int effectiveSlotRows = Mathf.Max(1, Mathf.Min(MaxSlotRows / SlotGridColumns, Mathf.CeilToInt((float)slotCount / SlotGridColumns)));
                float slotGridTotalH = effectiveSlotRows * SlotCardHeight + (effectiveSlotRows - 1) * SlotCellSpacing;
                float slotPanelHeight = SlotPanelHeaderHeight + 8f + slotGridTotalH + 12f;
                slotPanelRect.offsetMin = new Vector2(12f, -slotPanelHeight);
                slotPanelRect.offsetMax = new Vector2(-12f, 0f);
                slotGridRect.sizeDelta = new Vector2(-24f, slotGridTotalH);
            }
            if (loadoutSectionLabel != null)
                loadoutSectionLabel.text = $"Ship Loadout ({slotCount} slot{(slotCount != 1 ? "s" : "")}) — tap ✕ on a card to remove";
            var cards = currentShip.EquippedCards;
            for (int i = 0; i < slotBoxes.Length; i++)
            {
                if (slotBoxes[i] == null) continue;
                bool visible = i < slotCount;
                slotBoxes[i].SetActive(visible);
                if (!visible) continue;
                CardData card = (cards != null && i < cards.Count) ? cards[i] : null;
                if (slotTitleTexts != null && i < slotTitleTexts.Length && slotTitleTexts[i] != null)
                    slotTitleTexts[i].text = card != null ? card.displayName : "Empty";
                if (slotDescTexts != null && i < slotDescTexts.Length && slotDescTexts[i] != null)
                    slotDescTexts[i].text = card != null && !string.IsNullOrEmpty(card.description) ? card.description : "";
                if (slotLevelTexts[i] != null)
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
            CardShopSystem.Instance.PurchaseCardServerRpc(planetNo.NetworkObjectId, shipNo.NetworkObjectId, card.cardId);
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null) HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private bool _moonDockReparentDone;

        private void EnsureMoonDockChromeExists()
        {
            if (_moonDockChromeReady && moonDockRightRail != null)
            {
                EnsureMoonDockCloseButtonExists();
                return;
            }

            moonDockRightRail = new GameObject("MoonDockRightRail");
            moonDockRightRail.transform.SetParent(transform, false);
            var railRt = moonDockRightRail.AddComponent<RectTransform>();
            railRt.anchorMin = new Vector2(1f, 0.22f);
            railRt.anchorMax = new Vector2(1f, 0.78f);
            railRt.pivot = new Vector2(1f, 0.5f);
            railRt.anchoredPosition = new Vector2(-16f, 0f);
            railRt.sizeDelta = new Vector2(136f, 220f);
            var railVlg = moonDockRightRail.AddComponent<VerticalLayoutGroup>();
            railVlg.spacing = 14f;
            railVlg.childAlignment = TextAnchor.MiddleCenter;
            railVlg.childControlWidth = true;
            railVlg.childControlHeight = false;
            railVlg.padding = new RectOffset(10, 10, 10, 10);

            moonDockBtnCards = CreateMoonDockRailButton(moonDockRightRail.transform, "Cards");
            moonDockBtnShips = CreateMoonDockRailButton(moonDockRightRail.transform, "Ships");
            moonDockBtnCards.onClick.AddListener(() => SetMoonDockCenterView(MoonDockCenterView.Cards));
            moonDockBtnShips.onClick.AddListener(() => SetMoonDockCenterView(MoonDockCenterView.Ships));

            moonDockCenterBackdrop = new GameObject("MoonDockCenterBackdrop");
            moonDockCenterBackdrop.transform.SetParent(transform, false);
            var bdRt = moonDockCenterBackdrop.AddComponent<RectTransform>();
            bdRt.anchorMin = Vector2.zero;
            bdRt.anchorMax = Vector2.one;
            bdRt.offsetMin = bdRt.offsetMax = Vector2.zero;
            var bdImg = moonDockCenterBackdrop.AddComponent<Image>();
            bdImg.color = new Color(0f, 0f, 0f, 0.45f);
            bdImg.raycastTarget = true;

            var cardsScrollGo = new GameObject("MoonDockCardsScroll");
            cardsScrollGo.transform.SetParent(moonDockCenterBackdrop.transform, false);
            var cardsScrollRt = cardsScrollGo.AddComponent<RectTransform>();
            cardsScrollRt.anchorMin = new Vector2(0.04f, 0.06f);
            cardsScrollRt.anchorMax = new Vector2(0.88f, 0.94f);
            cardsScrollRt.offsetMin = cardsScrollRt.offsetMax = Vector2.zero;
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
            cardsVlg.spacing = 22f;
            cardsVlg.childAlignment = TextAnchor.UpperCenter;
            cardsVlg.childControlWidth = true;
            cardsVlg.childControlHeight = true;
            cardsVlg.childForceExpandWidth = true;
            cardsVlg.childForceExpandHeight = false;
            cardsVlg.padding = new RectOffset(20, 20, 20, 32);
            var cardsFitter = moonDockCenterCardsHost.gameObject.AddComponent<ContentSizeFitter>();
            cardsFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            cardsFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            moonDockCardsScroll.content = moonDockCenterCardsHost;

            moonDockCenterShipsHost = new GameObject("MoonDockShipsHost").AddComponent<RectTransform>();
            moonDockCenterShipsHost.SetParent(moonDockCenterBackdrop.transform, false);
            moonDockCenterShipsHost.anchorMin = new Vector2(0.04f, 0.06f);
            moonDockCenterShipsHost.anchorMax = new Vector2(0.96f, 0.94f);
            moonDockCenterShipsHost.offsetMin = moonDockCenterShipsHost.offsetMax = Vector2.zero;
            var shipsHostBg = moonDockCenterShipsHost.gameObject.AddComponent<Image>();
            shipsHostBg.color = new Color(0.06f, 0.07f, 0.11f, 0.97f);
            shipsHostBg.raycastTarget = false;
            if (panelBackgroundSprite != null) { shipsHostBg.sprite = panelBackgroundSprite; shipsHostBg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }
            var shipsHostVlg = moonDockCenterShipsHost.gameObject.AddComponent<VerticalLayoutGroup>();
            shipsHostVlg.childAlignment = TextAnchor.UpperCenter;
            shipsHostVlg.childControlWidth = true;
            shipsHostVlg.childControlHeight = true;
            shipsHostVlg.childForceExpandWidth = true;
            shipsHostVlg.childForceExpandHeight = false;
            shipsHostVlg.padding = new RectOffset(12, 12, 10, 22);
            shipsHostVlg.spacing = 4f;

            EnsureMoonDockCloseButtonExists();

            _moonDockChromeReady = true;
            moonDockRightRail.SetActive(false);
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
            moonDockCloseButton.onClick.AddListener(() => SetMoonDockCenterView(MoonDockCenterView.None));
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

        private Button CreateMoonDockRailButton(Transform parent, string label)
        {
            var go = new GameObject("MoonDock_" + label);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(118f, 46f);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 118f;
            le.preferredHeight = 46f;
            le.minHeight = 46f;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.22f, 0.32f, 0.48f, 0.96f);
            if (buttonSprite != null) { img.sprite = buttonSprite; img.type = Image.Type.Sliced; }
            var btn = go.AddComponent<Button>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var tr = textGo.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(6f, 4f);
            tr.offsetMax = new Vector2(-6f, -4f);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 17;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            if (fontAsset != null) tmp.font = fontAsset;
            return btn;
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
            if (moonDockRightRail != null) moonDockRightRail.SetActive(false);

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

            if (moonDockRightRail != null) moonDockRightRail.SetActive(false);
            if (moonDockCenterBackdrop != null) moonDockCenterBackdrop.SetActive(false);

            RestoreMoonDockParents();

            var myRect = transform as RectTransform;
            if (myRect != null)
            {
                myRect.anchorMin = new Vector2(0f, 1f);
                myRect.anchorMax = new Vector2(0f, 1f);
                myRect.pivot = new Vector2(0f, 1f);
                myRect.anchoredPosition = new Vector2(LeftMargin, -TopOffsetBelowShipStats);
                myRect.sizeDelta = new Vector2(Mathf.Max(PanelWidth, SlotPanelWidthConst), 720f);
            }
        }

        /// <summary>Inserts a tall spacer + extra air so the card shop sits well below loadout slots (no decorative line — avoids a bar crossing the slot row).</summary>
        private void EnsureMoonDockLoadoutCardsGap()
        {
            if (_moonDockLoadoutCardsGap != null || moonDockCenterCardsHost == null || cardsTabContent == null) return;

            int insertAt = cardsTabContent.transform.GetSiblingIndex();

            _moonDockLoadoutCardsGap = new GameObject("LoadoutToCardsGap");
            _moonDockLoadoutCardsGap.transform.SetParent(moonDockCenterCardsHost, false);
            var gapLe = _moonDockLoadoutCardsGap.AddComponent<LayoutElement>();
            gapLe.preferredHeight = 72f;
            gapLe.minHeight = 72f;
            gapLe.flexibleHeight = 0f;

            _moonDockLoadoutCardsGap.transform.SetSiblingIndex(insertAt);

            _moonDockCardsBelowDividerSpacer = new GameObject("CardsBelowLoadoutSpacer");
            _moonDockCardsBelowDividerSpacer.transform.SetParent(moonDockCenterCardsHost, false);
            var airLe = _moonDockCardsBelowDividerSpacer.AddComponent<LayoutElement>();
            airLe.preferredHeight = 88f;
            airLe.minHeight = 88f;
            airLe.flexibleHeight = 0f;
            _moonDockCardsBelowDividerSpacer.transform.SetSiblingIndex(cardsTabContent.transform.GetSiblingIndex());
        }

        private void ReparentMoonDockContent()
        {
            if (_moonDockReparentDone || slotPanel == null || moonDockCenterCardsHost == null || storeContentRoot == null || cardsTabContent == null || shipsTabContent == null)
                return;

            _moonDockSavedSlotPanelParent = slotPanel.transform.parent;
            _moonDockSavedSlotPanelSibling = slotPanel.transform.GetSiblingIndex();
            slotPanel.transform.SetParent(moonDockCenterCardsHost, false);

            if (gemsText != null)
            {
                _moonDockSavedGemsParent = gemsText.transform.parent;
                _moonDockSavedGemsSibling = gemsText.transform.GetSiblingIndex();
                gemsText.transform.SetParent(moonDockCenterCardsHost, false);
            }

            _moonDockSavedCardsTabParent = cardsTabContent.transform.parent;
            _moonDockSavedCardsTabSibling = cardsTabContent.transform.GetSiblingIndex();
            cardsTabContent.transform.SetParent(moonDockCenterCardsHost, false);

            _moonDockSavedShipsTabParent = shipsTabContent.transform.parent;
            _moonDockSavedShipsTabSibling = shipsTabContent.transform.GetSiblingIndex();
            shipsTabContent.transform.SetParent(moonDockCenterShipsHost, false);

            if (storePanel != null)
            {
                var storeLabel = storePanel.transform.Find("Store");
                if (storeLabel != null) storeLabel.gameObject.SetActive(false);
                var tabStrip = storePanel.transform.Find("TabStrip");
                if (tabStrip != null) tabStrip.gameObject.SetActive(false);
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

            if (gemsText != null)
            {
                var gemsLe = gemsText.GetComponent<LayoutElement>();
                if (gemsLe == null)
                {
                    gemsLe = gemsText.gameObject.AddComponent<LayoutElement>();
                    gemsLe.preferredHeight = 26f;
                    gemsLe.flexibleHeight = 0f;
                }
            }

            EnsureMoonDockLoadoutCardsGap();

            var cardsLe = cardsTabContent.GetComponent<LayoutElement>();
            if (cardsLe == null) cardsLe = cardsTabContent.AddComponent<LayoutElement>();
            cardsLe.flexibleHeight = 0f;
            cardsLe.minHeight = 520f;
            cardsLe.preferredHeight = Mathf.Max(cardsLe.preferredHeight, 520f);

            var shipsTabLe = shipsTabContent.GetComponent<LayoutElement>();
            if (shipsTabLe == null) shipsTabLe = shipsTabContent.AddComponent<LayoutElement>();
            shipsTabLe.flexibleWidth = 1f;
            shipsTabLe.flexibleHeight = 1f;
            shipsTabLe.minHeight = 320f;

            _moonDockReparentDone = true;
        }

        private void RestoreMoonDockParents()
        {
            if (!_moonDockReparentDone) return;

            if (_moonDockLoadoutCardsGap != null)
            {
                Destroy(_moonDockLoadoutCardsGap);
                _moonDockLoadoutCardsGap = null;
            }
            if (_moonDockCardsBelowDividerSpacer != null)
            {
                Destroy(_moonDockCardsBelowDividerSpacer);
                _moonDockCardsBelowDividerSpacer = null;
            }
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
            }
            if (gemsText != null && _moonDockSavedGemsParent != null)
            {
                gemsText.transform.SetParent(_moonDockSavedGemsParent, false);
                gemsText.transform.SetSiblingIndex(_moonDockSavedGemsSibling);
            }
            if (cardsTabContent != null && _moonDockSavedCardsTabParent != null)
            {
                cardsTabContent.transform.SetParent(_moonDockSavedCardsTabParent, false);
                cardsTabContent.transform.SetSiblingIndex(_moonDockSavedCardsTabSibling);
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
                var tabStrip = storePanel.transform.Find("TabStrip");
                if (tabStrip != null) tabStrip.gameObject.SetActive(true);
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
                if (moonDockRightRail != null) moonDockRightRail.SetActive(false);
                return;
            }

            bool cards = view == MoonDockCenterView.Cards;
            ApplyShipTreeHudObscuring(show);
            if (moonDockRightRail != null) moonDockRightRail.SetActive(show);
            if (moonDockCardsScroll != null) moonDockCardsScroll.gameObject.SetActive(cards);
            if (moonDockCenterShipsHost != null) moonDockCenterShipsHost.gameObject.SetActive(!cards);

            if (cards)
            {
                activeStoreTab = 0;
                _moonDockShipTreeHorizontal = false;
                RefreshStoreTabVisibility();
                ApplyMoonDockCardGridWidth();
                RefreshSlots();
                RefreshStoreLabels();
            }
            else
            {
                activeStoreTab = 1;
                _moonDockShipTreeHorizontal = true;
                _shipTreeStructureKey = "";
                RefreshStoreTabVisibility();
                RefreshShipsTab(scrollToActiveShipNode: false);
            }

            if (cards && moonDockCardsScroll != null) moonDockCardsScroll.verticalNormalizedPosition = 1f;
            if (moonDockCenterBackdrop != null) moonDockCenterBackdrop.transform.SetAsLastSibling();
            if (moonDockCloseButton != null) moonDockCloseButton.transform.SetAsLastSibling();
            ApplyMoonDockShipTreeRowLayout();
            Canvas.ForceUpdateCanvases();
        }

        private void RebuildMoonDockLayoutsAfterShow()
        {
            if (moonDockCenterCardsHost != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(moonDockCenterCardsHost);
            if (slotGridRoot != null)
            {
                var slotRect = slotGridRoot.transform as RectTransform;
                if (slotRect != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(slotRect);
            }
            Canvas.ForceUpdateCanvases();
        }

        private void ApplyMoonDockCardGridWidth()
        {
            if (moonDockCenterCardsHost == null) return;
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(moonDockCenterCardsHost);
            Canvas.ForceUpdateCanvases();
            if (_cardSpinRowLayout != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_cardSpinRowLayout.GetComponent<RectTransform>());
        }

        private void BuildShipUpgradeTreeVisualFullHorizontal()
        {
            ClearShipTreeVisuals();
            if (shipTreeCanvas == null) return;

            UpgradeTree tree = UpgradeSystem.Instance != null ? UpgradeSystem.Instance.UpgradeTree : null;
            if (tree == null || currentShip == null || GetShipUpgradeStorePlanet() == null || CardShopSystem.Instance == null)
            {
                if (shipTreeHintText != null) shipTreeHintText.text = "Upgrade tree unavailable.";
                return;
            }

            // Top-left canvas: matches HorizontalLayoutGroup UpperLeft so the tree fills from the panel's left edge.
            shipTreeCanvas.anchorMin = new Vector2(0f, 1f);
            shipTreeCanvas.anchorMax = new Vector2(0f, 1f);
            shipTreeCanvas.pivot = new Vector2(0f, 1f);
            shipTreeCanvas.anchoredPosition = Vector2.zero;

            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(shipsTabContent.GetComponent<RectTransform>());
            if (moonDockCenterShipsHost != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(moonDockCenterShipsHost);
            Canvas.ForceUpdateCanvases();

            float basis = GetShipTreeLayoutBasisWidth();
            const int maxLevel = 7;
            float margin = ShipTreeCanvasInnerMargin;
            float innerW = Mathf.Max(240f, basis - 2f * margin);
            float colGap = MoonHorizontalLevelColGap;
            float nodeW = (innerW - (maxLevel - 1) * colGap) / maxLevel;
            nodeW = Mathf.Clamp(nodeW, 74f, 210f);
            float nodeH = MoonHorizontalShipTreeNodeHeight;
            float branchGapY = MoonHorizontalBranchGapY;

            int maxNodesInAnyColumn = 1;
            for (int L = 1; L <= maxLevel; L++)
                maxNodesInAnyColumn = Mathf.Max(maxNodesInAnyColumn, UpgradeTree.GetShipCountForLevel(L));
            float maxColStackH = maxNodesInAnyColumn * nodeH + (maxNodesInAnyColumn - 1) * branchGapY;

            float canvasW = margin * 2f + maxLevel * nodeW + (maxLevel - 1) * colGap;
            float canvasH = margin * 2f + maxColStackH;

            shipTreeCanvas.sizeDelta = new Vector2(canvasW, canvasH);
            var treeLayoutEl = shipTreeCanvas.GetComponent<LayoutElement>();
            if (treeLayoutEl != null)
            {
                treeLayoutEl.preferredWidth = canvasW;
                treeLayoutEl.minWidth = canvasW;
                treeLayoutEl.flexibleWidth = 0f;
            }
            if (shipTreeCenterRow != null)
            {
                shipTreeCenterRow.sizeDelta = new Vector2(0f, canvasH);
                var rowLe = shipTreeCenterRow.GetComponent<LayoutElement>();
                if (rowLe != null) rowLe.preferredHeight = canvasH;
            }

            float maxPowerTree = ComputeMaxPowerScoreAcrossTree();
            TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> pathEdges);

            var nodesByLevel = new Dictionary<int, List<ShipTreeNodeView>>();
            for (int level = 1; level <= maxLevel; level++)
            {
                int count = UpgradeTree.GetShipCountForLevel(level);
                var views = new List<ShipTreeNodeView>(count);
                float colCenterX = margin + nodeW * 0.5f + (level - 1) * (nodeW + colGap);
                float stackH = count * nodeH + (count - 1) * branchGapY;
                float yBase = margin + (maxColStackH - stackH) * 0.5f + nodeH * 0.5f;
                for (int b = 0; b < count; b++)
                {
                    ShipUpgradeNode node = level == 1 ? null : tree.GetNodeForBranch(level, b);
                    var view = CreateShipTreeNodeMoonHorizontal(level, b, node, nodeW, nodeH, maxPowerTree);
                    views.Add(view);
                    // Match vertical tree: branch 0 = left → top; higher branch = right → bottom (was reversed as bottom/top).
                    float y = yBase + (count - 1 - b) * (nodeH + branchGapY);
                    view.Rect.anchorMin = new Vector2(0f, 0f);
                    view.Rect.anchorMax = new Vector2(0f, 0f);
                    view.Rect.pivot = new Vector2(0.5f, 0.5f);
                    view.Rect.anchoredPosition = new Vector2(colCenterX, y);
                }
                nodesByLevel[level] = views;
            }

            for (int level = 2; level <= maxLevel; level++)
            {
                if (!nodesByLevel.TryGetValue(level, out var levelViews)) continue;
                if (!nodesByLevel.TryGetValue(level - 1, out var previousViews)) continue;
                foreach (var prevView in previousViews)
                {
                    int p = prevView.BranchIndex;
                    foreach (var nextView in levelViews)
                    {
                        int j = nextView.BranchIndex;
                        if (!UpgradeTree.IsValidUpgradeStep(level - 1, p, level, j)) continue;
                        bool onPath = pathEdges != null && pathEdges.Contains((level - 1, p, level, j));
                        DrawTreeConnector(
                            GetMoonHorizontalTreeConnectorAnchor(prevView),
                            GetMoonHorizontalTreeConnectorAnchor(nextView),
                            onPath ? ShipTreeConnectorPath : ShipTreeConnectorDim,
                            onPath ? 3.5f : 2f);
                    }
                }
            }

            UpdateShipUpgradeTreeVisualState();

            if (shipTreeCenterRow != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(shipTreeCenterRow);
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
