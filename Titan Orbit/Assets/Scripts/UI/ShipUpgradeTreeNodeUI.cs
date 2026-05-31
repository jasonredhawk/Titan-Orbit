using TMPro;
using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Single ship node in the upgrade tree prefab. Population is driven by <see cref="ShipUpgradeTreeUI"/>.
    /// </summary>
    public class ShipUpgradeTreeNodeUI : MonoBehaviour
    {
        /// <summary>Reference metrics from <see cref="Editor.CreateShipUpgradeTreePrefab"/> node template (120×100).</summary>
        private static class RefLayout
        {
            public const float RootPadLeft = 4f;
            public const float RootPadRight = 6f;
            public const float RootPadTop = 4f;
            public const float RootPadBottom = 4f;
            public const float RootSpacing = 4f;
            public const float ContentMinHeight = 72f;
            public const float ContentHSpacing = 5f;
            public const float LeftSpacing = 2f;
            public const float LeftMinWidth = 40f;
            public const float LevelFontSize = 13f;
            public const float LevelHeight = 14f;
            public const float NameFontSize = 11f;
            public const float NameHeight = 26f;
            public const float NameMinHeight = 22f;
            public const float PriceFontSize = 11f;
            public const float PriceHeight = 16f;
            public const float PriceMinWidth = 40f;
            public const float PreviewColWidth = 56f;
            public const float PreviewSize = 56f;
            public const float PreviewMinHeight = 48f;
            public const float PowerBarHeight = 10f;
            public const float PowerBarMinWidth = 48f;
        }

        [Header("Reference layout (prefab editor preview; runtime width comes from panel)")]
        [Tooltip("Reference width for prefab authoring. Runtime nodes scale uniformly to fill the tree row.")]
        [SerializeField] private float layoutWidth = 120f;
        [Tooltip("Reference height; runtime height scales with width using this aspect ratio.")]
        [SerializeField] private float layoutHeight = 100f;

        [SerializeField] private Button button;
        [SerializeField] private Button priceButton;
        [SerializeField] private TextMeshProUGUI levelText;
        [SerializeField] private TextMeshProUGUI shipNameText;
        [SerializeField] private TextMeshProUGUI priceText;
        [SerializeField] private Image previewImage;
        [SerializeField] private ShipUpgradeTreePowerBarUI powerBar;
        [SerializeField] private bool moonHorizontalLayout;

        public int Level { get; private set; }
        public int BranchIndex { get; private set; }
        public ShipUpgradeNode Node { get; private set; }
        public RectTransform Rect => transform as RectTransform;
        public float NodeButtonWidth { get; private set; }
        public float PowerBarTrackWidth { get; private set; }
        public bool UsesMoonHorizontalLayout => moonHorizontalLayout;
        public Button Button => button;
        public Button PriceButton => priceButton;
        public TextMeshProUGUI LevelNumberText => levelText;
        public TextMeshProUGUI ShipNameText => shipNameText;
        public TextMeshProUGUI PriceText => priceText;
        public Image PreviewImage => previewImage;
        public float LayoutWidth => layoutWidth;
        public float LayoutHeight => layoutHeight;

        private float _boundHeight;
        private float _appliedWidth = -1f;
        private float _appliedHeight = -1f;
        private float _lastWidthScale = 1f;
        private float _lastHeightScale = 1f;
        private bool _layoutCached;
        private VerticalLayoutGroup _rootVlg;
        private LayoutElement _contentRowLe;
        private HorizontalLayoutGroup _contentRowHlg;
        private VerticalLayoutGroup _leftVlg;
        private LayoutElement _leftLe;
        private LayoutElement _levelLe;
        private LayoutElement _nameLe;
        private LayoutElement _priceLe;
        private Image _priceButtonImage;
        private Image _priceButtonBorder;
        private bool _priceButtonEnsured;
        private LayoutElement _previewColLe;
        private LayoutElement _previewImgLe;
        private LayoutElement _powerBarLe;

        public void ConfigureLayout(bool useMoonHorizontal)
        {
            moonHorizontalLayout = useMoonHorizontal;
        }

        public void BindSlot(int level, int branchIndex, ShipUpgradeNode node, float width, float height, float powerTrackWidth)
        {
            Level = level;
            BranchIndex = branchIndex;
            Node = node;
            NodeButtonWidth = width;
            PowerBarTrackWidth = powerTrackWidth;
            _boundHeight = height;
            ApplyFixedLayoutSize(width, height);
        }

        /// <summary>Re-applies slot size after child layout or power-bar updates.</summary>
        public void EnforceLayoutSize(float width, float height, float powerTrackWidth)
        {
            NodeButtonWidth = width;
            PowerBarTrackWidth = powerTrackWidth;
            _boundHeight = height;
            ApplyFixedLayoutSize(width, height);
        }

        private void ApplyFixedLayoutSize(float width, float height)
        {
            if (Rect == null)
                return;

            bool sizeChanged = Mathf.Abs(_appliedWidth - width) > 0.5f || Mathf.Abs(_appliedHeight - height) > 0.5f;

            Rect.anchorMin = new Vector2(0.5f, 0.5f);
            Rect.anchorMax = new Vector2(0.5f, 0.5f);
            Rect.pivot = new Vector2(0.5f, 0.5f);
            Rect.sizeDelta = new Vector2(width, height);

            var le = Rect.GetComponent<LayoutElement>();
            if (le == null)
                le = Rect.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            le.flexibleWidth = 0f;
            le.flexibleHeight = 0f;
            le.minWidth = width;
            le.preferredWidth = width;
            le.minHeight = height;
            le.preferredHeight = height;

            _appliedWidth = width;
            _appliedHeight = height;

            if (sizeChanged)
            {
                ApplyScaledChildLayout(width, height);
                LayoutRebuilder.ForceRebuildLayoutImmediate(Rect);
            }
        }

        private void EnsureLayoutCached()
        {
            if (_layoutCached)
                return;

            _layoutCached = true;
            _rootVlg = GetComponent<VerticalLayoutGroup>();

            var contentRow = transform.Find("ContentRow");
            if (contentRow != null)
            {
                _contentRowLe = contentRow.GetComponent<LayoutElement>();
                _contentRowHlg = contentRow.GetComponent<HorizontalLayoutGroup>();
            }

            var leftCol = contentRow != null ? contentRow.Find("LeftColumn") : null;
            if (leftCol != null)
            {
                _leftVlg = leftCol.GetComponent<VerticalLayoutGroup>();
                _leftLe = leftCol.GetComponent<LayoutElement>();
            }

            if (levelText != null)
                _levelLe = levelText.GetComponent<LayoutElement>();
            if (shipNameText != null)
                _nameLe = shipNameText.GetComponent<LayoutElement>();
            EnsurePriceButton();
            if (priceButton != null)
                _priceLe = priceButton.GetComponent<LayoutElement>();
            else if (priceText != null)
                _priceLe = priceText.GetComponent<LayoutElement>();

            if (previewImage != null)
            {
                _previewImgLe = previewImage.GetComponent<LayoutElement>();
                var previewCol = previewImage.transform.parent;
                if (previewCol != null)
                    _previewColLe = previewCol.GetComponent<LayoutElement>();
            }

            if (powerBar != null)
                _powerBarLe = powerBar.GetComponent<LayoutElement>();
        }

        private void ApplyScaledChildLayout(float width, float height)
        {
            if (layoutWidth < 1f || layoutHeight < 1f)
                return;

            EnsureLayoutCached();

            float wScale = width / layoutWidth;
            float hScale = height / layoutHeight;
            float fontScale = Mathf.Min(wScale, hScale);

            if (_rootVlg != null)
            {
                _rootVlg.padding = new RectOffset(
                    ScalePxInt(RefLayout.RootPadLeft, wScale),
                    ScalePxInt(RefLayout.RootPadRight, wScale),
                    ScalePxInt(RefLayout.RootPadTop, hScale),
                    ScalePxInt(RefLayout.RootPadBottom, hScale));
                _rootVlg.spacing = RefLayout.RootSpacing * hScale;
            }

            if (_contentRowLe != null)
                _contentRowLe.minHeight = ScalePx(RefLayout.ContentMinHeight, hScale);
            if (_contentRowHlg != null)
                _contentRowHlg.spacing = RefLayout.ContentHSpacing * wScale;

            if (_leftVlg != null)
                _leftVlg.spacing = RefLayout.LeftSpacing * hScale;
            if (_leftLe != null)
                _leftLe.minWidth = ScalePx(RefLayout.LeftMinWidth, wScale);

            ApplyTextScale(levelText, _levelLe, RefLayout.LevelFontSize, RefLayout.LevelHeight, fontScale, hScale);
            ApplyTextScale(shipNameText, _nameLe, RefLayout.NameFontSize, RefLayout.NameHeight, fontScale, hScale);
            if (_nameLe != null)
                _nameLe.minHeight = ScalePx(RefLayout.NameMinHeight, hScale);
            ApplyTextScale(priceText, _priceLe, RefLayout.PriceFontSize, RefLayout.PriceHeight, fontScale, hScale);
            if (_priceLe != null)
                _priceLe.minWidth = ScalePx(RefLayout.PriceMinWidth, wScale);

            float previewColW = ScalePx(RefLayout.PreviewColWidth, wScale);
            if (_previewColLe != null)
            {
                _previewColLe.preferredWidth = previewColW;
                _previewColLe.minWidth = previewColW;
            }

            float previewW = ScalePx(RefLayout.PreviewSize, wScale);
            float previewH = ScalePx(RefLayout.PreviewSize, hScale);
            float previewMinH = ScalePx(RefLayout.PreviewMinHeight, hScale);
            if (_previewImgLe != null)
            {
                _previewImgLe.preferredWidth = previewW;
                _previewImgLe.preferredHeight = previewH;
                _previewImgLe.minHeight = previewMinH;
            }

            float barH = ScalePx(RefLayout.PowerBarHeight, hScale);
            if (_powerBarLe != null)
            {
                _powerBarLe.preferredHeight = barH;
                _powerBarLe.minHeight = barH;
                _powerBarLe.minWidth = ScalePx(RefLayout.PowerBarMinWidth, wScale);
            }

            if (powerBar != null)
                powerBar.ConfigureLayoutScale(wScale, hScale);

            _lastWidthScale = wScale;
            _lastHeightScale = hScale;
        }

        private static void ApplyTextScale(
            TextMeshProUGUI text,
            LayoutElement le,
            float refFontSize,
            float refHeight,
            float fontScale,
            float heightScale)
        {
            if (text != null)
                text.fontSize = Mathf.Max(6f, refFontSize * fontScale);

            if (le != null)
            {
                float h = ScalePx(refHeight, heightScale);
                le.preferredHeight = h;
                le.minHeight = h;
            }
        }

        private static float ScalePx(float reference, float scale) =>
            Mathf.Max(1f, Mathf.Round(reference * scale));

        private static int ScalePxInt(float reference, float scale) =>
            Mathf.Max(1, Mathf.RoundToInt(reference * scale));

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (NodeButtonWidth > 0.01f)
                return;
            if (Rect == null || layoutWidth < 1f || layoutHeight < 1f)
                return;
            _layoutCached = false;
            ApplyFixedLayoutSize(layoutWidth, layoutHeight);
        }
