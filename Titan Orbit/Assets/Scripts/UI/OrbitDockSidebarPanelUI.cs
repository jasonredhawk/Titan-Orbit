using System;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Narrow left Orbit Menu dock: Upgrades/Store nav, current ship, bank, and compact purchased loadout.
    /// Upgrade Cards and Equipment hosts hold glance-sized cards (title + icon + power bar) so many
    /// owned items fit on one scroll pass. Full ability text lives on the right-hand store purchase cards.
    /// </summary>
    public class OrbitDockSidebarPanelUI : MonoBehaviour
    {
        public enum NavTarget
        {
            Upgrades,
            Store
        }

        /// <summary>
        /// Fixed width of the left Orbit Menu dock (nav + Your Ship + purchased loadout).
        /// Slightly wider than the old 252 so compact inventory cards can use a readable title row.
        /// </summary>
        public const float PanelWidth = 286f;
        public const string SectionTitleUpgradeCards = "Upgrade Cards";
        public const string SectionTitleEquipment = "Equipment";

        /// <summary>Accent stripe for upgrade-card sections (matches store tab card shop block).</summary>
        public static readonly Color UpgradeCardsAccent = new Color(0.35f, 0.55f, 0.95f, 1f);
        /// <summary>Accent stripe for equipment / store-item sections.</summary>
        public static readonly Color EquipmentAccent = new Color(0.28f, 0.72f, 0.48f, 1f);
        /// <summary>Accent stripe for bank / contributed gem balance.</summary>
        public static readonly Color BankBalanceAccent = new Color(0.95f, 0.78f, 0.22f, 1f);

        private const float NavStripHeight = 44f;
        /// <summary>
        /// Height of the "Your Ship" hero card (preview + labels + power bar).
        /// Tall enough that the scaled power bar fits inside the card instead of spilling into Bank below.
        /// </summary>
        private const float CurrentShipNodeHeight = 256f;
        private const float BankBalanceBannerHeight = 96f;
        private const float AutoDepositToggleHeight = 38f;
        public const string AutoDepositGemsPrefsKey = "TitanOrbit_AutoDepositGems";
        public const int AutoDepositGemsDefaultEnabled = 1;

        [SerializeField] private Sprite panelBackgroundSprite;
        [SerializeField] private Sprite buttonSprite;
        [SerializeField] private TMP_FontAsset fontAsset;

        private RectTransform _contentRoot;
        private RectTransform _currentShipHost;
        private RectTransform _loadoutHost;
        private RectTransform _equipmentHost;
        private TextMeshProUGUI _bankText;
        private TextMeshProUGUI _shipGemsValueText;
        private TextMeshProUGUI _bankValueText;
        private TextMeshProUGUI _planetProgressText;
        private Button _autoDepositToggle;
        private Image _autoDepositToggleBg;
        private TextMeshProUGUI _autoDepositToggleStateLabel;
        private Action<bool> _onAutoDepositChanged;
        private bool _autoDepositEnabled;
        private ShipUpgradeTreeNodeUI _currentShipNode;
        private Button _navUpgradesBtn;
        private Button _navStoreBtn;
        private Image _navUpgradesBg;
        private Image _navStoreBg;
        private IOrbitStationHost _station;
        private Action<NavTarget> _onNavSelected;
        private NavTarget _activeNav = NavTarget.Upgrades;
        private bool _built;

        public RectTransform LoadoutHost => _loadoutHost;
        public RectTransform EquipmentHost => _equipmentHost;
        public ShipUpgradeTreeNodeUI CurrentShipNode => _currentShipNode;

        public void ConfigureVisuals(Sprite panelBg, Sprite btnSprite, TMP_FontAsset font)
        {
            // --- ConfigureVisuals ---
            panelBackgroundSprite = panelBg;
            buttonSprite = btnSprite;
            fontAsset = font;
        }

        public void BindStation(IOrbitStationHost station)
        {
            _station = station;
        }

        public void BindNavigation(Action<NavTarget> onNavSelected)
        {
            _onNavSelected = onNavSelected;
        }

        public void SetActiveNav(NavTarget target)
        {
            _activeNav = target;
            ApplyNavVisuals();
        }

        public void EnsureBuilt()
        {
            // --- Ensure setup ---
            if (_built)
            {
                if (_autoDepositToggle == null)
                    CreateAutoDepositToggle(_contentRoot);
                return;
            }

            _built = true;

            var rootRt = transform as RectTransform;
            if (rootRt == null)
                rootRt = gameObject.AddComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = rootRt.offsetMax = Vector2.zero;

            var rootBg = gameObject.GetComponent<Image>();
            if (rootBg == null)
                rootBg = gameObject.AddComponent<Image>();
            rootBg.color = new Color(0.07f, 0.08f, 0.13f, 0.98f);
            if (panelBackgroundSprite != null)
            {
                rootBg.sprite = panelBackgroundSprite;
                rootBg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
            }

            BuildNavStrip();

            var scrollGo = new GameObject("SidebarScroll");
            scrollGo.transform.SetParent(transform, false);
            var scrollRt = scrollGo.AddComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(4f, 4f);
            scrollRt.offsetMax = new Vector2(-4f, -(NavStripHeight + 4f));
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 18f;

            var viewportGo = new GameObject("Viewport");
            viewportGo.transform.SetParent(scrollGo.transform, false);
            var viewportRt = viewportGo.AddComponent<RectTransform>();
            viewportRt.anchorMin = Vector2.zero;
            viewportRt.anchorMax = Vector2.one;
            viewportRt.offsetMin = Vector2.zero;
            viewportRt.offsetMax = Vector2.zero;
            viewportGo.AddComponent<RectMask2D>();
            var viewportImg = viewportGo.AddComponent<Image>();
            viewportImg.color = new Color(1f, 1f, 1f, 0.01f);
            scroll.viewport = viewportRt;

            _contentRoot = new GameObject("Content").AddComponent<RectTransform>();
            _contentRoot.SetParent(viewportGo.transform, false);
            _contentRoot.anchorMin = new Vector2(0f, 1f);
            _contentRoot.anchorMax = new Vector2(1f, 1f);
            _contentRoot.pivot = new Vector2(0.5f, 1f);
            _contentRoot.anchoredPosition = Vector2.zero;
            _contentRoot.sizeDelta = new Vector2(0f, 900f);
            var contentVlg = _contentRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            contentVlg.spacing = 10f;
            contentVlg.padding = new RectOffset(8, 8, 8, 16);
            contentVlg.childAlignment = TextAnchor.UpperCenter;
            contentVlg.childControlWidth = true;
            contentVlg.childControlHeight = true;
            contentVlg.childForceExpandWidth = true;
            contentVlg.childForceExpandHeight = false;
            var contentFitter = _contentRoot.gameObject.AddComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            scroll.content = _contentRoot;

            CreateSectionHeader(_contentRoot, "Your Ship", 28f);
            _currentShipHost = CreateStretchHost(_contentRoot, "CurrentShipHost", CurrentShipNodeHeight);

            CreateBankBalanceBanner(_contentRoot);
            CreateAutoDepositToggle(_contentRoot);

            CreateAccentSectionHeader(_contentRoot, SectionTitleUpgradeCards,
                "Equipped cards ΓÇö tap Γ£ò to remove.", UpgradeCardsAccent);
            _loadoutHost = CreateStretchHost(_contentRoot, "LoadoutHost", 80f);
            var loadoutLe = _loadoutHost.GetComponent<LayoutElement>();
            loadoutLe.minHeight = 64f;
            loadoutLe.preferredHeight = 80f;
            loadoutLe.flexibleHeight = 0f;

            CreateAccentSectionHeader(_contentRoot, SectionTitleEquipment,
                "Equipped store items ΓÇö tap Γ£ò to remove.", EquipmentAccent);
            _equipmentHost = CreateStretchHost(_contentRoot, "EquipmentHost", 80f);
            var equipmentLe = _equipmentHost.GetComponent<LayoutElement>();
            equipmentLe.minHeight = 64f;
            equipmentLe.preferredHeight = 80f;
            equipmentLe.flexibleHeight = 0f;

            ApplyNavVisuals();
        }

        private void BuildNavStrip()
        {
            // --- Build data ---
            var navGo = new GameObject("NavStrip");
            navGo.transform.SetParent(transform, false);
            var navRt = navGo.AddComponent<RectTransform>();
            navRt.anchorMin = new Vector2(0f, 1f);
            navRt.anchorMax = new Vector2(1f, 1f);
            navRt.pivot = new Vector2(0.5f, 1f);
            navRt.anchoredPosition = Vector2.zero;
            navRt.sizeDelta = new Vector2(0f, NavStripHeight);
            var navHlg = navGo.AddComponent<HorizontalLayoutGroup>();
            navHlg.spacing = 6f;
            navHlg.padding = new RectOffset(8, 8, 6, 6);
            navHlg.childAlignment = TextAnchor.MiddleCenter;
            navHlg.childControlWidth = true;
            navHlg.childControlHeight = true;
            navHlg.childForceExpandWidth = true;
            navHlg.childForceExpandHeight = true;

            _navUpgradesBtn = CreateNavButton(navGo.transform, "Upgrades", NavTarget.Upgrades, out _navUpgradesBg);
            _navStoreBtn = CreateNavButton(navGo.transform, "Store", NavTarget.Store, out _navStoreBg);
        }

        private Button CreateNavButton(Transform parent, string label, NavTarget target, out Image bg)
        {
            // --- Create instance ---
            var go = new GameObject("Nav_" + label);
            go.transform.SetParent(parent, false);
            bg = go.AddComponent<Image>();
            bg.color = new Color(0.14f, 0.18f, 0.28f, 0.95f);
            if (buttonSprite != null)
            {
                bg.sprite = buttonSprite;
                bg.type = Image.Type.Sliced;
            }

            var btn = go.AddComponent<Button>();
            NavTarget captured = target;
            btn.onClick.AddListener(() =>
            {
                if (_onNavSelected != null)
                    _onNavSelected(captured);
            });

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            var tr = textGo.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(4f, 2f);
            tr.offsetMax = new Vector2(-4f, -2f);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 12f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            ApplyFont(tmp);
            return btn;
        }

        private void ApplyNavVisuals()
        {
            // --- Apply changes ---
            if (_navUpgradesBg == null || _navStoreBg == null)
                return;

            bool upgrades = _activeNav == NavTarget.Upgrades;
            _navUpgradesBg.color = upgrades
                ? new Color(0.22f, 0.42f, 0.72f, 0.98f)
                : new Color(0.14f, 0.18f, 0.28f, 0.95f);
            _navStoreBg.color = !upgrades
                ? new Color(0.22f, 0.42f, 0.72f, 0.98f)
                : new Color(0.14f, 0.18f, 0.28f, 0.95f);
        }

        public void EnsureCurrentShipNode(ShipUpgradeTreeNodeUI nodePrefab, Sprite nodeBackgroundSprite)
        {
            // --- Ensure setup ---
            EnsureBuilt();
            if (nodePrefab == null || _currentShipHost == null)
                return;

            float innerW = PanelWidth - 32f;
            float trackW = Mathf.Max(48f, innerW - 56f);

            // Node already built — still re-apply hero layout so name-above-art / hide-"You" edits stick after code changes.
            if (_currentShipNode != null)
            {
                _currentShipNode.ApplySidebarHeroPreviewLayout(innerW, CurrentShipNodeHeight, trackW);
                _currentShipNode.ConfigureLayout(true);
                _currentShipNode.SetSidebarHeroCardClickHandler(() =>
                {
                    if (_station != null)
                        _station.OnCurrentShipDisplayNodeClicked();
                });
                return;
            }

            var view = Instantiate(nodePrefab, _currentShipHost);
            view.gameObject.SetActive(true);
            if (nodeBackgroundSprite != null)
            {
                var bg = view.GetComponent<Image>();
                if (bg != null)
                {
                    bg.sprite = nodeBackgroundSprite;
                    bg.type = Image.Type.Sliced;
                }
            }

            view.ApplySidebarHeroPreviewLayout(innerW, CurrentShipNodeHeight, trackW);
            view.ConfigureLayout(true);
            // [TITAN-ORBIT] Sidebar hero hides the dark price pill (it sat on top of the power bar).
            // Hull-swap click goes on the whole card instead.
            view.SetSidebarHeroCardClickHandler(() =>
            {
                if (_station != null)
                    _station.OnCurrentShipDisplayNodeClicked();
            });
            _currentShipNode = view;
        }

        public void BindAutoDeposit(Action<bool> onChanged)
        {
            _onAutoDepositChanged = onChanged;
        }

        public void RefreshAutoDepositToggle(bool enabled)
        {
            EnsureBuilt();
            SetAutoDepositToggleVisual(enabled, notify: false);
        }

        public void RefreshBank(float contributedGems) =>
            RefreshDepositStatus(0f, contributedGems, 0f, 1);

        public void RefreshDepositStatus(float shipGems, float bankBalance, float planetGems, int planetLevel)
        {
            // --- RefreshDepositStatus ---
            EnsureBuilt();

            // Dirty-check TMP — Orbit Menu called this every frame while open (ToString GC).
            string shipStr = shipGems.ToString("F0");
            string bankStr = bankBalance.ToString("F0");
            if (_shipGemsValueText != null && _shipGemsValueText.text != shipStr)
                _shipGemsValueText.text = shipStr;
            if (_bankValueText != null && _bankValueText.text != bankStr)
                _bankValueText.text = bankStr;

            if (_planetProgressText == null)
                return;

            planetLevel = Mathf.Max(1, planetLevel);
            string planetStr;
            if (planetLevel >= PlanetEconomyMath.MaxPlanetLevel)
                planetStr = $"Planet L{planetLevel} · max";
            else
            {
                float maxGems = PlanetEconomyMath.GetMaxGemsForLevel(planetLevel);
                planetStr = $"Planet L{planetLevel} · {planetGems:F0}/{maxGems:F0}";
            }

            if (_planetProgressText.text != planetStr)
                _planetProgressText.text = planetStr;
        }

        public void RefreshCurrentShip(
            Action<ShipUpgradeTreeNodeUI, ShipPowerBarStatMaxes> populateNode,
            ShipPowerBarStatMaxes maxes)
        {
            // --- RefreshCurrentShip ---
            if (_currentShipNode == null || populateNode == null)
                return;
            populateNode(_currentShipNode, maxes);
        }

        private void CreateSectionHeader(Transform parent, string text, float height)
        {
            CreateAccentSectionHeader(parent, text, null, new Color(0.88f, 0.92f, 1f, 1f), height, false);
        }

        private void CreateAccentSectionHeader(Transform parent, string title, string subtitle, Color accent,
            float blockHeight = 42f, bool showAccent = true)
        {
            var blockGo = new GameObject("Header_" + title.Replace(" ", ""));
            blockGo.transform.SetParent(parent, false);
            var blockLe = blockGo.AddComponent<LayoutElement>();
            blockLe.preferredHeight = blockHeight;
            blockLe.minHeight = blockHeight - 6f;
            blockLe.flexibleHeight = 0f;

            var row = blockGo.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 8f;
            row.padding = new RectOffset(0, 0, 0, 0);
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = true;

            if (showAccent)
            {
                var accentGo = new GameObject("Accent");
                accentGo.transform.SetParent(blockGo.transform, false);
                var accentLe = accentGo.AddComponent<LayoutElement>();
                accentLe.preferredWidth = 4f;
                accentLe.minWidth = 4f;
                accentLe.flexibleHeight = 1f;
                var accentImg = accentGo.AddComponent<Image>();
                accentImg.color = accent;
                accentImg.raycastTarget = false;
            }

            var textColGo = new GameObject("TextCol");
            textColGo.transform.SetParent(blockGo.transform, false);
            var textColLe = textColGo.AddComponent<LayoutElement>();
            textColLe.flexibleWidth = 1f;
            textColLe.minWidth = 80f;
            var textVlg = textColGo.AddComponent<VerticalLayoutGroup>();
            textVlg.spacing = 2f;
            textVlg.childAlignment = TextAnchor.UpperLeft;
            textVlg.childControlWidth = true;
            textVlg.childControlHeight = true;
            textVlg.childForceExpandWidth = true;
            textVlg.childForceExpandHeight = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(textColGo.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 20f;
            titleLe.minHeight = 18f;
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = title;
            titleTmp.fontSize = 15f;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.Left;
            titleTmp.color = new Color(0.92f, 0.95f, 1f, 1f);
            titleTmp.raycastTarget = false;
            ApplyFont(titleTmp);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var subGo = new GameObject("Subtitle");
                subGo.transform.SetParent(textColGo.transform, false);
                var subLe = subGo.AddComponent<LayoutElement>();
                subLe.preferredHeight = 16f;
                subLe.minHeight = 14f;
                var subTmp = subGo.AddComponent<TextMeshProUGUI>();
                subTmp.text = subtitle;
                subTmp.fontSize = 11f;
                subTmp.alignment = TextAlignmentOptions.Left;
                subTmp.color = new Color(0.68f, 0.74f, 0.86f, 0.95f);
                subTmp.raycastTarget = false;
                ApplyFont(subTmp);
            }
        }

        private void CreateBankBalanceBanner(Transform parent)
        {
            // --- Create instance ---
            var bannerGo = new GameObject("BankBalance");
            bannerGo.transform.SetParent(parent, false);
            var bannerLe = bannerGo.AddComponent<LayoutElement>();
            bannerLe.preferredHeight = BankBalanceBannerHeight;
            bannerLe.minHeight = BankBalanceBannerHeight;
            bannerLe.flexibleHeight = 0f;
            bannerLe.flexibleWidth = 1f;

            var bannerBg = bannerGo.AddComponent<Image>();
            bannerBg.color = new Color(0.1f, 0.12f, 0.18f, 0.98f);
            bannerBg.raycastTarget = false;
            if (panelBackgroundSprite != null)
            {
                bannerBg.sprite = panelBackgroundSprite;
                bannerBg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
            }

            var bannerOutline = bannerGo.AddComponent<Outline>();
            bannerOutline.effectColor = new Color(BankBalanceAccent.r, BankBalanceAccent.g, BankBalanceAccent.b, 0.55f);
            bannerOutline.effectDistance = new Vector2(1f, -1f);

            var bannerVlg = bannerGo.AddComponent<VerticalLayoutGroup>();
            bannerVlg.spacing = 3f;
            bannerVlg.padding = new RectOffset(8, 8, 6, 8);
            bannerVlg.childAlignment = TextAnchor.UpperCenter;
            bannerVlg.childControlWidth = true;
            bannerVlg.childControlHeight = true;
            bannerVlg.childForceExpandWidth = true;
            bannerVlg.childForceExpandHeight = false;

            var accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(bannerGo.transform, false);
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.preferredHeight = 4f;
            accentLe.minHeight = 4f;
            var accentImg = accentGo.AddComponent<Image>();
            accentImg.color = BankBalanceAccent;
            accentImg.raycastTarget = false;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(bannerGo.transform, false);
            var labelLe = labelGo.AddComponent<LayoutElement>();
            labelLe.preferredHeight = 14f;
            labelLe.minHeight = 12f;
            _bankText = labelGo.AddComponent<TextMeshProUGUI>();
            _bankText.text = "GEM DEPOSITS";
            _bankText.fontSize = 11f;
            _bankText.fontStyle = FontStyles.Bold;
            _bankText.characterSpacing = 2f;
            _bankText.alignment = TextAlignmentOptions.Center;
            _bankText.color = new Color(0.78f, 0.84f, 0.94f, 0.95f);
            _bankText.raycastTarget = false;
            ApplyFont(_bankText);

            var flowRowGo = new GameObject("GemFlowRow");
            flowRowGo.transform.SetParent(bannerGo.transform, false);
            var flowRowLe = flowRowGo.AddComponent<LayoutElement>();
            flowRowLe.preferredHeight = 40f;
            flowRowLe.minHeight = 36f;
            flowRowLe.flexibleHeight = 0f;
            var flowRow = flowRowGo.AddComponent<HorizontalLayoutGroup>();
            flowRow.spacing = 4f;
            flowRow.padding = new RectOffset(2, 2, 0, 0);
            flowRow.childAlignment = TextAnchor.MiddleCenter;
            flowRow.childControlWidth = true;
            flowRow.childControlHeight = true;
            flowRow.childForceExpandWidth = true;
            flowRow.childForceExpandHeight = true;

            _shipGemsValueText = CreateGemFlowColumn(
                flowRowGo.transform,
                "Ship",
                "0",
                new Color(0.82f, 0.92f, 1f, 1f));

            CreateGemFlowArrow(flowRowGo.transform);

            _bankValueText = CreateGemFlowColumn(
                flowRowGo.transform,
                "Bank",
                "0",
                new Color(1f, 0.92f, 0.55f, 1f));

            var planetGo = new GameObject("PlanetProgress");
            planetGo.transform.SetParent(bannerGo.transform, false);
            var planetLe = planetGo.AddComponent<LayoutElement>();
            planetLe.preferredHeight = 18f;
            planetLe.minHeight = 16f;
            planetLe.flexibleHeight = 0f;
            _planetProgressText = planetGo.AddComponent<TextMeshProUGUI>();
            _planetProgressText.text = "Planet L1 · 0/100";
            _planetProgressText.fontSize = 11f;
            _planetProgressText.alignment = TextAlignmentOptions.Center;
            _planetProgressText.color = new Color(0.72f, 0.82f, 0.95f, 0.95f);
            _planetProgressText.raycastTarget = false;
            ApplyFont(_planetProgressText);
        }

        TextMeshProUGUI CreateGemFlowColumn(Transform parent, string label, string value, Color valueColor)
        {
            // --- Create instance ---
            var colGo = new GameObject(label + "Column");
            colGo.transform.SetParent(parent, false);
            var colLe = colGo.AddComponent<LayoutElement>();
            colLe.flexibleWidth = 1f;
            colLe.minWidth = 56f;

            var colVlg = colGo.AddComponent<VerticalLayoutGroup>();
            colVlg.spacing = 0f;
            colVlg.childAlignment = TextAnchor.MiddleCenter;
            colVlg.childControlWidth = true;
            colVlg.childControlHeight = true;
            colVlg.childForceExpandWidth = true;
            colVlg.childForceExpandHeight = false;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(colGo.transform, false);
            var labelLe = labelGo.AddComponent<LayoutElement>();
            labelLe.preferredHeight = 12f;
            labelLe.minHeight = 10f;
            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text = label;
            labelTmp.fontSize = 10f;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.alignment = TextAlignmentOptions.Center;
            labelTmp.color = new Color(0.72f, 0.78f, 0.88f, 0.95f);
            labelTmp.raycastTarget = false;
            ApplyFont(labelTmp);

            var valueGo = new GameObject("Value");
            valueGo.transform.SetParent(colGo.transform, false);
            var valueLe = valueGo.AddComponent<LayoutElement>();
            valueLe.preferredHeight = 24f;
            valueLe.minHeight = 20f;
            var valueTmp = valueGo.AddComponent<TextMeshProUGUI>();
            valueTmp.text = value;
            valueTmp.fontSize = 22f;
            valueTmp.fontStyle = FontStyles.Bold;
            valueTmp.alignment = TextAlignmentOptions.Center;
            valueTmp.color = valueColor;
            valueTmp.enableWordWrapping = false;
            valueTmp.overflowMode = TextOverflowModes.Overflow;
            valueTmp.raycastTarget = false;
            ApplyFont(valueTmp);

            return valueTmp;
        }

        void CreateGemFlowArrow(Transform parent)
        {
            // --- Create instance ---
            var arrowGo = new GameObject("Arrow");
            arrowGo.transform.SetParent(parent, false);
            var arrowLe = arrowGo.AddComponent<LayoutElement>();
            arrowLe.preferredWidth = 18f;
            arrowLe.minWidth = 14f;
            arrowLe.flexibleWidth = 0f;

            var arrowTmp = arrowGo.AddComponent<TextMeshProUGUI>();
            arrowTmp.text = "→";
            arrowTmp.fontSize = 16f;
            arrowTmp.fontStyle = FontStyles.Bold;
            arrowTmp.alignment = TextAlignmentOptions.Center;
            arrowTmp.color = new Color(0.55f, 0.62f, 0.72f, 0.9f);
            arrowTmp.raycastTarget = false;
            ApplyFont(arrowTmp);
        }

        private void CreateAutoDepositToggle(Transform parent)
        {
            // --- Create instance ---
            var rowGo = new GameObject("AutoDepositToggle");
            rowGo.transform.SetParent(parent, false);
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.preferredHeight = AutoDepositToggleHeight;
            rowLe.minHeight = AutoDepositToggleHeight;
            rowLe.flexibleHeight = 0f;
            rowLe.flexibleWidth = 1f;

            var rowBg = rowGo.AddComponent<Image>();
            rowBg.color = new Color(0.09f, 0.11f, 0.17f, 0.96f);
            rowBg.raycastTarget = false;
            if (panelBackgroundSprite != null)
            {
                rowBg.sprite = panelBackgroundSprite;
                rowBg.type = panelBackgroundSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
            }

            var rowHlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowHlg.spacing = 8f;
            rowHlg.padding = new RectOffset(10, 8, 6, 6);
            rowHlg.childAlignment = TextAnchor.MiddleCenter;
            rowHlg.childControlWidth = true;
            rowHlg.childControlHeight = true;
            rowHlg.childForceExpandWidth = false;
            rowHlg.childForceExpandHeight = true;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var labelLe = labelGo.AddComponent<LayoutElement>();
            labelLe.flexibleWidth = 1f;
            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text = "Auto Deposit Gems";
            labelTmp.fontSize = 12f;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.alignment = TextAlignmentOptions.Left;
            labelTmp.color = new Color(0.88f, 0.92f, 1f, 0.98f);
            labelTmp.raycastTarget = false;
            ApplyFont(labelTmp);

            var toggleGo = new GameObject("Toggle");
            toggleGo.transform.SetParent(rowGo.transform, false);
            var toggleLe = toggleGo.AddComponent<LayoutElement>();
            toggleLe.preferredWidth = 52f;
            toggleLe.minWidth = 52f;
            toggleLe.preferredHeight = 24f;
            toggleLe.minHeight = 24f;
            _autoDepositToggleBg = toggleGo.AddComponent<Image>();
            _autoDepositToggleBg.color = new Color(0.14f, 0.18f, 0.28f, 0.95f);
            if (buttonSprite != null)
            {
                _autoDepositToggleBg.sprite = buttonSprite;
                _autoDepositToggleBg.type = Image.Type.Sliced;
            }

            _autoDepositToggle = toggleGo.AddComponent<Button>();
            _autoDepositToggle.onClick.AddListener(OnAutoDepositToggleClicked);

            var stateGo = new GameObject("State");
            stateGo.transform.SetParent(toggleGo.transform, false);
            var stateRt = stateGo.AddComponent<RectTransform>();
            stateRt.anchorMin = Vector2.zero;
            stateRt.anchorMax = Vector2.one;
            stateRt.offsetMin = Vector2.zero;
            stateRt.offsetMax = Vector2.zero;
            _autoDepositToggleStateLabel = stateGo.AddComponent<TextMeshProUGUI>();
            _autoDepositToggleStateLabel.fontSize = 11f;
            _autoDepositToggleStateLabel.fontStyle = FontStyles.Bold;
            _autoDepositToggleStateLabel.alignment = TextAlignmentOptions.Center;
            _autoDepositToggleStateLabel.color = Color.white;
            _autoDepositToggleStateLabel.raycastTarget = false;
            ApplyFont(_autoDepositToggleStateLabel);

            bool saved = PlayerPrefs.GetInt(AutoDepositGemsPrefsKey, AutoDepositGemsDefaultEnabled) != 0;
            SetAutoDepositToggleVisual(saved, notify: false);
        }

        private void OnAutoDepositToggleClicked()
        {
            SetAutoDepositToggleVisual(!_autoDepositEnabled, notify: true);
        }

        private void SetAutoDepositToggleVisual(bool enabled, bool notify)
        {
            // --- SetAutoDepositToggleVisual ---
            _autoDepositEnabled = enabled;
            if (_autoDepositToggleBg != null)
            {
                _autoDepositToggleBg.color = enabled
                    ? new Color(0.16f, 0.52f, 0.34f, 0.98f)
                    : new Color(0.14f, 0.18f, 0.28f, 0.95f);
            }

            if (_autoDepositToggleStateLabel != null)
                _autoDepositToggleStateLabel.text = enabled ? "ON" : "OFF";

            if (notify)
            {
                PlayerPrefs.SetInt(AutoDepositGemsPrefsKey, enabled ? 1 : 0);
                PlayerPrefs.Save();
                _onAutoDepositChanged?.Invoke(enabled);
            }
        }

        private TextMeshProUGUI CreateBodyLabel(Transform parent, string name, string text, float height)
        {
            // --- Create instance ---
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height - 4f;
            le.flexibleHeight = 0f;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 13f;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.color = new Color(0.78f, 0.86f, 1f, 1f);
            tmp.raycastTarget = false;
            ApplyFont(tmp);
            return tmp;
        }

        private static RectTransform CreateStretchHost(Transform parent, string name, float preferredHeight)
        {
            // --- Create instance ---
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = preferredHeight;
            le.minHeight = preferredHeight;
            le.flexibleHeight = 0f;
            le.flexibleWidth = 1f;
            return rt;
        }

        private void ApplyFont(TextMeshProUGUI tmp)
        {
            if (fontAsset != null)
                tmp.font = fontAsset;
        }
    }
}
