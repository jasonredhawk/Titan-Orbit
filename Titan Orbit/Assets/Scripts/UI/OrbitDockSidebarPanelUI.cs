using System;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Narrow left dock panel: navigation between Upgrades and Store, current ship, bank balance, and equipped loadout.
    /// </summary>
    public class OrbitDockSidebarPanelUI : MonoBehaviour
    {
        public enum NavTarget
        {
            Upgrades,
            Store
        }

        public const float PanelWidth = 252f;

        private const float NavStripHeight = 44f;
        private const float CurrentShipNodeHeight = 92f;

        [SerializeField] private Sprite panelBackgroundSprite;
        [SerializeField] private Sprite buttonSprite;
        [SerializeField] private TMP_FontAsset fontAsset;

        private RectTransform _contentRoot;
        private RectTransform _currentShipHost;
        private RectTransform _loadoutHost;
        private TextMeshProUGUI _bankText;
        private ShipUpgradeTreeNodeUI _currentShipNode;
        private Button _navUpgradesBtn;
        private Button _navStoreBtn;
        private Image _navUpgradesBg;
        private Image _navStoreBg;
        private OrbitStationUI _station;
        private Action<NavTarget> _onNavSelected;
        private NavTarget _activeNav = NavTarget.Upgrades;
        private bool _built;

        public RectTransform LoadoutHost => _loadoutHost;
        public ShipUpgradeTreeNodeUI CurrentShipNode => _currentShipNode;

        public void ConfigureVisuals(Sprite panelBg, Sprite btnSprite, TMP_FontAsset font)
        {
            panelBackgroundSprite = panelBg;
            buttonSprite = btnSprite;
            fontAsset = font;
        }

        public void BindStation(OrbitStationUI station)
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
            if (_built)
                return;

            _built = true;

            var rootRt = transform as RectTransform;
            if (rootRt == null)
                rootRt = gameObject.AddComponent<RectTransform>();

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

            _bankText = CreateBodyLabel(_contentRoot, "Bank", "Bank balance: 0 gems", 22f);

            CreateSectionHeader(_contentRoot, "Ship Loadout", 24f);
            var loadoutHint = CreateBodyLabel(_contentRoot, "LoadoutHint",
                "Tap ✕ on a card to remove it.", 22f);
            loadoutHint.fontSize = 11f;
            loadoutHint.color = new Color(0.68f, 0.74f, 0.86f, 0.95f);
            _loadoutHost = CreateStretchHost(_contentRoot, "LoadoutHost", 80f);
            var loadoutLe = _loadoutHost.GetComponent<LayoutElement>();
            loadoutLe.minHeight = 64f;
            loadoutLe.preferredHeight = 80f;
            loadoutLe.flexibleHeight = 0f;

            ApplyNavVisuals();
        }

        private void BuildNavStrip()
        {
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
            EnsureBuilt();
            if (_currentShipNode != null || nodePrefab == null || _currentShipHost == null)
                return;

            float innerW = PanelWidth - 32f;
            float trackW = Mathf.Max(48f, innerW - 56f);
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

            view.ApplySidebarPanelLayout(innerW, CurrentShipNodeHeight, trackW);
            view.ConfigureLayout(true);
            view.EnsureStableButtonRendering();
            view.SetPriceClickHandler(() =>
            {
                if (_station != null)
                    _station.OnCurrentShipDisplayNodeClicked();
            });
            _currentShipNode = view;
        }

        public void RefreshBank(float contributedGems)
        {
            EnsureBuilt();
            if (_bankText != null)
                _bankText.text = $"Bank balance: {contributedGems:F0} gems";
        }

        public void RefreshCurrentShip(Action<ShipUpgradeTreeNodeUI, float> populateNode, float maxPower)
        {
            if (_currentShipNode == null || populateNode == null)
                return;
            populateNode(_currentShipNode, maxPower);
        }

        private void CreateSectionHeader(Transform parent, string text, float height)
        {
            var go = new GameObject("Header_" + text.Replace(" ", ""));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height - 4f;
            le.flexibleHeight = 0f;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 15f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.color = new Color(0.88f, 0.92f, 1f, 1f);
            tmp.raycastTarget = false;
            ApplyFont(tmp);
        }

        private TextMeshProUGUI CreateBodyLabel(Transform parent, string name, string text, float height)
        {
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
