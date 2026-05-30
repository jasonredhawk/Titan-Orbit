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
            public const float PriceHeight = 14f;
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
            if (priceText != null)
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

        public void EnsureStableButtonRendering()
        {
            if (button == null)
                return;
            button.transition = Selectable.Transition.None;
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
        public void SetPrice(string text) { if (priceText != null) priceText.text = text; }
        public void SetPreview(Sprite sprite)
        {
            if (previewImage == null) return;
            previewImage.sprite = sprite;
            previewImage.color = sprite != null ? Color.white : new Color(0.07f, 0.09f, 0.12f, 0.95f);
        }

        public void SetButtonColors(Color normal) => SetButtonBackgroundColor(normal);
        public void SetInteractable(bool on) { if (button != null) button.interactable = on; }

        public void SetClickHandler(UnityEngine.Events.UnityAction handler)
        {
            if (button == null) return;
            button.onClick.RemoveAllListeners();
            if (handler != null)
                button.onClick.AddListener(handler);
        }
    }
}
