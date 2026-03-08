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
        [Tooltip("e.g. Rajdhani from Shift UI/Fonts.")]
        [SerializeField] private TMP_FontAsset fontAsset;

        private const float PanelWidth = 420f;
        private const float LeftMargin = 12f;
        /// <summary>Vertical offset from top so orbit panel sits below ShipStatsPanel (top-left anchor).</summary>
        private const float TopOffsetBelowShipStats = 168f;
        private const float SectionSpacing = 12f;
        private const int MaxSlotRows = 12;
        private const int SlotGridColumns = 6;
        /// <summary>Roomier slot card height so title/description/level bubble all fit.</summary>
        private const float SlotCardWidth = 100f;
        private const float SlotCardHeight = 82f;
        private const float SlotCellSpacing = 10f;
        private const float SlotPanelWidthConst = 12f + 6 * 100f + 5 * 10f + 12f; // 6 cards + spacing
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

        private Button btnOrbitDepositGems;
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
        private GameObject upgradeShipRow;
        private Button upgradeShipButton;
        private StoreItemType[] itemTypes;
        private Button[] itemButtons;
        private TextMeshProUGUI[] itemLabels;

        private float _cardsContentHeight;
        private float _shipsContentHeight;

        private Vector2 _storeScrollLastMousePos;
        private bool _storeScrollDragging;

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

        private float storeRefreshAccum;
        private const float StoreRefreshInterval = 0.35f;
        private float contributedGemsRequestAccum;
        private const float ContributedGemsRequestInterval = 1f; // Request contributed gems periodically so deposits show up

        private void Awake()
        {
            EnsurePanelExists();
            if (rootPanel != null) rootPanel.SetActive(false);
        }

        private void Update()
        {
            if (rootPanel == null || !rootPanel.activeSelf || currentShip == null || currentPlanet == null) return;
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
        }

        private void ApplyStoreScrollFallback()
        {
            if (storeScrollRect == null || storeScrollRect.viewport == null || storeScrollRect.content == null || !storeScrollRect.vertical) return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            Vector2 mousePos;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current == null) return;
            mousePos = Mouse.current.position.ReadValue();
            bool pointerDown = Mouse.current.leftButton.isPressed;
            float scrollY = Mouse.current.scroll.ReadValue().y;
#else
            mousePos = Input.mousePosition;
            bool pointerDown = Input.GetMouseButton(0);
            float scrollY = Input.mouseScrollDelta.y;
