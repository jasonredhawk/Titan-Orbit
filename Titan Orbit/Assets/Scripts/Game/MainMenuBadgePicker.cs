using TitanOrbit.Data;
using TitanOrbit.ECS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Main Menu profile-badge chip plus a cancelable full-grid overlay.
    /// Built at runtime by <see cref="MainMenuPresenter"/> — client presentation only.
    /// </summary>
    public sealed class MainMenuBadgePicker : MonoBehaviour
    {
        public const string RootObjectName = "PlayerBadgePicker";
        public const string OverlayObjectName = "PlayerBadgeOverlay";

        const int OverlaySortingOrder = 520;
        const int GridColumns = 7;
        const float TileSize = 72f;
        const float TileSpacing = 8f;

        static readonly Color RingSelected = new Color(1f, 0.82f, 0.35f, 0.95f);
        static readonly Color EmptySlot = new Color(0.18f, 0.24f, 0.32f, 0.85f);
        static readonly Color CaptionColor = new Color(0.72f, 0.84f, 0.96f, 0.92f);
        static readonly Color PanelFill = new Color(0.04f, 0.07f, 0.12f, 0.94f);

        Image _chipImage;
        Image _chipRing;
        TextMeshProUGUI _emptyMark;
        TextMeshProUGUI _caption;
        RectTransform _overlayRoot;
        bool _gridBuilt;

        /// <summary>Wires chip children created by <see cref="MainMenuPresenter"/>.</summary>
        public void Configure(
            Image chipImage,
            Image chipRing,
            TextMeshProUGUI emptyMark,
            TextMeshProUGUI caption)
        {
            _chipImage = chipImage;
            _chipRing = chipRing;
            _emptyMark = emptyMark;
            _caption = caption;
            RefreshChip();
        }

        /// <summary>Repaints the collapsed chip from <see cref="LocalPlayerBadge"/>.</summary>
        public void RefreshChip()
        {
            int badgeId = LocalPlayerBadge.Get();
            Sprite sprite = PlayerBadgeCatalog.FindSprite(badgeId);
            bool hasSprite = sprite != null;

            if (_chipImage != null)
            {
                _chipImage.sprite = sprite;
                _chipImage.color = hasSprite ? Color.white : EmptySlot;
                _chipImage.preserveAspect = true;
            }

            if (_emptyMark != null)
                _emptyMark.gameObject.SetActive(!hasSprite);

            // Click target only — no plate behind the circular sprite.
            if (_chipRing != null)
                _chipRing.color = Color.clear;

            Transform clearTf = transform.Find("Clear");
            if (clearTf != null)
                clearTf.gameObject.SetActive(hasSprite);

            if (_caption != null)
                _caption.text = hasSprite ? "Change badge" : "No badge";
        }

        /// <summary>Saves no emblem and closes the overlay if it is open.</summary>
        public void ClearBadge()
        {
            SelectBadge(PlayerBadgeIdUtil.None);
        }

        /// <summary>Opens the full-grid overlay (chip click).</summary>
        public void OpenOverlay()
        {
            EnsureOverlay();
            if (_overlayRoot == null)
                return;

            EnsureGrid();
            HighlightCurrent();
            _overlayRoot.gameObject.SetActive(true);
        }

        /// <summary>Closes the overlay without changing the saved badge.</summary>
        public void CancelOverlay()
        {
            if (_overlayRoot != null)
                _overlayRoot.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (_overlayRoot != null)
                Destroy(_overlayRoot.gameObject);
        }

        void EnsureOverlay()
        {
            if (_overlayRoot != null)
                return;

            var canvas = GetComponentInParent<Canvas>();
            Transform canvasTf = canvas != null ? canvas.transform : transform.root;

            var existing = canvasTf.Find(OverlayObjectName);
            if (existing != null)
            {
                _overlayRoot = existing.GetComponent<RectTransform>();
                EnsureFooterButtons();
                return;
            }

            var overlayGo = new GameObject(
                OverlayObjectName,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster));
            overlayGo.layer = gameObject.layer;
            overlayGo.transform.SetParent(canvasTf, false);

            _overlayRoot = overlayGo.GetComponent<RectTransform>();
            StretchFull(_overlayRoot);

            var overlayCanvas = overlayGo.GetComponent<Canvas>();
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = OverlaySortingOrder;

            // --- Dim backdrop (click = cancel) ---
            var backdropGo = CreateUi("Backdrop", _overlayRoot, typeof(Image), typeof(Button));
            StretchFull(backdropGo.GetComponent<RectTransform>());
            var backdropImage = backdropGo.GetComponent<Image>();
            backdropImage.color = new Color(0.02f, 0.04f, 0.08f, 0.72f);
            backdropImage.raycastTarget = true;
            var backdropBtn = backdropGo.GetComponent<Button>();
            backdropBtn.transition = Selectable.Transition.None;
            backdropBtn.onClick.AddListener(CancelOverlay);

            // --- Centered glass panel ---
            var panelGo = CreateUi("Panel", _overlayRoot, typeof(Image));
            var panelRt = panelGo.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(740f, 640f);
            panelRt.anchoredPosition = Vector2.zero;
            var panelImage = panelGo.GetComponent<Image>();
            panelImage.color = PanelFill;
            panelImage.raycastTarget = true;

            var title = CreateTmp(panelGo.transform, "Title", "Choose a badge", 26f, FontStyles.Bold);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.sizeDelta = new Vector2(-32f, 44f);
            titleRt.anchoredPosition = new Vector2(0f, -16f);
            title.alignment = TextAlignmentOptions.Center;
            title.color = CaptionColor;

            // --- Scrollable grid ---
            var scrollGo = CreateUi("Scroll", panelGo.transform, typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = new Vector2(0f, 0f);
            scrollRt.anchorMax = new Vector2(1f, 1f);
            scrollRt.offsetMin = new Vector2(20f, 72f);
            scrollRt.offsetMax = new Vector2(-20f, -68f);
            var scrollBg = scrollGo.GetComponent<Image>();
            scrollBg.color = new Color(0f, 0f, 0f, 0.28f);
            scrollBg.raycastTarget = true;

            var viewportGo = CreateUi("Viewport", scrollGo.transform, typeof(RectMask2D));
            var viewportRt = viewportGo.GetComponent<RectTransform>();
            StretchFull(viewportRt);

            var contentGo = CreateUi(
                "Content",
                viewportGo.transform,
                typeof(GridLayoutGroup),
                typeof(ContentSizeFitter));
            var contentRt = contentGo.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, 0f);

            var grid = contentGo.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(TileSize, TileSize);
            grid.spacing = new Vector2(TileSpacing, TileSpacing);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = GridColumns;
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.padding = new RectOffset(8, 8, 8, 8);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;

            var fitter = contentGo.GetComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.viewport = viewportRt;
            scroll.content = contentRt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;

            EnsureFooterButtons();
        }

        /// <summary>
        /// Always-visible Off / Cancel row so clearing a badge does not depend on finding
        /// the first grid cell among hundreds of sprites.
        /// </summary>
        void EnsureFooterButtons()
        {
            if (_overlayRoot == null)
                return;

            Transform panel = _overlayRoot.Find("Panel");
            if (panel == null)
                return;

            const float buttonW = 200f;
            const float buttonH = 44f;
            const float buttonY = 14f;

            Transform noneTf = panel.Find("NoBadge");
            GameObject noneGo = noneTf != null
                ? noneTf.gameObject
                : CreateUi("NoBadge", panel, typeof(Image), typeof(Button));
            var noneRt = noneGo.GetComponent<RectTransform>();
            noneRt.anchorMin = new Vector2(0.5f, 0f);
            noneRt.anchorMax = new Vector2(0.5f, 0f);
            noneRt.pivot = new Vector2(0.5f, 0f);
            noneRt.sizeDelta = new Vector2(buttonW, buttonH);
            noneRt.anchoredPosition = new Vector2(-118f, buttonY);
            var noneBtn = noneGo.GetComponent<Button>();
            noneBtn.onClick.RemoveAllListeners();
            noneBtn.onClick.AddListener(ClearBadge);
            MainMenuPresenter.StyleGameObjectAsMenuButton(noneGo, "No badge", buttonH, buttonW);

            Transform cancelTf = panel.Find("Cancel");
            GameObject cancelGo = cancelTf != null
                ? cancelTf.gameObject
                : CreateUi("Cancel", panel, typeof(Image), typeof(Button));
            var cancelRt = cancelGo.GetComponent<RectTransform>();
            cancelRt.anchorMin = new Vector2(0.5f, 0f);
            cancelRt.anchorMax = new Vector2(0.5f, 0f);
            cancelRt.pivot = new Vector2(0.5f, 0f);
            cancelRt.sizeDelta = new Vector2(buttonW, buttonH);
            cancelRt.anchoredPosition = new Vector2(118f, buttonY);
            var cancelBtn = cancelGo.GetComponent<Button>();
            cancelBtn.onClick.RemoveAllListeners();
            cancelBtn.onClick.AddListener(CancelOverlay);
            MainMenuPresenter.StyleGameObjectAsMenuButton(cancelGo, "Cancel", buttonH, buttonW);
        }

        void EnsureGrid()
        {
            if (_overlayRoot == null)
                return;

            Transform content = _overlayRoot.Find("Panel/Scroll/Viewport/Content");
            if (content == null)
                return;

            Transform noneTile = content.Find("Badge_" + PlayerBadgeIdUtil.None);
            if (noneTile == null)
            {
                CreateTile(content, PlayerBadgeIdUtil.None, null);
                noneTile = content.Find("Badge_" + PlayerBadgeIdUtil.None);
            }

            if (noneTile != null)
                noneTile.SetAsFirstSibling();

            if (_gridBuilt || content.childCount > 1)
            {
                _gridBuilt = true;
                return;
            }

            PlayerBadgeCatalog catalog = PlayerBadgeCatalog.LoadDefault();
            if (catalog != null && catalog.entries != null)
            {
                for (int i = 0; i < catalog.entries.Length; i++)
                {
                    PlayerBadgeCatalog.Entry entry = catalog.entries[i];
                    if (entry.badgeId <= 0 || entry.sprite == null)
                        continue;
                    CreateTile(content, entry.badgeId, entry.sprite);
                }
            }

            _gridBuilt = true;
        }

        void CreateTile(Transform parent, int badgeId, Sprite sprite)
        {
            var go = CreateUi("Badge_" + badgeId, parent, typeof(Image), typeof(Button));
            var backing = go.GetComponent<Image>();
            backing.color = EmptySlot;
            backing.raycastTarget = true;

            var iconGo = CreateUi("Icon", go.transform, typeof(Image));
            var iconRt = iconGo.GetComponent<RectTransform>();
            StretchFull(iconRt);
            iconRt.offsetMin = new Vector2(4f, 4f);
            iconRt.offsetMax = new Vector2(-4f, -4f);
            var icon = iconGo.GetComponent<Image>();
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            if (sprite != null)
            {
                icon.sprite = sprite;
                icon.color = Color.white;
            }
            else
            {
                icon.sprite = null;
                icon.color = new Color(0.10f, 0.13f, 0.18f, 0.95f);

                var slashGo = CreateUi("Slash", go.transform, typeof(Image));
                var slashRt = slashGo.GetComponent<RectTransform>();
                slashRt.anchorMin = new Vector2(0.5f, 0.5f);
                slashRt.anchorMax = new Vector2(0.5f, 0.5f);
                slashRt.pivot = new Vector2(0.5f, 0.5f);
                slashRt.sizeDelta = new Vector2(40f, 3f);
                slashRt.localRotation = Quaternion.Euler(0f, 0f, -42f);
                var slash = slashGo.GetComponent<Image>();
                slash.color = CaptionColor;
                slash.raycastTarget = false;

                var noneLabel = CreateTmp(go.transform, "None", "Off", 15f, FontStyles.Bold);
                var noneRt = noneLabel.rectTransform;
                noneRt.anchorMin = new Vector2(0f, 0f);
                noneRt.anchorMax = new Vector2(1f, 0f);
                noneRt.pivot = new Vector2(0.5f, 0f);
                noneRt.sizeDelta = new Vector2(0f, 22f);
                noneRt.anchoredPosition = new Vector2(0f, 4f);
                noneLabel.alignment = TextAlignmentOptions.Center;
                noneLabel.color = CaptionColor;
                noneLabel.raycastTarget = false;
            }

            int capturedId = badgeId;
            go.GetComponent<Button>().onClick.AddListener(() => SelectBadge(capturedId));
        }

        void SelectBadge(int badgeId)
        {
            LocalPlayerBadge.Set(badgeId);
            RefreshChip();
            HighlightCurrent();
            CancelOverlay();
        }

        void HighlightCurrent()
        {
            if (_overlayRoot == null)
                return;

            Transform content = _overlayRoot.Find("Panel/Scroll/Viewport/Content");
            if (content == null)
                return;

            int current = LocalPlayerBadge.Get();
            for (int i = 0; i < content.childCount; i++)
            {
                Transform tile = content.GetChild(i);
                var backing = tile.GetComponent<Image>();
                if (backing == null)
                    continue;
                bool on = tile.name == "Badge_" + current;
                backing.color = on ? RingSelected : EmptySlot;
            }
        }

        static GameObject CreateUi(string name, Transform parent, params System.Type[] components)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            for (int i = 0; i < components.Length; i++)
            {
                if (go.GetComponent(components[i]) == null)
                    go.AddComponent(components[i]);
            }

            return go;
        }

        static TextMeshProUGUI CreateTmp(
            Transform parent,
            string name,
            string text,
            float fontSize,
            FontStyles style)
        {
            var go = CreateUi(name, parent, typeof(TextMeshProUGUI));
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            return tmp;
        }

        static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }
    }
}
