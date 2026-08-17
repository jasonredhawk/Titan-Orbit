using System;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Narrow left Orbit Menu dock: SHIPS / GEAR / CARDS nav, current ship, family name,
    /// bank, and one LOADOUT list (cards + components + drones + rockets + mines share slots).
    /// Glance cards stay compact; full ability text lives on the right-hand purchase tiles.
    /// </summary>
    public class OrbitDockSidebarPanelUI : MonoBehaviour
    {
        /// <summary>Center panel the sidebar nav opens. Legacy aliases keep older hosts compiling.</summary>
        public enum NavTarget
        {
            Ships = 0,
            Gear = 1,
            Cards = 2,
            Upgrades = Ships,
            Store = Gear
        }

        /// <summary>
        /// Fixed width of the left Orbit Menu dock (nav + Your Ship + purchased loadout).
        /// Slightly wider than the old 252 so compact inventory cards can use a readable title row.
        /// </summary>
        public const float PanelWidth = 286f;
        public const string SectionTitleUpgradeCards = "LOADOUT";
        public const string SectionTitleEquipment = "LOADOUT";
        public const string SectionTitleLoadout = "LOADOUT";

        /// <summary>Accent stripe for upgrade-card sections (matches store tab card shop block).</summary>
        public static readonly Color UpgradeCardsAccent = new Color(0.35f, 0.55f, 0.95f, 1f);
        /// <summary>Accent stripe for equipment / store-item sections.</summary>
        public static readonly Color EquipmentAccent = new Color(0.28f, 0.72f, 0.48f, 1f);
        /// <summary>Accent stripe for bank / contributed gem balance.</summary>
        public static readonly Color BankBalanceAccent = new Color(0.95f, 0.78f, 0.22f, 1f);

        private const float NavStripHeight = 44f;
        /// <summary>
        /// Height of the "Your Ship" hero card (preview + labels + power bar).
        /// Kept compact so more LOADOUT slots stay on-screen at once.
        /// </summary>
        private const float CurrentShipNodeHeight = 220f;
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
        private Button _damageModeButton;
        private Image _damageModeBg;
        private TextMeshProUGUI _damageModeLabel;
        private Button _healModeButton;
        private Image _healModeBg;
        private TextMeshProUGUI _healModeLabel;
        private Action<bool> _onHealingChanged;
        private bool _healingEnabled;
        private ShipUpgradeTreeNodeUI _currentShipNode;
        private Button _navShipsBtn;
        private Button _navGearBtn;
        private Button _navCardsBtn;
        private Image _navShipsBg;
        private Image _navGearBg;
        private Image _navCardsBg;
        private TextMeshProUGUI _familyNameText;
        private TextMeshProUGUI _familyStatsText;
        private GameObject _familyStatsBlock;
        private GameObject _ordnanceBlock;
        private TextMeshProUGUI _ordnanceText;
        private MoonDockHoverTip _ordnanceTip;
        private Button _navUpgradesBtn;
        private Button _navStoreBtn;
        private Image _navUpgradesBg;
        private Image _navStoreBg;
        private IOrbitStationHost _station;
        private Action<NavTarget> _onNavSelected;
        private NavTarget _activeNav = NavTarget.Ships;
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
                if (_damageModeButton == null)
                    CreateHealingBulletsToggle(_contentRoot);
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
            rootBg.color = new Color(0.012f, 0.016f, 0.028f, 1f);
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
            contentVlg.spacing = 6f;
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

            CreateSectionHeader(_contentRoot, "YOUR SHIP", 28f);
            _familyNameText = CreateBodyLabel(_contentRoot, "FamilyName", "FAMILY", 18f);
            _familyNameText.fontSize = 12f;
            _familyNameText.fontStyle = FontStyles.Bold;
            _familyNameText.characterSpacing = 1.5f;
            _familyNameText.color = new Color(0.62f, 0.78f, 0.95f, 0.95f);
            _currentShipHost = CreateStretchHost(_contentRoot, "CurrentShipHost", CurrentShipNodeHeight);
            ApplyTopJustifiedHostLayout(_currentShipHost);
            ApplyYourShipHeroChrome(_currentShipHost);
            CreateFamilyStatsBlock(_contentRoot);
            CreateOrdnanceBlock(_contentRoot);

            CreateBankBalanceBanner(_contentRoot);
            CreateAutoDepositToggle(_contentRoot);
            CreateHealingBulletsToggle(_contentRoot);

            var loadoutGap = CreateStretchHost(_contentRoot, "LoadoutTopGap", 16f);
            var loadoutGapLe = loadoutGap.GetComponent<LayoutElement>();
            loadoutGapLe.minHeight = 16f;
            loadoutGapLe.preferredHeight = 16f;
            loadoutGapLe.flexibleHeight = 0f;

            var loadoutSection = CreateStretchHost(_contentRoot, "LoadoutSection", 8f);
            var loadoutSectionLe = loadoutSection.GetComponent<LayoutElement>();
            loadoutSectionLe.minHeight = 0f;
            loadoutSectionLe.preferredHeight = -1f;
            loadoutSectionLe.flexibleHeight = 0f;
            var loadoutSectionVlg = loadoutSection.gameObject.AddComponent<VerticalLayoutGroup>();
            loadoutSectionVlg.spacing = 4f;
            loadoutSectionVlg.padding = new RectOffset(0, 0, 0, 0);
            loadoutSectionVlg.childAlignment = TextAnchor.UpperCenter;
            loadoutSectionVlg.childControlWidth = true;
            loadoutSectionVlg.childControlHeight = true;
            loadoutSectionVlg.childForceExpandWidth = true;
            loadoutSectionVlg.childForceExpandHeight = false;
            var loadoutSectionFitter = loadoutSection.gameObject.AddComponent<ContentSizeFitter>();
            loadoutSectionFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            loadoutSectionFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            CreateAccentSectionHeader(loadoutSection, SectionTitleLoadout,
                "Cards and gear share these slots — tap × to remove.", EquipmentAccent, 50f, true, packToContent: true);
            _loadoutHost = CreateStretchHost(loadoutSection, "LoadoutHost", 8f);
            var loadoutLe = _loadoutHost.GetComponent<LayoutElement>();
            loadoutLe.minHeight = 0f;
            loadoutLe.preferredHeight = 8f;
            loadoutLe.flexibleHeight = 0f;
            ApplyTopJustifiedHostLayout(_loadoutHost);

            _equipmentHost = CreateStretchHost(loadoutSection, "EquipmentHost", 8f);
            var equipmentLe = _equipmentHost.GetComponent<LayoutElement>();
            equipmentLe.minHeight = 0f;
            equipmentLe.preferredHeight = 8f;
            equipmentLe.flexibleHeight = 0f;
            ApplyTopJustifiedHostLayout(_equipmentHost);

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

            _navShipsBtn = CreateNavButton(navGo.transform, "SHIPS", NavTarget.Ships, out _navShipsBg);
            _navGearBtn = CreateNavButton(navGo.transform, "GEAR", NavTarget.Gear, out _navGearBg);
            _navCardsBtn = CreateNavButton(navGo.transform, "CARDS", NavTarget.Cards, out _navCardsBg);
            _navUpgradesBtn = _navShipsBtn;
            _navStoreBtn = _navGearBtn;
            _navUpgradesBg = _navShipsBg;
            _navStoreBg = _navGearBg;
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
            tmp.fontSize = 11f;
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
            ApplyNavButtonVisual(_navShipsBg, _activeNav == NavTarget.Ships);
            ApplyNavButtonVisual(_navGearBg, _activeNav == NavTarget.Gear);
            ApplyNavButtonVisual(_navCardsBg, _activeNav == NavTarget.Cards);
        }

        static void ApplyNavButtonVisual(Image bg, bool selected)
        {
            if (bg == null)
                return;
            bg.color = selected
                ? new Color(0.22f, 0.42f, 0.72f, 0.98f)
                : new Color(0.10f, 0.14f, 0.22f, 0.95f);
        }

        /// <summary>
        /// Writes the uppercase family caption and optional FAMILY STATS rail.
        /// Hides the rail when every special bonus is 1× (Astro Eagle today).
        /// </summary>
        public void RefreshFamilyIdentity(ShipFamilyDefinition family, int shipLevel = 1)
        {
            EnsureBuilt();
            if (_familyNameText != null)
                _familyNameText.text = FamilyStatHudCopy.FormatFamilyCaption(family);

            bool showStats = FamilyStatHudCopy.HasVisibleFamilyStats(family);
            if (_familyStatsBlock != null)
                _familyStatsBlock.SetActive(showStats);
            if (showStats && _familyStatsText != null)
                _familyStatsText.text = FamilyStatHudCopy.FormatNonIdentityBonuses(family.specialBonuses);

            string bankName = BulletBankHudCopy.FormatFamilyTypeName(family);
            bool showWeapon = !string.IsNullOrEmpty(bankName);
            if (_ordnanceBlock != null)
                _ordnanceBlock.SetActive(showWeapon);
            if (showWeapon && _ordnanceText != null)
                _ordnanceText.text = BulletBankHudCopy.FormatFamilyWeaponGlance(family, shipLevel);
            if (_ordnanceTip != null)
            {
                _ordnanceTip.Caption = BulletBankHudCopy.WeaponTypeCaption;
                _ordnanceTip.Body = showWeapon
                    ? BulletBankHudCopy.BuildFamilyOrdnanceTooltip(family, shipLevel)
                    : string.Empty;
            }
        }

        void CreateFamilyStatsBlock(Transform parent)
        {
            _familyStatsBlock = new GameObject("FamilyStats");
            _familyStatsBlock.transform.SetParent(parent, false);
            var le = _familyStatsBlock.AddComponent<LayoutElement>();
            le.preferredHeight = 36f;
            le.minHeight = 28f;
            le.flexibleHeight = 0f;
            var bg = _familyStatsBlock.AddComponent<Image>();
            bg.color = new Color(0.018f, 0.028f, 0.045f, 1f);
            bg.raycastTarget = false;
            var vlg = _familyStatsBlock.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 4, 4);
            vlg.spacing = 1f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var capGo = new GameObject("Caption");
            capGo.transform.SetParent(_familyStatsBlock.transform, false);
            var cap = capGo.AddComponent<TextMeshProUGUI>();
            cap.text = "FAMILY STATS";
            cap.fontSize = 9f;
            cap.fontStyle = FontStyles.Bold;
            cap.characterSpacing = 1.2f;
            cap.color = new Color(0.62f, 0.78f, 0.95f, 0.92f);
            cap.raycastTarget = false;
            ApplyFont(cap);

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(_familyStatsBlock.transform, false);
            _familyStatsText = bodyGo.AddComponent<TextMeshProUGUI>();
            _familyStatsText.fontSize = 10f;
            _familyStatsText.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            _familyStatsText.raycastTarget = false;
            ApplyFont(_familyStatsText);
            _familyStatsBlock.SetActive(false);
        }

        void CreateOrdnanceBlock(Transform parent)
        {
            _ordnanceBlock = new GameObject("Ordnance");
            _ordnanceBlock.transform.SetParent(parent, false);
            var le = _ordnanceBlock.AddComponent<LayoutElement>();
            le.preferredHeight = 52f;
            le.minHeight = 40f;
            le.flexibleHeight = 0f;
            var bg = _ordnanceBlock.AddComponent<Image>();
            bg.color = new Color(0.018f, 0.028f, 0.045f, 1f);
            var vlg = _ordnanceBlock.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 4, 4);
            vlg.spacing = 1f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var capGo = new GameObject("Caption");
            capGo.transform.SetParent(_ordnanceBlock.transform, false);
            var cap = capGo.AddComponent<TextMeshProUGUI>();
            cap.text = BulletBankHudCopy.WeaponTypeCaption;
            cap.fontSize = 9f;
            cap.fontStyle = FontStyles.Bold;
            cap.characterSpacing = 1.2f;
            cap.color = new Color(1f, 0.67f, 0.4f, 0.95f);
            cap.raycastTarget = false;
            ApplyFont(cap);

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(_ordnanceBlock.transform, false);
            _ordnanceText = bodyGo.AddComponent<TextMeshProUGUI>();
            _ordnanceText.fontSize = 10f;
            _ordnanceText.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            _ordnanceText.enableWordWrapping = true;
            _ordnanceText.overflowMode = TextOverflowModes.Ellipsis;
            _ordnanceText.maxVisibleLines = 3;
            _ordnanceText.raycastTarget = false;
            ApplyFont(_ordnanceText);

            _ordnanceTip = _ordnanceBlock.AddComponent<MoonDockHoverTip>();
            _ordnanceTip.Caption = BulletBankHudCopy.WeaponTypeCaption;
            _ordnanceBlock.SetActive(false);
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

        public void BindHealingBullets(Action<bool> onChanged)
        {
            _onHealingChanged = onChanged;
        }

        public void RefreshHealingBulletsToggle(bool healingActive)
        {
            EnsureBuilt();
            SetHealingToggleVisual(healingActive, notify: false);
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
            float blockHeight = 42f, bool showAccent = true, bool packToContent = false)
        {
            var blockGo = new GameObject("Header_" + title.Replace(" ", ""));
            blockGo.transform.SetParent(parent, false);
            float titleH = 20f;
            float subtitleH = string.IsNullOrEmpty(subtitle) ? 0f : (packToContent ? 28f : 16f);
            float titleSubGap = subtitleH > 0f ? 2f : 0f;
            float packedH = titleH + titleSubGap + subtitleH;
            float headerH = packToContent ? packedH : blockHeight;

            var blockLe = blockGo.AddComponent<LayoutElement>();
            blockLe.flexibleHeight = 0f;
            blockLe.preferredHeight = headerH;
            blockLe.minHeight = packToContent ? packedH : Mathf.Max(18f, blockHeight - 6f);

            var row = blockGo.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 8f;
            row.padding = new RectOffset(0, 0, 0, 0);
            row.childAlignment = TextAnchor.UpperLeft;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;

            if (showAccent)
            {
                var accentGo = new GameObject("Accent");
                accentGo.transform.SetParent(blockGo.transform, false);
                var accentLe = accentGo.AddComponent<LayoutElement>();
                accentLe.preferredWidth = 4f;
                accentLe.minWidth = 4f;
                accentLe.preferredHeight = headerH;
                accentLe.minHeight = headerH;
                accentLe.flexibleHeight = 0f;
                var accentImg = accentGo.AddComponent<Image>();
                accentImg.color = accent;
                accentImg.raycastTarget = false;
            }

            var textColGo = new GameObject("TextCol");
            textColGo.transform.SetParent(blockGo.transform, false);
            var textColLe = textColGo.AddComponent<LayoutElement>();
            textColLe.flexibleWidth = 1f;
            textColLe.minWidth = 80f;
            textColLe.preferredHeight = headerH;
            textColLe.minHeight = headerH;
            textColLe.flexibleHeight = 0f;
            var textVlg = textColGo.AddComponent<VerticalLayoutGroup>();
            textVlg.spacing = titleSubGap;
            textVlg.padding = new RectOffset(0, 0, 0, 0);
            textVlg.childAlignment = TextAnchor.UpperLeft;
            textVlg.childControlWidth = true;
            textVlg.childControlHeight = true;
            textVlg.childForceExpandWidth = true;
            textVlg.childForceExpandHeight = false;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(textColGo.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = titleH;
            titleLe.minHeight = titleH;
            titleLe.flexibleHeight = 0f;
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = title;
            titleTmp.fontSize = 15f;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.TopLeft;
            titleTmp.color = new Color(0.92f, 0.95f, 1f, 1f);
            titleTmp.enableWordWrapping = false;
            titleTmp.overflowMode = TextOverflowModes.Ellipsis;
            titleTmp.raycastTarget = false;
            ApplyFont(titleTmp);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var subGo = new GameObject("Subtitle");
                subGo.transform.SetParent(textColGo.transform, false);
                var subLe = subGo.AddComponent<LayoutElement>();
                subLe.preferredHeight = subtitleH;
                subLe.minHeight = subtitleH;
                subLe.flexibleHeight = 0f;
                var subTmp = subGo.AddComponent<TextMeshProUGUI>();
                subTmp.text = subtitle;
                subTmp.fontSize = 11f;
                subTmp.alignment = TextAlignmentOptions.TopLeft;
                subTmp.color = new Color(0.68f, 0.74f, 0.86f, 0.95f);
                subTmp.enableWordWrapping = true;
                subTmp.overflowMode = TextOverflowModes.Ellipsis;
                subTmp.maxVisibleLines = packToContent ? 2 : 1;
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

        private void CreateHealingBulletsToggle(Transform parent)
        {
            var rowGo = new GameObject("HealingBulletsToggle");
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
            rowHlg.spacing = 6f;
            rowHlg.padding = new RectOffset(8, 8, 6, 6);
            rowHlg.childAlignment = TextAnchor.MiddleCenter;
            rowHlg.childControlWidth = true;
            rowHlg.childControlHeight = true;
            rowHlg.childForceExpandWidth = true;
            rowHlg.childForceExpandHeight = true;

            CreateModeButton(
                rowGo.transform,
                "Damage",
                "DAMAGE",
                out _damageModeButton,
                out _damageModeBg,
                out _damageModeLabel);
            _damageModeButton.onClick.AddListener(OnDamageModeClicked);

            CreateModeButton(
                rowGo.transform,
                "Heal",
                "HEAL",
                out _healModeButton,
                out _healModeBg,
                out _healModeLabel);
            _healModeButton.onClick.AddListener(OnHealModeClicked);

            SetHealingToggleVisual(false, notify: false);
        }

        private void CreateModeButton(
            Transform parent,
            string objectName,
            string label,
            out Button button,
            out Image background,
            out TextMeshProUGUI labelTmp)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minWidth = 72f;
            le.preferredHeight = 24f;
            le.minHeight = 24f;

            background = go.AddComponent<Image>();
            background.color = new Color(0.14f, 0.18f, 0.28f, 0.95f);
            if (buttonSprite != null)
            {
                background.sprite = buttonSprite;
                background.type = Image.Type.Sliced;
            }

            button = go.AddComponent<Button>();

            var stateGo = new GameObject("Label");
            stateGo.transform.SetParent(go.transform, false);
            var stateRt = stateGo.AddComponent<RectTransform>();
            stateRt.anchorMin = Vector2.zero;
            stateRt.anchorMax = Vector2.one;
            stateRt.offsetMin = Vector2.zero;
            stateRt.offsetMax = Vector2.zero;
            labelTmp = stateGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text = label;
            labelTmp.fontSize = 11f;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.alignment = TextAlignmentOptions.Center;
            labelTmp.color = Color.white;
            labelTmp.raycastTarget = false;
            ApplyFont(labelTmp);
        }

        private void OnDamageModeClicked()
        {
            SetHealingToggleVisual(false, notify: true);
        }

        private void OnHealModeClicked()
        {
            SetHealingToggleVisual(true, notify: true);
        }

        private void SetHealingToggleVisual(bool healingActive, bool notify)
        {
            bool changed = _healingEnabled != healingActive;
            _healingEnabled = healingActive;

            ApplyModeButtonVisual(
                _damageModeBg,
                _damageModeLabel,
                selected: !healingActive,
                selectedColor: new Color(0.42f, 0.18f, 0.16f, 0.98f));
            ApplyModeButtonVisual(
                _healModeBg,
                _healModeLabel,
                selected: healingActive,
                selectedColor: new Color(0.18f, 0.48f, 0.42f, 0.98f));

            if (notify && changed)
                _onHealingChanged?.Invoke(healingActive);
        }

        private static void ApplyModeButtonVisual(
            Image background,
            TextMeshProUGUI label,
            bool selected,
            Color selectedColor)
        {
            if (background != null)
            {
                background.color = selected
                    ? selectedColor
                    : new Color(0.14f, 0.18f, 0.28f, 0.95f);
            }

            if (label != null)
                label.color = selected ? Color.white : new Color(0.72f, 0.78f, 0.88f, 0.7f);
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

        /// <summary>Packs loadout / equipment cards from the top of the host instead of centering them.</summary>
        private static void ApplyTopJustifiedHostLayout(RectTransform host)
        {
            if (host == null)
                return;

            var vlg = host.GetComponent<VerticalLayoutGroup>();
            if (vlg == null)
                vlg = host.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 0f;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
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

        /// <summary>Quiet dark plate behind the Your Ship hero — no neon rail or outline.</summary>
        private static void ApplyYourShipHeroChrome(RectTransform host)
        {
            if (host == null)
                return;

            var outline = host.gameObject.GetComponent<Outline>();
            if (outline != null)
                outline.enabled = false;

            Transform existingRail = host.Find("CyanRail");
            if (existingRail != null)
                existingRail.gameObject.SetActive(false);

            var glass = host.gameObject.GetComponent<Image>();
            if (glass == null)
                glass = host.gameObject.AddComponent<Image>();
            glass.color = new Color(0.02f, 0.03f, 0.05f, 0.55f);
            glass.raycastTarget = false;
        }

        private void ApplyFont(TextMeshProUGUI tmp)
        {
            if (fontAsset != null)
                tmp.font = fontAsset;
        }
    }
}