#endif

        public void ApplyPowerBreakdown(ShipFamilyPowerScoreBreakdown breakdown, float strongestShipTotal)
        {
            if (powerBar == null)
                return;

            if (_appliedWidth < 0.5f && NodeButtonWidth > 0.01f)
                ApplyFixedLayoutSize(NodeButtonWidth, _boundHeight > 0.01f ? _boundHeight : layoutHeight);
            else
                powerBar.ConfigureLayoutScale(_lastWidthScale, _lastHeightScale);

            float track = PowerBarTrackWidth > 0.01f ? PowerBarTrackWidth : NodeButtonWidth;
            powerBar.ApplyBreakdown(breakdown, strongestShipTotal, track);
        }

        private static readonly Color PriceEnabledFill = new Color(0.14f, 0.46f, 0.24f, 1f);
        private static readonly Color PriceEnabledBorder = new Color(0.38f, 0.88f, 0.48f, 1f);
        private static readonly Color PriceEnabledText = new Color(0.96f, 1f, 0.97f, 1f);
        private static readonly Color PriceDisabledFill = new Color(0.1f, 0.11f, 0.13f, 0.95f);
        private static readonly Color PriceDisabledBorder = new Color(0.24f, 0.26f, 0.3f, 0.9f);
        private static readonly Color PriceDisabledText = new Color(0.46f, 0.5f, 0.54f, 1f);
        private const float PriceBorderInset = 1f;

        private void EnsurePriceButton()
        {
            Transform priceRoot = ResolvePriceRootTransform();
            if (priceRoot == null)
                return;

            if (_priceButtonEnsured && priceButton != null && _priceButtonImage != null
                && _priceButtonBorder != null && priceText != null)
                return;

            MigratePriceTextOffRoot(priceRoot);
            ResolvePriceTextReference(priceRoot);

            _priceButtonBorder = FindOrCreatePriceBorderImage(priceRoot);
            _priceButtonImage = FindOrCreatePriceBackgroundImage(priceRoot);
            if (_priceButtonImage == null)
                return;

            _priceButtonImage.raycastTarget = true;
            EnsurePriceLabelOnTop(priceRoot);

            priceButton = priceRoot.GetComponent<Button>();
            if (priceButton == null)
            {
                priceButton = priceRoot.gameObject.AddComponent<Button>();
                priceButton.transition = Selectable.Transition.None;
            }
            else
            {
                priceButton.transition = Selectable.Transition.None;
            }

            priceButton.targetGraphic = _priceButtonImage;

            if (priceText != null)
            {
                priceText.alignment = TextAlignmentOptions.Center;
                priceText.raycastTarget = false;
                priceText.fontStyle = FontStyles.Bold;
            }

            _priceButtonEnsured = true;
        }

        private void MigratePriceTextOffRoot(Transform priceRoot)
        {
            var rootTmp = priceRoot.GetComponent<TextMeshProUGUI>();
            if (rootTmp == null)
                return;

            Transform labelTransform = priceRoot.Find("Label");
            GameObject labelGo;
            if (labelTransform != null)
            {
                labelGo = labelTransform.gameObject;
            }
            else
            {
                labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(priceRoot, false);
            }

            StretchRectToFill(labelGo.transform as RectTransform);

            var labelTmp = labelGo.GetComponent<TextMeshProUGUI>();
            if (labelTmp == null)
            {
                labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
                CopyTextMeshSettings(rootTmp, labelTmp);
            }

            labelTmp.text = rootTmp.text;
            priceText = labelTmp;

            if (Application.isPlaying)
                Destroy(rootTmp);
            else
                DestroyImmediate(rootTmp);
        }

        private static void CopyTextMeshSettings(TextMeshProUGUI from, TextMeshProUGUI to)
        {
            to.font = from.font;
            to.fontSize = from.fontSize;
            to.fontStyle = FontStyles.Bold;
            to.color = from.color;
            to.alignment = TextAlignmentOptions.Center;
            to.enableWordWrapping = false;
            to.richText = false;
            to.raycastTarget = false;
        }

        private void EnsurePriceLabelOnTop(Transform priceRoot)
        {
            Transform label = priceRoot.Find("Label");
            if (label != null)
                label.SetAsLastSibling();
        }

        private static Image FindOrCreatePriceBorderImage(Transform priceRoot)
        {
            const string borderName = "Border";
            Transform border = priceRoot.Find(borderName);
            if (border == null)
            {
                var borderGo = new GameObject(borderName, typeof(RectTransform));
                borderGo.transform.SetParent(priceRoot, false);
                border = borderGo.transform;
            }

            border.SetAsFirstSibling();
            StretchRectToFill(border as RectTransform);

            if (!border.TryGetComponent(out Image image))
            {
                image = border.gameObject.AddComponent<Image>();
                image.raycastTarget = false;
            }

            return image;
        }

        private Transform ResolvePriceRootTransform()
        {
            if (priceButton != null)
                return priceButton.transform;

            if (priceText != null)
            {
                Transform t = priceText.transform;
                if (t.name == "Label" && t.parent != null)
                    return t.parent;
                if (t.name == "Price")
                    return t;
                if (t.parent != null && t.parent.name == "Price")
                    return t.parent;
            }

            return transform.Find("ContentRow/LeftColumn/Price");
        }

        private void ResolvePriceTextReference(Transform priceRoot)
        {
            if (priceText != null)
                return;

            priceText = priceRoot.GetComponent<TextMeshProUGUI>();
            if (priceText != null)
                return;

            Transform label = priceRoot.Find("Label");
            if (label != null)
                priceText = label.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        /// <summary>
        /// Price may already have an Image (new prefabs) or only TMP on the root (legacy).
        /// Unity allows one Graphic per GameObject, so legacy prefabs get a stretched Background child.
        /// </summary>
        private static Image FindOrCreatePriceBackgroundImage(Transform priceRoot)
        {
            if (priceRoot.TryGetComponent(out Image legacyRootImage))
            {
                if (Application.isPlaying)
                    Object.Destroy(legacyRootImage);
                else
                    Object.DestroyImmediate(legacyRootImage);
            }

            const string backgroundName = "Background";
            Transform background = priceRoot.Find(backgroundName);
            if (background != null && background.TryGetComponent(out Image childImage))
                return childImage;

            var bgGo = background != null
                ? background.gameObject
                : new GameObject(backgroundName, typeof(RectTransform));
            if (background == null)
            {
                bgGo.transform.SetParent(priceRoot, false);
                background = bgGo.transform;
            }

            StretchRectToFill(background as RectTransform);
            background.SetSiblingIndex(1);

            var bgRt = background as RectTransform;
            if (bgRt != null)
            {
                bgRt.offsetMin = new Vector2(PriceBorderInset, PriceBorderInset);
                bgRt.offsetMax = new Vector2(-PriceBorderInset, -PriceBorderInset);
            }

            if (!bgGo.TryGetComponent(out Image image))
            {
                image = bgGo.AddComponent<Image>();
                image.color = PriceDisabledFill;
            }

            return image;
        }

        private static void StretchRectToFill(RectTransform rt)
        {
            if (rt == null)
                return;

            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        public void EnsureStableButtonRendering()
        {
            if (button != null)
            {
                button.transition = Selectable.Transition.None;
                button.interactable = false;
                var rootGraphic = button.targetGraphic;
                if (rootGraphic != null)
                    rootGraphic.raycastTarget = false;
            }

            EnsurePriceButton();
            if (priceButton != null)
                priceButton.transition = Selectable.Transition.None;
        }

        public void SetButtonBackgroundColor(Color color)
        {
            if (button == null)
                return;
            var graphic = button.targetGraphic;
            if (graphic == null || graphic.color == color)
                return;
            graphic.color = color;
        }

        public void SetLevelLabel(string text) { if (levelText != null) levelText.text = text; }
        public void SetShipName(string text) { if (shipNameText != null) shipNameText.text = text; }
        public void SetPrice(string text)
        {
            EnsurePriceButton();
            if (priceText != null)
                priceText.text = text;
        }

        public void SetPriceButtonStyle(bool clickable)
        {
            EnsurePriceButton();
            Color fill = clickable ? PriceEnabledFill : PriceDisabledFill;
            Color border = clickable ? PriceEnabledBorder : PriceDisabledBorder;
            Color label = clickable ? PriceEnabledText : PriceDisabledText;

            if (_priceButtonBorder != null)
                _priceButtonBorder.color = border;
            if (_priceButtonImage != null)
                _priceButtonImage.color = fill;
            if (priceText != null)
                priceText.color = label;
        }
        public void SetPreview(Sprite sprite)
        {
            if (previewImage == null) return;
            previewImage.sprite = sprite;
            previewImage.color = sprite != null ? Color.white : new Color(0.07f, 0.09f, 0.12f, 0.95f);
        }

        public void SetButtonColors(Color normal) => SetButtonBackgroundColor(normal);
        public void SetInteractable(bool on)
        {
            EnsurePriceButton();
            if (priceButton != null)
                priceButton.interactable = on;
            SetPriceButtonStyle(on);
        }

        public void SetClickHandler(UnityEngine.Events.UnityAction handler)
        {
            if (button == null) return;
            button.onClick.RemoveAllListeners();
        }

        public void SetPriceClickHandler(UnityEngine.Events.UnityAction handler)
        {
            EnsurePriceButton();
            if (priceButton == null) return;
            priceButton.onClick.RemoveAllListeners();
            if (handler != null)
                priceButton.onClick.AddListener(handler);
        }
    }
}
