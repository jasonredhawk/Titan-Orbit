using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Core;

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
        [Tooltip("Padding from the root canvas rect’s left (before mobile scale). Positive moves right; negative moves left. Applied live in Play mode.")]
        [SerializeField] private float upgradeStripInsetFromLeft = 12f;
        [Tooltip("Padding from the root canvas rect’s bottom (before mobile scale). Applied live in Play mode.")]
        [SerializeField] private float upgradeStripInsetFromBottom = 12f;
        [Tooltip("Horizontal gap between the strip’s right edge and the minimap (logical pixels before mobile scale).")]
        [SerializeField] private float minimapHorizontalGap = 12f;
        [Tooltip("When no minimap is found: reserve this width on the right (logical pixels before mobile scale).")]
        [SerializeField] private float fallbackRightReserve = 400f;
        [Tooltip("Minimum horizontal squeeze when space is tight (1 = full nominal width).")]
        [SerializeField, Range(0.05f, 1f)] private float minWidthFitScale = 0.32f;

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

        private static readonly string[] Titles = new[]
        {
            "Fire Power", "Bullet Speed",
            "Max Health", "Health Regen",
            "Energy Cap", "Energy Regen",
            "Move Speed", "Turn Speed",
            "Max Gems", "Max People"
        };

        private Starship playerShip;
        private float lastShipLookupTime = -999f;
        private const float ShipLookupInterval = 0.3f;

        private GameObject rootPanel;
        /// <summary>After <see cref="BuildUI"/>, used to re-apply strip position when you tweak insets in the Inspector or the window resizes.</summary>
        private Canvas _stripRootCanvas;
        private RectTransform _stripPlacementParent;
        private RectTransform _stripRootRect;
        private bool _stripPlacementReady;

        private Button[] buttons = new Button[10];
        private TextMeshProUGUI[] titleTexts = new TextMeshProUGUI[10];
        private GameObject[] tickContainers = new GameObject[10];
        private Image[] buttonImages = new Image[10];
        private TextMeshProUGUI[] keyLabels = new TextMeshProUGUI[10];
        private TextMeshProUGUI[] costLabels = new TextMeshProUGUI[10];
        private Image[] costGemIcons = new Image[10];

        /// <summary>1 on desktop; <see cref="mobileHudScale"/> on mobile, clamped for safety.</summary>
        private float _layoutScale = 1f;
        /// <summary><see cref="_layoutScale"/> × width fit so the bar fits left of the minimap.</summary>
        private float _elementScale = 1f;

        private const float FontSizeScale = 1f;

        private float S(float v) => v * _layoutScale;
        private float E(float v) => v * _elementScale;
        private float F(float nominalFontSize) => E(nominalFontSize * FontSizeScale);

        /// <summary>Height of the upgrade strip in canvas units (for stacking other HUDs above it).</summary>
        public float GetUpgradeBarCanvasHeight()
        {
            if (rootPanel == null) return 0f;
            return ((RectTransform)rootPanel.transform).sizeDelta.y;
        }

        /// <summary>
        /// Distance from the root canvas bottom edge to the top of the upgrade strip (inset + bar height).
        /// Matches the strip's live anchored layout so HUDs stacked above it do not overlap.
        /// </summary>
        public float GetUpgradeStripReserveHeight()
        {
            if (_stripRootRect == null) return GetUpgradeBarCanvasHeight();
            return _stripRootRect.anchoredPosition.y + _stripRootRect.sizeDelta.y;
        }

        private void Start()
        {
            if (!upgradeBarEnabled) return;
            BuildUI();
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

        /// <summary>
        /// Maps a screen pixel (bottom-left origin) into <paramref name="canvasRt"/> local space by lerping across
        /// <see cref="Canvas.pixelRect"/> vs the rect’s local corners. Matches the visible canvas viewport better than a lone UI-camera ray.
        /// </summary>
        private static bool TryScreenPointToCanvasLocal(RectTransform canvasRt, Canvas rootCanvas, Vector2 screenPoint, out Vector2 localPoint)
        {
            localPoint = default;
            if (canvasRt == null || rootCanvas == null) return false;
            Rect pr = rootCanvas.pixelRect;
            if (pr.width < 1f || pr.height < 1f) return false;
            float u = (screenPoint.x - pr.x) / pr.width;
            float v = (screenPoint.y - pr.y) / pr.height;
            Rect r = canvasRt.rect;
            localPoint = new Vector2(
                Mathf.Lerp(r.xMin, r.xMax, u),
                Mathf.Lerp(r.yMin, r.yMax, v));
            return true;
        }

        /// <summary>Bottom-left of the strip’s pivot (0,0) in <paramref name="placementParent"/> local space.</summary>
        private void GetStripBottomLeftInParentLocal(Canvas rootCanvas, RectTransform placementParent, out Vector2 localBottomLeft)
        {
            float insetL = S(upgradeStripInsetFromLeft);
            float insetB = S(upgradeStripInsetFromBottom);
            if (stripAnchorUseCanvasRectPadding)
            {
                localBottomLeft = new Vector2(placementParent.rect.xMin + insetL, placementParent.rect.yMin + insetB);
                return;
            }

            Vector2 screenBl = new Vector2(Screen.safeArea.xMin + insetL, Screen.safeArea.yMin + insetB);
            if (TryScreenPointToCanvasLocal(placementParent, rootCanvas, screenBl, out localBottomLeft))
                return;

            UnityEngine.Camera uiCam = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(placementParent, screenBl, uiCam, out localBottomLeft))
                return;

            localBottomLeft = new Vector2(placementParent.rect.xMin + insetL, placementParent.rect.yMin + insetB);
        }

        private void RefreshUpgradeStripPlacement()
        {
            if (!_stripPlacementReady || _stripRootRect == null || _stripPlacementParent == null || _stripRootCanvas == null) return;
            GetStripBottomLeftInParentLocal(_stripRootCanvas, _stripPlacementParent, out Vector2 localBl);
            Vector2 canvasCornerLocal = new Vector2(_stripPlacementParent.rect.xMin, _stripPlacementParent.rect.yMin);
            _stripRootRect.anchoredPosition = localBl - canvasCornerLocal;
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _stripPlacementReady)
                RefreshUpgradeStripPlacement();
        }

        private void BuildUI()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            Canvas rootCanvas = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            RectTransform placementParent = rootCanvas.transform as RectTransform;
            if (placementParent == null) return;

            _layoutScale = Application.isMobilePlatform ? Mathf.Clamp(mobileHudScale, 1f, 2.25f) : 1f;

            // Must be a RectTransform from creation; SetParent before AddComponent<RectTransform> breaks layout on some Unity versions.
            rootPanel = new GameObject("ShipAttributeUpgradeBar", typeof(RectTransform));
            RectTransform rootRect = rootPanel.GetComponent<RectTransform>();
            rootRect.SetParent(placementParent, false);
            rootRect.localScale = Vector3.one;
            // Keep draw order with the HUD object (same canvas as minimap).
            int hudSibling = transform.GetSiblingIndex();
            if (rootPanel.transform.parent == transform.parent)
                rootPanel.transform.SetSiblingIndex(Mathf.Min(hudSibling + 1, rootPanel.transform.parent.childCount - 1));

            GetStripBottomLeftInParentLocal(rootCanvas, placementParent, out Vector2 localStripBl);
            float leftEdgeX = localStripBl.x;
            Vector2 canvasCornerLocal = new Vector2(placementParent.rect.xMin, placementParent.rect.yMin);

            float nominalW = 10f * S(buttonWidth) + 9f * S(buttonSpacing);
            float availableW = Mathf.Max(80f, placementParent.rect.xMax - leftEdgeX - S(fallbackRightReserve));
            if (TryGetMinimapLeftLocalX(placementParent, out float minimapLeftLocalX))
            {
                float capByMinimap = minimapLeftLocalX - S(minimapHorizontalGap) - leftEdgeX;
                availableW = Mathf.Max(80f, Mathf.Min(availableW, capByMinimap));
            }

            float widthFit = nominalW > 0.01f
                ? Mathf.Clamp(availableW / nominalW, minWidthFitScale, 1f)
                : 1f;
            _elementScale = _layoutScale * widthFit;

            float bw = E(buttonWidth);
            float sp = E(buttonSpacing);
            float bh = E(barHeight);
            float totalWidth = 10f * bw + 9f * sp;

            // Bottom-left: anchors (0,0), pivot (0,0). anchoredPosition = bottom-left in parent local minus canvas rect corner (see GetStripBottomLeftInParentLocal).
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.zero;
            rootRect.pivot = Vector2.zero;
            rootRect.sizeDelta = new Vector2(totalWidth, bh);
            rootRect.anchoredPosition = localStripBl - canvasCornerLocal;

            _stripRootCanvas = rootCanvas;
            _stripPlacementParent = placementParent;
            _stripRootRect = rootRect;
            _stripPlacementReady = true;

            Image bgImage = rootPanel.AddComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0f);
            bgImage.raycastTarget = false;

            string[] keyStrings = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

            for (int i = 0; i < 10; i++)
            {
                float x = bw / 2f + i * (bw + sp);
                Color statColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(i);
                var btn = CreateUpgradeButton(rootPanel.transform, x, i, statColor, keyStrings[i], bw, bh);
                buttons[i] = btn.button;
                titleTexts[i] = btn.titleText;
                tickContainers[i] = btn.tickContainer;
                buttonImages[i] = btn.bgImage;
                keyLabels[i] = btn.keyLabel;
                costLabels[i] = btn.costLabel;
                costGemIcons[i] = btn.costGemIcon;
            }
        }

        private (Button button, TextMeshProUGUI titleText, GameObject tickContainer, Image bgImage, TextMeshProUGUI keyLabel, TextMeshProUGUI costLabel, Image costGemIcon) CreateUpgradeButton(Transform parent, float x, int index, Color statColor, string keyStr, float scaledButtonWidth, float scaledBarHeight)
        {
            GameObject btnObj = new GameObject($"UpgradeBtn_{index}");
            btnObj.transform.SetParent(parent, false);

            RectTransform btnRect = btnObj.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0.5f);
            btnRect.anchorMax = new Vector2(0.5f, 0.5f);
            btnRect.pivot = new Vector2(0.5f, 0.5f);
            btnRect.anchoredPosition = new Vector2(x, 0f);
            btnRect.sizeDelta = new Vector2(scaledButtonWidth, scaledBarHeight - E(6f));

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

            // Key label (top-left)
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

            // Title: centered in the main area (above cost row, leaves right strip for ticks); one font size for every slot.
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

            // Tick container: vertical stack, flush to the right edge of the button
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

            // Cost row: number + gem icon (assign gem sprite on ShipAttributeUpgradeHUD in Inspector)
            GameObject costRow = new GameObject("CostRow");
            costRow.transform.SetParent(btnObj.transform, false);
            RectTransform costRowRect = costRow.AddComponent<RectTransform>();
            costRowRect.anchorMin = new Vector2(0.5f, 0f);
            costRowRect.anchorMax = new Vector2(0.5f, 0f);
            costRowRect.pivot = new Vector2(0.5f, 0f);
            costRowRect.anchoredPosition = new Vector2(0f, E(3f));
            float scaledGem = E(gemIconSize);
            costRowRect.sizeDelta = new Vector2(scaledButtonWidth - E(6f), Mathf.Max(E(14f), scaledGem + E(2f)));

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

            return (button, titleText, tickContainer, bgImage, keyLabel, costLabel, costGemIcon);
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
            if (!upgradeBarEnabled || rootPanel == null) return;

            if (playerShip == null || !playerShip.IsSpawned)
            {
                if (Time.time - lastShipLookupTime >= ShipLookupInterval)
                {
                    lastShipLookupTime = Time.time;
                    foreach (var ship in Object.FindObjectsByType<Starship>(FindObjectsSortMode.None))
                    {
                        if (ship.IsOwner)
                        {
                            playerShip = ship;
                            break;
                        }
                    }
                }
            }

            bool show = playerShip != null && playerShip.IsSpawned && !playerShip.IsDead
                && playerShip.ShipTeam != TeamManager.Team.None
                && !HUDController.ShipUpgradeTreeObscuresHud;
            rootPanel.SetActive(show);

            if (!show || playerShip == null) return;

            int maxUpgrades = playerShip.MaxAttributeUpgrades;
            int cost = playerShip.AttributeUpgradeCost;

            for (int i = 0; i < 10; i++)
            {
                int current = playerShip.GetAttributeLevel(i);
                UpdateTickMarks(i, current, maxUpgrades);

                bool canUpgrade = current < maxUpgrades && playerShip.CurrentGems >= cost - 0.01f;
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
            if (!upgradeBarEnabled) return;
            RefreshUpgradeStripPlacement();

            var keyboard = Keyboard.current;
            if (keyboard == null || playerShip == null || !playerShip.IsSpawned) return;

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
            if (playerShip == null || !playerShip.IsSpawned || !playerShip.IsOwner) return;
            if (index < 0 || index > 9) return;

            int current = playerShip.GetAttributeLevel(index);
            if (current >= playerShip.MaxAttributeUpgrades) return;
            if (playerShip.CurrentGems < playerShip.AttributeUpgradeCost - 0.01f) return;

            playerShip.UpgradeAttributeServerRpc(index);
        }
    }
}
