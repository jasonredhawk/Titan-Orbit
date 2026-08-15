using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Moon orbit station menu: deposit gems, ship upgrade tree, and store.
    /// Client-only hybrid UI. Ship-tree unlock / purchase rules live here; when
    /// <see cref="GameManager.DebugFreeShipUpgradeTree"/> is enabled in the Inspector
    /// (NceGameRoot → Game Manager), every tree node is free to click for local testing.
    /// Paired with <see cref="MoonOrbitStoreSystem"/> on the server for purchase validation.
    /// </summary>
    public class MoonOrbitStationUI : MonoBehaviour, IOrbitStationHost
    {
        const float MainPanelScreenWidthFraction = 0.78f;
        const float LeftMargin = 12f;
        const float TopOffsetBelowShipStats = 168f;

        [SerializeField] UpgradeTree upgradeTree;
        [SerializeField] ShipUpgradeTreeUI shipUpgradeTreePrefab;
        [SerializeField] Sprite panelBackgroundSprite;
        [SerializeField] Sprite buttonSprite;

        GameObject _rootPanel;
        GameObject _storePanel;
        GameObject _shipsTabContent;
        RectTransform _mainPanelRt;
        OrbitDockSidebarPanelUI _sidebar;
        ShipUpgradeTreeUI _shipTree;
        TextMeshProUGUI _gemsText;
        int _storePlanetId;
        int _homePlanetId;
        float _contributedGems;
        float _gemsRequestAccum;
        /// <summary>
        /// Accrual for throttled full ECS refresh (tree/store). Deposit digits update every frame /
        /// on beat — rebuilding the ship tree every Update was a major Orbit Menu hitch.
        /// </summary>
        float _fullUiRefreshAccum;
        const float FullUiRefreshIntervalSeconds = 0.25f;
        int _lastFullRefreshShipLevel = int.MinValue;
        int _lastFullRefreshBranch = int.MinValue;
        bool _visible;
        readonly List<int> _nextTargets = new List<int>(4);

        /// <summary>Store row bindings so <see cref="RefreshStore"/> can update level-scaled prices.</summary>
        sealed class StoreRowBinding
        {
            public StoreItemType ItemType;
            public TextMeshProUGUI Label;
        }

        readonly List<StoreRowBinding> _storeRows = new List<StoreRowBinding>(8);

        public UpgradeTree UpgradeTree => upgradeTree;
        public float ContributedGems => _contributedGems;
        public int StorePlanetId => _storePlanetId;
        public int StorePlanetLevel { get; private set; } = 1;
        public int HomePlanetLevel { get; private set; } = 1;
        public int ShipLevel { get; private set; } = 1;
        public int BranchIndex { get; private set; }

        public static MoonOrbitStationUI Instance { get; private set; }

        public static MoonOrbitStationUI GetOrCreate()
        {
            if (Instance != null)
                return Instance;

            Canvas canvas = FindOverlayCanvas();
            if (canvas == null)
            {
                var canvasGo = new GameObject("MoonOrbitCanvas");
                canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 200;
                var scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                canvasGo.AddComponent<GraphicRaycaster>();
                EnsureEventSystemExists();
            }

            var go = new GameObject("MoonOrbitStationUI");
            var hostRt = go.AddComponent<RectTransform>();
            hostRt.SetParent(canvas.transform, false);
            hostRt.anchorMin = Vector2.zero;
            hostRt.anchorMax = Vector2.one;
            hostRt.offsetMin = hostRt.offsetMax = Vector2.zero;

            Instance = go.AddComponent<MoonOrbitStationUI>();
            return Instance;
        }

        static Canvas FindOverlayCanvas()
        {
            var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            Canvas best = null;
            for (int i = 0; i < canvases.Length; i++)
            {
                var canvas = canvases[i];
                if (canvas == null || !canvas.isActiveAndEnabled)
                    continue;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    continue;
                if (best == null || canvas.sortingOrder > best.sortingOrder)
                    best = canvas;
            }

            return best;
        }

        static void EnsureEventSystemExists()
        {
            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null)
                return;

            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
#if ENABLE_INPUT_SYSTEM
            esGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
#endif
        }

        void Awake()
        {
            // --- Unity lifecycle ---
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            // [TITAN-ORBIT] Debug free-tree flag lives on GameManager (Inspector on NceGameRoot).
            // EnsureExists creates a fallback only when the scene object is missing.
            GameManager.EnsureExists();

            if (upgradeTree == null)
                upgradeTree = Resources.Load<UpgradeTree>("UpgradeTree");
            EnsureUiBuilt();
            Hide();
        }

        /// <summary>
        /// True when designers enabled free ship-tree unlocks on <see cref="GameManager"/>.
        /// Same gate the old <c>OrbitStationUI</c> used before the moon menu rewrite.
        /// </summary>
        static bool IsDebugFreeShipUpgradeTree() => GameManager.IsDebugFreeShipUpgradeTreeActive;

        void OnDestroy()
        {
            // --- Unity lifecycle ---
            if (Instance == this)
            {
                Instance = null;
                MoonOrbitClientState.SetOrbitMenuVisible(false);
            }
        }

        void Update()
        {
            // --- Per-frame refresh ---
            // Authoritative Bank from contributed-gems RPC (reconcile optimistic metronome ticks).
            if (MoonOrbitClientState.TryConsumeContributedGems(out float gems))
                OnContributedGemsReceived(gems);

            if (MoonOrbitClientState.TryConsumeStoreMessage(out string message))
                Debug.LogWarning($"[MoonOrbitStore] {message}");

            if (!_visible)
                return;

            // --- Deposit column: live while depositing (metronome + ghost cargo) ---
            // [TITAN-ORBIT] Do NOT call full RefreshFromEcs every frame — that rewrote store TMP,
            // refreshed the whole ship tree visual state, and repainted sidebar every Update,
            // which made the 0.5s deposit metronome feel choppy (GC + main-thread hitch).
            RefreshGemDepositFlow();

            // --- Tree / store: throttle unless level/branch changed ---
            bool needImmediateFull = false;
            if (EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                int lvl = Mathf.Max(1, ship.ShipLevel);
                int branch = Mathf.Max(0, ship.BranchIndex);
                if (lvl != _lastFullRefreshShipLevel || branch != _lastFullRefreshBranch)
                    needImmediateFull = true;
            }

            _fullUiRefreshAccum += Time.deltaTime;
            if (needImmediateFull || _fullUiRefreshAccum >= FullUiRefreshIntervalSeconds)
            {
                _fullUiRefreshAccum = 0f;
                RefreshFromEcs();
            }

            _gemsRequestAccum += Time.deltaTime;
            // Keep polling while depositing — OnContributedGemsReceived soft-reconciles only.
            if (_gemsRequestAccum >= 1.5f)
            {
                _gemsRequestAccum = 0f;
                if (_homePlanetId > 0)
                    MoonOrbitRpcClient.RequestContributedGems(_homePlanetId);
            }
        }

        void OnEnable()
        {
            // --- Subscribe to deposit metronome for Bank UI sync ---
            MoonOrbitClientState.LocalDepositBeat += OnLocalDepositBeat;
        }

        void OnDisable()
        {
            MoonOrbitClientState.LocalDepositBeat -= OnLocalDepositBeat;
        }

        /// <summary>
        /// Metronome beat — snap Ship ↓ and Bank ↑ from optimistic state in the same stack as SFX.
        /// </summary>
        void OnLocalDepositBeat(float chunkAmount)
        {
            if (chunkAmount <= 0.001f)
                return;

            // NotifyLocalDepositBeat already added chunkAmount to OptimisticDepositBankGems.
            if (MoonOrbitClientState.TryGetOptimisticDepositBank(out float optBank))
                _contributedGems = optBank;
            else
                _contributedGems += chunkAmount;

            RefreshGemDepositFlow();
        }

        public void Show(int storePlanetId, int homePlanetId)
        {
            EnsureUiBuilt();

            _storePlanetId = storePlanetId;
            _homePlanetId = homePlanetId;
            _visible = true;
            MoonOrbitClientState.SetOrbitMenuVisible(true);
            if (_rootPanel != null)
                _rootPanel.SetActive(true);
            gameObject.SetActive(true);
            if (transform.parent != null)
                transform.parent.SetAsLastSibling();
            transform.SetAsLastSibling();

            bool autoDeposit = PlayerPrefs.GetInt(
                OrbitDockSidebarPanelUI.AutoDepositGemsPrefsKey,
                OrbitDockSidebarPanelUI.AutoDepositGemsDefaultEnabled) != 0;
            MoonOrbitRpcClient.SetWantDepositGems(autoDeposit);
            MoonOrbitRpcClient.RequestContributedGems(_homePlanetId);
            OnNavSelected(OrbitDockSidebarPanelUI.NavTarget.Upgrades);
            RefreshFromEcs();
            Canvas.ForceUpdateCanvases();
            if (_mainPanelRt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_mainPanelRt);
            RefreshShipTree();
        }

        public void Hide()
        {
            _visible = false;
            MoonOrbitClientState.SetOrbitMenuVisible(false);
            MoonOrbitRpcClient.SetWantDepositGems(false);
            if (_rootPanel != null)
                _rootPanel.SetActive(false);
        }

        public void OnContributedGemsReceived(float amount)
        {
            // --- Authoritative Bank from server ---
            // [TITAN-ORBIT] While depositing, soft-reconcile into metronome Bank — never snap the
            // GEM DEPOSITS column to the RPC poll clock (that desynced it from Ship ↓ / SFX).
            MoonOrbitClientState.RememberContributedGems(amount);
            if (MoonOrbitClientState.WantDepositGems)
            {
                MoonOrbitClientState.EnsureOptimisticDepositBankSeed(amount);
                MoonOrbitClientState.ApplyAuthoritativeBankBaseline(amount);
                if (MoonOrbitClientState.TryGetOptimisticDepositBank(out float optBank))
                    _contributedGems = optBank;
                else
                    _contributedGems = amount;
            }
            else
            {
                _contributedGems = amount;
            }

            RefreshGemDepositFlow();
            if (_gemsText != null)
                _gemsText.text = $"Bank: {_contributedGems:0} gems";
        }

        /// <summary>
        /// Ship → Bank flow row: live (or optimistic) cargo, contributed Bank, and planet progress.
        /// Called on metronome beats and when ECS / RPC refresh the menu.
        /// </summary>
        void RefreshGemDepositFlow()
        {
            // --- Resolve ship cargo (prefer metronome estimate while depositing) ---
            float shipGems = 0f;
            bool haveGhost = EcsGameBridge.TryGetLocalShipState(out var ship);
            float ghostGems = haveGhost ? ship.CurrentGems : 0f;

            if (MoonOrbitClientState.TryGetOptimisticDepositCargo(out float optimisticCargo) &&
                !(optimisticCargo <= 0.001f && ghostGems > 0.001f))
                shipGems = optimisticCargo;
            else if (haveGhost)
            {
                shipGems = ghostGems;
                if (MoonOrbitClientState.WantDepositGems)
                    MoonOrbitClientState.EnsureOptimisticDepositCargoSeed(ghostGems);
            }

            // --- Bank (prefer metronome estimate while depositing) ---
            float bankGems = _contributedGems;
            if (MoonOrbitClientState.WantDepositGems)
            {
                MoonOrbitClientState.EnsureOptimisticDepositBankSeed(_contributedGems);
                if (MoonOrbitClientState.TryGetOptimisticDepositBank(out float optBank))
                {
                    bankGems = optBank;
                    _contributedGems = optBank;
                }
            }

            // --- Planet treasury progress under the Bank banner ---
            float planetGems = 0f;
            int planetLevel = 1;
            if (_storePlanetId > 0 && EcsGameBridge.TryGetPlanetStateByPlanetId(_storePlanetId, out var planet))
            {
                planetGems = planet.CurrentGems;
                planetLevel = Mathf.Max(1, planet.PlanetLevel);
            }

            _sidebar?.RefreshDepositStatus(shipGems, bankGems, planetGems, planetLevel);
        }

        void RefreshFromEcs()
        {
            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
                return;

            // [NETCODE] BranchIndex is ghosted on ShipState (not only loadout).
            ShipLevel = Mathf.Max(1, ship.ShipLevel);
            BranchIndex = Mathf.Max(0, ship.BranchIndex);
            _lastFullRefreshShipLevel = ShipLevel;
            _lastFullRefreshBranch = BranchIndex;

            if (EcsGameBridge.TryGetPlanetStateByPlanetId(_storePlanetId, out var storePlanet))
                StorePlanetLevel = Mathf.Max(1, storePlanet.PlanetLevel);

            HomePlanetLevel = StorePlanetLevel;
            if (_homePlanetId > 0 && EcsGameBridge.TryGetPlanetStateByPlanetId(_homePlanetId, out var home))
                HomePlanetLevel = Mathf.Max(1, home.PlanetLevel);

            RefreshShipTree();
            RefreshStore();
            RefreshGemDepositFlow();
            _sidebar?.RefreshCurrentShip(PopulateTreeNode, ResolvePowerBarStatMaxes());
        }

        void EnsureUiBuilt()
        {
            var hostRt = transform as RectTransform;
            if (hostRt == null)
                hostRt = gameObject.AddComponent<RectTransform>();

            hostRt.anchorMin = Vector2.zero;
            hostRt.anchorMax = Vector2.one;
            hostRt.offsetMin = hostRt.offsetMax = Vector2.zero;

            if (_rootPanel == null)
            {
                BuildUi();
                return;
            }

            var rootRt = _rootPanel.transform as RectTransform;
            if (rootRt == null || rootRt.rect.width < 8f || rootRt.rect.height < 8f)
            {
                if (_shipTree != null)
                    Destroy(_shipTree.gameObject);
                Destroy(_rootPanel);
                _rootPanel = null;
                _storePanel = null;
                _shipsTabContent = null;
                _mainPanelRt = null;
                _sidebar = null;
                _shipTree = null;
                _gemsText = null;
                _storeRows.Clear();
                BuildUi();
            }
        }

        void BuildUi()
        {
            _rootPanel = new GameObject("OrbitStationRoot");
            _rootPanel.transform.SetParent(transform, false);
            var rootRt = _rootPanel.AddComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = new Vector2(MainPanelScreenWidthFraction, 1f);
            rootRt.pivot = new Vector2(0f, 0.5f);
            rootRt.anchoredPosition = Vector2.zero;
            rootRt.offsetMin = new Vector2(LeftMargin, 12f);
            rootRt.offsetMax = new Vector2(0f, -TopOffsetBelowShipStats);

            var rootBg = _rootPanel.AddComponent<Image>();
            rootBg.color = new Color(0.08f, 0.1f, 0.16f, 0.94f);
            rootBg.raycastTarget = true;
            if (panelBackgroundSprite != null)
                rootBg.sprite = panelBackgroundSprite;

            var sidebarGo = new GameObject("Sidebar");
            sidebarGo.transform.SetParent(_rootPanel.transform, false);
            var sidebarRt = sidebarGo.AddComponent<RectTransform>();
            sidebarRt.anchorMin = new Vector2(0f, 0f);
            sidebarRt.anchorMax = new Vector2(0f, 1f);
            sidebarRt.pivot = new Vector2(0f, 0.5f);
            sidebarRt.anchoredPosition = Vector2.zero;
            sidebarRt.sizeDelta = new Vector2(OrbitDockSidebarPanelUI.PanelWidth, 0f);
            sidebarRt.offsetMin = new Vector2(0f, 48f);
            sidebarRt.offsetMax = new Vector2(OrbitDockSidebarPanelUI.PanelWidth, 0f);
            _sidebar = sidebarGo.AddComponent<OrbitDockSidebarPanelUI>();
            _sidebar.ConfigureVisuals(panelBackgroundSprite, buttonSprite, null);
            _sidebar.BindStation(this);
            _sidebar.BindNavigation(OnNavSelected);
            _sidebar.BindAutoDeposit(OnAutoDepositChanged);
            _sidebar.EnsureBuilt();

            var mainGo = new GameObject("MainPanel");
            mainGo.transform.SetParent(_rootPanel.transform, false);
            _mainPanelRt = mainGo.AddComponent<RectTransform>();
            _mainPanelRt.anchorMin = Vector2.zero;
            _mainPanelRt.anchorMax = Vector2.one;
            _mainPanelRt.offsetMin = new Vector2(OrbitDockSidebarPanelUI.PanelWidth + 8f, 48f);
            _mainPanelRt.offsetMax = Vector2.zero;

            _shipsTabContent = new GameObject("ShipsTab");
            _shipsTabContent.transform.SetParent(mainGo.transform, false);
            var shipsRt = _shipsTabContent.AddComponent<RectTransform>();
            shipsRt.anchorMin = Vector2.zero;
            shipsRt.anchorMax = Vector2.one;
            shipsRt.offsetMin = shipsRt.offsetMax = Vector2.zero;

            _storePanel = new GameObject("StorePanel");
            _storePanel.transform.SetParent(mainGo.transform, false);
            var storeRt = _storePanel.AddComponent<RectTransform>();
            storeRt.anchorMin = Vector2.zero;
            storeRt.anchorMax = Vector2.one;
            storeRt.offsetMin = storeRt.offsetMax = Vector2.zero;
            _storePanel.SetActive(false);

            EnsureShipTree(_shipsTabContent.transform);
            BuildStorePanel(_storePanel.transform);

            var depositRow = new GameObject("DepositRow");
            depositRow.transform.SetParent(_rootPanel.transform, false);
            var depositRt = depositRow.AddComponent<RectTransform>();
            depositRt.anchorMin = new Vector2(0f, 0f);
            depositRt.anchorMax = new Vector2(1f, 0f);
            depositRt.pivot = new Vector2(0.5f, 0f);
            depositRt.anchoredPosition = new Vector2(0f, 4f);
            depositRt.sizeDelta = new Vector2(-16f, 40f);

            _gemsText = CreateLabel(depositRow.transform, "Bank: 0 gems", 14f, TextAlignmentOptions.Left);
            var gemsRt = _gemsText.rectTransform;
            gemsRt.anchorMin = new Vector2(0f, 0f);
            gemsRt.anchorMax = new Vector2(0.55f, 1f);
            gemsRt.offsetMin = new Vector2(OrbitDockSidebarPanelUI.PanelWidth + 12f, 0f);
            gemsRt.offsetMax = Vector2.zero;

            var depositBtn = CreateButton(depositRow.transform, "Deposit Gems", new Vector2(0.72f, 0.5f));
            depositBtn.onClick.AddListener(() => MoonOrbitRpcClient.SetWantDepositGems(true));
        }

        void EnsureShipTree(Transform parent)
        {
            if (_shipTree != null)
                return;

            if (shipUpgradeTreePrefab == null)
            {
                shipUpgradeTreePrefab = Resources.Load<ShipUpgradeTreeUI>("ShipUpgradeTree");
                if (shipUpgradeTreePrefab == null)
                {
                    var prefabGo = Resources.Load<GameObject>("ShipUpgradeTree");
                    if (prefabGo != null)
                        shipUpgradeTreePrefab = prefabGo.GetComponent<ShipUpgradeTreeUI>();
                }
            }

            if (shipUpgradeTreePrefab == null)
            {
                var hint = CreateLabel(parent, "Assign ShipUpgradeTree prefab in Resources.", 16f, TextAlignmentOptions.Center);
                hint.rectTransform.anchorMin = Vector2.zero;
                hint.rectTransform.anchorMax = Vector2.one;
                hint.rectTransform.offsetMin = hint.rectTransform.offsetMax = Vector2.zero;
                return;
            }

            _shipTree = Instantiate(shipUpgradeTreePrefab, parent);
            _shipTree.BindStation(this);
            var treeRt = (RectTransform)_shipTree.transform;
            treeRt.anchorMin = Vector2.zero;
            treeRt.anchorMax = Vector2.one;
            treeRt.offsetMin = treeRt.offsetMax = Vector2.zero;
        }

        void BuildStorePanel(Transform parent)
        {
            _storeRows.Clear();
            var scrollGo = new GameObject("StoreScroll");
            scrollGo.transform.SetParent(parent, false);
            var scrollRt = scrollGo.AddComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(scrollGo.transform, false);
            var contentRt = contentGo.AddComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8f;
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.childControlHeight = true;
            vlg.childForceExpandHeight = false;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = contentRt;
            scroll.viewport = scrollRt;

            AddStoreSection(contentGo.transform, "Drones", new[]
            {
                StoreItemType.FighterDrone,
                StoreItemType.ShieldDrone,
                StoreItemType.MiningDrone,
            });

            AddStoreSection(contentGo.transform, "Rockets & Mines", new[]
            {
                StoreItemType.SmallRockets,
                StoreItemType.LargeRockets,
                StoreItemType.SmallMines,
                StoreItemType.LargeMines,
            });
        }

        void AddStoreSection(Transform parent, string title, StoreItemType[] items)
        {
            var header = CreateLabel(parent, title, 18f, TextAlignmentOptions.Left);
            header.fontStyle = FontStyles.Bold;
            var headerLe = header.gameObject.AddComponent<LayoutElement>();
            headerLe.preferredHeight = 28f;

            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                // Initial label uses level 1; RefreshStore rewrites with min(ship, planet).
                string label = FormatStoreRowLabel(item, 1);
                var btn = CreateButton(parent, label, Vector2.zero);
                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 52f;
                int captured = (int)item;
                btn.onClick.AddListener(() =>
                {
                    MoonOrbitRpcClient.PurchaseStoreItem(_homePlanetId, (StoreItemType)captured);
                    MoonOrbitRpcClient.RequestContributedGems(_homePlanetId);
                });

                // --- Keep label ref for level/price refresh ---
                var tmp = btn.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                    _storeRows.Add(new StoreRowBinding { ItemType = item, Label = tmp });
            }
        }

        /// <summary>Builds the store button caption for the store purchase level (min of ship and planet).</summary>
        static string FormatStoreRowLabel(StoreItemType item, int shipLevel)
        {
            int level = Mathf.Max(1, shipLevel);
            float price = StoreItemData.GetPrice(item, level);
            string name = StoreItemData.IsLeveledDrone(item)
                ? StoreItemData.GetDisplayName(item, level)
                : StoreItemData.GetDisplayName(item);
            string desc = StoreItemData.GetDescription(item, level);
            return string.IsNullOrEmpty(desc)
                ? $"{name} — {price:0}g"
                : $"{name} — {price:0}g\n<size=12>{desc}</size>";
        }

        void OnNavSelected(OrbitDockSidebarPanelUI.NavTarget target)
        {
            bool store = target == OrbitDockSidebarPanelUI.NavTarget.Store;
            if (_shipsTabContent != null)
                _shipsTabContent.SetActive(!store);
            if (_storePanel != null)
                _storePanel.SetActive(store);
            _sidebar?.SetActiveNav(target);
        }

        void OnAutoDepositChanged(bool enabled)
        {
            PlayerPrefs.SetInt(OrbitDockSidebarPanelUI.AutoDepositGemsPrefsKey, enabled ? 1 : 0);
            MoonOrbitRpcClient.SetWantDepositGems(enabled);
        }

        void RefreshShipTree()
        {
            if (_shipTree == null || upgradeTree == null)
                return;

            _shipTree.RebuildIfNeeded(true, MoonOrbitStationTreeKeys.MoonDockStructureKey);
            _shipTree.RefreshVisualState();
        }

        void RefreshStore()
        {
            // --- Rewrite drone rows at min(ship, this moon's planet) ---
            // [TITAN-ORBIT] A level-6 ship on a level-3 moon sees level-3 prices and damage text.
            int level = StoreItemData.GetStorePurchaseLevel(ShipLevel, StorePlanetLevel);
            for (int i = 0; i < _storeRows.Count; i++)
            {
                var row = _storeRows[i];
                if (row?.Label == null)
                    continue;
                string label = FormatStoreRowLabel(row.ItemType, level);
                if (row.Label.text != label)
                    row.Label.text = label;
            }
        }

        #region IOrbitStationHost

        public bool IsTreeDataAvailable() => upgradeTree != null && _storePlanetId > 0;

        public float GetShipTreeLayoutBasisWidthPublic()
        {
            if (_mainPanelRt != null && _mainPanelRt.rect.width > 8f)
                return _mainPanelRt.rect.width;
            return Screen.width * MainPanelScreenWidthFraction - OrbitDockSidebarPanelUI.PanelWidth - 32f;
        }

        public bool TryGetPlayerUpgradePathEdges(out HashSet<(int fL, int fB, int tL, int tB)> edges)
        {
            edges = new HashSet<(int, int, int, int)>();
            int level = ShipLevel;
            int branch = BranchIndex;
            if (level < 1)
                return false;

            while (level > 1)
            {
                int prevLevel = level - 1;
                int parentBranch = -1;
                int count = UpgradeTree.GetShipCountForLevel(prevLevel);
                for (int p = 0; p < count; p++)
                {
                    if (UpgradeTree.IsValidUpgradeStep(prevLevel, p, level, branch))
                    {
                        parentBranch = p;
                        break;
                    }
                }

                if (parentBranch < 0)
                {
                    edges.Clear();
                    return false;
                }

                edges.Add((prevLevel, parentBranch, level, branch));
                level = prevLevel;
                branch = parentBranch;
            }

            return true;
        }

        public void RefreshShipUpgradeTreeNodeStates(IReadOnlyList<ShipUpgradeTreeNodeUI> nodes, ShipPowerBarStatMaxes maxes)
        {
            // --- Hint line (debug vs normal purchase rules) ---
            UpdateShipTreeHintText();

            if (nodes == null)
                return;
            for (int i = 0; i < nodes.Count; i++)
                PopulateTreeNode(nodes[i], maxes);
        }

        /// <summary>
        /// Updates the upgrade-tree subtitle under the title. Debug mode explains that every node is free.
        /// </summary>
        void UpdateShipTreeHintText()
        {
            if (_shipTree == null)
                return;

            _shipTree.EnsurePanelHeader();
            if (_shipTree.Title != null)
                _shipTree.Title.text = ShipUpgradeTreeUI.PanelTitleText;

            if (_shipTree.Hint == null)
                return;

            // [TITAN-ORBIT] Same copy the old OrbitStationUI used when Debug Free Ship Upgrade Tree is on.
            if (IsDebugFreeShipUpgradeTree())
                _shipTree.Hint.text = "Debug: click any ship to try it for free (all tiers unlocked).";
            else
                _shipTree.Hint.text = ShipUpgradeTreeUI.PanelDefaultSubtitle;
        }

        /// <summary>
        /// Colors, prices, and click handlers for one tree node (or current-ship display).
        /// Debug mode unlocks every node; normal mode only next-tier purchases / current slot.
        /// </summary>
        public void PopulateTreeNode(ShipUpgradeTreeNodeUI view, ShipPowerBarStatMaxes maxes)
        {
            if (view == null || upgradeTree == null)
                return;

            // --- Current-ship sidebar card ---
            // [HYBRID] Left panel "You" node uses the same populate path with IsCurrentShipDisplay.
            if (view.IsCurrentShipDisplay)
            {
                PopulateCurrentShipDisplayNode(view, maxes);
                return;
            }

            // --- Debug: unlock entire tree ---
            // [TITAN-ORBIT] GameManager.DebugFreeShipUpgradeTree — Inspector toggle on NceGameRoot.
            if (IsDebugFreeShipUpgradeTree())
            {
                PopulateTreeNodeDebug(view, maxes);
                return;
            }

            // --- Normal unlock / purchase eligibility ---
            int currentLevel = ShipLevel;
            int currentBranch = BranchIndex;
            int nextLevel = currentLevel + 1;
            bool isCurrent = view.Level == currentLevel && view.BranchIndex == currentBranch;
            bool tierBlocked = view.Level > StorePlanetLevel || view.Level > HomePlanetLevel;

            UpgradeTree.GetNextLevelBranchTargets(currentLevel, currentBranch, _nextTargets);
            bool isNextChoice = view.Level == nextLevel && _nextTargets.Contains(view.BranchIndex);
            float nodeCost = isNextChoice ? MoonOrbitStorePricing.GetShipUpgradeCost(nextLevel) : 0f;
            bool canPurchase = isNextChoice && _contributedGems >= nodeCost && !tierBlocked;

            view.SetInteractable(canPurchase || isCurrent);
            view.SetButtonBackgroundColor(isCurrent
                ? new Color(0.26f, 0.62f, 0.36f, 0.98f)
                : canPurchase
                    ? new Color(0.28f, 0.45f, 0.82f, 0.98f)
                    : new Color(0.2f, 0.22f, 0.28f, 0.92f));

            view.SetLevelLabel(view.Level == 1 ? "Lv 1" : $"Lv {view.Level}");
            TryGetChassisIdForTreeSlot(view.Level, view.BranchIndex, out string chassisId);
            view.SetShipName(GetShipDisplayNameForSlot(view.Level, view.BranchIndex, chassisId));
            view.SetPreview(GetMenuPreviewForChassis(chassisId));

            if (isCurrent)
                view.SetPrice("You");
            else if (tierBlocked)
                view.SetPrice($"Planet Lv {view.Level}+");
            else if (isNextChoice)
                view.SetPrice($"{nodeCost:0}g");
            else
                view.SetPrice("—");

            view.ApplyPowerBreakdown(GetPowerBreakdownForTreeNode(view.Level, view.BranchIndex), maxes);
            UnityEngine.Events.UnityAction click = () => OnUpgradeTreeNodeClicked(view.Level, view.BranchIndex);
            view.SetClickHandler(canPurchase || isCurrent ? click : null);
            view.SetPriceClickHandler(canPurchase || isCurrent ? click : null);
        }

        /// <summary>
        /// Debug populate: every node is interactable and priced "Free" so designers can jump to any hull.
        /// </summary>
        void PopulateTreeNodeDebug(ShipUpgradeTreeNodeUI view, ShipPowerBarStatMaxes maxes)
        {
            int level = view.Level;
            int branch = view.BranchIndex;
            bool isCurrent = level == ShipLevel && branch == BranchIndex;
            bool hasChassis = TryGetChassisIdForTreeSlot(level, branch, out string chassisId);

            view.SetInteractable(hasChassis);
            view.SetButtonBackgroundColor(!hasChassis
                ? new Color(0.15f, 0.16f, 0.18f, 0.92f)
                : isCurrent
                    ? new Color(0.26f, 0.62f, 0.36f, 0.98f)
                    : new Color(0.28f, 0.68f, 0.82f, 0.98f));

            view.SetLevelLabel(level == 1 ? "Lv 1" : $"Lv {level}");
            view.SetShipName(GetShipDisplayNameForSlot(level, branch, chassisId));
            view.SetPreview(GetMenuPreviewForChassis(chassisId));
            view.SetPrice(hasChassis ? "Free" : "—");
            view.ApplyPowerBreakdown(GetPowerBreakdownForTreeNode(level, branch), maxes);

            // Whole card + Free button both purchase (not only the price chip).
            UnityEngine.Events.UnityAction click = () => OnUpgradeTreeNodeClicked(level, branch);
            view.SetClickHandler(hasChassis ? click : null);
            view.SetPriceClickHandler(hasChassis ? click : null);
        }

        /// <summary>Sidebar "current ship" card — always shows your hull; debug mode still allows click-through.</summary>
        void PopulateCurrentShipDisplayNode(ShipUpgradeTreeNodeUI view, ShipPowerBarStatMaxes maxes)
        {
            bool debugFree = IsDebugFreeShipUpgradeTree();
            view.SetInteractable(debugFree);
            view.SetButtonBackgroundColor(new Color(0.26f, 0.62f, 0.36f, 0.98f));
            // Sidebar hero: no "You" label — centered ship name sits above the art instead.
            if (view.UsesSidebarHeroLayout)
                view.SetLevelLabel(string.Empty);
            else
                view.SetLevelLabel("You");
            view.SetShipName($"Lv {ShipLevel}");
            view.SetPrice(debugFree ? "Free" : "—");
            view.ApplyPowerBreakdown(GetCurrentShipPowerBreakdown(), maxes);
        }

        /// <summary>
        /// Handles upgrade purchase or debug-free hull select. Sends <see cref="MoonOrbitRpcClient.PurchaseShipUpgrade"/>.
        /// Server only accepts arbitrary levels when <see cref="GameManager.DebugFreeShipUpgradeTree"/> is on.
        /// </summary>
        public void OnUpgradeTreeNodeClicked(int nodeLevel, int targetBranchIndex)
        {
            // --- Debug free select (any tier / branch) ---
            if (IsDebugFreeShipUpgradeTree())
            {
                // Skip no-op click on the hull you already fly.
                if (nodeLevel == ShipLevel && targetBranchIndex == BranchIndex)
                    return;

                MoonOrbitRpcClient.PurchaseShipUpgrade(_storePlanetId, nodeLevel, targetBranchIndex);
                MoonOrbitRpcClient.RequestContributedGems(_homePlanetId);
                return;
            }

            // --- Normal: only next ship level along a valid branch edge ---
            if (nodeLevel == ShipLevel + 1)
            {
                MoonOrbitRpcClient.PurchaseShipUpgrade(_storePlanetId, nodeLevel, targetBranchIndex);
                MoonOrbitRpcClient.RequestContributedGems(_homePlanetId);
            }
        }

        public void OnCurrentShipDisplayNodeClicked() =>
            OnUpgradeTreeNodeClicked(ShipLevel, BranchIndex);

        /// <summary>Your Ship bar: Extra Level at current ship level with every HUD ability maxed.</summary>
        public ShipFamilyPowerScoreBreakdown GetCurrentShipPowerBreakdown()
        {
            if (!TryGetChassisIdForTreeSlot(ShipLevel, BranchIndex, out string chassisId))
                return default;
            var config = ShipStatApplyLogic.Config;
            return config != null
                ? config.GetPowerScoreBreakdownForChassisIdAtShipLevel(chassisId, ShipLevel)
                : default;
        }

        /// <summary>Tree node bar: Extra Level at the node's ship <paramref name="level"/> with every HUD ability maxed.</summary>
        public ShipFamilyPowerScoreBreakdown GetPowerBreakdownForTreeNode(int level, int branchIndex)
        {
            if (!TryGetChassisIdForTreeSlot(level, branchIndex, out string chassisId))
                return default;
            var config = ShipStatApplyLogic.Config;
            return config != null
                ? config.GetPowerScoreBreakdownForChassisIdAtShipLevel(chassisId, level)
                : default;
        }

        /// <summary>Global per-stat ceilings for equal-slot power bars (all families, tree-level Extra Level).</summary>
        static ShipPowerBarStatMaxes ResolvePowerBarStatMaxes()
        {
            var config = ShipStatApplyLogic.Config;
            return config != null
                ? config.GetGlobalPowerBarStatMaxes()
                : ShipFamilyPowerBarNorm.GetGlobalMaxPerStat();
        }

        /// <summary>
        /// Resolves the PlanetShipFamilyConfig ladder chassis for this store slot.
        /// Same mapping the server uses in <see cref="ShipStatApplyLogic.TryResolveChassisId"/>.
        /// </summary>
        bool TryGetChassisIdForTreeSlot(int level, int branchIndex, out string chassisId)
        {
            chassisId = null;
            if (!EcsGameBridge.TryGetLocalShipState(out var ship) || ship.Team == TeamId.None)
            {
                // Menu can open briefly before team is known — still resolve home family ladder.
                return ShipStatApplyLogic.TryResolveChassisId(
                    TeamId.TeamA,
                    level,
                    branchIndex,
                    out chassisId,
                    allowFallback: false,
                    shipFamilyConfigIndex: PlanetShipFamilyAssignment.HomeFamilyConfigIndex);
            }

            // Prefer the docked store planet's family so the tree matches Cosmic Shark / etc.
            int familyIndex = ship.ShipFamilyConfigIndex;
            int storePlanetId = OrbitStationEcsContext.StorePlanetId;
            if (storePlanetId > 0 &&
                EcsGameBridge.TryGetPlanetStateByPlanetId(storePlanetId, out var planet))
            {
                if (planet.IsHomePlanet)
                    familyIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
                else if (planet.ShipFamilyConfigIndex > 0)
                    familyIndex = planet.ShipFamilyConfigIndex;
            }

            return ShipStatApplyLogic.TryResolveChassisId(
                ship.Team,
                level,
                branchIndex,
                out chassisId,
                allowFallback: false,
                shipFamilyConfigIndex: familyIndex);
        }

        /// <summary>
        /// Display name from family upgradeTree (Hawk, Sparrow, …) — not legacy UpgradeTree.asset labels (2.1).
        /// </summary>
        string GetShipDisplayNameForSlot(int level, int branchIndex, string chassisId)
        {
            var config = ShipStatApplyLogic.Config;
            if (config != null && !string.IsNullOrEmpty(chassisId))
            {
                string treeName = config.GetUpgradeTreeShipNameForChassisId(chassisId);
                if (!string.IsNullOrEmpty(treeName))
                    return treeName.Trim();
            }

            if (upgradeTree != null)
            {
                var node = upgradeTree.GetNodeForBranch(level, branchIndex);
                if (node != null && !string.IsNullOrEmpty(node.shipName))
                    return node.shipName.Trim();
            }

            return string.IsNullOrEmpty(chassisId)
                ? $"Ship {level}.{branchIndex + 1}"
                : chassisId;
        }

        /// <summary>Menu thumbnail for the chassis, or null when the slot has no family entry.</summary>
        Sprite GetMenuPreviewForChassis(string chassisId)
        {
            var config = ShipStatApplyLogic.Config;
            if (config == null || string.IsNullOrEmpty(chassisId))
                return null;

            TeamManager.Team team = TeamManager.Team.None;
            if (EcsGameBridge.TryGetLocalShipState(out var ship))
                team = (TeamManager.Team)(int)ship.Team;

            return config.GetMenuPreviewSpriteForChassisId(chassisId, team);
        }

        #endregion

        static TextMeshProUGUI CreateLabel(Transform parent, string text, float fontSize, TextAlignmentOptions align)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = align;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            return tmp;
        }

        static Button CreateButton(Transform parent, string label, Vector2 anchorCenter)
        {
            var go = new GameObject("Button");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            if (anchorCenter != Vector2.zero)
            {
                rt.anchorMin = rt.anchorMax = anchorCenter;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(160f, 32f);
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(0f, 36f);
            }

            var img = go.AddComponent<Image>();
            img.color = new Color(0.22f, 0.42f, 0.78f, 0.95f);
            var btn = go.AddComponent<Button>();

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = textRt.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            return btn;
        }
    }

    static class MoonOrbitStationTreeKeys
    {
        public const string MoonDockStructureKey = "moon-dock-horizontal";
    }
}
