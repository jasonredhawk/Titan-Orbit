using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Ship Upgrade Menu at bottom-left of the screen, sized to end before the minimap. 10 abilities bound to keys 1-9 and 0.
    /// Strip position: <b>Screen / strip placement</b> on this component (defaults anchor the strip to the root canvas bottom-left with small padding; re-applied every frame in Play mode).
    /// Each upgrade costs ShipLevel * 5 gems. Max upgrades per ability = ShipLevel.
    /// </summary>
    public class ShipAttributeUpgradeHUD : MonoBehaviour
    {
        [Header("Enable")]
        [Tooltip("Uncheck to disable this HUD (e.g. if it causes crashes).")]
        [SerializeField] private bool upgradeBarEnabled = true;

        [Header("Screen / strip placement")]
        [Tooltip("When on (recommended), the strip sits on the root canvas bottom-left plus the insets below — Inspector values apply every frame in Play mode. When off, the anchor follows Screen.safeArea mapped through the canvas pixel rect (useful on some notched layouts).")]
        [SerializeField] private bool stripAnchorUseCanvasRectPadding = true;
        [Tooltip("Padding from the root canvas rect's left (before mobile scale). Positive moves right; negative moves left. Applied live in Play mode.")]
        [SerializeField] private float upgradeStripInsetFromLeft = 12f;
        [Tooltip("Padding from the root canvas rect's bottom (before mobile scale). Applied live in Play mode.")]
        [SerializeField] private float upgradeStripInsetFromBottom = 12f;
        [Tooltip("Horizontal gap between the strip's right edge and the minimap (logical pixels before mobile scale).")]
        [SerializeField] private float minimapHorizontalGap = 12f;
        [Tooltip("When no minimap is found: reserve this width on the right (logical pixels before mobile scale).")]
        [SerializeField] private float fallbackRightReserve = 400f;
        [Tooltip("Minimum horizontal squeeze when space is tight (1 = full nominal width).")]
        [SerializeField, Range(0.25f, 1f)] private float minWidthFitScale = 0.55f;

        [Header("Layout")]
        [SerializeField] private float barHeight = 68f;
        [SerializeField] private float buttonWidth = 136f;
        [SerializeField] private float buttonSpacing = 10f;
        [Header("Mobile / touch")]
        [Tooltip("Multiplies bar height, button width, fonts, ticks, and padding on phones/tablets so the bottom upgrade strip is easier to read and tap.")]
        [SerializeField] private float mobileHudScale = 1.48f;
        [Tooltip("Top inset for the title area (from button top). Increase to move title down.")]
        [SerializeField] private float titleAreaTopInset = 16f;
        [Tooltip("Right inset reserved for the vertical upgrade ticks column.")]
        [SerializeField] private float tickColumnRightInset = 14f;
        [Tooltip("Horizontal offset of the tick column from the button's right edge (negative = inset).")]
        [SerializeField] private float tickColumnFromRight = -5f;
        [Tooltip("Vertical offset of the upgrade tick column (center anchor). Increase to move ticks up.")]
        [SerializeField] private float ticksCenterYOffset = -3f;
        [Tooltip("Uniform font size for all ability titles (scaled on mobile with the upgrade bar).")]
        [SerializeField, FormerlySerializedAs("titleFontSizeMax")] private float titleFontSize = 12f;

        [Header("Visual Styling")]
        [SerializeField] private Color buttonFrameColor = new Color(0.95f, 0.98f, 1f, 0.42f);
        [SerializeField] private Color buttonInnerShadeColor = new Color(0f, 0f, 0f, 0.22f);
        [SerializeField] private Color buttonAccentColor = new Color(0.75f, 0.88f, 1f, 0.28f);
        [SerializeField] private Color buttonShadowColor = new Color(0f, 0f, 0f, 0.45f);

        [Header("Cost icon (assign in Inspector)")]
        [Tooltip("Shown next to the gem cost number on each upgrade slot. Leave empty until you have a sprite.")]
        [SerializeField] private Sprite gemCostIconSprite;
        [SerializeField] private float gemIconSize = 14f;

        private static readonly string[] Titles =
        {
            "Fire Power", "Bullet Speed",
            "Max Health", "Health Regen",
            "Energy Cap", "Energy Regen",
            "Move Speed", "Turn Speed",
            "Max Gems", "Max People"
        };

        private GameObject rootPanel;
        private RectTransform _stripRootRect;
        private RectTransform _layoutCanvasRect;
        private RectTransform[] _buttonRects = new RectTransform[10];
        private bool _uiBuilt;
        private float _lastLayoutWidth = -1f;

        private Button[] buttons = new Button[10];
        private TextMeshProUGUI[] titleTexts = new TextMeshProUGUI[10];
        private GameObject[] tickContainers = new GameObject[10];
        private Image[] buttonImages = new Image[10];
        private TextMeshProUGUI[] keyLabels = new TextMeshProUGUI[10];
        private TextMeshProUGUI[] costLabels = new TextMeshProUGUI[10];
        private Image[] costGemIcons = new Image[10];

        private float _layoutScale = 1f;
        private float _elementScale = 1f;

        private const float FontSizeScale = 1f;

        private float S(float v) => v * _layoutScale;
        private float E(float v) => v * _elementScale;
        private float F(float nominalFontSize) => E(nominalFontSize * FontSizeScale);

        public float GetUpgradeBarCanvasHeight()
        {
            if (rootPanel == null) return 0f;
            return ((RectTransform)rootPanel.transform).sizeDelta.y;
        }

        public float GetUpgradeStripReserveHeight()
        {
            if (_stripRootRect == null) return GetUpgradeBarCanvasHeight();
            return _stripRootRect.anchoredPosition.y + _stripRootRect.sizeDelta.y;
        }

        private void OnEnable()
        {
            if (rootPanel != null)
                rootPanel.SetActive(false);
        }

        private void OnDisable()
        {
            if (rootPanel != null)
                rootPanel.SetActive(false);
        }

        /// <summary>Gameplay gate only — does not require the bar to already exist.</summary>
        private bool CanShowUpgradeBar()
        {
            if (!upgradeBarEnabled)
                return false;
            if (!EcsGameBridge.HasLocalPlayerShip())
                return false;
            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
                return false;
            return !ship.IsDead
                && !ship.AwaitingTeamSelection
                && ship.Team != TeamId.None
                && !HUDController.ShipUpgradeTreeObscuresHud;
        }

        private bool ShouldShowUpgradeBar() =>
            _uiBuilt && rootPanel != null && CanShowUpgradeBar();

        private bool TryResolveLayoutCanvas()
        {
            if (_layoutCanvasRect != null)
                return true;

            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
                return false;

            canvas = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            _layoutCanvasRect = canvas.transform as RectTransform;
            return _layoutCanvasRect != null;
        }

        private static bool TryGetMinimapLeftLocalX(RectTransform layoutSpace, out float minimapLeftLocalX)
        {
            minimapLeftLocalX = 0f;
            if (layoutSpace == null) return false;
            var minimap = Object.FindFirstObjectByType<MinimapController>();
            if (minimap == null) return false;
            var mmRt = minimap.transform as RectTransform;
            if (mmRt == null) return false;
            Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(layoutSpace, mmRt);
            minimapLeftLocalX = b.min.x;
            return true;
        }

        private bool TryComputeStripMetrics(out float insetL, out float insetB, out float availableWidth, out float barH, out float buttonW, out float spacing)
        {
            insetL = insetB = availableWidth = barH = buttonW = spacing = 0f;
            if (!TryResolveLayoutCanvas())
                return false;

            Canvas.ForceUpdateCanvases();

            Rect canvasRect = _layoutCanvasRect.rect;
            float canvasW = canvasRect.width;
            float canvasH = canvasRect.height;
            if (canvasW < 100f || canvasH < 100f)
            {
                var scaler = _layoutCanvasRect.GetComponent<CanvasScaler>();
                if (scaler != null)
                {
                    canvasW = scaler.referenceResolution.x;
                    canvasH = scaler.referenceResolution.y;
                }
            }

            if (canvasW < 100f || canvasH < 100f)
                return false;

            _layoutScale = Application.isMobilePlatform ? Mathf.Clamp(mobileHudScale, 1f, 2.25f) : 1f;
            insetL = S(upgradeStripInsetFromLeft);
            insetB = S(upgradeStripInsetFromBottom);
            spacing = S(buttonSpacing);
            barH = S(barHeight);

            float leftEdgeX = canvasRect.xMin + insetL;
            float rightEdgeX = canvasRect.xMax;

            if (TryGetMinimapLeftLocalX(_layoutCanvasRect, out float minimapLeftLocalX))
                rightEdgeX = minimapLeftLocalX - S(minimapHorizontalGap);
            else
                rightEdgeX = canvasRect.xMin + canvasW - S(fallbackRightReserve);

            availableWidth = rightEdgeX - leftEdgeX;
            float nominalW = 10f * S(buttonWidth) + 9f * spacing;
            float minW = nominalW * minWidthFitScale;
            availableWidth = Mathf.Max(minW, availableWidth);

            // Fill the strip between the left edge and minimap (scale up as well as down).
            _elementScale = _layoutScale * (nominalW > 0.01f ? availableWidth / nominalW : 1f);
            buttonW = Mathf.Max(24f, (availableWidth - 9f * spacing) / 10f);
            availableWidth = 10f * buttonW + 9f * spacing;
            return availableWidth > 1f;
        }

        private void RefreshUpgradeStripLayout(bool force)
        {
            if (!_uiBuilt || _stripRootRect == null)
                return;
            if (!TryComputeStripMetrics(out float insetL, out float insetB, out float availableWidth, out float barH, out float buttonW, out float spacing))
                return;

            if (!force && Mathf.Approximately(_lastLayoutWidth, availableWidth))
            {
                _stripRootRect.anchoredPosition = new Vector2(insetL, insetB);
                return;
            }

            _lastLayoutWidth = availableWidth;
            _stripRootRect.anchoredPosition = new Vector2(insetL, insetB);
            _stripRootRect.sizeDelta = new Vector2(availableWidth, barH);

            for (int i = 0; i < 10; i++)
            {
                if (_buttonRects[i] == null)
                    continue;
                _buttonRects[i].anchorMin = new Vector2(0f, 0.5f);
                _buttonRects[i].anchorMax = new Vector2(0f, 0.5f);
                _buttonRects[i].pivot = new Vector2(0f, 0.5f);
                _buttonRects[i].anchoredPosition = new Vector2(i * (buttonW + spacing), 0f);
                _buttonRects[i].sizeDelta = new Vector2(buttonW, barH - E(6f));

                if (titleTexts[i] != null)
                    titleTexts[i].fontSize = E(titleFontSize);
                if (keyLabels[i] != null)
                    keyLabels[i].fontSize = F(13f);
                if (costLabels[i] != null)
                    costLabels[i].fontSize = F(11f);
            }
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _uiBuilt)
                RefreshUpgradeStripLayout(force: true);
        }

        private void EnsureUiBuilt()
        {
            if (_uiBuilt || !upgradeBarEnabled || !CanShowUpgradeBar())
                return;
            BuildUI();
        }

        private void BuildUI()
        {
            if (_uiBuilt || !TryResolveLayoutCanvas())
                return;

            rootPanel = new GameObject("ShipAttributeUpgradeBar", typeof(RectTransform));
            RectTransform rootRect = rootPanel.GetComponent<RectTransform>();
            rootRect.SetParent(_layoutCanvasRect, false);
            rootRect.SetAsLastSibling();
            rootRect.localScale = Vector3.one;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.zero;
            rootRect.pivot = Vector2.zero;

            _stripRootRect = rootRect;
            _uiBuilt = true;

            Image bgImage = rootPanel.AddComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0f);
            bgImage.raycastTarget = false;
            rootPanel.SetActive(false);

            string[] keyStrings = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

            for (int i = 0; i < 10; i++)
            {
                Color statColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(i);
                var btn = CreateUpgradeButton(rootPanel.transform, i, statColor, keyStrings[i]);
                buttons[i] = btn.button;
                titleTexts[i] = btn.titleText;
                tickContainers[i] = btn.tickContainer;
                buttonImages[i] = btn.bgImage;
                keyLabels[i] = btn.keyLabel;
                costLabels[i] = btn.costLabel;
                costGemIcons[i] = btn.costGemIcon;
                _buttonRects[i] = btn.buttonRect;
            }

            RefreshUpgradeStripLayout(force: true);
        }

        private (Button button, RectTransform buttonRect, TextMeshProUGUI titleText, GameObject tickContainer, Image bgImage, TextMeshProUGUI keyLabel, TextMeshProUGUI costLabel, Image costGemIcon) CreateUpgradeButton(Transform parent, int index, Color statColor, string keyStr)
        {
            GameObject btnObj = new GameObject($"UpgradeBtn_{index}");
            btnObj.transform.SetParent(parent, false);

            RectTransform btnRect = btnObj.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0f, 0.5f);
            btnRect.anchorMax = new Vector2(0f, 0.5f);
            btnRect.pivot = new Vector2(0f, 0.5f);

            Image bgImage = btnObj.AddComponent<Image>();
            bgImage.color = statColor;
            bgImage.raycastTarget = true;
            var buttonOutline = btnObj.AddComponent<Outline>();
            buttonOutline.effectColor = buttonFrameColor;
            buttonOutline.effectDistance = new Vector2(E(1f), E(1f));
            var buttonShadow = btnObj.AddComponent<Shadow>();
            buttonShadow.effectColor = buttonShadowColor;
            buttonShadow.effectDistance = new Vector2(0f, E(-2f));

            GameObject innerShade = new GameObject("InnerShade");
            innerShade.transform.SetParent(btnObj.transform, false);
            RectTransform shadeRect = innerShade.AddComponent<RectTransform>();
            shadeRect.anchorMin = Vector2.zero;
            shadeRect.anchorMax = Vector2.one;
            shadeRect.offsetMin = new Vector2(E(3f), E(3f));
            shadeRect.offsetMax = new Vector2(E(-3f), E(-3f));
            Image shadeImage = innerShade.AddComponent<Image>();
            shadeImage.color = buttonInnerShadeColor;
            shadeImage.raycastTarget = false;

            GameObject accentLine = new GameObject("AccentLine");
            accentLine.transform.SetParent(btnObj.transform, false);
            RectTransform accentRect = accentLine.AddComponent<RectTransform>();
            accentRect.anchorMin = new Vector2(0f, 1f);
            accentRect.anchorMax = new Vector2(1f, 1f);
            accentRect.pivot = new Vector2(0.5f, 1f);
            accentRect.offsetMin = new Vector2(E(5f), E(-3f));
            accentRect.offsetMax = new Vector2(E(-5f), E(-1f));
            Image accentImage = accentLine.AddComponent<Image>();
            accentImage.color = buttonAccentColor;
            accentImage.raycastTarget = false;

            Button button = btnObj.AddComponent<Button>();
            button.targetGraphic = bgImage;
            int capturedIndex = index;
            button.onClick.AddListener(() => TryUpgrade(capturedIndex));

            GameObject keyObj = new GameObject("KeyLabel");
            keyObj.transform.SetParent(btnObj.transform, false);
            RectTransform keyRect = keyObj.AddComponent<RectTransform>();
            keyRect.anchorMin = new Vector2(0f, 1f);
            keyRect.anchorMax = new Vector2(0f, 1f);
            keyRect.pivot = new Vector2(0f, 1f);
            keyRect.anchoredPosition = new Vector2(E(4f), E(-4f));
            keyRect.sizeDelta = new Vector2(E(20f), E(16f));
            TextMeshProUGUI keyLabel = keyObj.AddComponent<TextMeshProUGUI>();
            keyLabel.text = keyStr;
            keyLabel.fontSize = F(13f);
            if (TMP_Settings.defaultFontAsset != null) keyLabel.font = TMP_Settings.defaultFontAsset;
            keyLabel.color = new Color(1f, 1f, 1f, 0.9f);
            keyLabel.alignment = TextAlignmentOptions.TopLeft;

            GameObject titleObj = new GameObject("Title");
            titleObj.transform.SetParent(btnObj.transform, false);
            RectTransform titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.pivot = new Vector2(0.5f, 0.5f);
            float bottomForCost = E(20f);
            titleRect.offsetMin = new Vector2(E(4f), bottomForCost);
            titleRect.offsetMax = new Vector2(-E(tickColumnRightInset), -E(titleAreaTopInset));
            TextMeshProUGUI titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = Titles[index];
            if (TMP_Settings.defaultFontAsset != null) titleText.font = TMP_Settings.defaultFontAsset;
            titleText.color = Color.white;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.enableWordWrapping = true;
            titleText.overflowMode = TextOverflowModes.Overflow;
            titleText.enableAutoSizing = false;
            titleText.fontSize = E(titleFontSize);
            titleText.raycastTarget = false;

            GameObject tickContainer = new GameObject("Ticks");
            tickContainer.transform.SetParent(btnObj.transform, false);
            RectTransform tickRect = tickContainer.AddComponent<RectTransform>();
            tickRect.anchorMin = new Vector2(1f, 0.5f);
            tickRect.anchorMax = new Vector2(1f, 0.5f);
            tickRect.pivot = new Vector2(1f, 0.5f);
            tickRect.anchoredPosition = new Vector2(E(tickColumnFromRight), E(ticksCenterYOffset));
            float tickCell = E(7f) + E(2f);
            tickRect.sizeDelta = new Vector2(E(12f), tickCell * 7f + E(4f));

            VerticalLayoutGroup tickLayout = tickContainer.AddComponent<VerticalLayoutGroup>();
            tickLayout.spacing = E(2f);
            tickLayout.padding = new RectOffset(0, 0, 0, 0);
            tickLayout.childAlignment = TextAnchor.MiddleRight;
            tickLayout.childControlWidth = true;
            tickLayout.childControlHeight = true;
            tickLayout.childForceExpandWidth = false;
            tickLayout.childForceExpandHeight = false;

            GameObject costRow = new GameObject("CostRow");
            costRow.transform.SetParent(btnObj.transform, false);
            RectTransform costRowRect = costRow.AddComponent<RectTransform>();
            costRowRect.anchorMin = new Vector2(0.5f, 0f);
            costRowRect.anchorMax = new Vector2(0.5f, 0f);
            costRowRect.pivot = new Vector2(0.5f, 0f);
            costRowRect.anchoredPosition = new Vector2(0f, E(3f));
            float scaledGem = E(gemIconSize);
            costRowRect.sizeDelta = new Vector2(E(buttonWidth) - E(6f), Mathf.Max(E(14f), scaledGem + E(2f)));

            HorizontalLayoutGroup costRowLayout = costRow.AddComponent<HorizontalLayoutGroup>();
            costRowLayout.childAlignment = TextAnchor.MiddleCenter;
            costRowLayout.spacing = E(1f);
            costRowLayout.padding = new RectOffset(0, 0, 0, 0);
            costRowLayout.childForceExpandWidth = false;
            costRowLayout.childForceExpandHeight = false;
            costRowLayout.childControlWidth = true;
            costRowLayout.childControlHeight = true;

            GameObject costObj = new GameObject("CostLabel");
            costObj.transform.SetParent(costRow.transform, false);
            RectTransform costRect = costObj.AddComponent<RectTransform>();
            costRect.sizeDelta = Vector2.zero;
            TextMeshProUGUI costLabel = costObj.AddComponent<TextMeshProUGUI>();
            costLabel.text = "";
            costLabel.fontSize = F(11f);
            if (TMP_Settings.defaultFontAsset != null) costLabel.font = TMP_Settings.defaultFontAsset;
            costLabel.color = new Color(0.9f, 0.9f, 0.6f, 1f);
            costLabel.alignment = TextAlignmentOptions.MidlineRight;
            costLabel.overflowMode = TextOverflowModes.Overflow;
            ContentSizeFitter costCsf = costObj.AddComponent<ContentSizeFitter>();
            costCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            costCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            LayoutElement costLe = costObj.AddComponent<LayoutElement>();
            costLe.flexibleWidth = 0f;

            GameObject gemObj = new GameObject("GemIcon");
            gemObj.transform.SetParent(costRow.transform, false);
            RectTransform gemRect = gemObj.AddComponent<RectTransform>();
            float gemSz = E(gemIconSize);
            gemRect.sizeDelta = new Vector2(gemSz, gemSz);
            Image costGemIcon = gemObj.AddComponent<Image>();
            costGemIcon.raycastTarget = false;
            costGemIcon.preserveAspect = true;
            costGemIcon.enabled = gemCostIconSprite != null;
            if (gemCostIconSprite != null) costGemIcon.sprite = gemCostIconSprite;
            LayoutElement gemLe = gemObj.AddComponent<LayoutElement>();
            gemLe.preferredWidth = gemSz;
            gemLe.preferredHeight = gemSz;
            gemLe.flexibleWidth = 0f;

            return (button, btnRect, titleText, tickContainer, bgImage, keyLabel, costLabel, costGemIcon);
        }

        private void CreateTickMarks(GameObject container, int maxCount)
        {
            maxCount = Mathf.Clamp(maxCount, 0, 7);
            for (int i = 0; i < maxCount; i++)
            {
                GameObject tick = new GameObject($"Tick_{i}");
                tick.transform.SetParent(container.transform, false);
                Image img = tick.AddComponent<Image>();
                img.color = new Color(0.3f, 0.3f, 0.35f, 0.8f);
                img.raycastTarget = false;
                LayoutElement le = tick.AddComponent<LayoutElement>();
                le.preferredWidth = E(7f);
                le.preferredHeight = E(7f);
            }
        }

        private void UpdateTickMarks(int index, int currentLevel, int maxLevel)
        {
            if (tickContainers == null || index < 0 || index >= tickContainers.Length || tickContainers[index] == null) return;
            maxLevel = Mathf.Clamp(maxLevel, 0, 7);
            Transform container = tickContainers[index].transform;
            int childCount = container.childCount;

            if (childCount != maxLevel)
            {
                for (int i = container.childCount - 1; i >= 0; i--)
                    Destroy(container.GetChild(i).gameObject);
                CreateTickMarks(tickContainers[index], maxLevel);
                childCount = maxLevel;
            }

            for (int i = 0; i < childCount; i++)
            {
                Image img = container.GetChild(i).GetComponent<Image>();
                if (img != null)
                    img.color = i < currentLevel ? new Color(1f, 1f, 0.9f, 1f) : new Color(0.3f, 0.3f, 0.35f, 0.8f);
            }
        }

        private void Update()
        {
            if (!upgradeBarEnabled)
                return;

            EnsureUiBuilt();
            if (rootPanel == null)
                return;

            bool show = CanShowUpgradeBar();
            rootPanel.SetActive(show);

            if (!show)
                return;
            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
                return;
            if (!EcsGameBridge.TryGetLocalShipAttributeUpgrades(out var attrs))
                attrs = default;

            int maxUpgrades = ShipAttributeUpgradeLogic.GetMaxUpgrades(ship.ShipLevel);
            int cost = ShipAttributeUpgradeLogic.GetUpgradeCost(ship.ShipLevel);

            for (int i = 0; i < 10; i++)
            {
                int current = ShipAttributeUpgradeLogic.GetAttributeLevel(attrs, i);
                UpdateTickMarks(i, current, maxUpgrades);

                bool canUpgrade = current < maxUpgrades && ship.CurrentGems >= cost - 0.01f;
                if (buttons[i] != null)
                    buttons[i].interactable = canUpgrade;

                if (costLabels[i] != null)
                {
                    if (current >= maxUpgrades)
                    {
                        costLabels[i].text = "MAX";
                        if (costGemIcons[i] != null) costGemIcons[i].enabled = false;
                    }
                    else
                    {
                        costLabels[i].text = $"{cost}";
                        if (costGemIcons[i] != null)
                            costGemIcons[i].enabled = gemCostIconSprite != null;
                    }
                }
            }
        }

        private void LateUpdate()
        {
            if (!upgradeBarEnabled)
                return;

            EnsureUiBuilt();

            if (_uiBuilt)
                RefreshUpgradeStripLayout(force: false);

            if (!ShouldShowUpgradeBar())
                return;

            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            for (int i = 0; i < 9; i++)
            {
                Key key = (Key)((int)Key.Digit1 + i);
                if (keyboard[key].wasPressedThisFrame)
                {
                    TryUpgrade(i);
                    return;
                }
            }
            if (keyboard.digit0Key.wasPressedThisFrame)
                TryUpgrade(9);
        }

        private void TryUpgrade(int index)
        {
            if (!CanShowUpgradeBar())
                return;
            if (index < 0 || index > 9)
                return;
            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
                return;

            if (!EcsGameBridge.TryGetLocalShipAttributeUpgrades(out var attrs))
                attrs = default;

            int current = ShipAttributeUpgradeLogic.GetAttributeLevel(attrs, index);
            if (current >= ShipAttributeUpgradeLogic.GetMaxUpgrades(ship.ShipLevel)) return;
            if (ship.CurrentGems < ShipAttributeUpgradeLogic.GetUpgradeCost(ship.ShipLevel) - 0.01f) return;

            MoonOrbitRpcClient.PurchaseAttributeUpgrade(index);
        }
    }
}