#endif

            RectTransform viewport = storeScrollRect.viewport;
            bool overViewport = RectTransformUtility.RectangleContainsScreenPoint(viewport, mousePos, canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera);

            float viewportHeight = viewport.rect.height;
            float contentHeight = storeScrollRect.content.rect.height;
            float scrollable = contentHeight - viewportHeight;
            if (scrollable <= 0f) return;

            const float scrollWheelScale = 0.02f;
            const float dragScale = 1f;

            if (Mathf.Abs(scrollY) > 0.001f && overViewport)
            {
                float step = scrollY * scrollWheelScale;
                float next = storeScrollRect.verticalNormalizedPosition - step;
                storeScrollRect.verticalNormalizedPosition = Mathf.Clamp01(next);
            }

            if (pointerDown)
            {
                if (overViewport)
                {
                    if (_storeScrollDragging)
                    {
                        float deltaY = _storeScrollLastMousePos.y - mousePos.y; // positive when pointer moved up
                        float step = (deltaY / scrollable) * dragScale; // drag up -> see lower content -> decrease normalized
                        float next = storeScrollRect.verticalNormalizedPosition - step;
                        storeScrollRect.verticalNormalizedPosition = Mathf.Clamp01(next);
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

        private float _lastHomePlanetLookupTime = -999f;
        private const float HomePlanetLookupInterval = 1f;

        public void Show(Starship ship, Planet planet)
        {
            currentShip = ship;
            currentPlanet = planet;
            if (ship != null && (currentHomePlanet == null || Time.time - _lastHomePlanetLookupTime >= HomePlanetLookupInterval))
            {
                _lastHomePlanetLookupTime = Time.time;
                foreach (var h in UnityEngine.Object.FindObjectsByType<HomePlanet>(FindObjectsSortMode.None))
                {
                    if (h.AssignedTeam == ship.ShipTeam) { currentHomePlanet = h; break; }
                }
            }
            contributedGems = 0f;
            EnsurePanelExists();
            // Always pin orbit panel to top-left under ship stats (overrides any saved/scene position).
            var myRect = transform as RectTransform;
            if (myRect != null)
            {
                myRect.anchorMin = new Vector2(0f, 1f);
                myRect.anchorMax = new Vector2(0f, 1f);
                myRect.pivot = new Vector2(0f, 1f);
                myRect.anchoredPosition = new Vector2(LeftMargin, -TopOffsetBelowShipStats);
                myRect.sizeDelta = new Vector2(Mathf.Max(PanelWidth, SlotPanelWidthConst), 720f);
            }
            if (rootPanel != null)
            {
                rootPanel.SetActive(true);
                if (slotPanel != null) slotPanel.SetActive(true);
                if (storePanel != null) storePanel.SetActive(true);
                transform.SetAsLastSibling(); // Bring orbit panel to front so it draws above other HUD
            }
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null)
                HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
            RefreshAll();
            RefreshStoreTabVisibility();
            RefreshShipsTab();
            // Force layout rebuild so content (slots, store, scroll) gets correct size after panel becomes active.
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
        }

        public void Hide()
        {
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
            const float orbitActionsHeight = 36f;
            float slotPanelHeight = SlotPanelHeaderHeight + 8f + orbitActionsHeight + 8f + slotGridTotalH + 12f;

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

            float slotY = -8f;
            btnOrbitDepositGems = CreateOrbitActionButton(slotPanel.transform, "Deposit Gems", ref slotY);
            if (btnOrbitDepositGems != null) btnOrbitDepositGems.onClick.AddListener(OnOrbitDepositGemsClick);
            slotY -= 8f;
            loadoutSectionLabel = CreateSectionLabelWithRef(slotPanel.transform, "Loadout", "Ship Loadout (click card to remove)", ref slotY);
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
            for (int i = 0; i < MaxSlotRows; i++)
            {
                CreateSlotBoxForGrid(slotGridRoot.transform, SlotCardWidth, SlotCardHeight, i, out slotBoxes[i], out slotBgImages[i], out slotBorderImages[i], out slotLevelTexts[i], out slotTitleTexts[i], out slotDescTexts[i]);
                int idx = i;
                var slotBtn = slotBoxes[i].GetComponent<Button>();
                if (slotBtn != null) slotBtn.onClick.AddListener(() => OnRemoveCard(idx));
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
            storeContentRoot.anchorMin = new Vector2(0f, 1f);
            storeContentRoot.anchorMax = new Vector2(1f, 1f);
            storeContentRoot.pivot = new Vector2(0f, 1f);
            storeContentRoot.anchoredPosition = Vector2.zero;
            storeContentRoot.sizeDelta = new Vector2(Mathf.Max(PanelWidth - 24f, 360f), 800f); // updated below after content built
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
            CreateRowLabel(cardsTabContent.transform, "Cards (click Buy to add to empty slot)", ref y);
            const int cardGridCols = 3;
            const int cardGridRows = 7;
            const int maxStoreCards = cardGridCols * cardGridRows;
            float cardCellW = (PanelWidth - 24f - 8f * (cardGridCols - 1)) / cardGridCols;
            float cardCellH = 100f;
            float gridHeight = cardGridRows * cardCellH + (cardGridRows - 1) * 6f;
            var cardGridGo = new GameObject("CardGrid");
            cardGridGo.transform.SetParent(cardsTabContent.transform, false);
            var cardGridRect = cardGridGo.AddComponent<RectTransform>();
            cardGridRect.anchorMin = new Vector2(0f, 1f);
            cardGridRect.anchorMax = new Vector2(1f, 1f);
            cardGridRect.pivot = new Vector2(0.5f, 1f);
            cardGridRect.anchoredPosition = new Vector2(0f, y);
            cardGridRect.sizeDelta = new Vector2(-24f, gridHeight);
            y -= gridHeight + 8f;
            var cardGridLayout = cardGridGo.AddComponent<GridLayoutGroup>();
            cardGridLayout.cellSize = new Vector2(cardCellW, cardCellH);
            cardGridLayout.spacing = new Vector2(6f, 6f);
            cardGridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            cardGridLayout.constraintCount = cardGridCols;
            cardGridLayout.childAlignment = TextAnchor.UpperLeft;
            cardRoots = new GameObject[maxStoreCards];
            cardBgImages = new Image[maxStoreCards];
            cardTitleTexts = new TextMeshProUGUI[maxStoreCards];
            cardLevelTexts = new TextMeshProUGUI[maxStoreCards];
            cardDescTexts = new TextMeshProUGUI[maxStoreCards];
            cardButtons = new Button[maxStoreCards];
            cardEntries = new CardData[maxStoreCards];
            for (int i = 0; i < maxStoreCards; i++)
            {
                CreateStoreCard(cardGridGo.transform, cardCellW, cardCellH, i, out cardRoots[i], out cardBgImages[i], out cardTitleTexts[i], out cardLevelTexts[i], out cardDescTexts[i], out cardButtons[i]);
                if (cardRoots[i] != null)
                    cardRoots[i].AddComponent<ScrollRectForwarder>();
                int idx = i;
                cardButtons[i].onClick.AddListener(() => OnBuyCard(idx));
            }
            float cardsContentHeight = -y + 20f;
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
            upgradeShipRow = CreateUpgradeShipRow(shipsTabContent.transform, ref shipY);
            upgradeShipButton = upgradeShipRow != null ? upgradeShipRow.GetComponentInChildren<Button>() : null;
            if (upgradeShipButton != null) upgradeShipButton.onClick.AddListener(OnUpgradeShipLevel);
            CreateRowLabel(shipsTabContent.transform, "Available Ships (unlocked by home planet level)", ref shipY);
            shipY -= 6f;
            for (int i = 0; i < MaxShipCards; i++)
            {
                chassisButtons[i] = CreateShipSlotButton(shipsTabContent.transform, ref shipY);
                chassisLabels[i] = chassisButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                int idx = i;
                chassisButtons[i].onClick.AddListener(() => OnBuyChassis(idx));
            }
            shipsRowsContainer = null;
            shipPreviewsRoot = new GameObject("ShipPreviewsRoot").transform;
            shipPreviewsRoot.SetParent(transform, false);
            shipPreviewsRoot.localPosition = Vector3.zero;
            shipPreviewsRoot.localRotation = Quaternion.identity;
            shipPreviewsRoot.localScale = Vector3.one;

            float shipsContentHeight = Mathf.Max(650f, MaxShipCards * 40f + 60f);
            _shipsContentHeight = shipsContentHeight;
            var shipsLayoutEl = shipsTabContent.AddComponent<LayoutElement>();
            shipsLayoutEl.preferredHeight = shipsContentHeight;
            shipsLayoutEl.flexibleWidth = 1f;

            float contentWidth = Mathf.Max(PanelWidth - 24f, 360f);
            storeContentRoot.sizeDelta = new Vector2(contentWidth, Mathf.Max(cardsContentHeight, shipsContentHeight, 600f));

            // Vertical scrollbar for store
            var scrollbarGo = new GameObject("StoreScrollbar");
            scrollbarGo.transform.SetParent(storePanel.transform, false);
            var scrollbarRect = scrollbarGo.AddComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 1f);
            scrollbarRect.anchoredPosition = Vector2.zero;
            scrollbarRect.offsetMin = new Vector2(-20f, 12f);
            scrollbarRect.offsetMax = new Vector2(-12f, storeY);
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

            itemTypes = (StoreItemType[])System.Enum.GetValues(typeof(StoreItemType));
            itemButtons = new Button[itemTypes.Length];
            itemLabels = new TextMeshProUGUI[itemTypes.Length];
            RefreshStoreTabVisibility();
            RefreshShipsTab();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(storeContentRoot);
            if (canvas != null) Canvas.ForceUpdateCanvases();
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
            cardsTabContent.SetActive(activeStoreTab == 0);
            shipsTabContent.SetActive(activeStoreTab == 1);
            if (shipPreviewsRoot != null)
                shipPreviewsRoot.gameObject.SetActive(activeStoreTab == 1);
            if (storeScrollRect != null && storeScrollRect.content != null)
            {
                storeScrollRect.verticalNormalizedPosition = 1f;
                LayoutRebuilder.ForceRebuildLayoutImmediate(storeScrollRect.content);
                Canvas.ForceUpdateCanvases();
                // Ensure content is taller than viewport so scrolling is possible
                RectTransform content = storeScrollRect.content;
                RectTransform viewport = storeScrollRect.viewport;
                if (viewport != null && content != null)
                {
                    float viewportHeight = viewport.rect.height;
                    float contentHeight = activeStoreTab == 0 ? _cardsContentHeight : _shipsContentHeight;
                    float minContentHeight = viewportHeight + 50f;
                    if (content.sizeDelta.y < minContentHeight)
                        content.sizeDelta = new Vector2(content.sizeDelta.x, Mathf.Max(contentHeight, minContentHeight));
                }
            }
            if (activeStoreTab == 1)
                EnsureShipsTabPopulated();
            var cardsImg = tabCardsButton.GetComponent<Image>();
            var shipsImg = tabShipsButton.GetComponent<Image>();
            if (cardsImg != null) cardsImg.color = activeStoreTab == 0 ? new Color(0.25f, 0.4f, 0.6f, 0.98f) : new Color(0.2f, 0.28f, 0.4f, 0.95f);
            if (shipsImg != null) shipsImg.color = activeStoreTab == 1 ? new Color(0.25f, 0.4f, 0.6f, 0.98f) : new Color(0.2f, 0.28f, 0.4f, 0.95f);
        }

        private void EnsureShipsTabPopulated()
        {
            EnsurePanelExists();
            RefreshShipsTab();
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

        private void OnUpgradeShipLevel()
        {
            if (currentShip == null || currentPlanet == null || CardShopSystem.Instance == null) return;
            var planetNo = currentPlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (planetNo == null || !planetNo.IsSpawned) return;
            CardShopSystem.Instance.PurchaseShipLevelUpgradeServerRpc(planetNo.NetworkObjectId, currentShip.NetworkObjectId);
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null) HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private void RefreshShipsTab()
        {
            if (chassisButtons == null || chassisLabels == null) return;
            int homeLevel = currentHomePlanet != null ? Mathf.Max(1, currentHomePlanet.HomePlanetLevel) : 6;
            bool isHome = currentPlanet is HomePlanet;
            int storePlanetId = currentPlanet != null ? currentPlanet.PlanetId : 0;
            List<ShipUnlockEntry> unlocked = CardShopSystem.Instance != null
                ? CardShopSystem.Instance.GetUnlockedChassisEntriesForHomeLevel(homeLevel, isHome, storePlanetId)
                : new List<ShipUnlockEntry>();
            if (unlocked == null) unlocked = new List<ShipUnlockEntry>();

            int nextLevel = 0;
            float upgradeCost = 0f;
            bool canUpgrade = currentShip != null && currentPlanet != null && CardShopSystem.Instance != null
                && CardShopSystem.Instance.CanPurchaseShipLevelUpgrade(currentShip, currentPlanet, out nextLevel, out upgradeCost, out _);
            if (upgradeShipRow != null)
            {
                upgradeShipRow.SetActive(canUpgrade);
                if (canUpgrade)
                {
                    var tmp = upgradeShipRow.GetComponentInChildren<TextMeshProUGUI>();
                    if (tmp != null) tmp.text = $"Upgrade to Level {nextLevel} — {upgradeCost:F0}g";
                    if (upgradeShipButton != null) upgradeShipButton.interactable = contributedGems >= upgradeCost;
                }
            }

            if (unlocked.Count > 0)
            {
                unlocked.Sort((a, b) =>
                {
                    if (a == null || b == null) return 0;
                    int tierA = Mathf.Max(1, a.minHomePlanetLevel);
                    int tierB = Mathf.Max(1, b.minHomePlanetLevel);
                    if (tierA != tierB) return tierA.CompareTo(tierB);
                    string nameA = a.chassis != null ? a.chassis.displayName : "";
                    string nameB = b.chassis != null ? b.chassis.displayName : "";
                    return string.Compare(nameA, nameB, StringComparison.Ordinal);
                });
            }
            for (int i = 0; i < chassisButtons.Length; i++)
            {
                shipUnlockEntries[i] = null;
                if (chassisButtons[i] != null)
                {
                    chassisButtons[i].gameObject.SetActive(i < unlocked.Count);
                    if (i < unlocked.Count)
                    {
                        ShipUnlockEntry entry = unlocked[i];
                        shipUnlockEntries[i] = entry;
                        ShipChassisDefinition chassis = entry?.chassis;
                        int tierLevel = Mathf.Max(1, entry.minHomePlanetLevel);
                        float cost = entry.gemCost > 0f ? entry.gemCost : ShipUnlockTable.GetTierCost(tierLevel);
                        string family = chassis != null && !string.IsNullOrEmpty(chassis.shipFamily) ? chassis.shipFamily : "Ship";
                        string name = chassis != null ? chassis.displayName : "Ship";
                        chassisButtons[i].interactable = contributedGems >= cost;
                        if (chassisLabels[i] != null)
                            chassisLabels[i].text = $"Lv.{tierLevel} • {name} ({family}) — {cost:F0}g";
                    }
                }
            }
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

        /// <summary>Creates a roomy slot card: level bubble (top-right), title (top-center), description (bottom-center), bg + highlighted border by slot type.</summary>
        private void CreateSlotBoxForGrid(Transform gridParent, float cellW, float cellH, int index, out GameObject boxRoot, out Image bgImage, out Image borderImage, out TextMeshProUGUI levelText, out TextMeshProUGUI titleText, out TextMeshProUGUI descText)
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
            boxRoot.AddComponent<Button>(); // Click to remove card (handler set in EnsurePanelExists)

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
            titleRect.anchoredPosition = new Vector2(0f, -6f);
            titleRect.sizeDelta = new Vector2(-36f, -24f);
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

        private void CreateStoreCard(Transform parent, float width, float height, int index, out GameObject root, out Image bgImage, out TextMeshProUGUI titleText, out TextMeshProUGUI levelText, out TextMeshProUGUI descText, out Button buyButton)
        {
            root = new GameObject("StoreCard_" + (index + 1));
            root.transform.SetParent(parent, false);
            var rootRect = root.AddComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(width, height);
            bgImage = root.AddComponent<Image>();
            bgImage.color = GetSlotTypeColor(SlotType.Ship);
            bgImage.raycastTarget = true;
            if (buttonSprite != null) { bgImage.sprite = buttonSprite; bgImage.type = Image.Type.Sliced; }

            var levelGo = new GameObject("LevelBubble");
            levelGo.transform.SetParent(root.transform, false);
            var levelRect = levelGo.AddComponent<RectTransform>();
            levelRect.anchorMin = new Vector2(1f, 1f);
            levelRect.anchorMax = new Vector2(1f, 1f);
            levelRect.pivot = new Vector2(1f, 1f);
            levelRect.anchoredPosition = new Vector2(-4f, -4f);
            levelRect.sizeDelta = new Vector2(24f, 24f);
            var levelBg = levelGo.AddComponent<Image>();
            levelBg.color = new Color(0.15f, 0.2f, 0.35f, 0.95f);
            levelBg.raycastTarget = false;
            var levelTextGo = new GameObject("LevelText");
            levelTextGo.transform.SetParent(levelGo.transform, false);
            var levelTextRect = levelTextGo.AddComponent<RectTransform>();
            levelTextRect.anchorMin = Vector2.zero;
            levelTextRect.anchorMax = Vector2.one;
            levelTextRect.offsetMin = Vector2.zero;
            levelTextRect.offsetMax = Vector2.zero;
            levelText = levelTextGo.AddComponent<TextMeshProUGUI>();
            levelText.text = "—";
            levelText.fontSize = 10;
            levelText.alignment = TextAlignmentOptions.Center;
            levelText.color = new Color(0.9f, 0.95f, 1f, 1f);
            if (fontAsset != null) levelText.font = fontAsset;
            levelText.raycastTarget = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(root.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = Vector2.zero;
            titleRect.offsetMin = new Vector2(6f, height - 30f);
            titleRect.offsetMax = new Vector2(-32f, -4f);
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.text = "";
            titleText.fontSize = 13;
            titleText.alignment = TextAlignmentOptions.Top;
            titleText.enableAutoSizing = true;
            titleText.fontSizeMin = 9;
            titleText.fontSizeMax = 14;
            titleText.color = new Color(0.95f, 0.97f, 1f, 0.98f);
            titleText.overflowMode = TextOverflowModes.Ellipsis;
            if (fontAsset != null) titleText.font = fontAsset;
            titleText.raycastTarget = false;

            var descGo = new GameObject("Desc");
            descGo.transform.SetParent(root.transform, false);
            var descRect = descGo.AddComponent<RectTransform>();
            descRect.anchorMin = new Vector2(0f, 0f);
            descRect.anchorMax = new Vector2(1f, 0f);
            descRect.offsetMin = new Vector2(6f, 34f);
            descRect.offsetMax = new Vector2(-6f, height - 34f);
            descText = descGo.AddComponent<TextMeshProUGUI>();
            descText.text = "";
            descText.fontSize = 10;
            descText.alignment = TextAlignmentOptions.TopLeft;
            descText.enableWordWrapping = true;
            descText.enableAutoSizing = true;
            descText.fontSizeMin = 7;
            descText.fontSizeMax = 11;
            descText.color = new Color(0.78f, 0.85f, 0.92f, 0.94f);
            descText.overflowMode = TextOverflowModes.Ellipsis;
            if (fontAsset != null) descText.font = fontAsset;
            descText.raycastTarget = false;

            var buyGo = new GameObject("BuyButton");
            buyGo.transform.SetParent(root.transform, false);
            var buyRect = buyGo.AddComponent<RectTransform>();
            buyRect.anchorMin = new Vector2(0.5f, 0f);
            buyRect.anchorMax = new Vector2(0.5f, 0f);
            buyRect.pivot = new Vector2(0.5f, 0f);
            buyRect.anchoredPosition = new Vector2(0f, 6f);
            buyRect.sizeDelta = new Vector2(width - 16f, 24f);
            var buyImg = buyGo.AddComponent<Image>();
            buyImg.color = new Color(0.2f, 0.4f, 0.65f, 0.95f);
            if (buttonSprite != null) { buyImg.sprite = buttonSprite; buyImg.type = Image.Type.Sliced; }
            buyButton = buyGo.AddComponent<Button>();
            var buyLabelGo = new GameObject("Text");
            buyLabelGo.transform.SetParent(buyGo.transform, false);
            var buyLabelRect = buyLabelGo.AddComponent<RectTransform>();
            buyLabelRect.anchorMin = Vector2.zero;
            buyLabelRect.anchorMax = Vector2.one;
            buyLabelRect.offsetMin = Vector2.zero;
            buyLabelRect.offsetMax = Vector2.zero;
            var buyLabel = buyLabelGo.AddComponent<TextMeshProUGUI>();
            buyLabel.text = "Buy";
            buyLabel.fontSize = 11;
            buyLabel.alignment = TextAlignmentOptions.Center;
            buyLabel.color = Color.white;
            buyLabel.raycastTarget = false;
            if (fontAsset != null) buyLabel.font = fontAsset;
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
            if (!pendingGemsRequest) contributedGems = lastReceivedGems;
            if (gemsText != null) gemsText.text = $"Your contributed gems: {contributedGems:F0}";

            if (cardRoots == null || cardButtons == null || currentShip == null || currentPlanet == null) return;
            if (CardShopSystem.Instance == null)
            {
                for (int i = 0; i < cardRoots.Length; i++)
                {
                    if (cardRoots[i] != null) cardRoots[i].SetActive(false);
                }
                RefreshShipsTab();
                return;
            }
            int homeLevel = currentHomePlanet != null ? currentHomePlanet.HomePlanetLevel : 1;
            bool isHomeStore = currentPlanet is HomePlanet;
            List<CardData> availableCards = isHomeStore
                ? CardShopSystem.Instance.GetAvailableCardsForHomeStore(homeLevel, currentShip.ShipTeam)
                : CardShopSystem.Instance.GetAvailableCardsForPlanet(homeLevel, currentPlanet.PlanetId);
            bool hasEmptySlot = currentShip.HasEmptySlot;
            int shipLevel = currentShip.ShipLevel;

            for (int i = 0; i < cardRoots.Length; i++)
            {
                CardData card = (i < availableCards.Count) ? availableCards[i] : null;
                cardEntries[i] = card;
                bool show = card != null;
                if (cardRoots[i] != null)
                    cardRoots[i].SetActive(show);
                if (!show) continue;

                if (cardBgImages[i] != null)
                    cardBgImages[i].color = GetSlotTypeColor(card.slotType);
                if (cardTitleTexts[i] != null)
                    cardTitleTexts[i].text = card.displayName;
                if (cardLevelTexts[i] != null)
                    cardLevelTexts[i].text = $"Lv.{Mathf.Max(1, card.cardLevel)}";
                if (cardDescTexts[i] != null)
                    cardDescTexts[i].text = string.IsNullOrEmpty(card.description) ? "" : card.description;
                float price = Mathf.Max(card.gemCost, 1f);
                int cardLvl = Mathf.Max(1, card.cardLevel);
                bool levelOk = cardLvl <= shipLevel;
                if (cardButtons[i] != null)
                {
                    cardButtons[i].interactable = hasEmptySlot && levelOk && contributedGems >= price;
                    var buyLabel = cardButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                    if (buyLabel != null)
                        buyLabel.text = levelOk ? $"Buy {price:F0}g" : $"Lv.{shipLevel}";
                }
            }

            // Ships tab: refresh the fixed list of ship slots
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
                    storeContentRoot.sizeDelta = new Vector2(storeContentRoot.sizeDelta.x, totalHeight + 80f);
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

        private Button CreateOrbitActionButton(Transform parent, string label, ref float y)
        {
            var go = new GameObject("Btn_" + label.Replace(" ", ""));
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-24f, 32f);
            y -= 36f;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.35f, 0.55f, 0.95f);
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
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            if (fontAsset != null) tmp.font = fontAsset;
            tmp.raycastTarget = false;
            return btn;
        }

        private void OnOrbitDepositGemsClick()
        {
            if (currentShip == null) return;
            currentShip.SetWantToDepositGemsServerRpc(!currentShip.WantToDepositGems);
            // Request contributed gems soon so UI updates as gems deposit
            contributedGemsRequestAccum = ContributedGemsRequestInterval - 0.5f;
        }

        private void RefreshSlots()
        {
            if (currentShip == null || slotBoxes == null) return;

            // Refresh Deposit Gems button: show active state, disable when no gems or can't deposit at this planet
            if (btnOrbitDepositGems != null && currentPlanet != null)
            {
                bool canDeposit = (currentPlanet is HomePlanet hp && (hp.AssignedTeam == TeamManager.Team.None || hp.AssignedTeam == currentShip.ShipTeam))
                    || (currentPlanet.TeamOwnership == currentShip.ShipTeam);
                bool hasGems = currentShip.CurrentGems > 0f;
                btnOrbitDepositGems.interactable = canDeposit && hasGems;
                var btnImg = btnOrbitDepositGems.GetComponent<Image>();
                var btnText = btnOrbitDepositGems.GetComponentInChildren<TextMeshProUGUI>();
                if (btnImg != null)
                    btnImg.color = currentShip.WantToDepositGems ? new Color(0.3f, 0.6f, 0.35f, 0.98f) : new Color(0.2f, 0.35f, 0.55f, 0.95f);
                if (btnText != null)
                    btnText.text = currentShip.WantToDepositGems ? "Depositing Gems..." : "Deposit Gems";
            }
            int slotCount = currentShip.SlotCount;

            // Resize slot panel and grid to match ship's slot count (level 2 = 2 slots, level 3 = 3 slots, etc.)
            if (slotPanelRect != null && slotGridRect != null && storePanelRect != null)
            {
                const float orbitActionsHeight = 36f;
                int effectiveSlotRows = Mathf.Max(1, Mathf.Min(MaxSlotRows / SlotGridColumns, Mathf.CeilToInt((float)slotCount / SlotGridColumns)));
                float slotGridTotalH = effectiveSlotRows * SlotCardHeight + (effectiveSlotRows - 1) * SlotCellSpacing;
                float slotPanelHeight = SlotPanelHeaderHeight + 8f + orbitActionsHeight + 8f + slotGridTotalH + 12f;
                slotPanelRect.offsetMin = new Vector2(12f, -slotPanelHeight);
                slotPanelRect.offsetMax = new Vector2(-12f, 0f);
                slotGridRect.sizeDelta = new Vector2(-24f, slotGridTotalH);
                float storePanelTop = slotPanelHeight + 8f;
                storePanelRect.offsetMax = new Vector2(-12f, -storePanelTop);
            }
            if (loadoutSectionLabel != null)
                loadoutSectionLabel.text = $"Ship Loadout ({slotCount} slot{(slotCount != 1 ? "s" : "")}) — click card to remove";
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
                var slotBtn = slotBoxes[i].GetComponent<Button>();
                if (slotBtn != null) slotBtn.interactable = (card != null); // Only clickable when slot has a card (to remove)
            }
        }

        private void OnRemoveCard(int slotIndex)
        {
            if (currentShip == null) return;
            var cards = currentShip.EquippedCards;
            if (cards == null || slotIndex < 0 || slotIndex >= cards.Count) return;
            currentShip.RemoveCardServerRpc(slotIndex);
        }

        private void OnBuyCard(int index)
        {
            if (currentShip == null || currentPlanet == null || CardShopSystem.Instance == null) return;
            if (cardEntries == null || index < 0 || index >= cardEntries.Length) return;
            CardData card = cardEntries[index];
            if (card == null) return;
            var planetNo = currentPlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (planetNo == null || !planetNo.IsSpawned) return;
            CardShopSystem.Instance.PurchaseCardServerRpc(planetNo.NetworkObjectId, currentShip.NetworkObjectId, card.cardId);
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null) HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
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
