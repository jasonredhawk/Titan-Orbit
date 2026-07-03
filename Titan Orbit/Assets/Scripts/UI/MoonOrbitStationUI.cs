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
    /// <summary>Moon orbit station menu: deposit gems, ship upgrade tree, and store.</summary>
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
        bool _visible;
        readonly List<int> _nextTargets = new List<int>(4);

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
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            if (upgradeTree == null)
                upgradeTree = Resources.Load<UpgradeTree>("UpgradeTree");
            EnsureUiBuilt();
            Hide();
        }

        void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                MoonOrbitClientState.SetOrbitMenuVisible(false);
            }
        }

        void Update()
        {
            if (MoonOrbitClientState.TryConsumeContributedGems(out float gems))
                OnContributedGemsReceived(gems);

            if (MoonOrbitClientState.TryConsumeStoreMessage(out string message))
                Debug.LogWarning($"[MoonOrbitStore] {message}");

            if (!_visible)
                return;

            RefreshFromEcs();
            _gemsRequestAccum += Time.deltaTime;
            if (_gemsRequestAccum >= 1.5f)
            {
                _gemsRequestAccum = 0f;
                if (_homePlanetId > 0)
                    MoonOrbitRpcClient.RequestContributedGems(_homePlanetId);
            }
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
            _contributedGems = amount;
            if (_sidebar != null)
                _sidebar.RefreshBank(amount);
            if (_gemsText != null)
                _gemsText.text = $"Bank: {amount:0} gems";
        }

        void RefreshFromEcs()
        {
            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
                return;

            ShipLevel = Mathf.Max(1, ship.ShipLevel);
            BranchIndex = 0;
            if (EcsGameBridge.TryGetLocalShipLoadout(out var loadout))
                BranchIndex = loadout.BranchIndex;

            if (EcsGameBridge.TryGetPlanetStateByPlanetId(_storePlanetId, out var storePlanet))
                StorePlanetLevel = Mathf.Max(1, storePlanet.PlanetLevel);

            HomePlanetLevel = StorePlanetLevel;
            if (_homePlanetId > 0 && EcsGameBridge.TryGetPlanetStateByPlanetId(_homePlanetId, out var home))
                HomePlanetLevel = Mathf.Max(1, home.PlanetLevel);

            RefreshShipTree();
            RefreshStore();
            _sidebar?.RefreshBank(_contributedGems);
            _sidebar?.RefreshCurrentShip(PopulateTreeNode, 100f);
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
                float price = StoreItemData.GetPrice(item);
                string desc = StoreItemData.GetDescription(item);
                string label = string.IsNullOrEmpty(desc)
                    ? $"{StoreItemData.GetDisplayName(item)} — {price:0}g"
                    : $"{StoreItemData.GetDisplayName(item)} — {price:0}g\n<size=12>{desc}</size>";
                var btn = CreateButton(parent, label, Vector2.zero);
                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 52f;
                int captured = (int)item;
                btn.onClick.AddListener(() =>
                {
                    MoonOrbitRpcClient.PurchaseStoreItem(_homePlanetId, (StoreItemType)captured);
                    MoonOrbitRpcClient.RequestContributedGems(_homePlanetId);
                });
            }
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
            // Store buttons are static; contributed gems refresh handles affordability display.
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

        public void RefreshShipUpgradeTreeNodeStates(IReadOnlyList<ShipUpgradeTreeNodeUI> nodes, float maxPower)
        {
            if (nodes == null)
                return;
            for (int i = 0; i < nodes.Count; i++)
                PopulateTreeNode(nodes[i], maxPower);
        }

        public void PopulateTreeNode(ShipUpgradeTreeNodeUI view, float maxPower)
        {
            if (view == null || upgradeTree == null)
                return;

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
            var node = upgradeTree.GetNodeForBranch(view.Level, view.BranchIndex);
            view.SetShipName(node != null && !string.IsNullOrEmpty(node.shipName)
                ? node.shipName
                : $"Ship {view.Level}.{view.BranchIndex + 1}");

            if (isCurrent)
                view.SetPrice("You");
            else if (tierBlocked)
                view.SetPrice($"Planet Lv {view.Level}+");
            else if (isNextChoice)
                view.SetPrice($"{nodeCost:0}g");
            else
                view.SetPrice("—");

            view.ApplyPowerBreakdown(GetPowerBreakdownForTreeNode(view.Level, view.BranchIndex), maxPower);
            view.SetPriceClickHandler(() => OnUpgradeTreeNodeClicked(view.Level, view.BranchIndex));
        }

        public void OnUpgradeTreeNodeClicked(int nodeLevel, int targetBranchIndex)
        {
            if (nodeLevel == ShipLevel + 1)
            {
                MoonOrbitRpcClient.PurchaseShipUpgrade(_storePlanetId, nodeLevel, targetBranchIndex);
                MoonOrbitRpcClient.RequestContributedGems(_homePlanetId);
            }
        }

        public void OnCurrentShipDisplayNodeClicked() =>
            OnUpgradeTreeNodeClicked(ShipLevel, BranchIndex);

        public ShipFamilyPowerScoreBreakdown GetCurrentShipPowerBreakdown() => default;

        public ShipFamilyPowerScoreBreakdown GetPowerBreakdownForTreeNode(int level, int branchIndex) => default;

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
