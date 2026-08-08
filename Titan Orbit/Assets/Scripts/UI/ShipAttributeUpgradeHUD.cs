using TitanOrbit.Core;
using TitanOrbit.Data;
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
    /// <para>
    /// [TITAN-ORBIT] Quick-stat chips sit above each button (value + next step + Lv). Hover opens
    /// a calculation card from <see cref="ShipAbilityStatBreakdown"/> (parts → stack → tier → ability).
    /// </para>
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
        [Tooltip("Height of the quick-stat chip band above each ability button.")]
        [SerializeField] private float chipBandHeight = 44f;
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

        // --- Quick-stat chips above each ability button ---
        private RectTransform[] _chipRects = new RectTransform[10];
        private TextMeshProUGUI[] _chipValueTexts = new TextMeshProUGUI[10];
        private readonly string[] _lastChipText = new string[10];
        private GameObject _abilityTipPanel;
        private RectTransform _abilityTipRect;
        private TextMeshProUGUI _abilityTipLabel;
        /// <summary>[TITAN-ORBIT] Sci-fi chrome handles — accent stripe recolors per ability category.</summary>
        private ShipStatTooltipChrome.Handles _abilityTipChrome;
        private int? _activeAbilityTipIndex;
        private int? _pendingHideAbilityTip;
        private string _lastAbilityTipBody = "";
        private ShipSpeedometerHUD _cachedSpeedometer;
        private bool _triedSpeedometerLookup;
        private float _lastChipBandH = -1f;

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
            float chipH = SnapUi(S(chipBandHeight));
            bool metricsUnchanged =
                !force &&
                NearlyEqual(availableWidth, _lastLayoutWidth) &&
                NearlyEqual(insetL, _lastInsetL) &&
                NearlyEqual(insetB, _lastInsetB) &&
                NearlyEqual(barH, _lastBarH) &&
                NearlyEqual(buttonW, _lastButtonW) &&
                NearlyEqual(spacing, _lastSpacing) &&
                NearlyEqual(chipH, _lastChipBandH);
            if (metricsUnchanged)
                return;

            _lastLayoutWidth = availableWidth;
            _lastInsetL = insetL;
            _lastInsetB = insetB;
            _lastBarH = barH;
            _lastButtonW = buttonW;
            _lastSpacing = spacing;
            _lastChipBandH = chipH;

            // [TITAN-ORBIT] Strip = chip band on top + ability buttons below.
            float gap = SnapUi(E(4f));
            float buttonH = SnapUi(barH - E(6f));
            float totalH = SnapUi(buttonH + gap + chipH);
            _stripRootRect.anchoredPosition = new Vector2(insetL, insetB);
            _stripRootRect.sizeDelta = new Vector2(availableWidth, totalH);

            for (int i = 0; i < 10; i++)
            {
                float x = i * (buttonW + spacing);
                if (_buttonRects[i] != null)
                {
                    // Buttons sit on the strip floor.
                    _buttonRects[i].anchorMin = new Vector2(0f, 0f);
                    _buttonRects[i].anchorMax = new Vector2(0f, 0f);
                    _buttonRects[i].pivot = new Vector2(0f, 0f);
                    _buttonRects[i].anchoredPosition = new Vector2(x, 0f);
                    _buttonRects[i].sizeDelta = new Vector2(buttonW, buttonH);
                }

                if (_chipRects[i] != null)
                {
                    _chipRects[i].anchorMin = new Vector2(0f, 0f);
                    _chipRects[i].anchorMax = new Vector2(0f, 0f);
                    _chipRects[i].pivot = new Vector2(0f, 0f);
                    _chipRects[i].anchoredPosition = new Vector2(x, buttonH + gap);
                    _chipRects[i].sizeDelta = new Vector2(buttonW, chipH);
                }

                if (titleTexts[i] != null)
                    titleTexts[i].fontSize = E(titleFontSize);
                if (keyLabels[i] != null)
                    keyLabels[i].fontSize = F(13f);
                if (costLabels[i] != null)
                    costLabels[i].fontSize = F(11f);
                if (_chipValueTexts[i] != null)
                    _chipValueTexts[i].fontSize = F(11f);
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

                var chip = CreateStatChip(rootPanel.transform, i, statColor);
                _chipRects[i] = chip.chipRect;
                _chipValueTexts[i] = chip.valueText;
            }

            BuildAbilityTipPanel();
            RefreshUpgradeStripLayout(force: true);
        }

        /// <summary>
        /// Quick-stat chip above one ability button — short label, big value, muted +step / Lv.
        /// </summary>
        (RectTransform chipRect, TextMeshProUGUI valueText) CreateStatChip(Transform parent, int index, Color statColor)
        {
            GameObject chipObj = new GameObject($"StatChip_{index}");
            chipObj.transform.SetParent(parent, false);
            RectTransform chipRect = chipObj.AddComponent<RectTransform>();

            Image bg = chipObj.AddComponent<Image>();
            Color glass = new Color(0.04f, 0.06f, 0.09f, 0.88f);
            bg.color = glass;
            bg.raycastTarget = true;

            var outline = chipObj.AddComponent<Outline>();
            Color accent = statColor;
            accent.a = 0.85f;
            outline.effectColor = accent;
            outline.effectDistance = new Vector2(E(1f), E(1f));

            // Top accent bar
            GameObject accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(chipObj.transform, false);
            RectTransform accentRt = accentGo.AddComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.offsetMin = new Vector2(E(2f), E(-3f));
            accentRt.offsetMax = new Vector2(E(-2f), E(-1f));
            Image accentImg = accentGo.AddComponent<Image>();
            accentImg.color = accent;
            accentImg.raycastTarget = false;

            GameObject textGo = new GameObject("ChipText");
            textGo.transform.SetParent(chipObj.transform, false);
            RectTransform textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(E(3f), E(2f));
            textRt.offsetMax = new Vector2(E(-3f), E(-4f));
            TextMeshProUGUI valueText = textGo.AddComponent<TextMeshProUGUI>();
            valueText.richText = true;
            valueText.enableWordWrapping = true;
            valueText.overflowMode = TextOverflowModes.Truncate;
            valueText.alignment = TextAlignmentOptions.Center;
            valueText.fontSize = F(11f);
            valueText.color = Color.white;
            valueText.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                valueText.font = TMP_Settings.defaultFontAsset;
            string shortLabel = ShipAbilityCategoryColors.PowerBreakdownStatLabels[index];
            valueText.text = $"<color=#AAAAAA>{shortLabel}</color>\n—";

            var zone = chipObj.AddComponent<ShipAbilityStatHoverZone>();
            zone.Owner = this;
            zone.AbilityIndex = index;

            return (chipRect, valueText);
        }

        /// <summary>
        /// Floating calculation card for ability-chip rollovers.
        /// [TITAN-ORBIT] Uses <see cref="ShipStatTooltipChrome"/> (Shift cut-frame + accent) so the
        /// tip matches orbit-station / spin-card sci-fi language instead of a plain debug box.
        /// </summary>
        void BuildAbilityTipPanel()
        {
            // Same canvas parent as the strip so anchoredPosition math matches GetUpgradeStripReserveHeight space.
            Transform tipParent = _layoutCanvasRect != null ? (Transform)_layoutCanvasRect : transform;
            _abilityTipChrome = ShipStatTooltipChrome.Build(
                "ShipAbilityStatTooltip",
                tipParent,
                "ABILITY MATRIX",
                E(560f),
                E(160f),
                _elementScale);
            _abilityTipPanel = _abilityTipChrome.Root;
            _abilityTipRect = _abilityTipChrome.RootRect;
            _abilityTipLabel = _abilityTipChrome.BodyLabel;
            if (_abilityTipRect != null)
                _abilityTipRect.pivot = new Vector2(0.5f, 0f);
            if (_abilityTipLabel != null)
                _abilityTipLabel.fontSize = F(11f);

            _abilityTipPanel.transform.SetAsLastSibling();
        }

        /// <summary>Pointer entered a quick-stat chip — show that ability's calculation card.</summary>
        public void ShowAbilityStatTooltip(int abilityIndex)
        {
            if (!_uiBuilt || _abilityTipPanel == null || _abilityTipLabel == null)
                return;
            if (abilityIndex < 0 || abilityIndex > 9)
                return;

            _pendingHideAbilityTip = null;
            _activeAbilityTipIndex = abilityIndex;
            // [TITAN-ORBIT] Recolor chrome accent to the hovered ability's ODEMC category tone.
            ShipStatTooltipChrome.ApplyAccent(
                in _abilityTipChrome,
                ShipStatTooltipChrome.AccentForAbilityIndex(abilityIndex));
            RefreshAbilityTipContent();
            PositionAbilityTipPanel(abilityIndex);
            // Draw above leaderboard / other HUD so bars and names cannot bleed through.
            _abilityTipPanel.transform.SetAsLastSibling();
            if (!_abilityTipPanel.activeSelf)
                _abilityTipPanel.SetActive(true);
        }

        /// <summary>Pointer left a chip — defer hide so neighboring chips can cancel.</summary>
        public void HideAbilityStatTooltip(int abilityIndex)
        {
            if (_activeAbilityTipIndex != abilityIndex)
                return;
            _pendingHideAbilityTip = abilityIndex;
        }

        void FlushPendingAbilityTipHide()
        {
            if (!_pendingHideAbilityTip.HasValue)
                return;
            int pending = _pendingHideAbilityTip.Value;
            _pendingHideAbilityTip = null;
            if (_activeAbilityTipIndex != pending)
                return;
            _activeAbilityTipIndex = null;
            if (_abilityTipPanel != null && _abilityTipPanel.activeSelf)
                _abilityTipPanel.SetActive(false);
        }

        void RefreshAbilityTipContent()
        {
            if (_abilityTipLabel == null || !_activeAbilityTipIndex.HasValue)
                return;
            if (!TryResolveChipLiveContext(out var parts, out var live, out var attrs))
            {
                _abilityTipLabel.text = "<color=#5B7A94>// AWAITING SHIP LINK...</color>";
                return;
            }

            string body = ShipAbilityStatBreakdown.BuildForAbilityIndex(
                _activeAbilityTipIndex.Value, in parts, in live, in attrs);
            if (body == _lastAbilityTipBody)
                return;
            _lastAbilityTipBody = body;
            _abilityTipLabel.text = body;
            _abilityTipLabel.ForceMeshUpdate(true);
            if (_abilityTipRect != null)
            {
                float tipW = _abilityTipRect.sizeDelta.x;
                // [TITAN-ORBIT] ExtraHeightPadding covers caption bar + frame insets from chrome.
                float tipH = Mathf.Max(
                    E(120f),
                    _abilityTipLabel.preferredHeight + _abilityTipChrome.ExtraHeightPadding);
                _abilityTipRect.sizeDelta = new Vector2(tipW, tipH);
            }
        }

        /// <summary>
        /// Places the ability calculation card above the hovered chip, then clamps X/Y so the tip
        /// stays on-screen and clear of the bottom-right minimap (wide tips near slot 0 or 9
        /// used to spill off the left edge or cover the map).
        /// </summary>
        /// <param name="abilityIndex">0–9 chip/button index.</param>
        void PositionAbilityTipPanel(int abilityIndex)
        {
            if (_abilityTipRect == null || _chipRects[abilityIndex] == null || _stripRootRect == null)
                return;

            // --- Preferred spot: above the chip, horizontally centered on that slot ---
            // Tip is parented to the layout canvas with the same anchors as the strip (bottom-left).
            // anchoredPosition is therefore in the same space as strip.anchoredPosition + chip local X.
            RectTransform chip = _chipRects[abilityIndex];
            _abilityTipRect.anchorMin = _stripRootRect.anchorMin;
            _abilityTipRect.anchorMax = _stripRootRect.anchorMax;
            _abilityTipRect.pivot = new Vector2(0.5f, 0f);

            float stripX = _stripRootRect.anchoredPosition.x;
            float stripY = _stripRootRect.anchoredPosition.y;
            float preferredX = stripX + chip.anchoredPosition.x + chip.sizeDelta.x * 0.5f;
            float tipBottom = stripY + chip.anchoredPosition.y + chip.sizeDelta.y + E(8f);

            float tipW = _abilityTipRect.sizeDelta.x;
            float tipH = _abilityTipRect.sizeDelta.y;
            float margin = E(8f);

            // --- Horizontal clamp (canvas left ↔ minimap left) ---
            // [TITAN-ORBIT] Same bounds the upgrade strip uses when measuring availableWidth.
            if (TryResolveLayoutCanvas())
            {
                Rect canvasRect = _layoutCanvasRect.rect;
                float canvasXMin = canvasRect.xMin;
                float halfW = tipW * 0.5f;

                // Tip left edge in canvas-local = canvasXMin + (centerX - halfW).
                // Keep at least `margin` inside the canvas left.
                float minCenterX = halfW + margin;

                // Tip right edge must stay left of the minimap (or canvas right if no minimap).
                EnsureMinimapRectCached();
                float maxCenterX;
                if (TryGetMinimapLeftLocalX(_layoutCanvasRect, _cachedMinimapRect, out float minimapLeftLocalX))
                {
                    // canvasXMin + centerX + halfW <= minimapLeft - gap
                    maxCenterX = minimapLeftLocalX - S(minimapHorizontalGap) - canvasXMin - halfW;
                }
                else
                {
                    maxCenterX = canvasRect.width - margin - halfW;
                }

                // If the tip is wider than the safe band, shrink width so clamp can succeed.
                float safeSpan = maxCenterX - minCenterX;
                if (safeSpan < 0f)
                {
                    float maxTipW = tipW + safeSpan * 2f;
                    maxTipW = Mathf.Max(E(220f), maxTipW);
                    tipW = maxTipW;
                    _abilityTipRect.sizeDelta = new Vector2(tipW, tipH);
                    halfW = tipW * 0.5f;
                    minCenterX = halfW + margin;
                    if (TryGetMinimapLeftLocalX(_layoutCanvasRect, _cachedMinimapRect, out minimapLeftLocalX))
                        maxCenterX = minimapLeftLocalX - S(minimapHorizontalGap) - canvasXMin - halfW;
                    else
                        maxCenterX = canvasRect.width - margin - halfW;
                }

                preferredX = Mathf.Clamp(preferredX, minCenterX, Mathf.Max(minCenterX, maxCenterX));

                // --- Vertical clamp (keep tip under the top of the canvas) ---
                // Tip top in canvas-local = canvas.yMin + tipBottom + tipH.
                float maxBottom = canvasRect.height - margin - tipH;
                if (tipBottom > maxBottom)
                    tipBottom = Mathf.Max(stripY + chip.sizeDelta.y + E(4f), maxBottom);
            }

            _abilityTipRect.anchoredPosition = new Vector2(preferredX, tipBottom);
        }

        bool TryResolveChipLiveContext(
            out ShipSpeedometerStatTooltips.PartCache parts,
            out ShipSpeedometerStatTooltips.LiveContext live,
            out ShipAttributeUpgradeState attrs)
        {
            parts = default;
            live = default;
            attrs = default;
            if (!TryGetUpgradeHudSnapshot(out _, out attrs))
                return false;

            if (!_triedSpeedometerLookup)
            {
                _triedSpeedometerLookup = true;
                _cachedSpeedometer = Object.FindFirstObjectByType<ShipSpeedometerHUD>();
            }

            if (_cachedSpeedometer != null
                && _cachedSpeedometer.TryGetTooltipSharedState(out parts, out live))
                return true;

            // Fallback before speedometer paints: ship vitals + local mass tax so MS/TS chips
            // still show post-tax cruise/turn instead of raw chassis.
            live = new ShipSpeedometerStatTooltips.LiveContext
            {
                Ship = _cachedShip,
                ChassisMaxSpeed = 0f,
                ChassisAccel = 0f,
                ChassisTurnDeg = 0f,
                CruiseMaxSpeed = 0f,
                TaxedAccel = 0f,
                TaxedTurnDeg = 0f,
                TotalMass = 0f,
            };

            // Best-effort effective stats from chassis id when available.
            if (EcsGameBridge.TryGetLocalShipState(out ShipState ship)
                && ShipStatApplyLogic.TryResolveChassisId(
                    ship.Team,
                    ship.ShipLevel,
                    ship.BranchIndex,
                    out string chassisId,
                    allowFallback: true,
                    ship.ShipFamilyConfigIndex)
                && ShipStatApplyLogic.TryGetBaseStatsForChassis(chassisId, ship.ShipLevel, out ShipComponentAbilityStats baseStats))
            {
                ShipAttributeUpgradeLogic.ApplyMultipliers(ref baseStats, in attrs);
                live.EffectiveStats = baseStats;
                live.ChassisMaxSpeed = baseStats.moveSpeed;
                live.ChassisAccel = baseStats.accelerationCap;
                // turnSpeed on ability stats may be definition units; speedometer converts when present.
                live.ChassisTurnDeg = baseStats.turnSpeed;
                live.Ship = ship;

                // [TITAN-ORBIT] Hull size unknown until speedometer paints — tax cargo only so
                // MS/TS still drop below chassis instead of showing pre-tax. Full ComponentSize
                // arrives via TryGetTooltipSharedState on the next speedometer frame.
                float componentSize = ShipMassLogic.MinMass;
                ShipMobilityResolution.TaxedMotorStats taxed = ShipMobilityResolution.ApplyMassTaxFromCargo(
                    live.ChassisMaxSpeed,
                    live.ChassisAccel,
                    live.ChassisTurnDeg,
                    ship.CurrentGems,
                    ship.CurrentPeople,
                    componentSize);
                live.TotalMass = taxed.TotalMass;
                live.CruiseMaxSpeed = taxed.MaxSpeed;
                live.TaxedAccel = taxed.EngineThrust;
                live.TaxedTurnDeg = taxed.RotationSpeed;
                live.ComponentSize = componentSize;
            }

            return true;
        }

        /// <summary>Formats one chip TMP line: label, big value, muted +step and Lv.</summary>
        static string FormatChipText(int index, float value, float nextStep, int abilityLv, string unit)
        {
            string label = ShipAbilityCategoryColors.PowerBreakdownStatLabels[index];
            string val = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var sb = new System.Text.StringBuilder(64);
            sb.Append("<color=#AAAAAA>").Append(label).Append("</color>\n");
            sb.Append("<b>").Append(val).Append(unit).Append("</b>");
            if (nextStep > 0.0001f)
            {
                sb.Append("\n<size=85%><color=#88AACC>+")
                    .Append(nextStep.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                if (index != 6)
                    sb.Append("</color></size>");
                else
                    sb.Append("</color></size>");
            }

            if (abilityLv > 0)
                sb.Append(" <size=85%><color=#CCCCAA>Lv").Append(abilityLv).Append("</color></size>");
            return sb.ToString();
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
                if (!show && _abilityTipPanel != null)
                {
                    _activeAbilityTipIndex = null;
                    _pendingHideAbilityTip = null;
                    _abilityTipPanel.SetActive(false);
                }
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

            // --- Quick-stat chips (value + step + Lv) ---
            RefreshChipValues(in ship, in attrs);

            if (_activeAbilityTipIndex.HasValue)
            {
                RefreshAbilityTipContent();
                PositionAbilityTipPanel(_activeAbilityTipIndex.Value);
            }

            FlushPendingAbilityTipHide();
        }

        /// <summary>Paints chip TMP from live / speedometer-shared context.</summary>
        void RefreshChipValues(in ShipState ship, in ShipAttributeUpgradeState attrs)
        {
            _ = ship;
            TryResolveChipLiveContext(out _, out var live, out _);
            // Prefer attrs from snapshot (already have) over tip resolve.
            for (int i = 0; i < 10; i++)
            {
                if (_chipValueTexts[i] == null)
                    continue;

                ShipAbilityStatBreakdown.ResolveChipDisplay(
                    i, in live, in attrs, out float value, out float nextStep, out int abilityLv, out string unit);
                string text = FormatChipText(i, value, nextStep, abilityLv, unit);
                if (_lastChipText[i] == text)
                    continue;
                _lastChipText[i] = text;
                _chipValueTexts[i].text = text;
            }
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
