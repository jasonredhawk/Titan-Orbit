using System.Globalization;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Screen corner placement for the local-player speedometer panel. BottomLeft avoids minimap overlap.
    /// </summary>
    public enum SpeedometerPlacement
    {
        [Tooltip("Clear of bottom-right minimap; pair with attribute bar on wide layouts.")]
        BottomLeft = 0,
        BottomRight = 1,
        TopLeft = 2,
        TopRight = 3
    }

    /// <summary>
    /// Local-player speed / accel / mass / ram / bullet HUD.
    /// Reads <see cref="ShipMotorConfig"/> (after client+server <see cref="ShipStatApplySystem"/>),
    /// <see cref="ShipKinematics"/>, weapon config, and reconstructed chassis stats from the visualization world.
    /// <para>
    /// [TITAN-ORBIT] Friendly territory triangles multiply thrust + max speed in
    /// <see cref="ShipPhysicsDriveLogic"/> (<c>1 + 0.05 × homePlanetLevel</c> — not a ship
    /// MovementSpeed attribute) but do <b>not</b> rewrite <see cref="ShipMotorConfig"/>.
    /// This HUD multiplies display max / accel by <see cref="PlanetConnectionGraphCache.LocalOwnerTerritoryMult"/>
    /// (sticky, same hold as <see cref="ShipTerritoryBoostLatch"/>) so the bar and SPD line match
    /// the boosted cruise the motor already applies.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] The speed bar always spans to OVERDRIVE top speed (motor baked capacity),
    /// even when Shift is not held. The right-hand band uses <see cref="overdriveZoneColor"/> so
    /// players see unused overdrive headroom; the cyan fill only enters that band while OD is active.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Pre–mass-tax baselines for SPD / ACC / turn are always chassis
    /// <see cref="ShipComponentAbilityStats"/> (leveled + attrs). Live subtractive mass tax
    /// (gems/people/ComponentSize) then matches <see cref="ShipPhysicsDriveLogic"/> (motor stores
    /// the same chassis baselines after <see cref="ShipStatApplyLogic"/>).
    /// </para>
    /// <para>
    /// Master on/off lives on <see cref="GameManager.ShowSpeedometer"/> (NceGameRoot Inspector → HUD).
    /// When off, this component does not build UI and LateUpdate returns immediately — no ECS ship
    /// queries, no bar math, no TMP rebuilds. Presentation-only — never writes ECS.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Hover pads over SPD / ACC / MASS / RAM / BUL open a rollover that lists the
    /// chassis (and moon-store) parts feeding that number — see <see cref="ShipSpeedometerStatTooltips"/>.
    /// </para>
    /// Hidden during team select, death, and when the upgrade tree obscures HUD.
    /// </summary>
    public class ShipSpeedometerHUD : MonoBehaviour
    {
        const float HudLayoutScale = 1.6f;

        /// <summary>
        /// Cached LocalPlayerShipTag query — CreateEntityQuery every LateUpdate was ~3ms
        /// (Profiler frame 1539 post-label-fix).
        /// </summary>
        EntityQuery _hudTaggedQuery;
        World _hudTaggedQueryWorld;

        /// <summary>Treat as "at max" when within this fraction of motor MaxSpeed (cruise lock / float noise).</summary>
        const float AtMaxSpeedFraction = 0.985f;

        /// <summary>Figure space — same advance as a digit in most fonts; pads without visible gaps jumping.</summary>
        const char FigureSpace = '\u2007';

        [Header("Layout")]
        [SerializeField] SpeedometerPlacement placement = SpeedometerPlacement.BottomLeft;
        [SerializeField] float panelWidth = 380f * HudLayoutScale;
        // Taller than the old 148×scale so bars, tick strips, and 4 body lines never share vertical bands.
        [SerializeField] float panelHeight = 178f * HudLayoutScale;
        [SerializeField, FormerlySerializedAs("accelerationDisplayResponsiveness")]
        float accelerationBarSmoothing = 5f;
        [SerializeField, FormerlySerializedAs("rightMargin")] float horizontalMargin = 20f;
        [SerializeField, FormerlySerializedAs("bottomMargin")] float verticalMargin = 20f;
        [SerializeField] float stackGapAboveUpgradeBar = 20f;

        [Header("Colors")]
        [SerializeField] Color backgroundColor = new Color(0f, 0f, 0f, 0.45f);
        [SerializeField] Color fillColor = new Color(0.35f, 0.85f, 1f, 0.9f);
        [SerializeField] Color trackColor = new Color(0.15f, 0.15f, 0.18f, 0.85f);
        /// <summary>
        /// [TITAN-ORBIT] Always-visible band from cruise max → OVERDRIVE top speed on the speed bar.
        /// Amber so it reads as "burst capacity" next to the cyan fill (matches SPD od tag).
        /// </summary>
        [SerializeField] Color overdriveZoneColor = new Color(1f, 0.72f, 0.28f, 0.38f);
        [SerializeField] Color textColor = new Color(0.92f, 0.95f, 1f, 1f);
        [SerializeField] Color accelPositiveColor = new Color(0.25f, 0.92f, 0.45f, 0.92f);
        [SerializeField] Color accelNegativeColor = new Color(0.95f, 0.28f, 0.28f, 0.92f);
        [SerializeField] Color tickLabelColor = new Color(0.78f, 0.82f, 0.9f, 0.72f);

        GameObject rootPanel;
        Slider speedSlider;
        /// <summary>Speed-bar band for OVERDRIVE headroom (behind the cyan fill).</summary>
        RectTransform overdriveZoneRect;
        Image overdriveZoneImage;
        RectTransform accelGreenFill;
        RectTransform accelRedFill;
        TextMeshProUGUI speedLabel;
        TextMeshProUGUI[] speedTickLabels;
        TextMeshProUGUI[] accelTickLabels;
        Entity accelSampleShip;
        float lastHorizontalSpeed;
        float smoothedHorizontalAccel;
        bool hasLastHorizontalSpeed;
        bool uiBuilt;

        /// <summary>Last max-speed used to paint static tick labels (avoid rewriting TMP every frame).</summary>
        float lastTickMaxSpeed = -1f;

        /// <summary>Last max-accel scale used for accel tick labels.</summary>
        float lastTickAccelSkew = -1f;

        /// <summary>
        /// [TITAN-ORBIT] Cached HUD snapshot — GhostSpawnBacklog skips full ship entity lookups, which
        /// used to SetActive(false) the speedometer for a frame on asteroid destroy (gem Instantiates).
        /// </summary>
        bool _hasHudCache;
        ShipState _cachedShip;
        ShipMotorConfig _cachedMotor;
        ShipKinematics _cachedKinematics;
        ShipWeaponConfig _cachedWeapon;
        ShipComponentAbilityStats _cachedEffectiveStats;
        Entity _cachedShipEntity;

        /// <summary>Throttle rich-text rebuilds — string allocs every LateUpdate show up as GC.Alloc.</summary>
        float nextTextRebuildTime;

        /// <summary>Cached HUD body text so we can skip TMP writes when unchanged.</summary>
        string lastHudBodyText = "";

        /// <summary>Last applied corner position — avoid rewriting RectTransform every LateUpdate (sub-pixel shimmer).</summary>
        Vector2 _lastAppliedAnchoredPos = new Vector2(float.NaN, float.NaN);

        /// <summary>Cached RectTransform on rootPanel — GetComponent every LateUpdate is wasteful.</summary>
        RectTransform _rootRect;

        /// <summary>
        /// Cached upgrade-bar HUD for bottom-left stack offset.
        /// [TITAN-ORBIT] FindFirstObjectByType every LateUpdate was ~2.5ms + GC (Profiler frame 5199).
        /// </summary>
        ShipAttributeUpgradeHUD _cachedUpgradeBar;
        bool _triedUpgradeBarLookup;
        float _lastUpgradeBoost = float.NaN;

        /// <summary>
        /// Chassis id string for tooltips / catalog lookups.
        /// Only refreshed when the stats cache rebuilds (not every LateUpdate — ToString allocates).
        /// </summary>
        string _cachedChassisId;

        /// <summary>
        /// FixedString chassis key for cache invalidation without allocating.
        /// Compared to live <see cref="ShipChassisState.ChassisId"/> each frame.
        /// </summary>
        FixedString64Bytes _statsCacheChassisKey;

        /// <summary>Last <see cref="ShipState.ShipFamilyConfigIndex"/> baked into the stats cache.</summary>
        byte _statsCacheFamilyIndex = byte.MaxValue;

        Entity _statsCacheShipEntity;
        int _statsCacheShipLevel = int.MinValue;
        int _statsCacheBranch = int.MinValue;
        ShipComponentAbilityStats _statsCacheEffective;
        ShipAttributeUpgradeState _statsCacheAttrs;
        /// <summary>Move Speed ability level for tooltip chassis breakdown (updated every HUD fill).</summary>
        int _moveSpeedAbilityLevel;

        /// <summary>
        /// Latched when GameManager turns the HUD off so we hide the panel once and clear samples.
        /// </summary>
        bool _idleBecauseDisabled;

        // --- Rollover tooltips (hover pads → floating TMP) ---

        /// <summary>Floating breakdown panel (inactive until a hover zone is entered).</summary>
        GameObject _tooltipPanel;

        /// <summary>Rich-text body inside <see cref="_tooltipPanel"/>.</summary>
        TextMeshProUGUI _tooltipLabel;

        /// <summary>Rect on the tooltip panel for positioning beside the speedometer.</summary>
        RectTransform _tooltipRect;

        /// <summary>Cached chassis part list for tooltip copy (refreshed on chassis / store change).</summary>
        ShipSpeedometerStatTooltips.PartCache _partCache;

        /// <summary>Last live numbers passed into tooltip Build (updated every LateUpdate while shown).</summary>
        ShipSpeedometerStatTooltips.LiveContext _liveTooltipContext;

        /// <summary>Section currently under the pointer, or null when the rollover is hidden.</summary>
        SpeedometerStatSection? _activeTooltipSection;

        /// <summary>
        /// Section scheduled to hide at end of LateUpdate. Cancelled if another pad calls
        /// <see cref="ShowStatTooltip"/> in the same frame (bar → line handoff).
        /// </summary>
        SpeedometerStatSection? _pendingHideSection;

        /// <summary>Last rich text written to the tooltip (skip TMP writes when unchanged).</summary>
        string _lastTooltipBody = "";

        /// <summary>
        /// [UNITY] Awake — subscribe to GameManager so we can disable this component when the
        /// speedometer is off (Unity then skips LateUpdate entirely). Unsubscribe only in OnDestroy
        /// so setting <c>enabled = false</c> does not drop the listener we need to turn back on.
        /// </summary>
        void Awake()
        {
            GameManager.ShowSpeedometerChanged += OnShowSpeedometerChanged;
        }

        /// <summary>
        /// [UNITY] Start — sync with GameManager (covers the case where GameManager Awake ran first
        /// and fired the event before we subscribed) and build UI when allowed.
        /// </summary>
        void Start()
        {
            ApplyFeatureEnabled(GameManager.IsShowSpeedometerActive);
        }

        /// <summary>
        /// [UNITY] OnDisable — hide the panel when this component is off (feature toggle or parent).
        /// </summary>
        void OnDisable()
        {
            if (rootPanel != null)
                rootPanel.SetActive(false);
        }

        /// <summary>
        /// [UNITY] OnEnable — restore the panel when re-enabled and the GameManager toggle is on.
        /// </summary>
        void OnEnable()
        {
            if (GameManager.IsShowSpeedometerActive && uiBuilt && rootPanel != null)
                rootPanel.SetActive(true);
        }

        /// <summary>
        /// [UNITY] OnDestroy — drop the static event subscription and dispose the cached ECS query.
        /// </summary>
        void OnDestroy()
        {
            GameManager.ShowSpeedometerChanged -= OnShowSpeedometerChanged;

            if (_hudTaggedQuery != default)
            {
                _hudTaggedQuery.Dispose();
                _hudTaggedQuery = default;
            }
            _hudTaggedQueryWorld = null;
        }

        /// <summary>
        /// True when NceGameRoot → Game Manager → Show Speedometer is on (or no GameManager yet).
        /// </summary>
        static bool IsFeatureEnabled() => GameManager.IsShowSpeedometerActive;

        /// <summary>
        /// GameManager published a new Show Speedometer value (Play Mode Inspector or Awake).
        /// </summary>
        void OnShowSpeedometerChanged(bool show) => ApplyFeatureEnabled(show);

        /// <summary>
        /// Turns the HUD fully on or off. Off sets <c>enabled = false</c> so Unity does not call
        /// LateUpdate at all — not merely hiding the panel while queries keep running.
        /// </summary>
        void ApplyFeatureEnabled(bool show)
        {
            if (!show)
            {
                EnterDisabledIdle();
                // [UNITY] disabled MonoBehaviour → no LateUpdate / no background HUD work.
                if (enabled)
                    enabled = false;
                return;
            }

            _idleBecauseDisabled = false;
            if (!enabled)
                enabled = true;
            BuildUIIfNeeded();
        }

        /// <summary>
        /// One-time procedural HUD build. Vertical bands are non-overlapping so tick strips never
        /// sit inside the body-text region (the old 0–0.46 label band overlapped accel ticks).
        /// </summary>
        void BuildUIIfNeeded()
        {
            // --- One-time procedural HUD build ---
            if (uiBuilt || !IsFeatureEnabled())
                return;

            // --- Resolve parent canvas ---
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
                return;

            rootPanel = new GameObject("ShipSpeedometer");
            rootPanel.transform.SetParent(transform, false);
            RectTransform rootRect = rootPanel.AddComponent<RectTransform>();
            ApplyPlacement(rootRect);
            rootRect.sizeDelta = new Vector2(panelWidth, panelHeight);

            Image bg = rootPanel.AddComponent<Image>();
            bg.color = backgroundColor;
            bg.raycastTarget = false;

            // --- Vertical layout bands (normalized Y, bottom→top) ---
            // [TITAN-ORBIT] Clear gaps between each band so TMP ticks cannot paint over bars/text.
            float pad = 8f * HudLayoutScale;
            const float textNormTop = 0.48f;
            const float accelTickNormBottom = 0.50f;
            const float accelTickNormTop = 0.56f;
            const float accelNormBottom = 0.58f;
            const float accelNormTop = 0.68f;
            const float speedTickNormBottom = 0.70f;
            const float speedTickNormTop = 0.76f;
            const float speedNormBottom = 0.78f;
            const float speedNormTop = 1f;

            GameObject sliderGo = new GameObject("SpeedBar");
            sliderGo.transform.SetParent(rootPanel.transform, false);
            RectTransform sliderRect = sliderGo.AddComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, speedNormBottom);
            sliderRect.anchorMax = new Vector2(1f, speedNormTop);
            sliderRect.offsetMin = new Vector2(pad, 2f * HudLayoutScale);
            sliderRect.offsetMax = new Vector2(-pad, -4f * HudLayoutScale);

            speedSlider = sliderGo.AddComponent<Slider>();
            speedSlider.minValue = 0f;
            speedSlider.maxValue = 1f;
            speedSlider.wholeNumbers = false;
            speedSlider.interactable = false;

            GameObject track = new GameObject("Background");
            track.transform.SetParent(sliderGo.transform, false);
            RectTransform tr = track.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            Image trackImg = track.AddComponent<Image>();
            trackImg.color = trackColor;
            trackImg.raycastTarget = false;
            speedSlider.targetGraphic = trackImg;

            // --- OVERDRIVE capacity zone (always painted; fill covers it when speed enters OD) ---
            // [TITAN-ORBIT] Sibling between track and Fill Area so unused headroom stays visible
            // while cruising at normal max. Anchors updated each frame from baseMax / odMax.
            GameObject odZoneGo = new GameObject("OverdriveZone");
            odZoneGo.transform.SetParent(sliderGo.transform, false);
            overdriveZoneRect = odZoneGo.AddComponent<RectTransform>();
            overdriveZoneRect.anchorMin = new Vector2(0.57f, 0f);
            overdriveZoneRect.anchorMax = Vector2.one;
            overdriveZoneRect.offsetMin = Vector2.zero;
            overdriveZoneRect.offsetMax = Vector2.zero;
            overdriveZoneImage = odZoneGo.AddComponent<Image>();
            overdriveZoneImage.color = overdriveZoneColor;
            overdriveZoneImage.raycastTarget = false;

            GameObject fillArea = new GameObject("Fill Area");
            fillArea.transform.SetParent(sliderGo.transform, false);
            RectTransform far = fillArea.AddComponent<RectTransform>();
            far.anchorMin = Vector2.zero;
            far.anchorMax = Vector2.one;
            far.offsetMin = Vector2.zero;
            far.offsetMax = Vector2.zero;

            GameObject fill = new GameObject("Fill");
            fill.transform.SetParent(fillArea.transform, false);
            RectTransform fr = fill.AddComponent<RectTransform>();
            fr.anchorMin = Vector2.zero;
            fr.anchorMax = new Vector2(0f, 1f);
            fr.pivot = new Vector2(0f, 0.5f);
            fr.offsetMin = Vector2.zero;
            fr.offsetMax = Vector2.zero;
            Image fillImg = fill.AddComponent<Image>();
            fillImg.color = fillColor;
            fillImg.raycastTarget = false;
            speedSlider.fillRect = fr;

            GameObject speedTickStrip = new GameObject("SpeedTicks");
            speedTickStrip.transform.SetParent(rootPanel.transform, false);
            RectTransform speedTickRect = speedTickStrip.AddComponent<RectTransform>();
            speedTickRect.anchorMin = new Vector2(0f, speedTickNormBottom);
            speedTickRect.anchorMax = new Vector2(1f, speedTickNormTop);
            speedTickRect.offsetMin = new Vector2(pad, 0f);
            speedTickRect.offsetMax = new Vector2(-pad, 0f);
            speedTickLabels = CreateTickLabelRow(speedTickStrip.transform, 5, 8f * HudLayoutScale);

            GameObject accelRoot = new GameObject("AccelBar");
            accelRoot.transform.SetParent(rootPanel.transform, false);
            RectTransform accelRootRect = accelRoot.AddComponent<RectTransform>();
            accelRootRect.anchorMin = new Vector2(0f, accelNormBottom);
            accelRootRect.anchorMax = new Vector2(1f, accelNormTop);
            accelRootRect.offsetMin = new Vector2(pad, 0f);
            accelRootRect.offsetMax = new Vector2(-pad, 0f);

            GameObject accelTickStrip = new GameObject("AccelTicks");
            accelTickStrip.transform.SetParent(rootPanel.transform, false);
            RectTransform accelTickRect = accelTickStrip.AddComponent<RectTransform>();
            accelTickRect.anchorMin = new Vector2(0f, accelTickNormBottom);
            accelTickRect.anchorMax = new Vector2(1f, accelTickNormTop);
            accelTickRect.offsetMin = new Vector2(pad, 0f);
            accelTickRect.offsetMax = new Vector2(-pad, 0f);
            accelTickLabels = CreateTickLabelRow(accelTickStrip.transform, 5, 8f * HudLayoutScale);

            GameObject accelTrack = new GameObject("Track");
            accelTrack.transform.SetParent(accelRoot.transform, false);
            RectTransform accelTrackRt = accelTrack.AddComponent<RectTransform>();
            accelTrackRt.anchorMin = Vector2.zero;
            accelTrackRt.anchorMax = Vector2.one;
            accelTrackRt.offsetMin = Vector2.zero;
            accelTrackRt.offsetMax = Vector2.zero;
            Image accelTrackImg = accelTrack.AddComponent<Image>();
            accelTrackImg.color = trackColor;
            accelTrackImg.raycastTarget = false;

            GameObject redGo = new GameObject("DecelFill");
            redGo.transform.SetParent(accelRoot.transform, false);
            accelRedFill = redGo.AddComponent<RectTransform>();
            accelRedFill.anchorMin = new Vector2(0.5f, 0f);
            accelRedFill.anchorMax = new Vector2(0.5f, 1f);
            accelRedFill.offsetMin = Vector2.zero;
            accelRedFill.offsetMax = Vector2.zero;
            Image redImg = redGo.AddComponent<Image>();
            redImg.color = accelNegativeColor;
            redImg.raycastTarget = false;

            GameObject greenGo = new GameObject("AccelFill");
            greenGo.transform.SetParent(accelRoot.transform, false);
            accelGreenFill = greenGo.AddComponent<RectTransform>();
            accelGreenFill.anchorMin = new Vector2(0.5f, 0f);
            accelGreenFill.anchorMax = new Vector2(0.5f, 1f);
            accelGreenFill.offsetMin = Vector2.zero;
            accelGreenFill.offsetMax = Vector2.zero;
            Image greenImg = greenGo.AddComponent<Image>();
            greenImg.color = accelPositiveColor;
            greenImg.raycastTarget = false;

            GameObject centerLine = new GameObject("CenterLine");
            centerLine.transform.SetParent(accelRoot.transform, false);
            RectTransform cl = centerLine.AddComponent<RectTransform>();
            cl.anchorMin = new Vector2(0.5f, 0.1f);
            cl.anchorMax = new Vector2(0.5f, 0.9f);
            cl.pivot = new Vector2(0.5f, 0.5f);
            cl.sizeDelta = new Vector2(1.5f * HudLayoutScale, 0f);
            Image cli = centerLine.AddComponent<Image>();
            cli.color = new Color(1f, 1f, 1f, 0.28f);
            cli.raycastTarget = false;

            GameObject labelGo = new GameObject("HudText");
            labelGo.transform.SetParent(rootPanel.transform, false);
            RectTransform lr = labelGo.AddComponent<RectTransform>();
            lr.anchorMin = new Vector2(0f, 0f);
            lr.anchorMax = new Vector2(1f, textNormTop);
            lr.offsetMin = new Vector2(10f * HudLayoutScale, 4f * HudLayoutScale);
            lr.offsetMax = new Vector2(-10f * HudLayoutScale, -2f * HudLayoutScale);
            speedLabel = labelGo.AddComponent<TextMeshProUGUI>();
            speedLabel.text = "—";
            speedLabel.fontSize = 11f * HudLayoutScale;
            speedLabel.lineSpacing = -4f * HudLayoutScale;
            speedLabel.richText = true;
            // [UNITY] Wrap inside the reserved text band (0→textNormTop) so long SPD / boost /
            // stop lines stay fully readable. Ellipsis used to hide territory + OVERDRIVE tags.
            // [TITAN-ORBIT] Bars/ticks sit above textNormTop — wrapping cannot climb into them.
            speedLabel.enableWordWrapping = true;
            speedLabel.overflowMode = TextOverflowModes.Overflow;
            // [UNITY] Hover pads own raycasts — body TMP must not steal pointer enters.
            speedLabel.raycastTarget = false;
            // [UNITY] Monospace keeps SPD/ACC columns from shifting when digits change.
            if (TMP_Settings.defaultFontAsset != null)
                speedLabel.font = TMP_Settings.defaultFontAsset;
            speedLabel.color = textColor;
            bool alignLeft = placement == SpeedometerPlacement.BottomLeft || placement == SpeedometerPlacement.TopLeft;
            speedLabel.alignment = alignLeft ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.TopRight;

            // --- Hover pads (SPD / ACC / MASS / RAM / BUL) ---
            // [TITAN-ORBIT] Invisible Images catch pointer enter/exit; decorative chrome stays
            // raycastTarget=false so only these pads (and the floating tip) participate.
            // Text band is four equal rows (TMP TopLeft → line 0 at top of band).
            float textBand = textNormTop;
            float lineH = textBand * 0.25f;
            CreateHoverZone("Hover_SPD_Bar", new Vector2(0f, speedTickNormBottom), Vector2.one, SpeedometerStatSection.Speed);
            CreateHoverZone("Hover_SPD_Line", new Vector2(0f, textBand - lineH), new Vector2(1f, textBand), SpeedometerStatSection.Speed);
            CreateHoverZone("Hover_ACC_Bar", new Vector2(0f, accelTickNormBottom), new Vector2(1f, accelNormTop), SpeedometerStatSection.Accel);
            // ACC / MASS share body line 1 — left ~62% Accel, right ~38% Mass.
            CreateHoverZone(
                "Hover_ACC_Line",
                new Vector2(0f, textBand - lineH * 2f),
                new Vector2(0.62f, textBand - lineH),
                SpeedometerStatSection.Accel);
            CreateHoverZone(
                "Hover_MASS_Line",
                new Vector2(0.62f, textBand - lineH * 2f),
                new Vector2(1f, textBand - lineH),
                SpeedometerStatSection.Mass);
            CreateHoverZone(
                "Hover_RAM_Line",
                new Vector2(0f, textBand - lineH * 3f),
                new Vector2(1f, textBand - lineH * 2f),
                SpeedometerStatSection.Ram);
            CreateHoverZone(
                "Hover_BUL_Line",
                new Vector2(0f, 0f),
                new Vector2(1f, textBand - lineH * 3f),
                SpeedometerStatSection.Bullets);

            BuildTooltipPanel(canvas);

            uiBuilt = true;
        }

        /// <summary>
        /// Adds a full-stretch invisible Image + <see cref="ShipSpeedometerHoverZone"/> under the
        /// root panel. Anchors are normalized Y bands matching the bar / body layout.
        /// </summary>
        void CreateHoverZone(string name, Vector2 anchorMin, Vector2 anchorMax, SpeedometerStatSection section)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(rootPanel.transform, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            // [UNITY] Transparent Image is required for GraphicRaycaster hit testing.
            Image img = go.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            img.raycastTarget = true;

            var zone = go.AddComponent<ShipSpeedometerHoverZone>();
            zone.Owner = this;
            zone.Section = section;
        }

        /// <summary>
        /// Builds the floating breakdown panel as a sibling of the speedometer (under this HUD
        /// transform so it follows the same canvas). Starts inactive — only shown on hover.
        /// </summary>
        void BuildTooltipPanel(Canvas canvas)
        {
            _tooltipPanel = new GameObject("ShipSpeedometerTooltip");
            // [UNITY] Parent under the same HUD host as the speedometer so canvas scale matches.
            _tooltipPanel.transform.SetParent(transform, false);
            _tooltipRect = _tooltipPanel.AddComponent<RectTransform>();
            _tooltipRect.pivot = new Vector2(0f, 0f);
            _tooltipRect.sizeDelta = new Vector2(320f * HudLayoutScale, 120f * HudLayoutScale);

            Image tipBg = _tooltipPanel.AddComponent<Image>();
            tipBg.color = new Color(0.05f, 0.07f, 0.1f, 0.92f);
            tipBg.raycastTarget = false;

            GameObject tipTextGo = new GameObject("Body");
            tipTextGo.transform.SetParent(_tooltipPanel.transform, false);
            RectTransform tipTextRt = tipTextGo.AddComponent<RectTransform>();
            tipTextRt.anchorMin = Vector2.zero;
            tipTextRt.anchorMax = Vector2.one;
            tipTextRt.offsetMin = new Vector2(10f * HudLayoutScale, 8f * HudLayoutScale);
            tipTextRt.offsetMax = new Vector2(-10f * HudLayoutScale, -8f * HudLayoutScale);

            _tooltipLabel = tipTextGo.AddComponent<TextMeshProUGUI>();
            _tooltipLabel.fontSize = 11f * HudLayoutScale;
            _tooltipLabel.richText = true;
            _tooltipLabel.enableWordWrapping = true;
            _tooltipLabel.overflowMode = TextOverflowModes.Overflow;
            _tooltipLabel.raycastTarget = false;
            _tooltipLabel.alignment = TextAlignmentOptions.TopLeft;
            _tooltipLabel.color = textColor;
            if (TMP_Settings.defaultFontAsset != null)
                _tooltipLabel.font = TMP_Settings.defaultFontAsset;
            _tooltipLabel.text = "";

            // Sort above the speedometer panel so the tip is never covered by its background.
            if (canvas != null)
                _tooltipPanel.transform.SetAsLastSibling();

            _tooltipPanel.SetActive(false);
        }

        /// <summary>
        /// [UNITY] Pointer entered a hover pad — show that section's component breakdown.
        /// Safe to call repeatedly for the same section (refreshes copy from latest live context).
        /// </summary>
        public void ShowStatTooltip(SpeedometerStatSection section)
        {
            if (!uiBuilt || _tooltipPanel == null || _tooltipLabel == null)
                return;

            // Entering any pad cancels a pending hide from a sibling pad this frame.
            _pendingHideSection = null;
            _activeTooltipSection = section;
            RefreshTooltipContent();
            PositionTooltipPanel();
            if (!_tooltipPanel.activeSelf)
                _tooltipPanel.SetActive(true);
        }

        /// <summary>
        /// [UNITY] Pointer left a hover pad. Defers hide to LateUpdate so moving between the
        /// SPD bar and SPD line (same section) does not flicker the tip off for a frame.
        /// </summary>
        public void HideStatTooltip(SpeedometerStatSection section)
        {
            if (_activeTooltipSection != section)
                return;

            _pendingHideSection = section;
        }

        /// <summary>
        /// Applies a deferred hide after EventSystem pointer callbacks for this frame are done.
        /// Called from LateUpdate so bar→line handoffs can cancel via <see cref="ShowStatTooltip"/>.
        /// </summary>
        void FlushPendingTooltipHide()
        {
            if (!_pendingHideSection.HasValue)
                return;

            SpeedometerStatSection pending = _pendingHideSection.Value;
            _pendingHideSection = null;
            if (_activeTooltipSection != pending)
                return;

            _activeTooltipSection = null;
            if (_tooltipPanel != null && _tooltipPanel.activeSelf)
                _tooltipPanel.SetActive(false);
        }

        /// <summary>Rebuilds tooltip TMP from the active section + cached parts + live context.</summary>
        void RefreshTooltipContent()
        {
            if (_tooltipLabel == null || !_activeTooltipSection.HasValue)
                return;

            string body = ShipSpeedometerStatTooltips.Build(
                _activeTooltipSection.Value,
                _partCache,
                _liveTooltipContext);
            if (body == _lastTooltipBody)
                return;

            _lastTooltipBody = body;
            _tooltipLabel.text = body;
            // [UNITY] Size tip height to the wrapped text so BottomLeft "above panel" placement
            // does not leave a huge empty box or clip long breakdowns.
            _tooltipLabel.ForceMeshUpdate(true);
            if (_tooltipRect != null)
            {
                float padY = 16f * HudLayoutScale;
                float tipW = _tooltipRect.sizeDelta.x;
                float tipH = Mathf.Max(80f * HudLayoutScale, _tooltipLabel.preferredHeight + padY);
                _tooltipRect.sizeDelta = new Vector2(tipW, tipH);
            }
        }

        /// <summary>
        /// Places the tip clear of the speedometer and the bottom ability-upgrade strip.
        /// Bottom placements: tip sits <b>above</b> the panel so it cannot cover upgrade buttons.
        /// Top placements: tip sits beside the panel on the free horizontal side.
        /// </summary>
        void PositionTooltipPanel()
        {
            if (_tooltipRect == null)
                return;
            if (_rootRect == null && rootPanel != null)
                _rootRect = rootPanel.GetComponent<RectTransform>();
            if (_rootRect == null)
                return;

            // --- Match canvas anchors with the speedometer corner ---
            Vector2 panelPos = _rootRect.anchoredPosition;
            Vector2 panelSize = _rootRect.sizeDelta;
            float gap = 10f * HudLayoutScale;
            _tooltipRect.anchorMin = _rootRect.anchorMin;
            _tooltipRect.anchorMax = _rootRect.anchorMax;

            bool bottom = placement == SpeedometerPlacement.BottomLeft
                || placement == SpeedometerPlacement.BottomRight;
            bool left = placement == SpeedometerPlacement.BottomLeft
                || placement == SpeedometerPlacement.TopLeft;

            if (bottom)
            {
                // [TITAN-ORBIT] Above the speedometer — beside+down overlapped the upgrade strip.
                // Pivot at bottom so tip grows upward from just above the panel top.
                _tooltipRect.pivot = left ? new Vector2(0f, 0f) : new Vector2(1f, 0f);
                float x = left ? panelPos.x : panelPos.x;
                _tooltipRect.anchoredPosition = new Vector2(x, panelPos.y + panelSize.y + gap);
            }
            else
            {
                // Top corners: beside the panel, hanging down (upgrade strip is not a concern).
                _tooltipRect.pivot = left ? new Vector2(0f, 1f) : new Vector2(1f, 1f);
                float x = left
                    ? panelPos.x + panelSize.x + gap
                    : panelPos.x - gap;
                _tooltipRect.anchoredPosition = new Vector2(x, panelPos.y);
            }
        }

        /// <summary>
        /// Builds a row of fixed-width tick labels under a bar. Edge ticks left/right-align so
        /// neighboring labels do not overlap at the strip ends.
        /// </summary>
        TextMeshProUGUI[] CreateTickLabelRow(Transform parent, int count, float fontSize)
        {
            var labels = new TextMeshProUGUI[count];
            // Narrower than spacing between 5 anchors on a ~608px panel so "+12.5" cannot collide.
            float tickCellW = 44f * HudLayoutScale;
            for (int i = 0; i < count; i++)
            {
                GameObject go = new GameObject($"Tick{i}");
                go.transform.SetParent(parent, false);
                RectTransform rt = go.AddComponent<RectTransform>();
                float x = count <= 1 ? 0.5f : (float)i / (count - 1);
                rt.anchorMin = new Vector2(x, 0f);
                rt.anchorMax = new Vector2(x, 1f);
                // Edge pivots pull text inward so first/last ticks stay inside the panel.
                float pivotX = i == 0 ? 0f : (i == count - 1 ? 1f : 0.5f);
                rt.pivot = new Vector2(pivotX, 0.5f);
                rt.sizeDelta = new Vector2(tickCellW, 0f);
                rt.anchoredPosition = Vector2.zero;
                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.text = "—";
                tmp.fontSize = fontSize;
                tmp.enableAutoSizing = false;
                tmp.richText = false;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.alignment = TextAlignmentOptions.Midline;
                if (TMP_Settings.defaultFontAsset != null)
                    tmp.font = TMP_Settings.defaultFontAsset;
                tmp.color = tickLabelColor;
                tmp.raycastTarget = false;
                labels[i] = tmp;
            }

            return labels;
        }

        /// <summary>Up to two decimals, fixed character width (figure-space pad) so layout never jumps.</summary>
        static string FormatFixed1(float v, int width = 6)
        {
            string s = v.ToString("0.##", CultureInfo.InvariantCulture);
            return PadFigure(s, width);
        }

        /// <summary>Always signed (+/-) with up to two decimals — ACC never drops the sign glyph.</summary>
        static string FormatFixedSigned1(float v, int width = 7)
        {
            string s = (v >= 0f ? "+" : "-") + Mathf.Abs(v).ToString("0.##", CultureInfo.InvariantCulture);
            return PadFigure(s, width);
        }

        /// <summary>Left-pad with figure spaces so digit columns stay aligned.</summary>
        static string PadFigure(string s, int width)
        {
            if (s == null)
                s = "";
            if (s.Length >= width)
                return s;
            return new string(FigureSpace, width - s.Length) + s;
        }

        float GetBottomLeftStackYBoost()
        {
            if (placement != SpeedometerPlacement.BottomLeft)
                return 0f;

            // --- Resolve upgrade bar once (not every LateUpdate) ---
            if (!_triedUpgradeBarLookup)
            {
                _triedUpgradeBarLookup = true;
                _cachedUpgradeBar = Object.FindFirstObjectByType<ShipAttributeUpgradeHUD>();
            }

            if (_cachedUpgradeBar == null)
                return 0f;
            return _cachedUpgradeBar.GetUpgradeStripReserveHeight() + stackGapAboveUpgradeBar;
        }

        void ApplyPlacement(RectTransform rootRect)
        {
            // --- Corner anchor + margin (stack above upgrade bar when bottom-left) ---
            // Round to whole canvas units and skip writes when unchanged — continuous rewrites
            // shimmer in windowed Game views the same way the upgrade strip did.
            float boost = GetBottomLeftStackYBoost();
            // Skip full anchor rewrite when only boost is stable and position already applied.
            if (!float.IsNaN(_lastUpgradeBoost) &&
                Mathf.Abs(boost - _lastUpgradeBoost) < 0.5f &&
                !float.IsNaN(_lastAppliedAnchoredPos.x))
                return;
            _lastUpgradeBoost = boost;

            float h = Mathf.Round(horizontalMargin);
            float v = Mathf.Round(verticalMargin + boost);
            Vector2 pos;
            switch (placement)
            {
                case SpeedometerPlacement.BottomLeft:
                    rootRect.anchorMin = new Vector2(0f, 0f);
                    rootRect.anchorMax = new Vector2(0f, 0f);
                    rootRect.pivot = new Vector2(0f, 0f);
                    pos = new Vector2(h, v);
                    break;
                case SpeedometerPlacement.BottomRight:
                    rootRect.anchorMin = new Vector2(1f, 0f);
                    rootRect.anchorMax = new Vector2(1f, 0f);
                    rootRect.pivot = new Vector2(1f, 0f);
                    pos = new Vector2(-h, v);
                    break;
                case SpeedometerPlacement.TopLeft:
                    rootRect.anchorMin = new Vector2(0f, 1f);
                    rootRect.anchorMax = new Vector2(0f, 1f);
                    rootRect.pivot = new Vector2(0f, 1f);
                    pos = new Vector2(h, -v);
                    break;
                default: // TopRight
                    rootRect.anchorMin = new Vector2(1f, 1f);
                    rootRect.anchorMax = new Vector2(1f, 1f);
                    rootRect.pivot = new Vector2(1f, 1f);
                    pos = new Vector2(-h, -v);
                    break;
            }

            if (!float.IsNaN(_lastAppliedAnchoredPos.x) &&
                Mathf.Abs(pos.x - _lastAppliedAnchoredPos.x) < 0.5f &&
                Mathf.Abs(pos.y - _lastAppliedAnchoredPos.y) < 0.5f)
                return;

            rootRect.anchoredPosition = pos;
            _lastAppliedAnchoredPos = pos;
        }

        /// <summary>
        /// Gathers local ship ECS data for HUD display. Recomputes effective stats the same way
        /// ShipStatApplyLogic does (chassis sum + level curve + attribute multipliers) for ram rating
        /// and as a sanity fallback if motor MaxSpeed still looks like bake defaults.
        /// </summary>

        bool TryGetLocalShipHudData(
            out ShipState ship,
            out ShipMotorConfig motor,
            out ShipKinematics kinematics,
            out ShipWeaponConfig weapon,
            out ShipComponentAbilityStats effectiveStats,
            out Entity shipEntity)
        {
            ship = default;
            motor = default;
            kinematics = default;
            weapon = default;
            effectiveStats = default;
            shipEntity = Entity.Null;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;

            // --- Tiny tagged lookup first (safe during GhostSpawnBacklog) ---
            // [TITAN-ORBIT] Cache the EntityQuery — recreating it every LateUpdate was ~3ms.
            if (_hudTaggedQueryWorld != world || _hudTaggedQuery == default)
            {
                if (_hudTaggedQuery != default)
                    _hudTaggedQuery.Dispose();
                _hudTaggedQuery = em.CreateEntityQuery(
                    typeof(LocalPlayerShipTag),
                    typeof(ShipState),
                    typeof(ShipMotorConfig),
                    typeof(ShipKinematics));
                _hudTaggedQueryWorld = world;
            }

            // --- Instantiates / post–TeamChoice hold: no tagged CalculateEntityCount ---
            // [TITAN-ORBIT] Broader resolve is gated below; tagged count must also skip
            // (Player.log 2026-07-30 Confirm flush → Crash!!!).
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            if (_hudTaggedQuery.CalculateEntityCount() == 1)
            {
                shipEntity = _hudTaggedQuery.GetSingletonEntity();
                return FillHudDataFromEntity(
                    em, shipEntity, out ship, out motor, out kinematics, out weapon, out effectiveStats);
            }

            // --- Broader resolve (skipped during Settling / GhostSpawnBacklog — Crash!!! risk) ---
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out shipEntity))
                return false;

            return FillHudDataFromEntity(
                em, shipEntity, out ship, out motor, out kinematics, out weapon, out effectiveStats);
        }

        /// <summary>Reads motor/kinematics/weapon/stats from a resolved ship entity.</summary>
        bool FillHudDataFromEntity(
            EntityManager em,
            Entity shipEntity,
            out ShipState ship,
            out ShipMotorConfig motor,
            out ShipKinematics kinematics,
            out ShipWeaponConfig weapon,
            out ShipComponentAbilityStats effectiveStats)
        {
            ship = default;
            motor = default;
            kinematics = default;
            weapon = default;
            effectiveStats = default;

            if (!em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<ShipMotorConfig>(shipEntity) ||
                !em.HasComponent<ShipKinematics>(shipEntity))
                return false;

            ship = em.GetComponentData<ShipState>(shipEntity);
            motor = em.GetComponentData<ShipMotorConfig>(shipEntity);
            kinematics = em.GetComponentData<ShipKinematics>(shipEntity);
            weapon = em.HasComponent<ShipWeaponConfig>(shipEntity)
                ? em.GetComponentData<ShipWeaponConfig>(shipEntity)
                : default;

            // --- Reconstruct effective stats for ram display (read-only mirror of stat apply) ---
            int branchIndex = 0;
            if (em.HasComponent<ShipLoadoutState>(shipEntity))
                branchIndex = em.GetComponentData<ShipLoadoutState>(shipEntity).BranchIndex;

            // --- Live chassis identity (no string alloc) ---
            // [TITAN-ORBIT] Buying a new ship / family often keeps ShipLevel + branch + attrs the same
            // while ChassisId and ShipFamilyConfigIndex change. Omitting those from the cache key left
            // SPD/ACC tooltips on the previous hull's part list until something else busted the cache.
            FixedString64Bytes chassisKey = default;
            bool hasChassisState = em.HasComponent<ShipChassisState>(shipEntity);
            if (hasChassisState)
                chassisKey = em.GetComponentData<ShipChassisState>(shipEntity).ChassisId;
            byte familyIndex = ship.ShipFamilyConfigIndex;

            // Reuse cached chassis/effective stats when identity + level + branch + family + attrs match.
            // ChassisId.ToString() + catalog lookups every LateUpdate were ~13KB GC (Profiler 5199).
            ShipAttributeUpgradeState attrs = default;
            bool hasAttrs = em.HasComponent<ShipAttributeUpgradeState>(shipEntity);
            if (hasAttrs)
                attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);

            if (_statsCacheShipEntity == shipEntity &&
                _statsCacheShipLevel == ship.ShipLevel &&
                _statsCacheBranch == branchIndex &&
                _statsCacheFamilyIndex == familyIndex &&
                _statsCacheChassisKey.Equals(chassisKey) &&
                (!hasAttrs || AttrsEqual(attrs, _statsCacheAttrs)))
            {
                effectiveStats = _statsCacheEffective;
                // [TITAN-ORBIT] Still refresh ability level — early return used to skip attrs when
                // HasComponent was false and leave a stale MovementSpeed on the tooltip.
                _moveSpeedAbilityLevel = hasAttrs ? attrs.MovementSpeed : 0;
                return true;
            }

            // --- Cache miss: always re-read ChassisId (never reuse stale _cachedChassisId) ---
            // [TITAN-ORBIT] Older code reused the string when only the entity matched, so a T1→T2
            // or family swap on the same ghost kept the previous chassis id for tooltips.
            string chassisId = null;
            if (hasChassisState && !chassisKey.IsEmpty)
            {
                chassisId = chassisKey.ToString();
                _cachedChassisId = chassisId;
            }

            if (string.IsNullOrEmpty(chassisId))
            {
                ShipStatApplyLogic.TryResolveChassisId(
                    ship.Team,
                    ship.ShipLevel,
                    branchIndex,
                    out chassisId,
                    allowFallback: true,
                    shipFamilyConfigIndex: familyIndex);
                _cachedChassisId = chassisId;
            }

            if (!string.IsNullOrEmpty(chassisId) &&
                ShipStatApplyLogic.TryGetBaseStatsForChassis(chassisId, ship.ShipLevel, out ShipComponentAbilityStats summed))
            {
                float growth = ShipFamilyDefinition.DefaultShipLevelStatGrowthFraction;
                if (ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family) &&
                    family != null)
                    growth = family.ResolveShipLevelStatGrowthFraction();
                effectiveStats = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(
                    summed, ship.ShipLevel, growth);
                if (hasAttrs)
                {
                    ShipAttributeUpgradeLogic.ApplyMultipliers(ref effectiveStats, attrs);
                    // [TITAN-ORBIT] Same additive Move Speed path as ShipStatApplyLogic (not ×1.1).
                    ShipAttributeUpgradeLogic.ResolveMoveSpeedAbilitySteps(
                        summed, out float moveStep, out float accelStep, out float odDrainStep);
                    ShipAttributeUpgradeLogic.ApplyMoveSpeedAbilitySteps(
                        ref effectiveStats, attrs, moveStep, accelStep, odDrainStep);
                }
            }

            // --- Bust tooltip part cache when hull identity changed ---
            if (_partCache.Valid &&
                (_partCache.ChassisId != chassisId || _partCache.ShipLevel != ship.ShipLevel))
            {
                _partCache.Valid = false;
            }

            _statsCacheShipEntity = shipEntity;
            _statsCacheShipLevel = ship.ShipLevel;
            _statsCacheBranch = branchIndex;
            _statsCacheFamilyIndex = familyIndex;
            _statsCacheChassisKey = chassisKey;
            _statsCacheAttrs = attrs;
            _statsCacheEffective = effectiveStats;
            _moveSpeedAbilityLevel = hasAttrs ? attrs.MovementSpeed : 0;
            return true;
        }

        /// <summary>True when all attribute investment levels match (HUD cache key).</summary>
        static bool AttrsEqual(in ShipAttributeUpgradeState a, in ShipAttributeUpgradeState b) =>
            a.FirePower == b.FirePower &&
            a.BulletSpeed == b.BulletSpeed &&
            a.MaxHealth == b.MaxHealth &&
            a.HealthRegen == b.HealthRegen &&
            a.EnergyCapacity == b.EnergyCapacity &&
            a.EnergyRegen == b.EnergyRegen &&
            a.MovementSpeed == b.MovementSpeed &&
            a.RotationSpeed == b.RotationSpeed &&
            a.GemCapacity == b.GemCapacity &&
            a.PeopleCapacity == b.PeopleCapacity;

        /// <summary>
        /// Chassis MaxSpeed before mass tax: leveled + attributed <see cref="ShipComponentAbilityStats.moveSpeed"/>.
        /// Falls back to motor only when chassis stats are missing (first frames / bake).
        /// </summary>
        static float ResolveChassisMaxSpeed(in ShipMotorConfig motor, in ShipComponentAbilityStats effectiveStats)
        {
            if (effectiveStats.moveSpeed > 0.1f)
                return effectiveStats.moveSpeed;
            return Mathf.Max(0.1f, motor.MaxSpeed);
        }

        /// <summary>
        /// Chassis acceleration before mass tax: leveled + attributed accelerationCap.
        /// Falls back to motor EngineThrust (accel) only when chassis Accel is missing.
        /// </summary>
        static float ResolveChassisAccel(in ShipMotorConfig motor, in ShipComponentAbilityStats effectiveStats)
        {
            if (effectiveStats.accelerationCap > 0.1f)
                return effectiveStats.accelerationCap;
            if (effectiveStats.moveSpeed > 0.1f)
                return effectiveStats.moveSpeed;
            return Mathf.Max(0.1f, motor.EngineThrust);
        }

        /// <summary>
        /// Chassis turn (°/s) before mass tax from definition-unit turnSpeed.
        /// Falls back to motor RotationSpeed when chassis turn is unset.
        /// </summary>
        static float ResolveChassisTurnDeg(in ShipMotorConfig motor, in ShipComponentAbilityStats effectiveStats)
        {
            float fromChassis = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(
                effectiveStats.turnSpeed);
            if (fromChassis > 0.5f)
                return fromChassis;
            return Mathf.Max(0.5f, motor.RotationSpeed);
        }

        /// <summary>
        /// Sticky friendly-triangle movement multiplier for the local owner (1 when outside).
        /// Same cache that grows engine/thruster meshes and that predicted drive publishes.
        /// </summary>
        static float ResolveTerritoryMovementMult() =>
            Mathf.Max(1f, PlanetConnectionGraphCache.LocalOwnerTerritoryMult);

        /// <summary>
        /// Baked OVERDRIVE MaxSpeed multiplier from the motor (always ≥ 1), even when Shift is up.
        /// Used to size the speed bar's amber capacity zone and the "od N.N" label.
        /// </summary>
        /// <param name="motor">Local ship motor (ProfileSet × family OD baked in).</param>
        static float ResolveOverdriveCapacityMult(in ShipMotorConfig motor) =>
            Mathf.Max(1f, ShipOverdriveTuning.ResolveSpeedMultiplier(motor));

        /// <summary>
        /// OVERDRIVE movement multiplier matching <see cref="ShipPhysicsDriveLogic"/> —
        /// <see cref="ShipOverdriveTuning.IsBurstActive"/> with pending Shift/Thrust + ghosted
        /// energy/lockout. Returns 1 when burst is off (capacity zone still uses
        /// <see cref="ResolveOverdriveCapacityMult"/>).
        /// </summary>
        /// <param name="em">Client visualization world EntityManager.</param>
        /// <param name="shipEntity">Local ship ghost.</param>
        /// <param name="ship">Current vitals + <see cref="ShipState.OverdriveLockout"/>.</param>
        static float ResolveOverdriveMovementMult(
            EntityManager em,
            Entity shipEntity,
            in ShipState ship)
        {
            // --- Guards ---
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
                return 1f;

            // [TITAN-ORBIT] Prefer ShipPendingInput — ghost ShipInput can lag one tick / skip
            // under join backlog, which made SPD overdrive flicker independently of the motor.
            bool thrustHeld;
            bool shiftHeld;
            if (ShipPendingInput.HasValue)
            {
                thrustHeld = ShipPendingInput.Latest.Thrust;
                shiftHeld = ShipPendingInput.Latest.Overdrive;
            }
            else if (em.HasComponent<ShipInput>(shipEntity))
            {
                var input = em.GetComponentData<ShipInput>(shipEntity);
                thrustHeld = input.Thrust;
                shiftHeld = input.Overdrive;
            }
            else
                return 1f;

            if (!ShipOverdriveTuning.IsBurstActive(
                    shiftHeld,
                    thrustHeld,
                    useOrbit: false,
                    ship.CurrentEnergy,
                    ship.OverdriveLockout))
                return 1f;

            if (em.HasComponent<ShipMotorConfig>(shipEntity))
            {
                var motor = em.GetComponentData<ShipMotorConfig>(shipEntity);
                return ShipOverdriveTuning.ResolveSpeedMultiplier(motor);
            }

            return ShipOverdriveTuning.SpeedMultiplier;
        }

        /// <summary>
        /// Layouts the amber OVERDRIVE band from cruise max → bar max (right side of the speed bar).
        /// Hidden when capacity mul is ~1 (no OD headroom to show).
        /// </summary>
        /// <param name="cruiseMax">Normal max speed (territory + load, no OD).</param>
        /// <param name="barMax">Full bar scale = cruise × OD capacity mul.</param>
        void UpdateOverdriveZone(float cruiseMax, float barMax)
        {
            if (overdriveZoneRect == null || overdriveZoneImage == null)
                return;

            float safeBar = Mathf.Max(0.01f, barMax);
            float cruiseFrac = Mathf.Clamp01(cruiseMax / safeBar);
            bool showZone = barMax > cruiseMax * 1.001f && cruiseFrac < 0.999f;
            if (overdriveZoneImage.enabled != showZone)
                overdriveZoneImage.enabled = showZone;
            if (!showZone)
                return;

            // --- Right-hand band: everything above normal cruise is OD capacity ---
            overdriveZoneRect.anchorMin = new Vector2(cruiseFrac, 0f);
            overdriveZoneRect.anchorMax = Vector2.one;
            overdriveZoneRect.offsetMin = Vector2.zero;
            overdriveZoneRect.offsetMax = Vector2.zero;
            // Keep inspector/runtime color in sync if the designer tweaks the serialized field.
            if (overdriveZoneImage.color != overdriveZoneColor)
                overdriveZoneImage.color = overdriveZoneColor;
        }

        /// <summary>Planar speed magnitude — top-down game ignores Y velocity.</summary>
        static float GetHorizontalSpeed(in ShipKinematics kinematics)
        {
            float3 vel = kinematics.Velocity;
            vel.y = 0f;
            return math.length(vel);
        }

        /// <summary>
        /// Same movement mass the motor uses (hull bulk + gems + people) — not hull-reference alone.
        /// </summary>
        static float GetMovementMass(in ShipState ship, in ShipMotorConfig motor)
        {
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            return ShipMassLogic.ComputeMovementMass(
                motor.HullMassReference,
                ship.MaxHealth,
                motor.ChassisReferenceHealth,
                ship.CurrentGems,
                baseMass,
                ship.CurrentPeople);
        }

        /// <summary>
        /// Estimates asteroid impact damage: rating × mobility totalMass × current speed
        /// (same product as <see cref="ShipRammingCollisionDamageSystem"/>).
        /// </summary>
        static void GetRamDamageEstimate(
            in ShipState ship,
            in ShipMotorConfig motor,
            in ShipComponentAbilityStats effectiveStats,
            float inboundSpeed,
            out float asteroidDamage,
            out float selfDamage,
            out float ramRating,
            out float totalMass)
        {
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float componentSize = motor.HullMassReference > 0f
                ? motor.HullMassReference
                : Mathf.Max(ShipMassLogic.MinMass, baseMass * ShipMassLogic.HullMassScale);

            // Prefer motor.RammingPower (server-applied) when present; else rebuild from family stats.
            float familyRammingPower = motor.RammingPower > 0f
                ? motor.RammingPower
                : (effectiveStats.rammingPower > 0f
                    ? effectiveStats.rammingPower
                    : ShipFamilyDefaultFallbackStats.CreateBaseline().rammingPower);
            ramRating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(familyRammingPower);

            // [TITAN-ORBIT] Same totalMass + product as server ram/grind — HUD cannot drift.
            ShipMobilityResolution.TaxedMotorStats taxed = ShipMobilityResolution.ApplyMassTaxFromCargo(
                motor.MaxSpeed,
                motor.EngineThrust,
                motor.RotationSpeed,
                ship.CurrentGems,
                ship.CurrentPeople,
                componentSize);
            totalMass = taxed.TotalMass;

            asteroidDamage = ShipComponentRammingSuggestions.ComputeImpactDamage(
                ramRating, totalMass, inboundSpeed);
            selfDamage = ShipComponentRammingSuggestions.ComputeImpactSelfDamage(
                ramRating, totalMass, inboundSpeed);
        }

        /// <summary>
        /// [UNITY] LateUpdate — after ship presentation has written pose/kinematics for this frame.
        /// When <see cref="GameManager.ShowSpeedometer"/> is off, latches idle and returns with
        /// zero ECS / TMP work until the toggle turns back on.
        /// </summary>
        void LateUpdate()
        {
            // --- Master toggle (GameManager → HUD → Show Speedometer) ---
            // [TITAN-ORBIT] Off means no background work: hide once, then pure return.
            if (!IsFeatureEnabled())
            {
                EnterDisabledIdle();
                return;
            }

            // Leaving idle — allow BuildUI / refresh again.
            _idleBecauseDisabled = false;

            if (!uiBuilt)
                BuildUIIfNeeded();
            if (rootPanel == null || speedSlider == null || speedLabel == null || accelGreenFill == null || accelRedFill == null
                || speedTickLabels == null || accelTickLabels == null || overdriveZoneRect == null)
            {
                return;
            }

            bool hasShip = TryGetLocalShipHudData(
                out ShipState ship,
                out ShipMotorConfig motor,
                out ShipKinematics kinematics,
                out ShipWeaponConfig weapon,
                out ShipComponentAbilityStats effectiveStats,
                out Entity shipEntity);

            // --- Hold last good snapshot during GhostSpawnBacklog ---
            // Tagged lookup usually succeeds; if LocalPlayerShipTag is briefly missing while pose stays,
            // keep showing cached numbers instead of SetActive(false) for one frame.
            if (hasShip)
            {
                _hasHudCache = true;
                _cachedShip = ship;
                _cachedMotor = motor;
                _cachedKinematics = kinematics;
                _cachedWeapon = weapon;
                _cachedEffectiveStats = effectiveStats;
                _cachedShipEntity = shipEntity;
            }
            else if (_hasHudCache &&
                     (EcsGameBridge.HasLocalPlayerShip() || ShipDisplayPose.HasLocalPose) &&
                     !ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                ship = _cachedShip;
                motor = _cachedMotor;
                kinematics = _cachedKinematics;
                weapon = _cachedWeapon;
                effectiveStats = _cachedEffectiveStats;
                shipEntity = _cachedShipEntity;
                hasShip = true;
            }

            bool show = hasShip &&
                        !ship.IsDead &&
                        !ship.AwaitingTeamSelection &&
                        ship.Team != TeamId.None &&
                        !ClientTeamFlowState.ShouldSuppressLocalPlayerControl();
            if (HUDController.ShipUpgradeTreeObscuresHud)
                show = false;

            // --- Visibility and layout refresh ---
            rootPanel.SetActive(show);
            if (placement == SpeedometerPlacement.BottomLeft)
            {
                if (_rootRect == null)
                    _rootRect = rootPanel.GetComponent<RectTransform>();
                if (_rootRect != null)
                    ApplyPlacement(_rootRect);
            }

            if (!show)
            {
                hasLastHorizontalSpeed = false;
                accelSampleShip = Entity.Null;
                smoothedHorizontalAccel = 0f;
                // --- Hide rollover when the speedometer itself is hidden ---
                if (_tooltipPanel != null && _tooltipPanel.activeSelf)
                {
                    _activeTooltipSection = null;
                    _pendingHideSection = null;
                    _tooltipPanel.SetActive(false);
                }
                return;
            }

            // --- Speed / accel bars ---
            if (accelSampleShip != shipEntity)
            {
                accelSampleShip = shipEntity;
                hasLastHorizontalSpeed = false;
                smoothedHorizontalAccel = 0f;
            }

            float cur = GetHorizontalSpeed(kinematics);

            // --- Chassis baselines (leveled + attrs) — only pre–mass-tax numbers ---
            // [TITAN-ORBIT] Do not use motor.MaxSpeed / EngineThrust as a second "untaxed" source;
            // those should match chassis after ApplyToShip, and when they diverge the motor line confused the HUD.
            float chassisMove = ResolveChassisMaxSpeed(motor, effectiveStats);
            float chassisAccel = ResolveChassisAccel(motor, effectiveStats);
            float chassisTurnDeg = ResolveChassisTurnDeg(motor, effectiveStats);

            // --- Live subtractive mass tax (same formula as ShipPhysicsDriveLogic) ---
            float componentSize = motor.HullMassReference > 0f
                ? motor.HullMassReference
                : ShipMassLogic.MinMass;
            ShipMobilityResolution.TaxedMotorStats taxed = ShipMobilityResolution.ApplyMassTaxFromCargo(
                chassisMove,
                chassisAccel,
                chassisTurnDeg,
                ship.CurrentGems,
                ship.CurrentPeople,
                componentSize);
            float cruiseMax = taxed.MaxSpeed;
            float maxFwd = taxed.EngineThrust;

            // --- Friendly territory boost (same mult as ShipPhysicsDriveLogic) ---
            // [TITAN-ORBIT] Motor multiplies MaxSpeed / accel at drive time only; chassis
            // ShipMotorConfig stays unboosted. Without this, the bar saturates early and SPD
            // shows e.g. 13.5/13.5 "at max" while kinematics are still climbing past chassis cruise.
            float territoryMult = ResolveTerritoryMovementMult();
            cruiseMax *= territoryMult;
            maxFwd *= territoryMult;

            // --- OVERDRIVE capacity (always) vs live burst (only while engaged) ---
            // [TITAN-ORBIT] Bar scale = cruise × baked OD mul so the amber zone is always visible.
            // Live cruise / "at max" / thrust use active overdrive only.
            float overdriveCapacityMult = ResolveOverdriveCapacityMult(motor);
            float overdriveActiveMult = 1f;
            var vizWorld = EcsGameBridge.GetVisualizationWorld();
            if (vizWorld != null && vizWorld.IsCreated)
                overdriveActiveMult = ResolveOverdriveMovementMult(vizWorld.EntityManager, shipEntity, ship);
            bool overdriveActive = overdriveActiveMult > 1.001f;

            float barMax = cruiseMax * overdriveCapacityMult;
            // Live motor ceiling this frame (cruise when OD off, OD top when on).
            float liveMax = overdriveActive ? barMax : cruiseMax;
            maxFwd *= overdriveActiveMult;

            // Fill against full OD scale so unused amber headroom stays readable at cruise.
            speedSlider.value = Mathf.Clamp01(cur / Mathf.Max(0.01f, barMax));
            UpdateOverdriveZone(cruiseMax, barMax);

            float mass = GetMovementMass(ship, motor);
            maxFwd = Mathf.Max(0.01f, maxFwd);

            float maxBrake = Mathf.Max(0.01f, motor.BrakeDeceleration > 0f
                ? motor.BrakeDeceleration
                : ShipMassLogic.DefaultBrakeDeceleration);

            // Mobility tax totalMass — same number the MASS line and MASS tooltip show.
            float displayMass = taxed.TotalMass;

            // --- Rollover context (parts + live numbers) ---
            // [TITAN-ORBIT] Part cache Instantiates the chassis prefab only when chassis / store gear changes.
            // LiveContext updates every frame so an open tip stays in sync with bars.
            if (vizWorld != null && vizWorld.IsCreated && !string.IsNullOrEmpty(_cachedChassisId))
            {
                ShipSpeedometerStatTooltips.TryRefreshPartCache(
                    vizWorld.EntityManager,
                    shipEntity,
                    _cachedChassisId,
                    ship.ShipLevel,
                    ref _partCache);
            }

            GetRamDamageEstimate(
                ship,
                motor,
                effectiveStats,
                cur,
                out float tipRamAst,
                out float tipRamSelf,
                out float tipRamRating,
                out _);

            _liveTooltipContext = new ShipSpeedometerStatTooltips.LiveContext
            {
                Ship = ship,
                Motor = motor,
                EffectiveStats = effectiveStats,
                Weapon = weapon,
                CurrentSpeed = cur,
                LiveMaxSpeed = liveMax,
                CruiseMaxSpeed = cruiseMax,
                BarMaxSpeed = barMax,
                TerritoryMult = territoryMult,
                TotalMass = taxed.TotalMass,
                ChassisMaxSpeed = chassisMove,
                ChassisAccel = chassisAccel,
                ChassisTurnDeg = chassisTurnDeg,
                TaxedAccel = taxed.EngineThrust,
                OverdriveCapacityMult = overdriveCapacityMult,
                OverdriveActiveMult = overdriveActiveMult,
                MovementMass = mass,
                MaxForwardAccel = maxFwd,
                MaxBrake = maxBrake,
                RamAsteroidDamage = tipRamAst,
                RamSelfDamage = tipRamSelf,
                RamRating = tipRamRating,
                ComponentSize = componentSize,
                MoveSpeedAbilityLevel = _moveSpeedAbilityLevel,
            };

            if (_activeTooltipSection.HasValue)
            {
                RefreshTooltipContent();
                PositionTooltipPanel();
            }

            FlushPendingTooltipHide();

            // --- Accel bar from frame-to-frame speed delta ---
            // [TITAN-ORBIT] Presentation-only. Editor ~30 FPS amplifies sample noise; dead-zone + cruise flatten.
            float sampleDt = Mathf.Max(Time.deltaTime, 0.001f);
            float speedDelta = hasLastHorizontalSpeed ? (cur - lastHorizontalSpeed) : 0f;
            float rawAccel = hasLastHorizontalSpeed ? speedDelta / sampleDt : 0f;
            lastHorizontalSpeed = cur;
            hasLastHorizontalSpeed = true;

            float speedNoiseFloor = liveMax * 0.015f;
            if (Mathf.Abs(speedDelta) < speedNoiseFloor)
                rawAccel = 0f;

            bool atCruise = cur >= liveMax * AtMaxSpeedFraction;
            if (atCruise && Mathf.Abs(rawAccel) < maxFwd * 0.35f)
                rawAccel = 0f;

            float k = Mathf.Clamp01(sampleDt * accelerationBarSmoothing);
            smoothedHorizontalAccel = Mathf.Lerp(smoothedHorizontalAccel, rawAccel, k);

            float scale = Mathf.Max(maxFwd, maxBrake, Mathf.Abs(smoothedHorizontalAccel), 0.01f);
            float posFrac = smoothedHorizontalAccel > 0f ? Mathf.Clamp01(smoothedHorizontalAccel / scale) : 0f;
            float negFrac = smoothedHorizontalAccel < 0f ? Mathf.Clamp01(-smoothedHorizontalAccel / scale) : 0f;

            accelGreenFill.anchorMin = new Vector2(0.5f, 0f);
            accelGreenFill.anchorMax = new Vector2(0.5f + 0.5f * posFrac, 1f);
            accelGreenFill.offsetMin = Vector2.zero;
            accelGreenFill.offsetMax = Vector2.zero;

            accelRedFill.anchorMin = new Vector2(0.5f - 0.5f * negFrac, 0f);
            accelRedFill.anchorMax = new Vector2(0.5f, 1f);
            accelRedFill.offsetMin = Vector2.zero;
            accelRedFill.offsetMax = Vector2.zero;

            // --- Tick labels: full bar = OD capacity (amber zone included) ---
            float skew = Mathf.Max(maxFwd, maxBrake, 0.01f);
            if (!Mathf.Approximately(lastTickMaxSpeed, barMax))
            {
                lastTickMaxSpeed = barMax;
                for (int i = 0; i < speedTickLabels.Length; i++)
                {
                    float t = speedTickLabels.Length <= 1 ? 0f : (float)i / (speedTickLabels.Length - 1);
                    float tickSpd = t * barMax;
                    speedTickLabels[i].text = FormatFixed1(tickSpd, 4);
                    speedTickLabels[i].alignment = i == 0
                        ? TextAlignmentOptions.MidlineLeft
                        : (i == speedTickLabels.Length - 1 ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Midline);
                }
            }

            if (!Mathf.Approximately(lastTickAccelSkew, skew))
            {
                lastTickAccelSkew = skew;
                for (int i = 0; i < accelTickLabels.Length; i++)
                {
                    float t = accelTickLabels.Length <= 1 ? 0.5f : (float)i / (accelTickLabels.Length - 1);
                    float v = Mathf.Lerp(-skew, skew, t);
                    accelTickLabels[i].text = FormatFixedSigned1(v, 5);
                    accelTickLabels[i].alignment = i == 0
                        ? TextAlignmentOptions.MidlineLeft
                        : (i == accelTickLabels.Length - 1 ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Midline);
                }
            }

            // --- Body text at ~10 Hz — bars still update every frame ---
            if (Time.unscaledTime < nextTextRebuildTime)
            {
                return;
            }
            nextTextRebuildTime = Time.unscaledTime + 0.1f;

            // Clamp displayed speed for text so we never show 13.6/13.5 from float noise.
            float displayCur = Mathf.Min(cur, liveMax);
            if (atCruise)
                displayCur = liveMax;

            // [TITAN-ORBIT] Territory tag + always-on OD capacity; active OD also shows xN.Nod.
            // ASCII "x" instead of "×" — wide unicode glyphs look crushed under <mspace>.
            string territoryTag = territoryMult > 1.001f
                ? $" <color=#AAEEDD>x{FormatFixed1(territoryMult, 4)}t</color>"
                : string.Empty;
            string overdriveCapTag = overdriveCapacityMult > 1.001f
                ? $" <color=#FFCC66>od {FormatFixed1(barMax)}</color>"
                : string.Empty;
            string overdriveActiveTag = overdriveActive
                ? $" <color=#FFCC66>x{FormatFixed1(overdriveActiveMult, 4)}on</color>"
                : string.Empty;
            string boostTags = territoryTag + overdriveCapTag + overdriveActiveTag;

            // --- Compact body lines (wrap inside text band if a row is wider than the panel) ---
            // Denominator = live motor ceiling (cruise or OD top); amber "od N.N" is always the bar end.
            string spdLine;
            if (atCruise)
            {
                spdLine =
                    $"SPD {FormatFixed1(displayCur)}/{FormatFixed1(liveMax)}  <color=#AAAAAA>max</color>{boostTags}";
            }
            else
            {
                float remaining = Mathf.Max(0f, liveMax - cur);
                float tMax = remaining / maxFwd;
                tMax = Mathf.Clamp(tMax, 0f, 99.9f);
                spdLine =
                    $"SPD {FormatFixed1(displayCur)}/{FormatFixed1(liveMax)}  <color=#AAEEDD>{FormatFixed1(tMax, 4)}s</color> to max{boostTags}";
            }

            string stopPart = cur > 0.35f
                ? $"  stop {FormatFixed1(cur / maxBrake, 4)}s"
                : "  stop —.−s";

            string line2 =
                $"ACC {FormatFixedSigned1(smoothedHorizontalAccel)}/{FormatFixed1(maxFwd)}  brk {FormatFixed1(maxBrake)}  MASS {FormatFixed1(displayMass)}";

            GetRamDamageEstimate(
                ship,
                motor,
                effectiveStats,
                cur,
                out float ramAst,
                out float ramSelf,
                out float ramRating,
                out float ramTotalMass);

            string line3 =
                $"RAM {FormatFixed1(ramRating, 4)} × m{FormatFixed1(ramTotalMass, 4)} × v{FormatFixed1(cur, 4)}  ast {FormatFixed1(ramAst)}  hull {FormatFixed1(ramSelf)}";

            string line4;
            if (weapon.FireRate > 0.01f && weapon.BulletDamage > 0.01f)
            {
                // [TITAN-ORBIT] BulletDamage / FireRate on ShipWeaponConfig are averages across
                // mounts — each barrel still fires its own FirePower (see ShipWeaponMountElement).
                float dps = weapon.BulletDamage * weapon.FireRate;
                line4 =
                    $"BUL {FormatFixed1(weapon.BulletDamage)}/hit  {FormatFixed1(dps)}/s  <color=#888888>{FormatFixed1(weapon.FireRate)}/s</color>";
            }
            else
                line4 = "BUL  —.−/hit  —.−/s";

            // [UNITY] <mspace> forces equal advance so digit columns do not jump.
            // Must be wide enough for LiberationSans SDF glyphs — 0.52em crushed letters together.
            string body =
                "<mspace=0.72em>" + spdLine + stopPart + "\n" + line2 + "\n" + line3 + "\n" + line4 + "</mspace>";
            if (body != lastHudBodyText)
            {
                lastHudBodyText = body;
                speedLabel.text = body;
            }
        }

        /// <summary>
        /// Hides the panel once and clears sample state when the GameManager toggle turns off.
        /// Subsequent frames while still disabled hit the early return with no further work.
        /// </summary>
        void EnterDisabledIdle()
        {
            if (_idleBecauseDisabled)
                return;

            _idleBecauseDisabled = true;
            if (rootPanel != null)
                rootPanel.SetActive(false);
            if (_tooltipPanel != null && _tooltipPanel.activeSelf)
            {
                _activeTooltipSection = null;
                _pendingHideSection = null;
                _tooltipPanel.SetActive(false);
            }

            // --- Drop transient sample state so re-enable does not flash a stale accel spike ---
            hasLastHorizontalSpeed = false;
            accelSampleShip = Entity.Null;
            smoothedHorizontalAccel = 0f;
            nextTextRebuildTime = 0f;
        }
    }
}
