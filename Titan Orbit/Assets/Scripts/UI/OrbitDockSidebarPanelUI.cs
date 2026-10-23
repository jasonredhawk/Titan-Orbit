using System;
using System.Text;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Simulation;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Narrow left Orbit Menu dock. The top of the panel stays put: a header for
    /// the docked planet (world name, level, and gem reservoir bar, matching the
    /// leaderboard), then GEM DEPOSITS, then the SHIPS / GEAR / CARDS buttons,
    /// then the live camera of the ship you are flying. Everything under that
    /// picture (family bonuses, weapons, toggles, and the LOADOUT list) lives
    /// in a scroll view, so the camera cannot scroll away.
    /// The WEAPONS plate names each owned bullet type and what that shot does.
    /// Hover still opens the full ordnance tooltip.
    /// The CARDS nav button follows <see cref="GameManager.ShowOrbitMenuCardsPanel"/> (default off)
    /// so the dock can show only SHIPS and GEAR until that panel is turned back on.
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
        /// Height of the "Your Ship" hero card (live camera + name + power bar).
        /// This card is pinned above the scroll, so LOADOUT uses whatever height
        /// is left under the picture.
        /// </summary>
        private const float CurrentShipNodeHeight = 220f;
        /// <summary>Family name line kept with the pinned camera.</summary>
        private const float FamilyNameHeight = 18f;
        /// <summary>
        /// World-name line above the reservoir track. Tall enough for the large
        /// name and level (the leaderboard uses a smaller caption).
        /// </summary>
        private const float PlanetHeaderTitleHeight = 30f;
        /// <summary>Gap between the name line and the gem track.</summary>
        private const float PlanetHeaderTitleGap = 2f;
        /// <summary>Gem track height. Matches the leaderboard reservoir bar so the count fits inside.</summary>
        private const float PlanetHeaderBarHeight = 18f;
        /// <summary>Top inset so the name is not flush with the screen edge.</summary>
        private const float PlanetHeaderPadTop = 8f;
        /// <summary>
        /// Ship → Bank plate only. The planet level and reservoir used to sit on
        /// the bottom of this plate; they now live in the header bar above it.
        /// </summary>
        private const float BankBalanceBannerHeight = 78f;
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
        /// <summary>
        /// Caption above the reservoir track: place name plus level
        /// ("Helios  LV.2"). Set by <see cref="ApplyPlanetHeaderTitle"/>.
        /// </summary>
        private TextMeshProUGUI _planetNameText;
        /// <summary>
        /// Filled horizontal image inside the reservoir track. Width is the
        /// fraction of this level's gem cap the planet currently holds.
        /// </summary>
        private Image _planetGemFill;
        /// <summary>Current / max gems, drawn inside the track.</summary>
        private TextMeshProUGUI _planetGemValueText;
        /// <summary>Catalog place name for the docked planet. Empty until an id arrives.</summary>
        private string _dockedPlanetName = string.Empty;
        /// <summary>Last planet id painted into the header. <see cref="int.MinValue"/> until the first name refresh.</summary>
        private int _shownPlanetId = int.MinValue;
        /// <summary>Last level written into the caption.</summary>
        private int _shownPlanetLevel = int.MinValue;
        /// <summary>Last rounded gem count written into the track.</summary>
        private int _shownPlanetGems = int.MinValue;
        /// <summary>Last gem cap written into the track.</summary>
        private int _shownPlanetMax = int.MinValue;
        /// <summary>True after the reservoir fill has been tinted for an owner.</summary>
        private bool _haveShownPlanetOwner;
        /// <summary>Owner whose color is on the fill. Ignored until <see cref="_haveShownPlanetOwner"/>.</summary>
        private TeamId _shownPlanetOwner;
        /// <summary>1×1 sprite so the track and fill are not invisible without a sliced panel sprite.</summary>
        private static Sprite s_WhiteSprite;
        private TextMeshProUGUI _shipGemsValueText;
        private TextMeshProUGUI _bankValueText;
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
        private Button _customizeHullButton;
        private TeamId _paintTeam = TeamId.TeamA;

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

        /// <summary>
        /// Creates the dock the first time the Orbit Menu opens: GEM DEPOSITS and the
        /// live ship camera stay fixed, and the list under the camera scrolls.
        /// Later calls only fill in controls that a script reload dropped.
        /// </summary>
        public void EnsureBuilt()
        {
            // --- Ensure setup ---
            if (_built)
            {
                // A script reload can drop the runtime header. Put it back above GEM DEPOSITS.
                if (_planetNameText == null || _planetGemFill == null)
                    CreatePlanetHeader();
                if (_autoDepositToggle == null)
                    CreateAutoDepositToggle(_contentRoot);
                if (_damageModeButton == null)
                    CreateHealingBulletsToggle(_contentRoot);
                if (_customizeHullButton == null)
                    CreateCustomizeHullButton(_contentRoot);
                DestroyChildIfPresent(_contentRoot, "PlayerAccentPicker");
                ApplyCardsNavVisibility();
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

            // --- Fixed top + scrolling body ---
            // [UNITY] A VerticalLayoutGroup on the dock gives the leftover height
            // to the scroll view. GEM DEPOSITS, the nav buttons, and the live
            // camera are siblings above that scroll, so they are not clipped or
            // moved when the player scrolls LOADOUT.
            var rootVlg = gameObject.AddComponent<VerticalLayoutGroup>();
            rootVlg.spacing = 0f;
            rootVlg.padding = new RectOffset(0, 0, 0, 0);
            rootVlg.childAlignment = TextAnchor.UpperCenter;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = true;
            rootVlg.childForceExpandHeight = false;

            BuildFixedDockHeader();

            var scrollGo = new GameObject("SidebarScroll");
            scrollGo.transform.SetParent(transform, false);
            var scrollRt = scrollGo.AddComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = Vector2.zero;
            scrollRt.offsetMax = Vector2.zero;
            var scrollLe = scrollGo.AddComponent<LayoutElement>();
            // flexibleHeight takes every pixel under the pinned camera.
            // preferredHeight -1 lets the layout group use minHeight as the
            // preferred size, then pour the surplus into this one child.
            scrollLe.flexibleHeight = 1f;
            scrollLe.minHeight = 48f;
            scrollLe.preferredHeight = -1f;
            scrollLe.flexibleWidth = 1f;
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

            // Family bonuses, weapons, toggles, and LOADOUT start under the
            // pinned camera. GEM DEPOSITS is built above the nav buttons.
            CreateFamilyStatsBlock(_contentRoot);
            CreateOrdnanceBlock(_contentRoot);

            CreateAutoDepositToggle(_contentRoot);
            CreateHealingBulletsToggle(_contentRoot);
            CreateCustomizeHullButton(_contentRoot);

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

        /// <summary>
        /// Builds the dock chrome that must stay on screen: the planet header
        /// (name, level, gem bar), the GEM DEPOSITS plate, the SHIPS / GEAR
        /// buttons, and the live ship camera.
        /// Called once from <see cref="EnsureBuilt"/>. These objects are direct
        /// children of the dock, above <c>SidebarScroll</c>, so the scroll view's
        /// mask cannot clip the picture and scrolling cannot move it.
        /// </summary>
        private void BuildFixedDockHeader()
        {
            // --- Planet header, then GEM DEPOSITS ---
            // [TITAN-ORBIT] Name, level, and the reservoir bar are the header.
            // Ship cargo and the bank sit on the plate directly under that bar.
            // Both used to live under the camera, inside the scroll, so a long
            // LOADOUT hid them.
            CreatePlanetHeader();
            const int gemPadH = 8;
            const int gemPadTop = 6;
            const int gemPadBottom = 4;
            var gemHost = new GameObject("GemDepositsHost");
            gemHost.transform.SetParent(transform, false);
            var gemLe = gemHost.AddComponent<LayoutElement>();
            gemLe.preferredHeight = BankBalanceBannerHeight + gemPadTop + gemPadBottom;
            gemLe.minHeight = gemLe.preferredHeight;
            gemLe.flexibleHeight = 0f;
            gemLe.flexibleWidth = 1f;
            var gemVlg = gemHost.AddComponent<VerticalLayoutGroup>();
            gemVlg.spacing = 0f;
            gemVlg.padding = new RectOffset(gemPadH, gemPadH, gemPadTop, gemPadBottom);
            gemVlg.childAlignment = TextAnchor.UpperCenter;
            gemVlg.childControlWidth = true;
            gemVlg.childControlHeight = true;
            gemVlg.childForceExpandWidth = true;
            gemVlg.childForceExpandHeight = true;
            CreateBankBalanceBanner(gemHost.transform);

            BuildNavStrip();

            // --- Live camera, pinned under the buttons ---
            // The family name stays with the picture. There is no "YOUR SHIP"
            // caption — the camera is the ship. Family bonuses and LOADOUT
            // are created later, inside the scroll.
            const float labelGap = 4f;
            const int shipPadH = 8;
            const int shipPadTop = 4;
            const int shipPadBottom = 6;
            float shipStack = FamilyNameHeight + labelGap + CurrentShipNodeHeight;
            var shipHostGo = new GameObject("PinnedShip");
            shipHostGo.transform.SetParent(transform, false);
            var shipHostLe = shipHostGo.AddComponent<LayoutElement>();
            shipHostLe.preferredHeight = shipStack + shipPadTop + shipPadBottom;
            shipHostLe.minHeight = shipHostLe.preferredHeight;
            shipHostLe.flexibleHeight = 0f;
            shipHostLe.flexibleWidth = 1f;
            var shipVlg = shipHostGo.AddComponent<VerticalLayoutGroup>();
            shipVlg.spacing = labelGap;
            shipVlg.padding = new RectOffset(shipPadH, shipPadH, shipPadTop, shipPadBottom);
            shipVlg.childAlignment = TextAnchor.UpperCenter;
            shipVlg.childControlWidth = true;
            shipVlg.childControlHeight = true;
            shipVlg.childForceExpandWidth = true;
            shipVlg.childForceExpandHeight = false;

            _familyNameText = CreateBodyLabel(shipHostGo.transform, "FamilyName", "FAMILY", FamilyNameHeight);
            _familyNameText.fontSize = 12f;
            _familyNameText.fontStyle = FontStyles.Bold;
            _familyNameText.characterSpacing = 1.5f;
            _familyNameText.color = new Color(0.62f, 0.78f, 0.95f, 0.95f);
            _currentShipHost = CreateStretchHost(shipHostGo.transform, "CurrentShipHost", CurrentShipNodeHeight);
            ApplyTopJustifiedHostLayout(_currentShipHost);
            ApplyYourShipHeroChrome(_currentShipHost);
        }

        /// <summary>
        /// Builds the SHIPS / GEAR / CARDS row between GEM DEPOSITS and the live camera.
        /// Called from <see cref="BuildFixedDockHeader"/> so the row is not inside the scroll.
        /// </summary>
        private void BuildNavStrip()
        {
            // --- SHIPS / GEAR row ---
            // The dock's VerticalLayoutGroup reads this LayoutElement for the row height.
            // Parenting under the dock already supplies a RectTransform; adding a second
            // one would fail and leave the row with no anchors.
            var navGo = new GameObject("NavStrip");
            navGo.transform.SetParent(transform, false);
            // [UNITY] Parenting to a RectTransform converts the child. If that has not
            // happened yet, add the rect once — never twice.
            var navRt = navGo.GetComponent<RectTransform>();
            if (navRt == null)
                navRt = navGo.AddComponent<RectTransform>();
            var navLe = navGo.AddComponent<LayoutElement>();
            navLe.preferredHeight = NavStripHeight;
            navLe.minHeight = NavStripHeight;
            navLe.flexibleHeight = 0f;
            navLe.flexibleWidth = 1f;
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
            // [TITAN-ORBIT] Default off: hide CARDS so the strip is SHIPS + GEAR. The button
            // stays in the hierarchy so GameManager can show it again without a rebuild.
            ApplyCardsNavVisibility();
        }

        /// <summary>
        /// Shows or hides the CARDS nav button from <see cref="GameManager.ShowOrbitMenuCardsPanel"/>.
        /// Off leaves SHIPS and GEAR sharing the strip. Called when the dock is built and again
        /// if the Inspector checkbox changes during Play Mode.
        /// </summary>
        public void ApplyCardsNavVisibility()
        {
            if (_navCardsBtn == null)
                return;

            // [TITAN-ORBIT] Missing GameManager matches the Inspector default: cards panel off.
            bool show = GameManager.IsShowOrbitMenuCardsPanelActive;
            if (_navCardsBtn.gameObject.activeSelf == show)
                return;

            // [UNITY] Deactivating a HorizontalLayoutGroup child drops it from the strip
            // so SHIPS and GEAR expand to fill the nav row.
            _navCardsBtn.gameObject.SetActive(show);
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
            ApplyCardsNavVisibility();
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
        /// Writes the uppercase family caption and the FAMILY BONUSES list.
        /// Home planets are exactly 1×, so the rail stays hidden there.
        /// Other planets list this match's 0.5–2.0 roll. The upgrade-tree wheel
        /// still shows the full board, including 1× spokes.
        /// </summary>
        public void RefreshFamilyIdentity(
            ShipFamilyDefinition family,
            int shipLevel = 1,
            int planetOrHullBankIndex = -1)
        {
            EnsureBuilt();
            if (_familyNameText != null)
                _familyNameText.text = FamilyStatHudCopy.FormatFamilyCaption(family);

            // Family Special Bonuses only. Bullet-bank vs-target lines live on the weapon glance.
            string familyLines = family != null
                ? FamilyStatHudCopy.FormatListedBonusesRichText(family.ResolveSpecialBonuses())
                : string.Empty;
            bool showStats = !string.IsNullOrEmpty(familyLines);
            if (_familyStatsBlock != null)
                _familyStatsBlock.SetActive(showStats);
            if (showStats && _familyStatsText != null)
            {
                _familyStatsText.text = familyLines;
                ApplyFamilyStatsBlockHeight();
            }

            string bankName = BulletBankHudCopy.FormatFamilyTypeName(family, planetOrHullBankIndex);
            bool listedOwned = TryFormatOwnedWeaponsGlance(shipLevel, out string ownedGlance, out string ownedTip);
            bool showWeapon = listedOwned || !string.IsNullOrEmpty(bankName);
            if (_ordnanceBlock != null)
                _ordnanceBlock.SetActive(showWeapon);
            if (showWeapon && _ordnanceText != null)
            {
                // Owned guns win once the ghost has hydrated. Before that, describe
                // the family's live bank so the plate is never just a type name.
                string body = listedOwned
                    ? ownedGlance
                    : BulletBankHudCopy.FormatDockWeaponDescription(
                        BulletBankProfileUtility.ResolveBankIndexForFamily(family, planetOrHullBankIndex),
                        shipLevel);
                if (string.IsNullOrEmpty(body))
                    body = bankName;
                if (_ordnanceText.text != body)
                {
                    _ordnanceText.text = body;
                    ApplyOrdnanceBlockHeight();
                }
            }
            if (_ordnanceTip != null)
            {
                _ordnanceTip.Caption = BulletBankHudCopy.WeaponTypeCaption;
                _ordnanceTip.Body = !showWeapon
                    ? string.Empty
                    : (listedOwned
                        ? ownedTip
                        : BulletBankHudCopy.BuildFamilyOrdnanceTooltip(family, shipLevel, planetOrHullBankIndex));
            }
        }

        /// <summary>
        /// Dark FAMILY BONUSES plate under Your Ship. Starts hidden; RefreshFamilyIdentity
        /// turns it on when the docked family has any ≠1 multiplier.
        /// </summary>
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
            cap.text = "FAMILY BONUSES";
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
            _familyStatsText.richText = true;
            _familyStatsText.enableWordWrapping = true;
            _familyStatsText.raycastTarget = false;
            ApplyFont(_familyStatsText);
            _familyStatsBlock.SetActive(false);
        }

        /// <summary>
        /// Grows the sidebar FAMILY BONUSES plate to fit every ≠1 line.
        /// Cosmic Shark has four live fields; Hyper Falcon has six — a fixed
        /// 36px rail used to clip the rest.
        /// </summary>
        void ApplyFamilyStatsBlockHeight()
        {
            if (_familyStatsBlock == null || _familyStatsText == null)
                return;

            var le = _familyStatsBlock.GetComponent<LayoutElement>();
            if (le == null)
                return;

            // Caption + padding + one 12px line per bonus. Clamp so a huge
            // designer stack cannot shove LOADOUT off the dock.
            // [STANDARD] Count newlines instead of Split — dock refresh is rare,
            // but we still skip the string[] alloc.
            string body = _familyStatsText.text;
            int lines = 1;
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == '\n')
                    lines++;
            }

            float h = 22f + lines * 13f;
            le.minHeight = 36f;
            le.preferredHeight = Mathf.Clamp(h, 36f, 160f);
        }

        static readonly VisibleBankRow[] s_OwnedWeaponRows = new VisibleBankRow[16];

        /// <summary>
        /// Builds the WEAPONS plate for guns the local ship can actually fire:
        /// the hull's original type, plus each purchased B-key type.
        /// Each block is the type name, a sentence per ability, and the combat
        /// modifiers. The hull tag used to sit after the name; the ship card
        /// above this plate already identifies the hull.
        /// </summary>
        /// <param name="shipLevel">Hull level for Extra-Level ability numbers.</param>
        /// <param name="glance">Rich-text body, or empty when nothing is owned.</param>
        /// <param name="tip">Full hover tooltip, one WEAPONS section per owned type.</param>
        /// <returns>False when the ghost has not hydrated yet, so the caller can
        /// fall back to the family's live bank.</returns>
        static bool TryFormatOwnedWeaponsGlance(int shipLevel, out string glance, out string tip)
        {
            glance = string.Empty;
            tip = string.Empty;
            var world = EcsGameBridge.GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship) || ship == Entity.Null)
                return false;

            // --- Collect ---
            // Reused buffer. CollectVisibleBankRows writes the hull gun first, then purchases.
            int count = BulletBankOwnership.CollectVisibleBankRows(
                world.EntityManager, ship, s_OwnedWeaponRows);
            if (count <= 0)
                return false;

            var body = new StringBuilder(192);
            var tipSb = new StringBuilder(256);
            int listed = 0;
            for (int i = 0; i < count; i++)
            {
                VisibleBankRow row = s_OwnedWeaponRows[i];
                if (!row.IsOwned)
                    continue;

                // --- Body ---
                // Name + ability sentences + ≠1 fire-power / speed / rate / range / ram.
                string block = BulletBankHudCopy.FormatDockWeaponDescription(row.BankIndex, shipLevel);
                if (string.IsNullOrEmpty(block))
                    continue;
                if (body.Length > 0)
                    body.Append("\n\n");
                body.Append(block);

                // Hover keeps the long breakdown (energy drain, per-extra scaling).
                if (tipSb.Length > 0)
                    tipSb.Append("\n\n");
                tipSb.Append(BulletBankHudCopy.BuildFamilyOrdnanceTooltip(null, shipLevel, row.BankIndex));
                listed++;
            }

            if (listed <= 0)
                return false;
            glance = body.ToString();
            tip = tipSb.ToString();
            return true;
        }

        /// <summary>
        /// Grows the WEAPONS plate to the description. A fixed 72px rail used to
        /// clip everything after the type name. Called only when the body text changes.
        /// </summary>
        void ApplyOrdnanceBlockHeight()
        {
            if (_ordnanceBlock == null || _ordnanceText == null)
                return;

            var le = _ordnanceBlock.GetComponent<LayoutElement>();
            if (le == null)
                return;

            // --- Measure ---
            // [UNITY] preferredHeight needs a width. The scroll layout may not have
            // run yet, so we stamp the dock's inner width before asking TMP.
            float innerW = Mathf.Max(120f, PanelWidth - 48f);
            _ordnanceText.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, innerW);
            _ordnanceText.ForceMeshUpdate();
            float bodyH = Mathf.Max(14f, _ordnanceText.preferredHeight);

            var bodyLe = _ordnanceText.GetComponent<LayoutElement>();
            if (bodyLe == null)
                bodyLe = _ordnanceText.gameObject.AddComponent<LayoutElement>();
            bodyLe.preferredHeight = bodyH;
            bodyLe.minHeight = bodyH;
            bodyLe.flexibleHeight = 0f;

            // Caption (~16) + padding (8) + gap (1) + body. Cap so a long owned
            // list scrolls with the dock instead of covering the whole menu.
            float h = 25f + bodyH;
            le.minHeight = 44f;
            le.preferredHeight = Mathf.Clamp(h, 44f, 280f);
        }

        void CreateOrdnanceBlock(Transform parent)
        {
            _ordnanceBlock = new GameObject("Weapons");
            _ordnanceBlock.transform.SetParent(parent, false);
            var le = _ordnanceBlock.AddComponent<LayoutElement>();
            le.preferredHeight = 72f;
            le.minHeight = 44f;
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
            _ordnanceText.richText = true;
            _ordnanceText.enableWordWrapping = true;
            // Plate height follows the copy. Overflow keeps a wrapped last line
            // from turning into "..." when the measure is a pixel short.
            _ordnanceText.overflowMode = TextOverflowModes.Overflow;
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

            // Node already built — still re-apply hero layout so the name overlay / hide-You edits stick after code changes.
            if (_currentShipNode != null)
            {
                _currentShipNode.ApplySidebarHeroPreviewLayout(innerW, CurrentShipNodeHeight, trackW);
                _currentShipNode.ConfigureLayout(true);
                // Picture only. The price chip on the upgrade-tree card is the buy control.
                _currentShipNode.SetSidebarHeroCardClickHandler();
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
            // The picture is not a buy button. Same-slot swaps use the tree card's price chip.
            view.SetSidebarHeroCardClickHandler();
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

        /// <summary>Stores the docked ship's team so the hull studio locks Color1 correctly.</summary>
        public void RefreshAccentPicker(TeamId team)
        {
            EnsureBuilt();
            _paintTeam = team == TeamId.None ? TeamId.TeamA : team;
        }

        public void RefreshAutoDepositToggle(bool enabled)
        {
            EnsureBuilt();
            SetAutoDepositToggleVisual(enabled, notify: false);
        }

        /// <summary>
        /// Paints the docked planet's world name (and retints the reservoir bar)
        /// in the header above GEM DEPOSITS. Level and gem count are painted by
        /// <see cref="RefreshDepositStatus"/> on the same header.
        /// Called whenever the left dock refreshes (sidebar paint and deposit beats).
        /// The string is the same catalog the world label and minimap use: a designer
        /// <c>planetName</c> override wins, otherwise <see cref="PlanetDisplayNames"/>
        /// picks a stable name from the planet id. Skips the text write when the
        /// name has not changed, because assigning <c>TextMeshProUGUI.text</c> allocates.
        /// </summary>
        /// <param name="planetId">
        /// Ghosted <c>PlanetState.PlanetId</c> for the moon you are orbiting.
        /// Zero or negative clears the name.
        /// </param>
        public void RefreshOrbitPlanetName(int planetId)
        {
            // --- Make sure the header exists ---
            EnsureBuilt();
            if (_planetNameText == null)
                return;

            // --- Resolve, then dirty-check ---
            // [TITAN-ORBIT] Names are not ghosted. Every client derives the same
            // string from the planet id, so this caption matches the world stamp.
            string raw = ResolveDockedPlanetName(planetId);
            bool changed = planetId != _shownPlanetId
                || !string.Equals(raw, _dockedPlanetName, StringComparison.Ordinal);
            _shownPlanetId = planetId;
            if (!changed)
            {
                // The place name stayed put, but capture can still recolor the bar.
                ApplyPlanetGemFillColor(planetId);
                return;
            }

            _dockedPlanetName = raw ?? string.Empty;
            ApplyPlanetHeaderTitle();
            ApplyPlanetGemFillColor(planetId);
        }

        /// <summary>
        /// Player-facing world name for the docked planet. Live <c>PlanetState</c>
        /// supplies the home flag and family index so a designer override wins.
        /// If that body is not in the ghost set yet, the name is still deterministic
        /// from the id (homes under 100, neutrals from 100 up).
        /// </summary>
        /// <param name="planetId">Stable planet id. Zero or negative returns empty.</param>
        /// <returns>Display name, or empty when there is no planet to name.</returns>
        static string ResolveDockedPlanetName(int planetId)
        {
            // --- No moon yet ---
            if (planetId <= 0)
                return string.Empty;

            // --- Home vs neutral when the ghost is missing ---
            // Homes use team ids 1–5. Neutrals start at 100. That split matches
            // PlanetDisplayNames even before PlanetState replicates.
            bool isHome = planetId < PlanetDisplayNames.NeutralPlanetIdBase;
            int familyIndex = -1;
            if (EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
            {
                isHome = state.IsHomePlanet;
                familyIndex = state.ShipFamilyConfigIndex;
            }

            // --- Catalog (cached Resources asset; no per-frame load) ---
            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            if (config != null)
                return config.GetPlanetDisplayName(planetId, isHome, familyIndex);

            return PlanetDisplayNames.Resolve(planetId, isHome);
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

            // Level and reservoir live in the header bar, not on this plate.
            PaintDockedPlanetGemBar(planetGems, planetLevel);
        }

        /// <summary>
        /// Caption above the track: place name, then the level in the same ice
        /// color the leaderboard uses. A dash until a planet name has arrived.
        /// </summary>
        void ApplyPlanetHeaderTitle()
        {
            if (_planetNameText == null)
                return;

            // --- Unknown moon ---
            string title;
            if (string.IsNullOrEmpty(_dockedPlanetName))
                title = "—";
            else
            {
                // Level arrives from the deposit refresh. Until then, show LV.1
                // rather than a bare name that jumps when the first gem paint lands.
                // The level uses the same size as the place name so both read as the header.
                int level = _shownPlanetLevel == int.MinValue ? 1 : Mathf.Max(1, _shownPlanetLevel);
                title = _dockedPlanetName + "  <color=#9EB6D8>LV." + level + "</color>";
            }

            if (_planetNameText.text != title)
                _planetNameText.text = title;
        }

        /// <summary>
        /// Fills the header track the way the leaderboard reservoir bar does:
        /// team-colored fill from the left, and "current / cap" inside the track.
        /// Also writes the level into the caption when it changes.
        /// Skips TMP writes when the numbers have not changed.
        /// </summary>
        /// <param name="planetGems">Gems currently stored on the docked planet.</param>
        /// <param name="planetLevel">Planet level (at least 1). Cap doubles each level from 100.</param>
        void PaintDockedPlanetGemBar(float planetGems, int planetLevel)
        {
            if (_planetGemFill == null)
                return;

            // --- Cap and fill ---
            // [TITAN-ORBIT] Same math as the leaderboard: level 1 holds 100, each
            // level doubles. The filled image clips that fraction from the left.
            planetLevel = Mathf.Max(1, planetLevel);
            int cap = Mathf.Max(0, Mathf.RoundToInt(PlanetEconomyMath.GetMaxGemsForLevel(planetLevel)));
            int shown = Mathf.Clamp(Mathf.RoundToInt(planetGems), 0, cap);
            _planetGemFill.fillAmount = cap > 0 ? shown / (float)cap : 0f;

            if (_planetGemValueText != null &&
                (shown != _shownPlanetGems || cap != _shownPlanetMax))
            {
                _shownPlanetGems = shown;
                _shownPlanetMax = cap;
                _planetGemValueText.text = shown + " / " + cap;
            }

            if (planetLevel != _shownPlanetLevel)
            {
                _shownPlanetLevel = planetLevel;
                ApplyPlanetHeaderTitle();
            }

            ApplyPlanetGemFillColor(_shownPlanetId);
        }

        /// <summary>
        /// Tints the reservoir fill with the planet owner's team color.
        /// Unowned worlds use the same ice as the leaderboard so the track
        /// still reads as a fill. Skips the write when the owner has not changed.
        /// </summary>
        /// <param name="planetId">Docked planet. Zero or negative stays ice.</param>
        void ApplyPlanetGemFillColor(int planetId)
        {
            if (_planetGemFill == null || planetId == int.MinValue)
                return;

            // --- Owner from the ghost, then a stable tint ---
            TeamId owner = TeamId.None;
            if (planetId > 0 &&
                EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                owner = state.Ownership;

            if (_haveShownPlanetOwner && owner == _shownPlanetOwner)
                return;

            _haveShownPlanetOwner = true;
            _shownPlanetOwner = owner;
            Color color = owner == TeamId.None
                ? new Color(0.45f, 0.62f, 0.82f, 0.92f)
                : owner.ToColor();
            color.a = 0.92f;
            _planetGemFill.color = color;
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

        /// <summary>
        /// Builds the planet header at the top of the left dock, above GEM DEPOSITS.
        /// The caption is the world name and level. Under it is a reservoir track
        /// styled like the leaderboard gem bar: dark plate, team-colored fill from
        /// the left, and the current / cap count inside the track.
        /// Called once from <see cref="BuildFixedDockHeader"/>, and again from
        /// <see cref="EnsureBuilt"/> if a script reload dropped the widgets.
        /// Text stays as a dash until <see cref="RefreshOrbitPlanetName"/> runs.
        /// </summary>
        private void CreatePlanetHeader()
        {
            // --- Replace a previous name-only row ---
            // [UNITY] Destroy is end-of-frame. The new host uses a different name
            // so this frame still has one live header. SetAsFirstSibling keeps it
            // above GEM DEPOSITS when the rest of the dock already exists.
            DestroyChildIfPresent(transform, "PlanetNameHost");
            DestroyChildIfPresent(transform, "PlanetHeaderHost");

            const int padH = 8;
            const int padBottom = 2;
            float stack = PlanetHeaderTitleHeight + PlanetHeaderTitleGap + PlanetHeaderBarHeight;
            var host = new GameObject("PlanetHeaderHost");
            host.transform.SetParent(transform, false);
            host.transform.SetAsFirstSibling();
            var hostLe = host.AddComponent<LayoutElement>();
            hostLe.preferredHeight = stack + PlanetHeaderPadTop + padBottom;
            hostLe.minHeight = hostLe.preferredHeight;
            hostLe.flexibleHeight = 0f;
            hostLe.flexibleWidth = 1f;
            var hostVlg = host.AddComponent<VerticalLayoutGroup>();
            hostVlg.spacing = PlanetHeaderTitleGap;
            hostVlg.padding = new RectOffset(padH, padH, (int)PlanetHeaderPadTop, padBottom);
            hostVlg.childAlignment = TextAnchor.UpperCenter;
            hostVlg.childControlWidth = true;
            hostVlg.childControlHeight = true;
            hostVlg.childForceExpandWidth = true;
            hostVlg.childForceExpandHeight = false;

            // --- Name + level (rich text, same ice "LV." as the leaderboard) ---
            // Larger than the leaderboard caption: this line is the dock header.
            _planetNameText = CreateBodyLabel(host.transform, "PlanetName", "—", PlanetHeaderTitleHeight);
            _planetNameText.fontSize = 22f;
            _planetNameText.fontStyle = FontStyles.Bold;
            _planetNameText.alignment = TextAlignmentOptions.Center;
            _planetNameText.color = new Color(0.92f, 0.95f, 1f, 1f);
            _planetNameText.richText = true;
            _planetNameText.enableWordWrapping = false;
            _planetNameText.overflowMode = TextOverflowModes.Ellipsis;

            // --- Reservoir track ---
            // [UNITY] Filled horizontal clips the owner tint from the left as the
            // planet fills toward this level's cap. The count sits on top of both
            // the empty plate and the bright fill, so it gets a dark outline.
            var barGo = new GameObject("PlanetGemBar");
            barGo.transform.SetParent(host.transform, false);
            var barLe = barGo.AddComponent<LayoutElement>();
            barLe.preferredHeight = PlanetHeaderBarHeight;
            barLe.minHeight = PlanetHeaderBarHeight;
            barLe.flexibleHeight = 0f;
            barLe.flexibleWidth = 1f;
            var track = barGo.AddComponent<Image>();
            track.sprite = GetWhiteSprite();
            track.color = new Color(0.04f, 0.055f, 0.09f, 0.95f);
            track.raycastTarget = false;

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(barGo.transform, false);
            StretchFull(fillGo.GetComponent<RectTransform>());
            _planetGemFill = fillGo.AddComponent<Image>();
            _planetGemFill.sprite = GetWhiteSprite();
            _planetGemFill.type = Image.Type.Filled;
            _planetGemFill.fillMethod = Image.FillMethod.Horizontal;
            _planetGemFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _planetGemFill.fillAmount = 0f;
            _planetGemFill.color = new Color(0.45f, 0.62f, 0.82f, 0.92f);
            _planetGemFill.raycastTarget = false;

            var valueGo = new GameObject("Value", typeof(RectTransform));
            valueGo.transform.SetParent(barGo.transform, false);
            StretchFull(valueGo.GetComponent<RectTransform>());
            _planetGemValueText = valueGo.AddComponent<TextMeshProUGUI>();
            _planetGemValueText.text = "0 / 0";
            _planetGemValueText.fontSize = 12f;
            _planetGemValueText.fontStyle = FontStyles.Bold;
            _planetGemValueText.alignment = TextAlignmentOptions.Center;
            _planetGemValueText.color = new Color(0.94f, 0.96f, 1f, 1f);
            _planetGemValueText.outlineWidth = 0.18f;
            _planetGemValueText.outlineColor = new Color32(0, 0, 0, 220);
            _planetGemValueText.raycastTarget = false;
            ApplyFont(_planetGemValueText);

            // A rebuild must repaint even if the last numbers are still in the fields.
            // Clearing the id forces the next name refresh to rewrite the new caption.
            _shownPlanetId = int.MinValue;
            _shownPlanetGems = int.MinValue;
            _shownPlanetMax = int.MinValue;
            _shownPlanetLevel = int.MinValue;
            _haveShownPlanetOwner = false;
        }

        /// <summary>
        /// Stretches a widget to its parent. Used by the gem track's fill and count,
        /// which are not in a layout group — the track's own layout element sets the size.
        /// </summary>
        static void StretchFull(RectTransform rt)
        {
            if (rt == null)
                return;

            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// 1×1 white sprite for the reservoir track and fill. A sliced panel sprite
        /// would not clip as a horizontal fill. Created once.
        /// </summary>
        static Sprite GetWhiteSprite()
        {
            if (s_WhiteSprite != null)
                return s_WhiteSprite;

            Texture2D texture = Texture2D.whiteTexture;
            s_WhiteSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            s_WhiteSprite.name = "OrbitDockWhite";
            return s_WhiteSprite;
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

            // Planet level and gem count used to be a third line here
            // ("Planet L2 · 0/200"). That readout is the header bar above this plate.
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

        void CreateCustomizeHullButton(Transform parent)
        {
            if (parent == null)
                return;

            DestroyChildIfPresent(parent, "PlayerAccentPicker");

            Transform existing = parent.Find("CustomizeHullButton");
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject("CustomizeHullButton", typeof(RectTransform), typeof(Image), typeof(Button));
            if (existing == null)
                go.transform.SetParent(parent, false);

            var le = go.GetComponent<LayoutElement>();
            if (le == null)
                le = go.AddComponent<LayoutElement>();
            le.minHeight = AutoDepositToggleHeight;
            le.preferredHeight = AutoDepositToggleHeight;
            le.flexibleHeight = 0f;
            le.flexibleWidth = 1f;

            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.14f, 0.18f, 0.28f, 0.95f);
            if (buttonSprite != null)
            {
                bg.sprite = buttonSprite;
                bg.type = Image.Type.Sliced;
            }

            _customizeHullButton = go.GetComponent<Button>();
            _customizeHullButton.targetGraphic = bg;
            _customizeHullButton.onClick.RemoveAllListeners();
            _customizeHullButton.onClick.AddListener(OpenCustomizeHull);

            Transform labelTf = go.transform.Find("Label");
            GameObject labelGo = labelTf != null
                ? labelTf.gameObject
                : new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            if (labelTf == null)
                labelGo.transform.SetParent(go.transform, false);
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
            var label = labelGo.GetComponent<TextMeshProUGUI>();
            label.text = "CUSTOMIZE HULL";
            label.fontSize = 12f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(0.88f, 0.92f, 1f, 0.98f);
            label.raycastTarget = false;
            ApplyFont(label);
        }

        void OpenCustomizeHull()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            Transform root = canvas != null ? canvas.rootCanvas.transform : transform;
            ShipCustomizeScreen.Open(root, _paintTeam);
        }

        static void DestroyChildIfPresent(Transform parent, string childName)
        {
            if (parent == null)
                return;
            Transform existing = parent.Find(childName);
            if (existing != null)
                Destroy(existing.gameObject);
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
