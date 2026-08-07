using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Shared;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Bottom-left ship attribute upgrade bar (10 slots, keys 1–9 and 0). Reads local ship
    /// ShipState and ShipAttributeUpgradeState from EcsGameBridge; sends purchases via
    /// MoonOrbitRpcClient.PurchaseAttributeUpgrade (server validates in ShipAttributeUpgradeSystem).
    /// Cost = ShipLevel × 5 gems; max levels per attribute = ShipLevel.
    /// Most abilities are +10% per purchase; Move Speed adds one chassis PerAbilityLevel step
    /// (move + accel + OD drain together) — see ShipAttributeUpgradeLogic.
    /// Strip layout: recomputed only when screen / canvas / minimap size changes; positions are
    /// snapped to whole canvas units so windowed (non-1:1) views do not shimmer.
    /// <para>
    /// [TITAN-ORBIT] Holds a last-good HUD cache during <see cref="ClientJoinSettleCache.GhostSpawnBacklog"/>
    /// (gem Instantiates after asteroid destroy). Without it the strip hid or zeroed ticks for a frame —
    /// the same combat flicker class already fixed in <see cref="ShipSpeedometerHUD"/>.
    /// </para>
    /// </summary>
    public class ShipAttributeUpgradeHUD : MonoBehaviour
    {
        [Header("Enable")]
        [Tooltip("Uncheck to disable this HUD (e.g. if it causes crashes).")]
        [SerializeField] private bool upgradeBarEnabled = true;

        [Header("Screen / strip placement")]
        [Tooltip("When on (recommended), the strip sits on the root canvas bottom-left plus the insets below. Layout only recomputes on screen/minimap size changes (not every frame). When off, reserved for safe-area anchoring on notched layouts.")]
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

        /// <summary>
        /// [TITAN-ORBIT] Last successful ship + attribute snapshot. GhostSpawnBacklog skips full ship
        /// scans; brief misses used to SetActive(false) the strip or paint zero ticks mid-combat.
        /// </summary>
        private bool _hasHudCache;
        private ShipState _cachedShip;
        private ShipAttributeUpgradeState _cachedAttrs;
        private bool _lastShowActive;
        private readonly int[] _lastTickLevels = { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
        private int _lastMaxUpgrades = -1;
        private int _lastCost = -1;
        private readonly string[] _lastCostText = new string[10];
        private readonly bool[] _lastInteractable = new bool[10];
        private bool _slotVisualsSeeded;

        /// <summary>Cached minimap rect so layout dirty-checks do not FindFirstObjectByType every frame.</summary>
        private RectTransform _cachedMinimapRect;

        /// <summary>
        /// Ignore sub-pixel noise when deciding whether the strip must relayout.
        /// Windowed / non-integer canvas scale makes 0.01–0.4 unit wobble look like the whole bar jittering.
        /// </summary>
        private const float LayoutDirtyEpsilon = 0.5f;

        private int _lastScreenW = -1;
        private int _lastScreenH = -1;
        private Vector2 _lastCanvasSize = new Vector2(-1f, -1f);
        private Vector2 _lastMinimapSize = new Vector2(-1f, -1f);
        private Vector2 _lastMinimapPos = new Vector2(float.NaN, float.NaN);
        private float _lastInsetL = float.NaN;
        private float _lastInsetB = float.NaN;
        private float _lastBarH = -1f;
        private float _lastButtonW = -1f;
        private float _lastSpacing = -1f;

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
            // [TITAN-ORBIT] Do not force-hide here — this component lives on gameplayRoot, so any
            // brief OnDisable/OnEnable would blink the whole strip for a frame.
            _lastShowActive = false;
        }

        private void OnDisable()
        {
            if (rootPanel != null)
                rootPanel.SetActive(false);
            _lastShowActive = false;
        }

        /// <summary>
        /// Resolves live ship + attrs, or holds the last-good cache while Instantiates gate ship scans.
        /// </summary>
        /// <returns>False only when there is no ship and no cache to show.</returns>
        private bool TryGetUpgradeHudSnapshot(out ShipState ship, out ShipAttributeUpgradeState attrs)
        {
            // --- Live ECS read ---
            bool hasShip = EcsGameBridge.TryGetLocalShipState(out ship);
            bool hasAttrs = EcsGameBridge.TryGetLocalShipAttributeUpgrades(out attrs);

            if (hasShip)
            {
                _cachedShip = ship;
                if (hasAttrs)
                    _cachedAttrs = attrs;
                else if (_hasHudCache)
                    attrs = _cachedAttrs;
                else
                    attrs = default;
                _hasHudCache = true;
                return true;
            }

            // --- Hold last good snapshot during GhostSpawnBacklog ---
            // [TITAN-ORBIT] Same pattern as ShipSpeedometerHUD: pose / HasLocalPlayerShip means the
            // ship still exists; only the entity query was skipped for Crash!!! safety.
            if (_hasHudCache &&
                (EcsGameBridge.HasLocalPlayerShip() || ShipDisplayPose.HasLocalPose) &&
                !ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                ship = _cachedShip;
                attrs = _cachedAttrs;
                return true;
            }

            ship = default;
            attrs = default;
            return false;
        }

        /// <summary>Gameplay gate only — does not require the bar to already exist.</summary>
        private bool CanShowUpgradeBar()
        {
            // --- CanShowUpgradeBar ---
            if (!upgradeBarEnabled)
                return false;
            if (!EcsGameBridge.HasLocalPlayerShip() && !(_hasHudCache && ShipDisplayPose.HasLocalPose))
                return false;
            if (!TryGetUpgradeHudSnapshot(out var ship, out _))
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
            // --- Attempt resolution ---
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

        /// <summary>Whole-canvas-unit snap — stops half-pixel shimmer in windowed / scaled canvases.</summary>
        private static float SnapUi(float v) => Mathf.Round(v);

        private static bool NearlyEqual(float a, float b) => Mathf.Abs(a - b) < LayoutDirtyEpsilon;

        private void EnsureMinimapRectCached()
        {
            // --- Cache minimap RectTransform ---
            if (_cachedMinimapRect != null)
                return;
            var minimap = Object.FindFirstObjectByType<MinimapController>();
            if (minimap != null)
                _cachedMinimapRect = minimap.transform as RectTransform;
        }

        private static bool TryGetMinimapLeftLocalX(RectTransform layoutSpace, RectTransform minimapRect, out float minimapLeftLocalX)
        {
            // --- Attempt resolution ---
            minimapLeftLocalX = 0f;
            if (layoutSpace == null || minimapRect == null)
                return false;
            Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(layoutSpace, minimapRect);
            minimapLeftLocalX = b.min.x;
            return true;
        }

        /// <summary>
        /// True when screen, canvas, or minimap geometry changed enough to justify a strip relayout.
        /// Sub-pixel wobble from CalculateRelativeRectTransformBounds is intentionally ignored.
        /// </summary>
        private bool IsStripLayoutDirty()
        {
            // --- Dirty check (no writes) ---
            if (Screen.width != _lastScreenW || Screen.height != _lastScreenH)
                return true;

            if (!TryResolveLayoutCanvas())
                return false;

            Vector2 canvasSize = _layoutCanvasRect.rect.size;
            if (!NearlyEqual(canvasSize.x, _lastCanvasSize.x) || !NearlyEqual(canvasSize.y, _lastCanvasSize.y))
                return true;

            EnsureMinimapRectCached();
            if (_cachedMinimapRect != null)
            {
                Vector2 mmSize = _cachedMinimapRect.sizeDelta;
                Vector2 mmPos = _cachedMinimapRect.anchoredPosition;
                if (!NearlyEqual(mmSize.x, _lastMinimapSize.x) || !NearlyEqual(mmSize.y, _lastMinimapSize.y))
                    return true;
                if (float.IsNaN(_lastMinimapPos.x) ||
                    !NearlyEqual(mmPos.x, _lastMinimapPos.x) ||
                    !NearlyEqual(mmPos.y, _lastMinimapPos.y))
                    return true;
            }

            return false;
        }

        private void RememberLayoutInputs()
        {
            // --- Latch inputs after a successful layout pass ---
            _lastScreenW = Screen.width;
            _lastScreenH = Screen.height;
            if (_layoutCanvasRect != null)
                _lastCanvasSize = _layoutCanvasRect.rect.size;
            EnsureMinimapRectCached();
            if (_cachedMinimapRect != null)
            {
                _lastMinimapSize = _cachedMinimapRect.sizeDelta;
                _lastMinimapPos = _cachedMinimapRect.anchoredPosition;
            }
        }

        private bool TryComputeStripMetrics(out float insetL, out float insetB, out float availableWidth, out float barH, out float buttonW, out float spacing, bool forceCanvasUpdate)
        {
            // --- Attempt resolution ---
            insetL = insetB = availableWidth = barH = buttonW = spacing = 0f;
            if (!TryResolveLayoutCanvas())
                return false;

            // [UNITY] ForceUpdateCanvases is expensive — only on first build / forced layout.
            if (forceCanvasUpdate)
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

            EnsureMinimapRectCached();
            if (TryGetMinimapLeftLocalX(_layoutCanvasRect, _cachedMinimapRect, out float minimapLeftLocalX))
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

            // --- Pixel snap (stable under windowed / non-integer canvas scale) ---
            // [UNITY] Fractional RectTransform sizes shimmer when the game view is not 1:1 pixels.
            insetL = SnapUi(insetL);
            insetB = SnapUi(insetB);
            spacing = SnapUi(spacing);
            barH = SnapUi(barH);
            buttonW = SnapUi(buttonW);
            availableWidth = 10f * buttonW + 9f * spacing;
            return availableWidth > 1f;
        }

        private void RefreshUpgradeStripLayout(bool force)
        {
            // --- RefreshUpgradeStripLayout ---
            if (!_uiBuilt || _stripRootRect == null)
                return;
            if (!TryComputeStripMetrics(out float insetL, out float insetB, out float availableWidth, out float barH, out float buttonW, out float spacing, forceCanvasUpdate: force))
                return;

            RememberLayoutInputs();

            // Skip writes when snapped metrics match last apply — no per-frame position chase.
            bool metricsUnchanged =
                !force &&
                NearlyEqual(availableWidth, _lastLayoutWidth) &&
                NearlyEqual(insetL, _lastInsetL) &&
                NearlyEqual(insetB, _lastInsetB) &&
                NearlyEqual(barH, _lastBarH) &&
                NearlyEqual(buttonW, _lastButtonW) &&
                NearlyEqual(spacing, _lastSpacing);
            if (metricsUnchanged)
                return;

            _lastLayoutWidth = availableWidth;
            _lastInsetL = insetL;
            _lastInsetB = insetB;
            _lastBarH = barH;
            _lastButtonW = buttonW;
            _lastSpacing = spacing;

            _stripRootRect.anchoredPosition = new Vector2(insetL, insetB);
            _stripRootRect.sizeDelta = new Vector2(availableWidth, barH);

            float buttonH = SnapUi(barH - E(6f));
            for (int i = 0; i < 10; i++)
            {
                if (_buttonRects[i] == null)
                    continue;
                _buttonRects[i].anchorMin = new Vector2(0f, 0.5f);
                _buttonRects[i].anchorMax = new Vector2(0f, 0.5f);
                _buttonRects[i].pivot = new Vector2(0f, 0.5f);
                // Integer step so each slot sits on a whole canvas unit.
                _buttonRects[i].anchoredPosition = new Vector2(i * (buttonW + spacing), 0f);
                _buttonRects[i].sizeDelta = new Vector2(buttonW, buttonH);

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
            // --- Ensure setup ---
            if (_uiBuilt || !upgradeBarEnabled || !CanShowUpgradeBar())
                return;
            BuildUI();
        }

        private void BuildUI()
        {
            // --- Build data ---
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
            // --- Create instance ---
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
            // --- Per-slot tick paint ---
            if (tickContainers == null || index < 0 || index >= tickContainers.Length || tickContainers[index] == null) return;
            maxLevel = Mathf.Clamp(maxLevel, 0, 7);
            Transform container = tickContainers[index].transform;
            int childCount = container.childCount;

            // Rebuild only when the allowed max level changes (ship level-up).
            // [UNITY] Destroy() is deferred to end-of-frame — CreateTickMarks right after would
            // duplicate children until then (same bug MinimapController fixed with DestroyImmediate).
            if (childCount != maxLevel)
            {
                for (int i = container.childCount - 1; i >= 0; i--)
                    DestroyImmediate(container.GetChild(i).gameObject);
                CreateTickMarks(tickContainers[index], maxLevel);
                childCount = maxLevel;
                _lastTickLevels[index] = -1; // force color refresh after rebuild
            }

            // Skip Image.color writes when fill count is unchanged (avoids per-frame dirty).
            if (_lastTickLevels[index] == currentLevel && _lastMaxUpgrades == maxLevel)
                return;

            for (int i = 0; i < childCount; i++)
            {
                Image img = container.GetChild(i).GetComponent<Image>();
                if (img != null)
                    img.color = i < currentLevel ? new Color(1f, 1f, 0.9f, 1f) : new Color(0.3f, 0.3f, 0.35f, 0.8f);
            }

            _lastTickLevels[index] = currentLevel;
        }

        private void Update()
        {
            // --- Per-frame refresh ---
            if (!upgradeBarEnabled)
                return;

            EnsureUiBuilt();
            if (rootPanel == null)
                return;

            bool show = CanShowUpgradeBar();

            // Only toggle active when visibility actually changes (avoids layout rebuild thrash).
            if (show != _lastShowActive)
            {
                rootPanel.SetActive(show);
                _lastShowActive = show;
            }

            if (!show)
                return;

            if (!TryGetUpgradeHudSnapshot(out var ship, out var attrs))
                return;

            int maxUpgrades = ShipAttributeUpgradeLogic.GetMaxUpgrades(ship.ShipLevel);
            int cost = ShipAttributeUpgradeLogic.GetUpgradeCost(ship.ShipLevel);
            bool maxChanged = maxUpgrades != _lastMaxUpgrades;
            bool costChanged = cost != _lastCost;

            for (int i = 0; i < 10; i++)
            {
                int current = ShipAttributeUpgradeLogic.GetAttributeLevel(attrs, i);
                UpdateTickMarks(i, current, maxUpgrades);

                bool canUpgrade = current < maxUpgrades && ship.CurrentGems >= cost - 0.01f;
                // Button defaults to interactable=true — seed once so unaffordable slots disable on first paint.
                if (buttons[i] != null && (!_slotVisualsSeeded || _lastInteractable[i] != canUpgrade))
                {
                    buttons[i].interactable = canUpgrade;
                    _lastInteractable[i] = canUpgrade;
                }

                if (costLabels[i] == null)
                    continue;

                string costText;
                bool showGemIcon;
                if (current >= maxUpgrades)
                {
                    costText = "MAX";
                    showGemIcon = false;
                }
                else
                {
                    costText = cost.ToString();
                    showGemIcon = gemCostIconSprite != null;
                }

                // Dirty-check TMP / icon — farming asteroids changes gems every frame; skip when text identical.
                if (costChanged || maxChanged || _lastCostText[i] != costText)
                {
                    costLabels[i].text = costText;
                    _lastCostText[i] = costText;
                    if (costGemIcons[i] != null)
                        costGemIcons[i].enabled = showGemIcon;
                }
            }

            _lastMaxUpgrades = maxUpgrades;
            _lastCost = cost;
            _slotVisualsSeeded = true;
        }

        private void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (!upgradeBarEnabled)
                return;

            EnsureUiBuilt();

            // Layout only when screen / canvas / minimap geometry actually changes — not every frame.
            // Continuous remeasure against the minimap left edge caused sub-pixel button jitter
            // (especially visible in a windowed Game view that is not 1:1 with canvas units).
            if (_uiBuilt && IsStripLayoutDirty())
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

        /// <summary>
        /// Client-side pre-check then RPC — server re-validates gems/caps in ShipAttributeUpgradeLogic.
        /// </summary>
        private void TryUpgrade(int index)
        {
            // --- Attempt resolution ---
            if (!CanShowUpgradeBar())
                return;
            if (index < 0 || index > 9)
                return;
            if (!TryGetUpgradeHudSnapshot(out var ship, out var attrs))
                return;

            int current = ShipAttributeUpgradeLogic.GetAttributeLevel(attrs, index);
            if (current >= ShipAttributeUpgradeLogic.GetMaxUpgrades(ship.ShipLevel)) return;
            if (ship.CurrentGems < ShipAttributeUpgradeLogic.GetUpgradeCost(ship.ShipLevel) - 0.01f) return;

            // [NETCODE] Authoritative purchase runs on server after RPC delivery.
            MoonOrbitRpcClient.PurchaseAttributeUpgrade(index);
        }
    }
}
