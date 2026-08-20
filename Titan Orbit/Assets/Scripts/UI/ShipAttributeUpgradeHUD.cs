using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using TMPro;
using Unity.Entities;
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
    /// Most abilities are +10% per purchase; Move Speed adds one chassis PerExtraLevel step
    /// (move + accel + OD drain together) — see ShipAttributeUpgradeLogic.
    /// <para>
    /// [TITAN-ORBIT] Optional quick-stat chips above each button show <b>current</b> and
    /// <c>+per-buy</c> (toggle via a small STATS control). Fire Power's chip is sustained
    /// DPS (<c>firePower × fireRate</c>), not damage per shot — same score as the Orbit
    /// Menu power-bar Fire Power lane. Bottom buttons keep name + gem cost
    /// and paint three purchase states: Ready (affordable), Locked (not enough gems), Maxed.
    /// Both rows share dark-glass + category-accent chrome (space-gamer HUD). Chip hover opens
    /// a calculation card from <see cref="ShipAbilityStatBreakdown"/> when the STATS row is on.
    /// That card uses a nested Canvas (sort 150) so rockets, brakes, turret pad, and sibling HUD
    /// cannot paint through it.
    /// MEGA hulls keep the ten buttons visible but disabled (no Extra Level purchases) and hide
    /// the little tick squares so the strip does not look like upgrades are still available.
    /// MEGA identity is latched through gem Instantiates (plow destroy) so ticks/costs do not flicker.
    /// Quick-stat chips and hover details use <see cref="MegaShipStatsCalculator"/> (no +per-buy).
    /// Chip values and tip bodies are rebuilt when the ship / ability snapshot key changes
    /// (new ship or ability purchase) — never every frame for live HP/speed/cargo.
    /// The snapshot key latches only after chassis stats <b>and</b> hull ComponentSize are ready
    /// (mass tax for MS/TS). Painting with a MinMass placeholder froze an untaxed Move Speed until
    /// the player toggled [STATS]. ComponentSize is part of the snapshot key so late hull refs repaint.
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
        [Tooltip("Height of the quick-stat chip band (value + +per-buy only — no title line).")]
        [SerializeField] private float chipBandHeight = 34f;
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

        [Header("STATS row toggle")]
        [Tooltip("When on, the top value/+per-buy chips and their hover tips are available.")]
        [SerializeField] private bool statsChipsVisible = true;
        [Tooltip("Width of the small STATS toggle control (logical pixels before scale).")]
        [SerializeField] private float statsToggleWidth = 58f;
        [Tooltip("Height of the small STATS toggle control (logical pixels before scale).")]
        [SerializeField] private float statsToggleHeight = 18f;

        [Header("Visual Styling — dark glass HUD")]
        [Tooltip("Near-black void glass fill shared by top chips and bottom upgrade buttons.")]
        [SerializeField] private Color glassFillColor = new Color(0.04f, 0.06f, 0.09f, 0.92f);
        [Tooltip("How much category colour bleeds into the glass fill when READY (0 = pure void, 1 = full flood).")]
        [SerializeField, Range(0f, 0.45f)] private float categoryFillBlend = 0.16f;
        [Tooltip("Subtle inner shade on bottom buttons (kept very dark).")]
        [SerializeField] private Color buttonInnerShadeColor = new Color(0f, 0f, 0f, 0.28f);

        [Header("Upgrade button states")]
        [Tooltip("Title / body text when the slot can be purchased (enough gems).")]
        [SerializeField] private Color readyTitleColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        [Tooltip("Fill darken + desaturate when LOCKED (not enough gems). Higher = flatter / greyer.")]
        [SerializeField, Range(0f, 1f)] private float lockedDim = 0.55f;
        [Tooltip("Cost digits when LOCKED — amber “can’t afford” signal.")]
        [SerializeField] private Color lockedCostColor = new Color(0.72f, 0.55f, 0.35f, 0.85f);
        [Tooltip("How much white is mixed into the ability colour for MAXED title / MAX label (proud completed chrome).")]
        [SerializeField, Range(0f, 0.55f)] private float maxedTitleBrighten = 0.32f;

        [Header("Cost icon")]
        [Tooltip("Shown next to the gem cost on each bottom upgrade slot. If empty, falls back to WorldStatLabelIcons.Gem.")]
        [SerializeField] private Sprite gemCostIconSprite;
        [SerializeField] private float gemIconSize = 11f;
        /// <summary>
        /// Off-white tint for gem icon + cost digits when READY. Not moon-label red.
        /// </summary>
        [SerializeField] private Color gemCostIconColor = new Color(0.9f, 0.92f, 0.95f, 1f);

        const string StatsChipsPrefsKey = "TitanOrbit.AbilityStatsChipsVisible";

        /// <summary>
        /// Nested-canvas sort for the hover calculation card.
        /// Main HUD canvas is 0; rocket / space-brake overlays are 80; turret pad is 120;
        /// orbit station is 200. 150 sits above gameplay HUD and below dock / death / match-end.
        /// <see cref="Transform.SetAsLastSibling"/> only wins inside one canvas — it cannot beat
        /// those overlay canvases.
        /// </summary>
        const int AbilityTipSortingOrder = 150;

        /// <summary>
        /// Purchase affordance for one bottom upgrade slot.
        /// Ready = can buy; Locked = room to level but not enough gems; Maxed = at ship-level cap;
        /// Unavailable = MEGA hull — button stays visible but purchases and tick squares are off.
        /// </summary>
        enum UpgradeSlotVisualState
        {
            Ready = 0,
            Locked = 1,
            Maxed = 2,
            Unavailable = 3
        }

        private static readonly string[] Titles =
        {
            "Fire Power", "Bullet Speed",
            "Health Cap", "Health Regen",
            "Energy Cap", "Energy Regen",
            "Move Speed", "Turn Speed",
            "Gem Cap", "Troop Cap"
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
        private Outline[] buttonOutlines = new Outline[10];
        private Image[] buttonAccentRails = new Image[10];
        private Color[] buttonCategoryColors = new Color[10];
        private TextMeshProUGUI[] keyLabels = new TextMeshProUGUI[10];
        private TextMeshProUGUI[] costLabels = new TextMeshProUGUI[10];
        private Image[] costGemIcons = new Image[10];
        private readonly UpgradeSlotVisualState[] _lastSlotVisualState =
        {
            (UpgradeSlotVisualState)(-1), (UpgradeSlotVisualState)(-1), (UpgradeSlotVisualState)(-1),
            (UpgradeSlotVisualState)(-1), (UpgradeSlotVisualState)(-1), (UpgradeSlotVisualState)(-1),
            (UpgradeSlotVisualState)(-1), (UpgradeSlotVisualState)(-1), (UpgradeSlotVisualState)(-1),
            (UpgradeSlotVisualState)(-1)
        };

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
        private bool _lastStatsChipsVisible = true;

        /// <summary>
        /// Fingerprint of ship identity + ability levels. When this changes we rebuild chips / tips.
        /// [TITAN-ORBIT] Avoids StringBuilder + TMP ForceMeshUpdate every Update (Profiler GC spike).
        /// </summary>
        private int _statsSnapshotKey = int.MinValue;

        // --- STATS toggle (shows/hides chip row + hover tips) ---
        private RectTransform _statsToggleRect;
        private TextMeshProUGUI _statsToggleLabel;
        private Image _statsToggleBg;

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
        private bool _slotVisualsSeeded;
        /// <summary>
        /// Last MEGA vs regular hull we painted. Null until the first Update so a MEGA spawn
        /// hides ticks immediately instead of waiting for a ship swap.
        /// </summary>
        private bool? _lastMegaHud;

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
            if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
                return false;
            if (HUDController.ShipUpgradeTreeObscuresHud || HUDController.MinimapExpandedObscuresHud)
                return false;

            return true;
        }

        /// <summary>
        /// True when the local hull is a MEGA. Stats chips stay useful; Extra Level purchases
        /// and the tick squares are blocked.
        /// <para>
        /// [TITAN-ORBIT] MEGA plow instantly destroys rocks → gem Instantiates →
        /// <see cref="ClientJoinSettleCache.GhostSpawnBacklog"/>. A miss from
        /// <see cref="TryGetLocalShipEntityOnWorld"/> used to read as “not MEGA” and flip the
        /// bottom strip (ticks + gem costs) for a frame. Seeded
        /// <see cref="EcsGameBridge.TryGetLocalMegaShipState"/> plus <see cref="_lastMegaHud"/>
        /// keep chrome stable through that burst.
        /// </para>
        /// </summary>
        bool IsLocalShipMega()
        {
            if (TryGetLocalMegaCatalogIndex(out _))
                return true;

            // --- Live regular hull ---
            // [HYBRID] Seeded ship state without MegaShipState means this is not a MEGA.
            if (EcsGameBridge.TryGetLocalShipState(out _))
                return false;

            // --- Instantiates miss: hold last painted identity ---
            // [TITAN-ORBIT] Do not treat a gated entity lookup as “sold the MEGA”.
            return _lastMegaHud == true;
        }

        /// <summary>
        /// Reads the local owner's <see cref="MegaShipState.CatalogIndex"/> when the hull is a MEGA.
        /// Uses the seeded local-ship lookup — no extra archetype gather (Join Team Crash!!! safe).
        /// </summary>
        /// <param name="catalogIndex">MEGA catalog row when this returns true.</param>
        /// <returns>True when the local ghost is a MEGA with a readable catalog index.</returns>
        static bool TryGetLocalMegaCatalogIndex(out ushort catalogIndex)
        {
            catalogIndex = 0;

            // --- Seeded / cached owner (safe during GhostSpawnBacklog) ---
            // [TITAN-ORBIT] TryGetLocalShipEntityOnWorld gathers and returns false while
            // ShouldSkipShipEntityQueries — that is the MEGA-plow UI flicker. Prefer the
            // Instantiates-hook seed the same way ShipState HUD reads do.
            if (EcsGameBridge.TryGetLocalMegaShipState(out MegaShipState mega))
            {
                catalogIndex = mega.CatalogIndex;
                return true;
            }

            return false;
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
                canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
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
            var minimap = UnityEngine.Object.FindFirstObjectByType<MinimapController>();
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
            bool chipsOn = statsChipsVisible;
            bool metricsUnchanged =
                !force &&
                NearlyEqual(availableWidth, _lastLayoutWidth) &&
                NearlyEqual(insetL, _lastInsetL) &&
                NearlyEqual(insetB, _lastInsetB) &&
                NearlyEqual(barH, _lastBarH) &&
                NearlyEqual(buttonW, _lastButtonW) &&
                NearlyEqual(spacing, _lastSpacing) &&
                NearlyEqual(chipH, _lastChipBandH) &&
                chipsOn == _lastStatsChipsVisible;
            if (metricsUnchanged)
                return;

            _lastLayoutWidth = availableWidth;
            _lastInsetL = insetL;
            _lastInsetB = insetB;
            _lastBarH = barH;
            _lastButtonW = buttonW;
            _lastSpacing = spacing;
            _lastChipBandH = chipH;
            _lastStatsChipsVisible = chipsOn;

            // [TITAN-ORBIT] Strip = optional chip band + ability buttons. STATS toggle sits top-left.
            float gap = SnapUi(E(4f));
            float buttonH = SnapUi(barH - E(6f));
            float toggleH = SnapUi(S(statsToggleHeight));
            float chipBand = chipsOn ? chipH : 0f;
            float chipGap = chipsOn ? gap : 0f;
            // Toggle always peeks above the button row (and above chips when they are on).
            float totalH = SnapUi(buttonH + chipGap + chipBand + gap + toggleH);
            _stripRootRect.anchoredPosition = new Vector2(insetL, insetB);
            _stripRootRect.sizeDelta = new Vector2(availableWidth, totalH);

            float toggleW = SnapUi(S(statsToggleWidth));
            if (_statsToggleRect != null)
            {
                _statsToggleRect.anchorMin = new Vector2(0f, 0f);
                _statsToggleRect.anchorMax = new Vector2(0f, 0f);
                _statsToggleRect.pivot = new Vector2(0f, 0f);
                _statsToggleRect.anchoredPosition = new Vector2(0f, buttonH + chipGap + chipBand + gap);
                _statsToggleRect.sizeDelta = new Vector2(toggleW, toggleH);
            }

            if (_statsToggleLabel != null)
                _statsToggleLabel.fontSize = F(10f);

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
                    bool showChip = chipsOn;
                    if (_chipRects[i].gameObject.activeSelf != showChip)
                        _chipRects[i].gameObject.SetActive(showChip);
                    if (showChip)
                    {
                        _chipRects[i].anchorMin = new Vector2(0f, 0f);
                        _chipRects[i].anchorMax = new Vector2(0f, 0f);
                        _chipRects[i].pivot = new Vector2(0f, 0f);
                        _chipRects[i].anchoredPosition = new Vector2(x, buttonH + gap);
                        _chipRects[i].sizeDelta = new Vector2(buttonW, chipH);
                    }
                }

                if (titleTexts[i] != null)
                    titleTexts[i].fontSize = E(titleFontSize);
                if (keyLabels[i] != null)
                    keyLabels[i].fontSize = F(13f);
                if (costLabels[i] != null)
                    costLabels[i].fontSize = F(11f);
                if (_chipValueTexts[i] != null)
                    _chipValueTexts[i].fontSize = F(12f);
            }

            RefreshStatsToggleVisual();
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

            // Restore last STATS-row choice (default on for first-time players).
            if (PlayerPrefs.HasKey(StatsChipsPrefsKey))
                statsChipsVisible = PlayerPrefs.GetInt(StatsChipsPrefsKey, 1) != 0;

            string[] keyStrings = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

            CreateStatsToggle(rootPanel.transform);

            for (int i = 0; i < 10; i++)
            {
                Color statColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(i);
                var btn = CreateUpgradeButton(rootPanel.transform, i, statColor, keyStrings[i]);
                buttons[i] = btn.button;
                titleTexts[i] = btn.titleText;
                tickContainers[i] = btn.tickContainer;
                buttonImages[i] = btn.bgImage;
                buttonOutlines[i] = btn.outline;
                buttonAccentRails[i] = btn.accentRail;
                buttonCategoryColors[i] = statColor;
                keyLabels[i] = btn.keyLabel;
                costLabels[i] = btn.costLabel;
                costGemIcons[i] = btn.costGemIcon;
                _buttonRects[i] = btn.buttonRect;
                _lastSlotVisualState[i] = (UpgradeSlotVisualState)(-1);

                var chip = CreateStatChip(rootPanel.transform, i, statColor);
                _chipRects[i] = chip.chipRect;
                _chipValueTexts[i] = chip.valueText;
            }

            BuildAbilityTipPanel();
            RefreshUpgradeStripLayout(force: true);
        }

        /// <summary>
        /// Small top-left STATS control — toggles the chip row and its hover tips on/off.
        /// </summary>
        void CreateStatsToggle(Transform parent)
        {
            GameObject go = new GameObject("StatsToggle");
            go.transform.SetParent(parent, false);
            _statsToggleRect = go.AddComponent<RectTransform>();

            _statsToggleBg = go.AddComponent<Image>();
            _statsToggleBg.raycastTarget = true;
            var outline = go.AddComponent<Outline>();
            outline.effectDistance = new Vector2(E(1f), E(1f));

            // Cool ice accent (not category-specific) — cockpit toggle chrome.
            Color ice = new Color(0.35f, 0.72f, 0.95f, 0.95f);
            ApplyGamerGlassChrome(_statsToggleBg, outline, ice, includeInnerShade: false);

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = _statsToggleBg;
            btn.onClick.AddListener(ToggleStatsChipsVisible);

            GameObject textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            RectTransform textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(E(2f), E(1f));
            textRt.offsetMax = new Vector2(E(-2f), E(-1f));
            _statsToggleLabel = textGo.AddComponent<TextMeshProUGUI>();
            _statsToggleLabel.alignment = TextAlignmentOptions.Center;
            _statsToggleLabel.fontStyle = FontStyles.Bold;
            _statsToggleLabel.fontSize = F(10f);
            _statsToggleLabel.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            _statsToggleLabel.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                _statsToggleLabel.font = TMP_Settings.defaultFontAsset;

            RefreshStatsToggleVisual();
        }

        /// <summary>Flips the STATS chip row and persists the choice.</summary>
        void ToggleStatsChipsVisible()
        {
            SetStatsChipsVisible(!statsChipsVisible);
        }

        /// <summary>
        /// Shows or hides the top value chips and their hover tooltips.
        /// Bottom upgrade buttons stay available either way.
        /// </summary>
        public void SetStatsChipsVisible(bool visible)
        {
            if (statsChipsVisible == visible && _uiBuilt)
            {
                RefreshStatsToggleVisual();
                return;
            }

            statsChipsVisible = visible;
            PlayerPrefs.SetInt(StatsChipsPrefsKey, visible ? 1 : 0);
            PlayerPrefs.Save();

            // Hide any open tip when collapsing the row (hover functionality off).
            if (!visible)
            {
                _activeAbilityTipIndex = null;
                _pendingHideAbilityTip = null;
                if (_abilityTipPanel != null && _abilityTipPanel.activeSelf)
                    _abilityTipPanel.SetActive(false);
            }
            else
            {
                // Turning STATS back on — force one chip rebuild on the next Update.
                _statsSnapshotKey = int.MinValue;
            }

            if (_uiBuilt)
                RefreshUpgradeStripLayout(force: true);
            else
                RefreshStatsToggleVisual();
        }

        /// <summary>Paints STATS label + accent for the current on/off state.</summary>
        void RefreshStatsToggleVisual()
        {
            if (_statsToggleLabel != null)
                _statsToggleLabel.text = "[STATS]";
            // Dim when off so the control still reads as a toggle, not a missing button.
            if (_statsToggleBg != null)
            {
                Color ice = new Color(0.35f, 0.72f, 0.95f, statsChipsVisible ? 0.95f : 0.45f);
                Color fill = Color.Lerp(glassFillColor, ice, statsChipsVisible ? categoryFillBlend : categoryFillBlend * 0.5f);
                fill.a = glassFillColor.a;
                _statsToggleBg.color = fill;
                var outline = _statsToggleBg.GetComponent<Outline>();
                if (outline != null)
                {
                    Color o = ice;
                    o.a = statsChipsVisible ? 0.9f : 0.4f;
                    outline.effectColor = o;
                }
            }

            if (_statsToggleLabel != null)
            {
                float a = statsChipsVisible ? 1f : 0.55f;
                _statsToggleLabel.color = new Color(0.88f, 0.92f, 0.98f, a);
            }
        }

        /// <summary>
        /// Shared dark-void glass + thin category accent — used by chips and bottom buttons.
        /// [TITAN-ORBIT] Matches titan-orbit-ui-space-gamer-theme (no full-panel colour floods).
        /// </summary>
        void ApplyGamerGlassChrome(Image fill, Outline outline, Color categoryAccent, bool includeInnerShade)
        {
            Color fillCol = Color.Lerp(glassFillColor, categoryAccent, categoryFillBlend);
            fillCol.a = glassFillColor.a;
            fill.color = fillCol;

            Color outlineCol = categoryAccent;
            outlineCol.a = 0.85f;
            outline.effectColor = outlineCol;
            outline.effectDistance = new Vector2(E(1f), E(1f));
            _ = includeInnerShade; // reserved — callers add InnerShade child when needed
        }

        /// <summary>
        /// Thin top accent rail (category tint only). Shared by chips and upgrade buttons.
        /// Returns the Image so hosts can recolor per Ready / Locked / Maxed state.
        /// </summary>
        static Image AddCategoryAccentRail(Transform parent, Color accent, float insetX, float thickness)
        {
            GameObject accentGo = new GameObject("Accent");
            accentGo.transform.SetParent(parent, false);
            RectTransform accentRt = accentGo.AddComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.offsetMin = new Vector2(insetX, -thickness);
            accentRt.offsetMax = new Vector2(-insetX, -1f);
            Image accentImg = accentGo.AddComponent<Image>();
            Color c = accent;
            c.a = 0.9f;
            accentImg.color = c;
            accentImg.raycastTarget = false;
            return accentImg;
        }

        /// <summary>
        /// Resolves Ready / Locked / Maxed from level vs ship-level cap and current gems.
        /// </summary>
        static UpgradeSlotVisualState ResolveUpgradeSlotState(int currentLevel, int maxUpgrades, float currentGems, int cost)
        {
            if (currentLevel >= maxUpgrades)
                return UpgradeSlotVisualState.Maxed;
            if (currentGems >= cost - 0.01f)
                return UpgradeSlotVisualState.Ready;
            return UpgradeSlotVisualState.Locked;
        }

        /// <summary>
        /// Paints one bottom upgrade button for Ready / Locked / Maxed / Unavailable.
        /// We drive colours ourselves — Unity's default Button grey fade fights dark-glass chrome.
        /// Called from Update when a slot's affordance changes (gems cross the cost, hit MAX, or
        /// the local hull becomes a MEGA).
        /// </summary>
        void ApplyUpgradeSlotVisual(int index, UpgradeSlotVisualState state)
        {
            if (index < 0 || index >= 10 || buttonImages[index] == null)
                return;

            Color category = buttonCategoryColors[index];
            Image fill = buttonImages[index];
            Outline outline = buttonOutlines[index];
            Image rail = buttonAccentRails[index];
            TextMeshProUGUI title = titleTexts[index];
            TextMeshProUGUI key = keyLabels[index];
            TextMeshProUGUI cost = costLabels[index];
            Image gem = costGemIcons[index];
            Button btn = buttons[index];

            Color accent;
            Color fillCol;
            Color titleCol;
            Color keyCol;
            Color costCol;
            Color gemCol;
            bool interactable;

            switch (state)
            {
                case UpgradeSlotVisualState.Maxed:
                    // --- Completed chrome in this ability’s own colour ---
                    // Same proud full-tint treatment as the old gold MAXED look (fill wash, title,
                    // key, MAX label, ticks) — but Fire Power stays orange, Move Speed cyan, etc.
                    accent = category;
                    accent.a = 0.95f;
                    fillCol = Color.Lerp(glassFillColor, category, 0.22f);
                    fillCol.a = glassFillColor.a;
                    // Slightly brighter than the rail so title / MAX read clearly on dark glass.
                    titleCol = Color.Lerp(category, Color.white, maxedTitleBrighten);
                    titleCol.a = 1f;
                    keyCol = new Color(category.r, category.g, category.b, 0.9f);
                    costCol = titleCol;
                    gemCol = titleCol;
                    interactable = false;
                    break;

                case UpgradeSlotVisualState.Unavailable:
                    // --- MEGA: visible but not purchasable ---
                    // [TITAN-ORBIT] Same dim as Locked so the button reads "off", but no amber
                    // “need more gems” — Extra Levels are not a MEGA feature at all.
                    accent = Color.Lerp(category, new Color(0.35f, 0.38f, 0.42f, 1f), lockedDim);
                    accent.a = 0.4f;
                    fillCol = Color.Lerp(glassFillColor, accent, categoryFillBlend * 0.25f);
                    fillCol.a = glassFillColor.a * 0.92f;
                    titleCol = Color.Lerp(readyTitleColor, new Color(0.45f, 0.48f, 0.52f, 1f), lockedDim);
                    keyCol = new Color(0.4f, 0.45f, 0.52f, 0.65f);
                    costCol = new Color(0.5f, 0.55f, 0.6f, 0.7f);
                    gemCol = costCol;
                    interactable = false;
                    break;

                case UpgradeSlotVisualState.Locked:
                    // Dimmed category glass + amber cost (“need more gems”).
                    accent = Color.Lerp(category, new Color(0.35f, 0.38f, 0.42f, 1f), lockedDim);
                    accent.a = 0.45f;
                    fillCol = Color.Lerp(glassFillColor, accent, categoryFillBlend * 0.35f);
                    fillCol.a = glassFillColor.a * 0.92f;
                    titleCol = Color.Lerp(readyTitleColor, new Color(0.45f, 0.48f, 0.52f, 1f), lockedDim);
                    keyCol = new Color(0.4f, 0.45f, 0.52f, 0.65f);
                    costCol = lockedCostColor;
                    gemCol = lockedCostColor;
                    interactable = false;
                    break;

                default: // Ready
                    accent = category;
                    accent.a = 0.9f;
                    fillCol = Color.Lerp(glassFillColor, category, categoryFillBlend);
                    fillCol.a = glassFillColor.a;
                    titleCol = readyTitleColor;
                    keyCol = new Color(0.62f, 0.78f, 0.95f, 0.92f);
                    costCol = gemCostIconColor;
                    gemCol = gemCostIconColor;
                    interactable = true;
                    break;
            }

            fill.color = fillCol;
            if (outline != null)
            {
                Color o = accent;
                o.a = state == UpgradeSlotVisualState.Ready ? 0.9f
                    : state == UpgradeSlotVisualState.Maxed ? 0.95f
                    : 0.4f;
                outline.effectColor = o;
            }

            if (rail != null)
            {
                Color r = accent;
                r.a = state == UpgradeSlotVisualState.Locked || state == UpgradeSlotVisualState.Unavailable
                    ? 0.4f
                    : 0.95f;
                rail.color = r;
            }

            if (title != null) title.color = titleCol;
            if (key != null) key.color = keyCol;
            if (cost != null) cost.color = costCol;
            if (gem != null && gem.enabled) gem.color = gemCol;

            if (btn != null && btn.interactable != interactable)
                btn.interactable = interactable;

            // Tick marks: lit ticks match the slot accent (category colour when MAXED).
            // Unavailable hides the whole column — do not paint squares the player cannot buy.
            if (state != UpgradeSlotVisualState.Unavailable)
                ApplyTickStateColors(index, state, category);
        }

        /// <summary>
        /// Retints upgrade ticks when the slot state changes.
        /// MAXED lit ticks use that ability’s category colour (same proud chrome as the button).
        /// </summary>
        void ApplyTickStateColors(int index, UpgradeSlotVisualState state, Color category)
        {
            if (tickContainers == null || index < 0 || index >= tickContainers.Length || tickContainers[index] == null)
                return;
            if (!tickContainers[index].activeSelf)
                return;

            Color lit;
            if (state == UpgradeSlotVisualState.Maxed)
            {
                // Full ability colour — matches title / rail / MAX label on the maxed button.
                lit = category;
                lit.a = 1f;
            }
            else
            {
                lit = new Color(1f, 1f, 0.9f, 1f);
                // Slight category wash on Ready lit ticks so they match the button accent.
                if (state == UpgradeSlotVisualState.Ready)
                    lit = Color.Lerp(lit, category, 0.35f);
                else if (state == UpgradeSlotVisualState.Locked)
                    lit = Color.Lerp(lit, new Color(0.45f, 0.48f, 0.52f, 1f), lockedDim);
            }

            Color empty = state == UpgradeSlotVisualState.Locked
                ? new Color(0.22f, 0.24f, 0.28f, 0.55f)
                : new Color(0.3f, 0.3f, 0.35f, 0.8f);

            Transform container = tickContainers[index].transform;
            int litCount = _lastTickLevels[index];
            for (int i = 0; i < container.childCount; i++)
            {
                Image img = container.GetChild(i).GetComponent<Image>();
                if (img == null) continue;
                img.color = i < litCount ? lit : empty;
            }
        }

        /// <summary>
        /// Quick-stat chip above one ability button — value and +per-buy only (no title).
        /// Hover opens the calculation tip when the STATS row is visible.
        /// </summary>
        (RectTransform chipRect, TextMeshProUGUI valueText) CreateStatChip(Transform parent, int index, Color statColor)
        {
            GameObject chipObj = new GameObject($"StatChip_{index}");
            chipObj.transform.SetParent(parent, false);
            RectTransform chipRect = chipObj.AddComponent<RectTransform>();

            Image bg = chipObj.AddComponent<Image>();
            bg.raycastTarget = true;
            var outline = chipObj.AddComponent<Outline>();
            ApplyGamerGlassChrome(bg, outline, statColor, includeInnerShade: false);
            AddCategoryAccentRail(chipObj.transform, statColor, E(2f), E(3f));

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
            // [UNITY] Overflow (not Truncate) so the +per-buy number is never clipped off.
            valueText.overflowMode = TextOverflowModes.Overflow;
            valueText.alignment = TextAlignmentOptions.Center;
            valueText.fontSize = F(12f);
            valueText.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            valueText.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                valueText.font = TMP_Settings.defaultFontAsset;
            valueText.text = "—";

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

            ElevateAbilityTipDrawOrder();
        }

        /// <summary>
        /// Gives the calculation card its own nested Canvas so it paints above other HUD.
        /// Called once at build and again on hover in case a later HUD sibling stole hierarchy order.
        /// </summary>
        void ElevateAbilityTipDrawOrder()
        {
            if (_abilityTipPanel == null)
                return;

            // --- Nested canvas (beats overlay HUDs that sibling-order cannot) ---
            // [UNITY] A child Canvas with overrideSorting is a separate draw batch. Without it,
            // RocketLoadoutHUD / SpaceBrakesHUD (order 80) and the turret pad (120) always
            // cover this tip even after SetAsLastSibling on the main canvas.
            Canvas tipCanvas = _abilityTipPanel.GetComponent<Canvas>();
            if (tipCanvas == null)
                tipCanvas = _abilityTipPanel.AddComponent<Canvas>();

            tipCanvas.overrideSorting = true;
            tipCanvas.sortingOrder = AbilityTipSortingOrder;

            // [UNITY] Nested canvases start with no extra shader channels. TMP needs TexCoord1
            // (and usually Normal / Tangent) or the body text disappears.
            tipCanvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;

            // Intentional: no GraphicRaycaster — fill/frame are already non-raycast so clicks
            // still reach the chips and the world under the card.
            _abilityTipPanel.transform.SetAsLastSibling();
        }

        /// <summary>Pointer entered a quick-stat chip — show that ability's calculation card.</summary>
        public void ShowAbilityStatTooltip(int abilityIndex)
        {
            // STATS row off → chips hidden and hover tips stay dormant.
            if (!statsChipsVisible)
                return;
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
            // Build once on enter — not every Update (LIVE vitals removed; body is static until upgrade).
            RefreshAbilityTipContent();
            PositionAbilityTipPanel(abilityIndex);
            ElevateAbilityTipDrawOrder();
            if (!_abilityTipPanel.activeSelf)
                _abilityTipPanel.SetActive(true);
        }

        /// <summary>
        /// Hash of ship chassis identity + the ten ability levels + hull ComponentSize + cargo.
        /// Used to dirty-check chip/tip rebuilds without allocating.
        /// </summary>
        /// <param name="ship">Local ship vitals (level / team / branch / family / cargo).</param>
        /// <param name="attrs">Ghost attribute upgrade levels.</param>
        /// <param name="componentSize">
        /// Hull ComponentSize used for mass tax. Included so MS/TS chips repaint when
        /// <see cref="ShipMotorConfig.HullMassReference"/> arrives after the first chassis paint.
        /// </param>
        /// <param name="megaCatalogKey">
        /// 0 for a regular hull; MEGA catalog index + 1 so chips rebuild when the MEGA row changes.
        /// </param>
        /// <returns>Stable fingerprint for the current loadout matrix.</returns>
        static int ComputeStatsSnapshotKey(
            in ShipState ship,
            in ShipAttributeUpgradeState attrs,
            float componentSize,
            int megaCatalogKey)
        {
            // [STANDARD] Unchecked hash combine — collisions are rare; worst case is one extra rebuild.
            unchecked
            {
                int h = 17;
                h = h * 31 + ship.ShipLevel;
                h = h * 31 + (int)ship.Team;
                h = h * 31 + ship.BranchIndex;
                h = h * 31 + ship.ShipFamilyConfigIndex;
                h = h * 31 + attrs.FirePower;
                h = h * 31 + attrs.BulletSpeed;
                h = h * 31 + attrs.MaxHealth;
                h = h * 31 + attrs.HealthRegen;
                h = h * 31 + attrs.EnergyCapacity;
                h = h * 31 + attrs.EnergyRegen;
                h = h * 31 + attrs.MovementSpeed;
                h = h * 31 + attrs.RotationSpeed;
                h = h * 31 + attrs.GemCapacity;
                h = h * 31 + attrs.PeopleCapacity;
                // Centi-units — ignores sub-0.01 noise, still catches MinMass → real hull size.
                h = h * 31 + Mathf.RoundToInt(componentSize * 100f);
                // [TITAN-ORBIT] Gems / people change mass tax → Move Speed and Turn chips must repaint.
                h = h * 31 + Mathf.RoundToInt(ship.CurrentGems);
                h = h * 31 + ship.CurrentPeople;
                h = h * 31 + BulletBankHudCopy.SnapshotKey();
                // MEGA catalog row — buying / swapping a MEGA must rebuild chips even when
                // ship level and family stay at 7. 0 = regular hull.
                h = h * 31 + megaCatalogKey;
                return h;
            }
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

        /// <summary>
        /// Rebuilds the open ability tip from a static capacity snapshot.
        /// Call on pointer-enter or when <see cref="_statsSnapshotKey"/> changes — not every Update.
        /// </summary>
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

        /// <summary>
        /// Builds a static chip/tip snapshot from the current ship + ability levels.
        /// Re-applies level growth + attribute multipliers locally so an upgrade never sticks to the
        /// previous speedometer frame. Parts / ram / OD extras may still come from the speedometer
        /// when its part cache is ready (expensive prefab Instantiates).
        /// </summary>
        /// <returns>
        /// True when a ship snapshot exists. Callers must also check <see cref="IsChipLiveContextReady"/>
        /// before latching chip text — chassis catalogs can lag the first Update after spawn.
        /// </returns>
        bool TryResolveChipLiveContext(
            out ShipSpeedometerStatTooltips.PartCache parts,
            out ShipSpeedometerStatTooltips.LiveContext live,
            out ShipAttributeUpgradeState attrs)
        {
            parts = default;
            live = default;
            attrs = default;
            if (!TryGetUpgradeHudSnapshot(out ShipState ship, out attrs))
                return false;

            if (!_triedSpeedometerLookup)
            {
                _triedSpeedometerLookup = true;
                _cachedSpeedometer = UnityEngine.Object.FindFirstObjectByType<ShipSpeedometerHUD>();
            }

            // --- Hull size for mass tax ---
            // [TITAN-ORBIT] ComponentSize = ShipMotorConfig.HullMassReference. Never fall back to
            // MinMass and latch — that paints nearly untaxed MS/TS until the player toggles [STATS].
            // Leave ComponentSize/Cruise at 0 until motor or speedometer mobility is ready.
            bool hasComponentSize = TryGetLocalHullComponentSize(out float componentSize);
            float moveStepPreview = 0f;
            ShipWeaponConfig weapon = default;
            float ramRating = 0f;
            float ramAst = 0f;
            float ramSelf = 0f;
            float barMax = 0f;
            float odCap = 1f;

            // Mobility shared does not require part-cache Instantiates (unlike tip grids).
            ShipSpeedometerStatTooltips.LiveContext mobilityShared = default;
            bool gotMobilityShared = _cachedSpeedometer != null
                && _cachedSpeedometer.TryGetMobilitySharedState(out mobilityShared);

            if (!hasComponentSize
                && gotMobilityShared
                && mobilityShared.ComponentSize > ShipMassLogic.MinMass + 0.0001f)
            {
                // [TITAN-ORBIT] Reject MinMass-only speedometer placeholders (HullMassReference lag) —
                // that painted nearly untaxed MS until [STATS] was toggled. Real hulls may equal
                // MinMass; those are accepted via TryGetLocalHullComponentSize above.
                componentSize = mobilityShared.ComponentSize;
                hasComponentSize = true;
            }

            if (gotMobilityShared)
            {
                // Move-step / ram / OD extras — independent of whether hull size came from motor.
                if (mobilityShared.MoveStepPreview > 0.0001f)
                    moveStepPreview = mobilityShared.MoveStepPreview;
                weapon = mobilityShared.Weapon;
                ramRating = mobilityShared.RamRating;
                ramAst = mobilityShared.RamAsteroidDamage;
                ramSelf = mobilityShared.RamSelfDamage;
                barMax = mobilityShared.BarMaxSpeed;
                odCap = mobilityShared.OverdriveCapacityMult;
            }

            // Tip part grids still need the stricter parts.Valid shared state when available.
            if (_cachedSpeedometer != null)
                _cachedSpeedometer.TryGetTooltipSharedState(out parts, out _);

            // --- Fresh chassis pipeline (same Extra Level path as ShipSpeedometerHUD) ---
            // [TITAN-ORBIT] Prefer AggregateAndEvaluate when part Ids/Stats are available.
            live = new ShipSpeedometerStatTooltips.LiveContext
            {
                Ship = ship,
                Weapon = weapon,
                MoveStepPreview = moveStepPreview,
                RamRating = ramRating,
                RamAsteroidDamage = ramAst,
                RamSelfDamage = ramSelf,
                BarMaxSpeed = barMax,
                OverdriveCapacityMult = odCap,
                ComponentSize = hasComponentSize ? componentSize : 0f,
                FirePowerAbilityLevel = attrs.FirePower,
                Motor = new ShipMotorConfig { SkipMassTax = IsLocalShipMega() ? (byte)1 : (byte)0 },
            };
            BulletBankHudCopy.ApplyLoadout(ref live);

            bool mega = IsLocalShipMega();
            ushort megaIndex = 0;
            if (mega)
                TryGetLocalMegaCatalogIndex(out megaIndex);
            else if (gotMobilityShared && mobilityShared.IsMega)
            {
                // Speedometer already latched MEGA this frame (entity lookup can miss during backlog).
                mega = true;
                megaIndex = mobilityShared.MegaCatalogIndex;
            }

            live.IsMega = mega;
            if (mega && MegaShipStatsCalculator.TrySumForCatalogIndex(megaIndex, out ShipComponentAbilityStats megaStats))
            {
                // --- MEGA: catalog totals only ---
                // [TITAN-ORBIT] Team+level+branch would resolve a regular L7 family chassis
                // (same slot index as the MEGA planet slot) and Extra-Level it. MEGAs are
                // static — no Extra Level, no +per-buy, gem cap stays 0.
                live.MegaCatalogIndex = megaIndex;
                live.EffectiveStats = megaStats;
                live.ChassisMaxSpeed = megaStats.moveSpeed;
                live.ChassisAccel = megaStats.accelerationCap > 0.1f
                    ? megaStats.accelerationCap
                    : megaStats.moveSpeed;
                live.ChassisTurnDeg = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(
                    megaStats.turnSpeed);
                live.MoveStepPreview = 0f;
                if (hasComponentSize)
                {
                    ShipMobilityResolution.TaxedMotorStats taxed = ShipMobilityResolution.ResolveLiveMotorStats(
                        live.ChassisMaxSpeed,
                        live.ChassisAccel,
                        live.ChassisTurnDeg,
                        ship.CurrentGems,
                        ship.CurrentPeople,
                        componentSize,
                        skipMassTax: true);
                    live.TotalMass = taxed.TotalMass;
                    live.CruiseMaxSpeed = taxed.MaxSpeed;
                    live.TaxedAccel = taxed.EngineThrust;
                    live.TaxedTurnDeg = taxed.RotationSpeed;
                    live.LiveMaxSpeed = taxed.MaxSpeed;
                }

                if (ramAst <= 0.0001f && megaStats.rammingPower > 0.01f)
                    live.RamRating = megaStats.rammingPower;

                return true;
            }

            if (ShipStatApplyLogic.TryResolveChassisId(
                    ship.Team,
                    ship.ShipLevel,
                    ship.BranchIndex,
                    out string chassisId,
                    allowFallback: true,
                    ship.ShipFamilyConfigIndex))
            {
                ShipAbilityLevelCounts abilityCounts =
                    ShipAttributeUpgradeLogic.ToAbilityLevelCounts(in attrs);
                ShipComponentAbilityStats effective;

                if (parts.Valid && parts.Ids != null && parts.Ids.Count > 0)
                {
                    effective = ShipComponentExtraLevelMath.AggregateAndEvaluate(
                        parts.Ids,
                        parts.Stats,
                        ship.ShipLevel,
                        in abilityCounts);
                    effective = ShipComponentExtraLevelMath.ApplyMobilityPenalties(effective, ship.ShipLevel);
                    ShipFamilyDefinition family = null;
                    if (ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out family)
                        && family != null)
                    {
                        effective = family.ApplyStatFallbacks(effective);
                        effective = family.ApplySpecialBonuses(effective);
                    }

                    // --- All-gun DPS for the Fire Power chip ---
                    float allGun = ShipWeaponDpsMath.SumAllGunDps(
                        parts.Ids, parts.Stats, ship.ShipLevel, in abilityCounts);
                    float allGunNext = ShipWeaponDpsMath.SumAllGunDpsAtNextFirePower(
                        parts.Ids, parts.Stats, ship.ShipLevel, in abilityCounts);
                    live.AllGunDps = ShipWeaponDpsMath.ApplyFamilyOffenseMuls(allGun, family);
                    live.AllGunDpsNextStep = ShipWeaponDpsMath.ApplyFamilyOffenseMuls(allGunNext, family);
                }
                else if (ShipStatApplyLogic.TryGetBaseStatsForChassis(
                             chassisId, ship.ShipLevel, out ShipComponentAbilityStats levelOneSummed))
                {
                    // Fallback when part cache is not ready yet.
                    effective = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(
                        levelOneSummed, ship.ShipLevel);
                    // [LEGACY] No-ops — kept so this path matches older call sites.
                    ShipAttributeUpgradeLogic.ApplyMultipliers(ref effective, in attrs);
                    ShipAttributeUpgradeLogic.ResolveMoveSpeedAbilitySteps(
                        levelOneSummed, out float moveStep, out float accelStep, out float odDrainStep);
                    ShipAttributeUpgradeLogic.ApplyMoveSpeedAbilitySteps(
                        ref effective, attrs, moveStep, accelStep, odDrainStep);
                }
                else
                {
                    effective = default;
                }

                live.EffectiveStats = effective;
                live.ChassisMaxSpeed = effective.moveSpeed;
                live.ChassisAccel = effective.accelerationCap > 0.1f
                    ? effective.accelerationCap
                    : effective.moveSpeed;
                // [TITAN-ORBIT] turnSpeed on the stats block is definition units — convert like the bar.
                live.ChassisTurnDeg = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(
                    effective.turnSpeed);

                // Mass tax only when ComponentSize is known — otherwise leave CruiseMaxSpeed at 0
                // so IsChipLiveContextReady keeps retrying (MS would look like chassis / no drag).
                if (hasComponentSize)
                {
                    ShipMobilityResolution.TaxedMotorStats taxed = ShipMobilityResolution.ResolveLiveMotorStats(
                        live.ChassisMaxSpeed,
                        live.ChassisAccel,
                        live.ChassisTurnDeg,
                        ship.CurrentGems,
                        ship.CurrentPeople,
                        componentSize,
                        skipMassTax: IsLocalShipMega());
                    live.TotalMass = taxed.TotalMass;
                    live.CruiseMaxSpeed = taxed.MaxSpeed;
                    live.TaxedAccel = taxed.EngineThrust;
                    live.TaxedTurnDeg = taxed.RotationSpeed;
                    live.LiveMaxSpeed = taxed.MaxSpeed;
                }

                // If speedometer has not filled max-ram yet, estimate at full cruise here.
                if (ramAst <= 0.0001f && effective.rammingPower > 0.01f)
                {
                    // Rating/mass/speed product matches tip language; exact server formula lives in
                    // ShipComponentRammingSuggestions — speedometer fills this when its snapshot runs.
                    live.RamRating = effective.rammingPower;
                }

                if (moveStepPreview <= 0.0001f)
                    live.MoveStepPreview = Mathf.Max(0f, effective.moveSpeedPerExtraLevel);
            }

            if (live.MoveStepPreview <= 0.0001f)
                live.MoveStepPreview = Mathf.Max(0f, live.EffectiveStats.moveSpeedPerExtraLevel);

            return true;
        }

        /// <summary>
        /// Reads <see cref="ShipMotorConfig.HullMassReference"/> from the seeded local ship.
        /// Uses Instantiates-hook seed only — no ship archetype gather (Join Team Crash!!! safe).
        /// </summary>
        /// <param name="componentSize">Hull ComponentSize for mass tax when known.</param>
        /// <returns>True when the motor has a positive HullMassReference.</returns>
        static bool TryGetLocalHullComponentSize(out float componentSize)
        {
            componentSize = 0f;

            // --- Seeded entity only (no CalculateEntityCount / WithEntityAccess) ---
            var world = EcsGameBridge.GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!LocalShipEntitySeed.TryGetSeededShip(em, out Entity ship)
                || ship == Entity.Null
                || !em.Exists(ship)
                || !em.HasComponent<ShipMotorConfig>(ship))
                return false;

            float hull = em.GetComponentData<ShipMotorConfig>(ship).HullMassReference;
            if (hull <= 0f)
                return false;

            componentSize = hull;
            return true;
        }

        /// <summary>
        /// Chip glance text: <b>current</b> and green <c>+per-buy</c> only (no FP/MS title).
        /// Ability name lives on the bottom button; full math is on hover.
        /// </summary>
        static string FormatChipText(
            int index,
            float value,
            float nextStep,
            int abilityLv,
            string unit,
            in ShipSpeedometerStatTooltips.LiveContext live)
        {
            _ = abilityLv;
            var sb = new System.Text.StringBuilder(64);
            AppendCurrentAndPerBuy(sb, value, nextStep, unit, sizePercent: 100);
            // Fire Power chip is DPS (/s). Bullet type sits under the number.
            if (index == 0 || index == 1)
            {
                string typeLine = BulletBankHudCopy.FormatChipTypeLine(in live);
                if (!string.IsNullOrEmpty(typeLine))
                    sb.Append('\n').Append("<size=85%><color=#FFAA66>").Append(typeLine).Append("</color></size>");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Appends <c>12.5 DPS/s  +1.25</c> — the two glance stats on each top chip (not the bottom button).
        /// </summary>
        /// <param name="sb">TMP rich-text builder.</param>
        /// <param name="value">Current effective value.</param>
        /// <param name="nextStep">Gain from the next gem purchase (0 hides the +).</param>
        /// <param name="unit">Optional unit suffix on the current value (e.g. <c>/s</c>, <c>°/s</c>).</param>
        /// <param name="sizePercent">TMP size tag for the whole stats line (100 = default).</param>
        static void AppendCurrentAndPerBuy(
            System.Text.StringBuilder sb,
            float value,
            float nextStep,
            string unit,
            int sizePercent)
        {
            string val = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            bool wrapSize = sizePercent > 0 && sizePercent != 100;
            if (wrapSize)
                sb.Append("<size=").Append(sizePercent).Append("%>");

            sb.Append("<b>").Append(val).Append(unit).Append("</b>");
            if (nextStep > 0.0001f)
            {
                // Green delta — gamification “what you get if you buy” signal.
                sb.Append(" <color=#7DFFB2>+")
                    .Append(nextStep.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("</color>");
            }

            if (wrapSize)
                sb.Append("</size>");
        }

        private (Button button, RectTransform buttonRect, TextMeshProUGUI titleText, GameObject tickContainer, Image bgImage, Outline outline, Image accentRail, TextMeshProUGUI keyLabel, TextMeshProUGUI costLabel, Image costGemIcon) CreateUpgradeButton(Transform parent, int index, Color statColor, string keyStr)
        {
            GameObject btnObj = new GameObject($"UpgradeBtn_{index}");
            btnObj.transform.SetParent(parent, false);

            RectTransform btnRect = btnObj.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0f, 0.5f);
            btnRect.anchorMax = new Vector2(0f, 0.5f);
            btnRect.pivot = new Vector2(0f, 0.5f);

            // --- Dark glass + category accent (same language as the top STATS chips) ---
            // [TITAN-ORBIT] No full-panel category flood — thin rail + outline carry the tint.
            // Per-frame ApplyUpgradeSlotVisual recolors for Ready / Locked / Maxed.
            Image bgImage = btnObj.AddComponent<Image>();
            bgImage.raycastTarget = true;
            var buttonOutline = btnObj.AddComponent<Outline>();
            ApplyGamerGlassChrome(bgImage, buttonOutline, statColor, includeInnerShade: true);
            Image accentRail = AddCategoryAccentRail(btnObj.transform, statColor, E(4f), E(3f));

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

            Button button = btnObj.AddComponent<Button>();
            button.targetGraphic = bgImage;
            // [UNITY] Neutral ColorBlock so interactable=false does not wash our custom state paints grey.
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.05f, 1.05f, 1.05f, 1f);
            colors.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = Color.white;
            colors.colorMultiplier = 1f;
            button.colors = colors;
            int capturedIndex = index;
            button.onClick.AddListener(() => TryUpgrade(capturedIndex));

            GameObject keyObj = new GameObject("KeyLabel");
            keyObj.transform.SetParent(btnObj.transform, false);
            RectTransform keyRect = keyObj.AddComponent<RectTransform>();
            keyRect.anchorMin = new Vector2(0f, 1f);
            keyRect.anchorMax = new Vector2(0f, 1f);
            keyRect.pivot = new Vector2(0f, 1f);
            keyRect.anchoredPosition = new Vector2(E(4f), E(-5f));
            keyRect.sizeDelta = new Vector2(E(20f), E(16f));
            TextMeshProUGUI keyLabel = keyObj.AddComponent<TextMeshProUGUI>();
            keyLabel.text = keyStr;
            keyLabel.fontSize = F(13f);
            if (TMP_Settings.defaultFontAsset != null) keyLabel.font = TMP_Settings.defaultFontAsset;
            // Cool caption tone (space-gamer theme), not pure white glare.
            keyLabel.color = new Color(0.62f, 0.78f, 0.95f, 0.92f);
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
            // [TITAN-ORBIT] Bottom button = ability name only. Current / +per-buy live on the chip above.
            titleText.text = Titles[index];
            if (TMP_Settings.defaultFontAsset != null) titleText.font = TMP_Settings.defaultFontAsset;
            titleText.color = new Color(0.88f, 0.92f, 0.98f, 1f);
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
            costRowLayout.spacing = E(2f);
            costRowLayout.padding = new RectOffset(0, 0, 0, 0);
            costRowLayout.childForceExpandWidth = false;
            costRowLayout.childForceExpandHeight = false;
            costRowLayout.childControlWidth = true;
            costRowLayout.childControlHeight = true;

            // --- Gem icon then cost digits: [💎] 15 ---
            // [TITAN-ORBIT] Same gem art as moon / defense-pad labels when Inspector sprite is empty.
            Sprite gemSprite = ResolveGemCostIcon();
            GameObject gemObj = new GameObject("GemIcon");
            gemObj.transform.SetParent(costRow.transform, false);
            RectTransform gemRect = gemObj.AddComponent<RectTransform>();
            float gemSz = E(gemIconSize);
            gemRect.sizeDelta = new Vector2(gemSz, gemSz);
            Image costGemIcon = gemObj.AddComponent<Image>();
            costGemIcon.raycastTarget = false;
            costGemIcon.preserveAspect = true;
            costGemIcon.color = gemCostIconColor;
            costGemIcon.sprite = gemSprite;
            costGemIcon.enabled = gemSprite != null;
            LayoutElement gemLe = gemObj.AddComponent<LayoutElement>();
            gemLe.preferredWidth = gemSz;
            gemLe.preferredHeight = gemSz;
            gemLe.flexibleWidth = 0f;

            GameObject costObj = new GameObject("CostLabel");
            costObj.transform.SetParent(costRow.transform, false);
            RectTransform costRect = costObj.AddComponent<RectTransform>();
            costRect.sizeDelta = Vector2.zero;
            TextMeshProUGUI costLabel = costObj.AddComponent<TextMeshProUGUI>();
            costLabel.text = "";
            costLabel.fontSize = F(11f);
            if (TMP_Settings.defaultFontAsset != null) costLabel.font = TMP_Settings.defaultFontAsset;
            // Same warm gold as gemCostIconColor so icon + digits match.
            costLabel.color = gemCostIconColor;
            costLabel.alignment = TextAlignmentOptions.MidlineLeft;
            costLabel.overflowMode = TextOverflowModes.Overflow;
            ContentSizeFitter costCsf = costObj.AddComponent<ContentSizeFitter>();
            costCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            costCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            LayoutElement costLe = costObj.AddComponent<LayoutElement>();
            costLe.flexibleWidth = 0f;

            return (button, btnRect, titleText, tickContainer, bgImage, buttonOutline, accentRail, keyLabel, costLabel, costGemIcon);
        }

        /// <summary>
        /// Gem sprite for the cost row: Inspector override, else shared <see cref="WorldStatLabelIcons.Gem"/>.
        /// </summary>
        Sprite ResolveGemCostIcon()
        {
            if (gemCostIconSprite != null)
                return gemCostIconSprite;

            // [HYBRID] Same CleanFlatIcon gem used by moon gem counts / defense pad costs.
            Sprite shared = WorldStatLabelIcons.Gem;
            if (shared != null)
                gemCostIconSprite = shared;
            return shared;
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

        /// <summary>
        /// Shows or hides the vertical Extra Level tick column on every bottom button.
        /// MEGA hulls hide the squares (they cannot buy Extra Levels). Regular hulls restore them
        /// and give the title its reserved right inset so text does not sit under the ticks.
        /// </summary>
        /// <param name="mega">True when the local ship is a MEGA this frame.</param>
        void ApplyMegaButtonChrome(bool mega)
        {
            // --- Tick column + title inset ---
            // [TITAN-ORBIT] The ticks are the 7×7 squares that show how many Extra Levels
            // remain. Painting them as empty or MAXED on a MEGA looked like upgrades were
            // still on offer. Buttons stay in the strip; only the squares go away.
            for (int i = 0; i < 10; i++)
            {
                if (tickContainers[i] != null && tickContainers[i].activeSelf == mega)
                    tickContainers[i].SetActive(!mega);

                if (titleTexts[i] == null)
                    continue;

                // When ticks are gone, reclaim the right gutter so the ability name can center.
                RectTransform titleRect = titleTexts[i].rectTransform;
                float rightInset = mega ? E(4f) : E(tickColumnRightInset);
                titleRect.offsetMax = new Vector2(-rightInset, -E(titleAreaTopInset));
            }
        }

        /// <summary>
        /// Rebuilds or retints the Extra Level squares on one bottom button.
        /// Skipped while the column is hidden (MEGA). Called from Update for regular hulls only.
        /// </summary>
        /// <param name="index">Ability slot 0–9 (Fire Power … Troop Cap).</param>
        /// <param name="currentLevel">Purchased Extra Levels for this ability.</param>
        /// <param name="maxLevel">Ship-level cap (how many squares to show).</param>
        /// <param name="slotState">Ready / Locked / Maxed — drives lit vs empty colours.</param>
        private void UpdateTickMarks(int index, int currentLevel, int maxLevel, UpgradeSlotVisualState slotState)
        {
            // --- Per-slot tick paint ---
            if (tickContainers == null || index < 0 || index >= tickContainers.Length || tickContainers[index] == null) return;
            // MEGA chrome hides this column — do not spawn squares behind SetActive(false).
            if (!tickContainers[index].activeSelf)
                return;
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

            // Skip Image.color writes when fill count + state are unchanged (avoids per-frame dirty).
            if (_lastTickLevels[index] == currentLevel
                && _lastMaxUpgrades == maxLevel
                && _lastSlotVisualState[index] == slotState)
                return;

            _lastTickLevels[index] = currentLevel;
            ApplyTickStateColors(index, slotState, buttonCategoryColors[index]);
        }

        /// <summary>
        /// Client HUD tick: show/hide the strip, then paint each slot's cost and Ready/Locked/Maxed
        /// (or Unavailable on a MEGA). Keyboard 1–0 is handled in LateUpdate via TryUpgrade.
        /// </summary>
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

            // MEGA — Extra Levels are not sold. Keep the ten buttons, hide the tick squares.
            bool mega = IsLocalShipMega();
            if (_lastMegaHud != mega)
            {
                _lastMegaHud = mega;
                _slotVisualsSeeded = false;
                _statsSnapshotKey = int.MinValue;
                ApplyMegaButtonChrome(mega);
            }

            for (int i = 0; i < 10; i++)
            {
                int current = ShipAttributeUpgradeLogic.GetAttributeLevel(attrs, i);

                // --- Slot state: Ready (buyable) / Locked (broke) / Maxed (cap) / Unavailable (MEGA) ---
                UpgradeSlotVisualState slotState = mega
                    ? UpgradeSlotVisualState.Unavailable
                    : ResolveUpgradeSlotState(current, maxUpgrades, ship.CurrentGems, cost);
                if (!mega)
                    UpdateTickMarks(i, current, maxUpgrades, slotState);

                if (costLabels[i] == null)
                    continue;

                string costText;
                bool showGemIcon;
                if (mega)
                {
                    costText = "—";
                    showGemIcon = false;
                }
                else if (slotState == UpgradeSlotVisualState.Maxed)
                {
                    // At cap — hide the gem; "MAX" is enough.
                    costText = "MAX";
                    showGemIcon = false;
                }
                else
                {
                    costText = cost.ToString();
                    showGemIcon = ResolveGemCostIcon() != null;
                }

                // Dirty-check TMP / icon — farming asteroids changes gems every frame; skip when text identical.
                if (costChanged || maxChanged || _lastCostText[i] != costText)
                {
                    costLabels[i].text = costText;
                    _lastCostText[i] = costText;
                    if (costGemIcons[i] != null)
                    {
                        Sprite gemSprite = ResolveGemCostIcon();
                        if (costGemIcons[i].sprite == null && gemSprite != null)
                            costGemIcons[i].sprite = gemSprite;
                        costGemIcons[i].enabled = showGemIcon && costGemIcons[i].sprite != null;
                    }
                }

                // Repaint chrome when affordance changes (gems cross the cost threshold, or hit MAX).
                if (!_slotVisualsSeeded || _lastSlotVisualState[i] != slotState)
                {
                    ApplyUpgradeSlotVisual(i, slotState);
                    _lastSlotVisualState[i] = slotState;
                }
            }

            _lastMaxUpgrades = maxUpgrades;
            _lastCost = cost;
            _slotVisualsSeeded = true;

            FlushPendingAbilityTipHide();
        }

        /// <summary>
        /// Rebuilds STATS chips when the loadout / hull-size / cargo snapshot changes.
        /// Gems and people are in the fingerprint because mass tax changes Move / Turn chips.
        /// Runs from LateUpdate so <see cref="ShipSpeedometerHUD"/> can publish ComponentSize first.
        /// </summary>
        void TryRefreshAbilityChipSnapshot(in ShipState ship, in ShipAttributeUpgradeState attrs)
        {
            // STATS row off — do not latch; SetStatsChipsVisible resets the key when turned back on.
            if (!statsChipsVisible)
                return;

            if (!TryResolveChipLiveContext(out _, out var live, out _) || !IsChipLiveContextReady(in live))
                return;

            // Key includes ComponentSize + CurrentGems/People so cargo mass tax repaints MS/TS.
            int snapshotKey = ComputeStatsSnapshotKey(
                in ship, in attrs, live.ComponentSize, live.IsMega ? live.MegaCatalogIndex + 1 : 0);
            if (snapshotKey == _statsSnapshotKey)
                return;

            _statsSnapshotKey = snapshotKey;
            PaintChipValues(in live, in attrs);
            if (_activeAbilityTipIndex.HasValue)
                RefreshAbilityTipContent();
        }

        /// <summary>
        /// Writes glance text onto every visible chip from a ready live context.
        /// </summary>
        void PaintChipValues(
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs)
        {
            for (int i = 0; i < 10; i++)
            {
                if (_chipValueTexts[i] == null)
                    continue;

                ShipAbilityStatBreakdown.ResolveChipDisplay(
                    i, in live, in attrs, out float value, out float nextStep, out int abilityLv, out string unit);
                string chipText = FormatChipText(i, value, nextStep, abilityLv, unit, in live);
                if (_lastChipText[i] == chipText)
                    continue;
                _lastChipText[i] = chipText;
                _chipValueTexts[i].text = chipText;
            }
        }

        /// <summary>
        /// True when chassis EffectiveStats and mass-tax inputs are ready to paint.
        /// Missing ComponentSize / CruiseMaxSpeed means MS/TS would show untaxed chassis speed.
        /// </summary>
        static bool IsChipLiveContextReady(in ShipSpeedometerStatTooltips.LiveContext live)
        {
            // Any primary pool above epsilon means GetEffectiveStatsAtShipLevel + attrs ran.
            ShipComponentAbilityStats eff = live.EffectiveStats;
            bool statsReady = eff.healthCap > 0.01f
                || eff.firePower > 0.01f
                || eff.energyCap > 0.01f
                || eff.moveSpeed > 0.01f;
            if (!statsReady)
                return false;

            // [TITAN-ORBIT] ComponentSize 0 = unknown (we refuse MinMass placeholders). A real hull
            // may equal MinMass (0.5); CruiseMaxSpeed > 0 means mass tax ran with that size.
            if (live.ComponentSize <= 0.01f)
                return false;
            if (live.CruiseMaxSpeed <= 0.01f)
                return false;

            return true;
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

            // --- STATS chips after speedometer LateUpdate when possible ---
            // [TITAN-ORBIT] Chip mass tax needs HullMassReference / speedometer ComponentSize.
            // Update() often runs before those exist; LateUpdate retries until ready then latches.
            if (TryGetUpgradeHudSnapshot(out var ship, out var attrs))
                TryRefreshAbilityChipSnapshot(in ship, in attrs);

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
            if (!CanShowUpgradeBar() || IsLocalShipMega())
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
