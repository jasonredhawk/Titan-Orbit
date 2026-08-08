using System;
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
    /// Screen placement for the local-player speedometer panel.
    /// Default is top-center so SPD/ACC bars sit near the top of the map.
    /// </summary>
    public enum SpeedometerPlacement
    {
        [Tooltip("Clear of bottom-right minimap; pair with attribute bar on wide layouts.")]
        BottomLeft = 0,
        BottomRight = 1,
        TopLeft = 2,
        TopRight = 3,
        /// <summary>[TITAN-ORBIT] Default — SPD/ACC bars centered under the top edge of the map.</summary>
        TopCenter = 4
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
    /// even when Shift is not held. The right-hand band uses the fill colour at alpha 0.1 so
    /// players see unused overdrive headroom; the solid fill only enters that band while OD is active.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Pre–mass-tax baselines for SPD / ACC / turn are always chassis
    /// <see cref="ShipComponentAbilityStats"/> (leveled + attrs). Live subtractive mass tax
    /// (gems/people/ComponentSize) then matches <see cref="ShipPhysicsDriveLogic"/> (motor stores
    /// the same chassis baselines after <see cref="ShipStatApplyLogic"/>).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Shows SPD + ACC bars only (top-center). MASS / RAM / BUL and ability
    /// calculation breakdowns live on the bottom Ship Ability chips — see
    /// <see cref="ShipAttributeUpgradeHUD"/> / <see cref="ShipAbilityStatBreakdown"/>.
    /// </para>
    /// <para>
    /// Master on/off lives on <see cref="GameManager.ShowSpeedometer"/> (NceGameRoot Inspector → HUD).
    /// When off, this component does not build UI and LateUpdate returns immediately — no ECS ship
    /// queries, no bar math, no TMP rebuilds. Presentation-only — never writes ECS.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Compact top-center SPD + ACC bars only (no numeric body text). Hover pads
    /// open short rollovers; full part pipelines live on ability chips —
    /// <see cref="ShipSpeedometerStatTooltips"/> / <see cref="ShipAttributeUpgradeHUD"/>.
    /// </para>
    /// Hidden during team select, death, and when the upgrade tree obscures HUD.
    /// </summary>
    public class ShipSpeedometerHUD : MonoBehaviour
    {
        /// <summary>Compact top-center gamer meter scale (bars only, no panel chrome).</summary>
        const float HudLayoutScale = 1f;

        /// <summary>Legible compact meter width (ticks + live value fit).</summary>
        const float SlimPanelWidth = 220f;
        /// <summary>Two thin bars + tick rows under each — small but readable.</summary>
        const float SlimPanelHeight = 36f;
        /// <summary>Left gutter for SPD/ACC micro-tags.</summary>
        const float TagGutter = 18f;
        /// <summary>Right gutter for the live speed readout.</summary>
        const float ValueGutter = 30f;

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
        [SerializeField] SpeedometerPlacement placement = SpeedometerPlacement.TopCenter;
        // Kept for Inspector/serialization; BuildUI forces SlimPanelWidth/Height.
        [SerializeField] float panelWidth = SlimPanelWidth;
        [SerializeField] float panelHeight = SlimPanelHeight;
        [SerializeField, FormerlySerializedAs("accelerationDisplayResponsiveness")]
        float accelerationBarSmoothing = 5f;
        [SerializeField, FormerlySerializedAs("rightMargin")] float horizontalMargin = 20f;
        [SerializeField, FormerlySerializedAs("bottomMargin")] float verticalMargin = 6f;
        [Tooltip("Unused when placement is TopCenter — kept for Inspector compatibility with older corner layouts.")]
        [SerializeField] float stackGapAboveUpgradeBar = 20f;

        [Header("Colors")]
        [SerializeField] Color backgroundColor = new Color(0f, 0f, 0f, 0f);
        [SerializeField] Color fillColor = new Color(0.35f, 0.85f, 1f, 0.95f);
        [SerializeField] Color trackColor = new Color(0.05f, 0.07f, 0.1f, 0.55f);
        /// <summary>
        /// [TITAN-ORBIT] OVERDRIVE headroom band on the speed bar. Runtime uses
        /// <see cref="fillColor"/> RGB with alpha 0.1 so the unused OD segment reads as a faint
        /// tint of the same cyan fill (not a separate amber slab).
        /// </summary>
        [SerializeField] Color overdriveZoneColor = new Color(0.35f, 0.85f, 1f, 0.1f);
        [SerializeField] Color textColor = new Color(0.92f, 0.95f, 1f, 1f);
        [SerializeField] Color accelPositiveColor = new Color(0.25f, 0.92f, 0.45f, 0.95f);
        [SerializeField] Color accelNegativeColor = new Color(0.95f, 0.28f, 0.28f, 0.95f);
        [SerializeField] Color tickLabelColor = new Color(0.85f, 0.9f, 1f, 0.85f);
        [SerializeField] Color frameAccentColor = new Color(0.35f, 0.85f, 1f, 0.35f);

        GameObject rootPanel;
        Slider speedSlider;
        /// <summary>Speed-bar band for OVERDRIVE headroom (behind the cyan fill).</summary>
        RectTransform overdriveZoneRect;
        Image overdriveZoneImage;
        RectTransform accelGreenFill;
        RectTransform accelRedFill;
        TextMeshProUGUI speedLabel;
        /// <summary>Live planar speed digits to the right of the SPD track (compact readout).</summary>
        TextMeshProUGUI speedLiveLabel;
        TextMeshProUGUI[] speedTickLabels;
        TextMeshProUGUI[] accelTickLabels;
        /// <summary>Anchors for the middle SPD interest tick (cruise / OD boundary) — moves with OD zone.</summary>
        RectTransform speedCruiseTickRt;
        RectTransform speedCruiseMarkRt;
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

        /// <summary>[TITAN-ORBIT] Sci-fi chrome — accent recolors per speedometer section.</summary>
        ShipStatTooltipChrome.Handles _tooltipChrome;

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
        /// Fingerprint for static tip / shared LiveContext rebuilds (ship + abilities + part cache).
        /// [TITAN-ORBIT] Tip copy used to rebuild every LateUpdate while hovered — large GC/FPS hit.
        /// </summary>
        int _tooltipSnapshotKey = int.MinValue;

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
        /// One-time procedural HUD build. Compact top-center gamer meter with thin SPD/ACC tracks,
        /// interest ticks (0 / cruise / OD max, ±accel), and a live speed readout — no panel background.
        /// </summary>
        void BuildUIIfNeeded()
        {
            if (!IsFeatureEnabled())
                return;

            // --- Rebuild when size drifts or tick strip is missing ---
            if (uiBuilt && rootPanel != null)
            {
                RectTransform existingRt = rootPanel.GetComponent<RectTransform>();
                bool needsRebuild = speedLabel != null
                    || rootPanel.GetComponent<Image>() != null
                    || rootPanel.GetComponent<Outline>() != null
                    || rootPanel.transform.Find("HudText") != null
                    || rootPanel.transform.Find("SpeedTicks") == null
                    || existingRt == null
                    || Mathf.Abs(existingRt.sizeDelta.y - SlimPanelHeight) > 0.5f
                    || Mathf.Abs(existingRt.sizeDelta.x - SlimPanelWidth) > 0.5f;
                if (!needsRebuild)
                    return;

                Destroy(rootPanel);
                rootPanel = null;
                speedLabel = null;
                speedLiveLabel = null;
                speedSlider = null;
                overdriveZoneRect = null;
                overdriveZoneImage = null;
                accelGreenFill = null;
                accelRedFill = null;
                speedCruiseTickRt = null;
                speedCruiseMarkRt = null;
                _rootRect = null;
                uiBuilt = false;
            }
            else if (uiBuilt)
            {
                return;
            }

            // Orphan from a previous tear-down / play session (rename so Create name does not collide).
            Transform existing = transform.Find("ShipSpeedometer");
            if (existing != null)
            {
                existing.name = "ShipSpeedometer_Legacy";
                Destroy(existing.gameObject);
            }

            // --- Resolve parent canvas ---
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
                return;

            // [TITAN-ORBIT] Lock slim top-center meter (ignore corner / fat scene overrides).
            placement = SpeedometerPlacement.TopCenter;
            panelWidth = SlimPanelWidth;
            panelHeight = SlimPanelHeight;

            rootPanel = new GameObject("ShipSpeedometer");
            rootPanel.transform.SetParent(transform, false);
            RectTransform rootRect = rootPanel.AddComponent<RectTransform>();
            ApplyPlacement(rootRect);
            rootRect.sizeDelta = new Vector2(SlimPanelWidth, SlimPanelHeight);
            // No panel Image / Outline — floating bars only (gamer HUD).

            // --- Bands (bottom→top): ACC ticks, ACC bar, SPD ticks, SPD bar ---
            float pad = 2f;
            const float accelTickB = 0.00f;
            const float accelTickT = 0.18f;
            const float accelBarB = 0.20f;
            const float accelBarT = 0.42f;
            const float speedTickB = 0.46f;
            const float speedTickT = 0.62f;
            const float speedBarB = 0.66f;
            const float speedBarT = 1.00f;

            CreateBarTag("Tag_SPD", "SPD", new Vector2(0f, speedBarB), new Vector2(0f, speedBarT), fillColor);
            CreateBarTag("Tag_ACC", "ACC", new Vector2(0f, accelBarB), new Vector2(0f, accelBarT), accelPositiveColor);

            // --- SPD track ---
            GameObject sliderGo = new GameObject("SpeedBar");
            sliderGo.transform.SetParent(rootPanel.transform, false);
            RectTransform sliderRect = sliderGo.AddComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, speedBarB);
            sliderRect.anchorMax = new Vector2(1f, speedBarT);
            sliderRect.offsetMin = new Vector2(pad + TagGutter, 0f);
            sliderRect.offsetMax = new Vector2(-(pad + ValueGutter), 0f);

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

            GameObject odZoneGo = new GameObject("OverdriveZone");
            odZoneGo.transform.SetParent(sliderGo.transform, false);
            overdriveZoneRect = odZoneGo.AddComponent<RectTransform>();
            overdriveZoneRect.anchorMin = new Vector2(0.57f, 0f);
            overdriveZoneRect.anchorMax = Vector2.one;
            overdriveZoneRect.offsetMin = Vector2.zero;
            overdriveZoneRect.offsetMax = Vector2.zero;
            overdriveZoneImage = odZoneGo.AddComponent<Image>();
            overdriveZoneImage.color = ResolveOverdriveZoneColor();
            overdriveZoneImage.raycastTarget = false;

            GameObject cruiseMark = new GameObject("CruiseMark");
            cruiseMark.transform.SetParent(sliderGo.transform, false);
            speedCruiseMarkRt = cruiseMark.AddComponent<RectTransform>();
            speedCruiseMarkRt.anchorMin = new Vector2(0.57f, 0f);
            speedCruiseMarkRt.anchorMax = new Vector2(0.57f, 1f);
            speedCruiseMarkRt.pivot = new Vector2(0.5f, 0.5f);
            speedCruiseMarkRt.sizeDelta = new Vector2(1.5f, 0f);
            Image cruiseMarkImg = cruiseMark.AddComponent<Image>();
            cruiseMarkImg.color = new Color(1f, 1f, 1f, 0.55f);
            cruiseMarkImg.raycastTarget = false;

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

            CreateBarEndCap(sliderGo.transform, fillColor);

            GameObject liveGo = new GameObject("SpeedLive");
            liveGo.transform.SetParent(rootPanel.transform, false);
            RectTransform liveRt = liveGo.AddComponent<RectTransform>();
            liveRt.anchorMin = new Vector2(1f, speedBarB);
            liveRt.anchorMax = new Vector2(1f, speedBarT);
            liveRt.pivot = new Vector2(1f, 0.5f);
            liveRt.sizeDelta = new Vector2(ValueGutter - 2f, 0f);
            liveRt.anchoredPosition = new Vector2(-pad, 0f);
            speedLiveLabel = liveGo.AddComponent<TextMeshProUGUI>();
            speedLiveLabel.text = "—";
            speedLiveLabel.fontSize = 8f;
            speedLiveLabel.fontStyle = FontStyles.Bold;
            speedLiveLabel.alignment = TextAlignmentOptions.MidlineRight;
            speedLiveLabel.color = fillColor;
            speedLiveLabel.enableWordWrapping = false;
            speedLiveLabel.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                speedLiveLabel.font = TMP_Settings.defaultFontAsset;

            GameObject speedTickStrip = new GameObject("SpeedTicks");
            speedTickStrip.transform.SetParent(rootPanel.transform, false);
            RectTransform speedTickRect = speedTickStrip.AddComponent<RectTransform>();
            speedTickRect.anchorMin = new Vector2(0f, speedTickB);
            speedTickRect.anchorMax = new Vector2(1f, speedTickT);
            speedTickRect.offsetMin = new Vector2(pad + TagGutter, 0f);
            speedTickRect.offsetMax = new Vector2(-(pad + ValueGutter), 0f);
            speedTickLabels = CreateInterestTickRow(speedTickStrip.transform, 7f, out speedCruiseTickRt);

            GameObject accelRoot = new GameObject("AccelBar");
            accelRoot.transform.SetParent(rootPanel.transform, false);
            RectTransform accelRootRect = accelRoot.AddComponent<RectTransform>();
            accelRootRect.anchorMin = new Vector2(0f, accelBarB);
            accelRootRect.anchorMax = new Vector2(1f, accelBarT);
            accelRootRect.offsetMin = new Vector2(pad + TagGutter, 0f);
            accelRootRect.offsetMax = new Vector2(-(pad + ValueGutter), 0f);

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
            cl.anchorMin = new Vector2(0.5f, 0f);
            cl.anchorMax = new Vector2(0.5f, 1f);
            cl.pivot = new Vector2(0.5f, 0.5f);
            cl.sizeDelta = new Vector2(1f, 0f);
            Image cli = centerLine.AddComponent<Image>();
            cli.color = new Color(1f, 1f, 1f, 0.4f);
            cli.raycastTarget = false;

            GameObject accelTickStrip = new GameObject("AccelTicks");
            accelTickStrip.transform.SetParent(rootPanel.transform, false);
            RectTransform accelTickRect = accelTickStrip.AddComponent<RectTransform>();
            accelTickRect.anchorMin = new Vector2(0f, accelTickB);
            accelTickRect.anchorMax = new Vector2(1f, accelTickT);
            accelTickRect.offsetMin = new Vector2(pad + TagGutter, 0f);
            accelTickRect.offsetMax = new Vector2(-(pad + ValueGutter), 0f);
            accelTickLabels = CreateTickLabelRow(accelTickStrip.transform, 3, 6.5f);

            speedLabel = null;
            lastTickMaxSpeed = -1f;
            lastTickAccelSkew = -1f;

            CreateHoverZone(
                "Hover_SPD_Bar",
                new Vector2(0f, speedTickB),
                new Vector2(1f, speedBarT),
                SpeedometerStatSection.Speed);
            CreateHoverZone(
                "Hover_ACC_Bar",
                new Vector2(0f, accelTickB),
                new Vector2(1f, accelBarT),
                SpeedometerStatSection.Accel);

            BuildTooltipPanel(canvas);

            uiBuilt = true;
        }

        /// <summary>
        /// Micro left-side tag (SPD / ACC) — chrome only, not live telemetry.
        /// </summary>
        void CreateBarTag(string name, string label, Vector2 anchorMin, Vector2 anchorMax, Color accent)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(rootPanel.transform, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(0f, 0f);
            rt.offsetMax = new Vector2(TagGutter - 1f, 0f);

            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 6.5f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.color = new Color(accent.r, accent.g, accent.b, 0.7f);
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
        }

        /// <summary>1px accent tip on the right of a track (reads as a HUD end stop).</summary>
        void CreateBarEndCap(Transform parent, Color accent)
        {
            GameObject cap = new GameObject("EndCap");
            cap.transform.SetParent(parent, false);
            RectTransform rt = cap.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(1.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            Image img = cap.AddComponent<Image>();
            img.color = new Color(accent.r, accent.g, accent.b, 0.55f);
            img.raycastTarget = false;
        }

        /// <summary>OVERDRIVE zone uses the speed-fill RGB at alpha 0.1.</summary>
        Color ResolveOverdriveZoneColor()
        {
            Color c = fillColor;
            c.a = 0.1f;
            return c;
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
        /// [TITAN-ORBIT] Uses <see cref="ShipStatTooltipChrome"/> so speedometer tips match the
        /// ability-chip calculation cards (Shift cut-frame + category accent).
        /// </summary>
        void BuildTooltipPanel(Canvas canvas)
        {
            // [UNITY] Parent under the same HUD host as the speedometer so canvas scale matches.
            _tooltipChrome = ShipStatTooltipChrome.Build(
                "ShipSpeedometerTooltip",
                transform,
                "TELEMETRY",
                520f * HudLayoutScale,
                120f * HudLayoutScale,
                HudLayoutScale);
            _tooltipPanel = _tooltipChrome.Root;
            _tooltipRect = _tooltipChrome.RootRect;
            _tooltipLabel = _tooltipChrome.BodyLabel;
            if (_tooltipRect != null)
                _tooltipRect.pivot = new Vector2(0f, 0f);
            if (_tooltipLabel != null)
            {
                _tooltipLabel.fontSize = 11f * HudLayoutScale;
                _tooltipLabel.color = textColor;
                _tooltipLabel.text = "";
            }

            // Sort above the speedometer panel so the tip is never covered by its background.
            if (canvas != null && _tooltipPanel != null)
                _tooltipPanel.transform.SetAsLastSibling();
        }

        /// <summary>
        /// Copies the latest part cache + live motor context for ability-chip rollovers.
        /// Returns false until the speedometer has painted at least one frame with a ship.
        /// </summary>
        public bool TryGetTooltipSharedState(
            out ShipSpeedometerStatTooltips.PartCache parts,
            out ShipSpeedometerStatTooltips.LiveContext live)
        {
            parts = _partCache;
            live = _liveTooltipContext;
            return _hasHudCache && parts.Valid;
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
            // [TITAN-ORBIT] Recolor chrome accent for SPD / ACC / MASS / RAM / BUL.
            ShipStatTooltipChrome.ApplyAccent(
                in _tooltipChrome,
                ShipStatTooltipChrome.AccentForSpeedometerSection(section));
            RefreshTooltipContent();
            PositionTooltipPanel();
            // Draw above other HUD chrome so bars / names cannot bleed through.
            _tooltipPanel.transform.SetAsLastSibling();
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

        /// <summary>
        /// Rebuilds tooltip TMP from the active section + cached parts + static capacity context.
        /// Call on hover-enter or when <see cref="_tooltipSnapshotKey"/> changes — not every LateUpdate.
        /// </summary>
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
                // [TITAN-ORBIT] ExtraHeightPadding covers caption bar + frame insets from chrome.
                float tipW = _tooltipRect.sizeDelta.x;
                float tipH = Mathf.Max(
                    100f * HudLayoutScale,
                    _tooltipLabel.preferredHeight + _tooltipChrome.ExtraHeightPadding);
                _tooltipRect.sizeDelta = new Vector2(tipW, tipH);
            }
        }

        /// <summary>
        /// Fingerprint for static tip / shared LiveContext rebuilds.
        /// Changes when the player buys a ship, upgrades an ability, or swaps store parts.
        /// </summary>
        static int ComputeTooltipSnapshotKey(
            in ShipState ship,
            in ShipAttributeUpgradeState attrs,
            int equipmentHash,
            string chassisId)
        {
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
                h = h * 31 + equipmentHash;
                // Chassis id — rare rebuild; GetHashCode is acceptable here.
                h = h * 31 + (chassisId != null ? chassisId.GetHashCode() : 0);
                return h;
            }
        }

        /// <summary>
        /// Places the tip clear of the speedometer.
        /// TopCenter: tip hangs below the panel toward the map.
        /// Bottom placements: tip sits above the panel.
        /// Top corners: tip sits beside the panel.
        /// </summary>
        void PositionTooltipPanel()
        {
            if (_tooltipRect == null)
                return;
            if (_rootRect == null && rootPanel != null)
                _rootRect = rootPanel.GetComponent<RectTransform>();
            if (_rootRect == null)
                return;

            Vector2 panelPos = _rootRect.anchoredPosition;
            Vector2 panelSize = _rootRect.sizeDelta;
            float gap = 10f * HudLayoutScale;
            _tooltipRect.anchorMin = _rootRect.anchorMin;
            _tooltipRect.anchorMax = _rootRect.anchorMax;

            if (placement == SpeedometerPlacement.TopCenter)
            {
                // [TITAN-ORBIT] Below the bars so the tip does not clip off the top of the screen.
                _tooltipRect.pivot = new Vector2(0.5f, 1f);
                _tooltipRect.anchoredPosition = new Vector2(panelPos.x, panelPos.y - panelSize.y - gap);
                return;
            }

            bool bottom = placement == SpeedometerPlacement.BottomLeft
                || placement == SpeedometerPlacement.BottomRight;
            bool left = placement == SpeedometerPlacement.BottomLeft
                || placement == SpeedometerPlacement.TopLeft;

            if (bottom)
            {
                _tooltipRect.pivot = left ? new Vector2(0f, 0f) : new Vector2(1f, 0f);
                float x = left ? panelPos.x : panelPos.x;
                _tooltipRect.anchoredPosition = new Vector2(x, panelPos.y + panelSize.y + gap);
            }
            else
            {
                _tooltipRect.pivot = left ? new Vector2(0f, 1f) : new Vector2(1f, 1f);
                float x = left
                    ? panelPos.x + panelSize.x + gap
                    : panelPos.x - gap;
                _tooltipRect.anchoredPosition = new Vector2(x, panelPos.y);
            }
        }

        /// <summary>
        /// Builds SPD interest ticks: left=0, middle=cruise (movable), right=bar max.
        /// Middle RectTransform is returned so LateUpdate can slide it to the OD boundary.
        /// </summary>
        TextMeshProUGUI[] CreateInterestTickRow(Transform parent, float fontSize, out RectTransform middleRt)
        {
            var labels = new TextMeshProUGUI[3];
            float[] xs = { 0f, 0.5f, 1f };
            float[] pivots = { 0f, 0.5f, 1f };
            middleRt = null;
            for (int i = 0; i < 3; i++)
            {
                GameObject go = new GameObject($"Interest{i}");
                go.transform.SetParent(parent, false);
                RectTransform rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(xs[i], 0f);
                rt.anchorMax = new Vector2(xs[i], 1f);
                rt.pivot = new Vector2(pivots[i], 0.5f);
                rt.sizeDelta = new Vector2(40f, 0f);
                rt.anchoredPosition = Vector2.zero;
                if (i == 1)
                    middleRt = rt;

                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.text = "—";
                tmp.fontSize = fontSize;
                tmp.fontStyle = FontStyles.Bold;
                tmp.enableAutoSizing = false;
                tmp.richText = false;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.alignment = i == 0
                    ? TextAlignmentOptions.MidlineLeft
                    : (i == 2 ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Midline);
                if (TMP_Settings.defaultFontAsset != null)
                    tmp.font = TMP_Settings.defaultFontAsset;
                tmp.color = tickLabelColor;
                tmp.raycastTarget = false;
                labels[i] = tmp;
            }

            return labels;
        }

        /// <summary>
        /// Builds a row of fixed-width tick labels under a bar. Edge ticks left/right-align so
        /// neighboring labels do not overlap at the strip ends.
        /// </summary>
        TextMeshProUGUI[] CreateTickLabelRow(Transform parent, int count, float fontSize)
        {
            var labels = new TextMeshProUGUI[count];
            float tickCellW = 36f;
            for (int i = 0; i < count; i++)
            {
                GameObject go = new GameObject($"Tick{i}");
                go.transform.SetParent(parent, false);
                RectTransform rt = go.AddComponent<RectTransform>();
                float x = count <= 1 ? 0.5f : (float)i / (count - 1);
                rt.anchorMin = new Vector2(x, 0f);
                rt.anchorMax = new Vector2(x, 1f);
                float pivotX = i == 0 ? 0f : (i == count - 1 ? 1f : 0.5f);
                rt.pivot = new Vector2(pivotX, 0.5f);
                rt.sizeDelta = new Vector2(tickCellW, 0f);
                rt.anchoredPosition = Vector2.zero;
                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.text = "—";
                tmp.fontSize = fontSize;
                tmp.fontStyle = FontStyles.Bold;
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

        /// <summary>Compact tick number (no figure-space pad) so small HUD stays readable.</summary>
        static string FormatTick(float v) =>
            v.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>Signed compact tick for accel scale ends.</summary>
        static string FormatTickSigned(float v)
        {
            string body = Mathf.Abs(v).ToString("0.#", CultureInfo.InvariantCulture);
            return (v >= 0f ? "+" : "-") + body;
        }

        /// <summary>
        /// Refreshes SPD interest ticks (0 / cruise / OD max), ACC ±scale ticks, live speed digits,
        /// and slides the cruise hash mark to the OD boundary.
        /// </summary>
        void UpdateInterestTicks(float cur, float cruiseMax, float barMax, float maxFwd, float maxBrake)
        {
            float safeBar = Mathf.Max(0.01f, barMax);
            float cruiseFrac = Mathf.Clamp01(cruiseMax / safeBar);

            // --- SPD: 0 · cruise · barMax ---
            if (speedTickLabels != null && speedTickLabels.Length >= 3)
            {
                if (!Mathf.Approximately(lastTickMaxSpeed, barMax)
                    || (speedCruiseTickRt != null
                        && Mathf.Abs(speedCruiseTickRt.anchorMin.x - cruiseFrac) > 0.001f))
                {
                    lastTickMaxSpeed = barMax;
                    speedTickLabels[0].text = "0";
                    speedTickLabels[1].text = FormatTick(cruiseMax);
                    speedTickLabels[2].text = FormatTick(barMax);

                    if (speedCruiseTickRt != null)
                    {
                        speedCruiseTickRt.anchorMin = new Vector2(cruiseFrac, 0f);
                        speedCruiseTickRt.anchorMax = new Vector2(cruiseFrac, 1f);
                    }
                }
            }

            if (speedCruiseMarkRt != null)
            {
                speedCruiseMarkRt.anchorMin = new Vector2(cruiseFrac, 0f);
                speedCruiseMarkRt.anchorMax = new Vector2(cruiseFrac, 1f);
            }

            // --- Live speed readout (right of SPD bar) ---
            if (speedLiveLabel != null)
            {
                string live = FormatTick(cur);
                if (speedLiveLabel.text != live)
                    speedLiveLabel.text = live;
            }

            // --- ACC: -scale · 0 · +scale ---
            float skew = Mathf.Max(maxFwd, maxBrake, 0.01f);
            if (accelTickLabels != null && accelTickLabels.Length >= 3
                && !Mathf.Approximately(lastTickAccelSkew, skew))
            {
                lastTickAccelSkew = skew;
                accelTickLabels[0].text = FormatTickSigned(-skew);
                accelTickLabels[0].alignment = TextAlignmentOptions.MidlineLeft;
                accelTickLabels[1].text = "0";
                accelTickLabels[1].alignment = TextAlignmentOptions.Midline;
                accelTickLabels[2].text = FormatTickSigned(skew);
                accelTickLabels[2].alignment = TextAlignmentOptions.MidlineRight;
            }
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
            // [TITAN-ORBIT] TopCenter (default) no longer stacks above the ability strip.
            if (placement != SpeedometerPlacement.BottomLeft)
                return 0f;

            if (!_triedUpgradeBarLookup)
            {
                _triedUpgradeBarLookup = true;
                _cachedUpgradeBar = UnityEngine.Object.FindFirstObjectByType<ShipAttributeUpgradeHUD>();
            }

            if (_cachedUpgradeBar == null)
                return 0f;
            return _cachedUpgradeBar.GetUpgradeStripReserveHeight() + stackGapAboveUpgradeBar;
        }

        void ApplyPlacement(RectTransform rootRect)
        {
            // --- Anchor + margin (BottomLeft still stacks above upgrade bar for legacy layouts) ---
            float boost = GetBottomLeftStackYBoost();
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
                case SpeedometerPlacement.TopRight:
                    rootRect.anchorMin = new Vector2(1f, 1f);
                    rootRect.anchorMax = new Vector2(1f, 1f);
                    rootRect.pivot = new Vector2(1f, 1f);
                    pos = new Vector2(-h, -v);
                    break;
                default: // TopCenter — [TITAN-ORBIT] default for SPD/ACC map HUD
                    rootRect.anchorMin = new Vector2(0.5f, 1f);
                    rootRect.anchorMax = new Vector2(0.5f, 1f);
                    rootRect.pivot = new Vector2(0.5f, 1f);
                    pos = new Vector2(0f, -v);
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
        /// Used to size the speed bar's OVERDRIVE capacity zone (faint fill-coloured band).
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
        /// Layouts the faint OVERDRIVE band from cruise max → bar max (right side of the speed bar).
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
            // [TITAN-ORBIT] Always fillColor RGB @ alpha 0.1 (ignore stale amber Inspector overrides).
            Color od = ResolveOverdriveZoneColor();
            if (overdriveZoneImage.color != od)
                overdriveZoneImage.color = od;
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

            // Build or rebuild (legacy HudText meter → compact bars-only).
            BuildUIIfNeeded();
            // Bars + interest ticks + live readout.
            if (rootPanel == null || speedSlider == null || accelGreenFill == null || accelRedFill == null
                || overdriveZoneRect == null || speedTickLabels == null || accelTickLabels == null)
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
            // [TITAN-ORBIT] Bar scale = cruise × baked OD mul so the faint OD zone is always visible.
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

            // Fill against full OD scale so unused faint OD headroom stays readable at cruise.
            speedSlider.value = Mathf.Clamp01(cur / Mathf.Max(0.01f, barMax));
            UpdateOverdriveZone(cruiseMax, barMax);

            float mass = GetMovementMass(ship, motor);
            maxFwd = Mathf.Max(0.01f, maxFwd);

            float maxBrake = Mathf.Max(0.01f, motor.BrakeDeceleration > 0f
                ? motor.BrakeDeceleration
                : ShipMassLogic.DefaultBrakeDeceleration);

            // --- Static tip / ability-chip shared context (not every flight frame) ---
            // [TITAN-ORBIT] Part cache Instantiates the chassis prefab only when chassis / store gear changes.
            // Tip bodies are capacity / max-impact snapshots — rebuild on ship/ability/parts dirty only.
            if (vizWorld != null && vizWorld.IsCreated && !string.IsNullOrEmpty(_cachedChassisId))
            {
                ShipSpeedometerStatTooltips.TryRefreshPartCache(
                    vizWorld.EntityManager,
                    shipEntity,
                    _cachedChassisId,
                    ship.ShipLevel,
                    ref _partCache);
            }

            int tipSnapshotKey = ComputeTooltipSnapshotKey(
                in ship, in _statsCacheAttrs, _partCache.EquipmentHash, _cachedChassisId);
            bool tipSnapshotDirty = tipSnapshotKey != _tooltipSnapshotKey;
            if (tipSnapshotDirty)
            {
                _tooltipSnapshotKey = tipSnapshotKey;

                // Max ramming at full cruise — not current closing speed (avoids per-frame tip churn).
                GetRamDamageEstimate(
                    ship,
                    motor,
                    effectiveStats,
                    cruiseMax,
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
                    CurrentSpeed = 0f,
                    LiveMaxSpeed = cruiseMax,
                    CruiseMaxSpeed = cruiseMax,
                    BarMaxSpeed = barMax,
                    TerritoryMult = territoryMult,
                    TotalMass = taxed.TotalMass,
                    ChassisMaxSpeed = chassisMove,
                    ChassisAccel = chassisAccel,
                    ChassisTurnDeg = chassisTurnDeg,
                    TaxedAccel = taxed.EngineThrust,
                    // [TITAN-ORBIT] Pre-territory taxed turn — same subtract as drive; chips show this not chassis.
                    TaxedTurnDeg = taxed.RotationSpeed,
                    OverdriveCapacityMult = overdriveCapacityMult,
                    OverdriveActiveMult = 1f,
                    MovementMass = mass,
                    MaxForwardAccel = maxFwd,
                    MaxBrake = maxBrake,
                    RamAsteroidDamage = tipRamAst,
                    RamSelfDamage = tipRamSelf,
                    RamRating = tipRamRating,
                    ComponentSize = componentSize,
                    MoveSpeedAbilityLevel = _moveSpeedAbilityLevel,
                    MoveStepPreview = _partCache.Valid
                        ? Mathf.Max(0f, _partCache.Propulsion.moveSpeedPerAbilityLevel)
                        : 0f,
                };

                if (_activeTooltipSection.HasValue)
                {
                    RefreshTooltipContent();
                    PositionTooltipPanel();
                }
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

            // Interest ticks + live speed (0 / cruise / OD max, ±accel scale).
            UpdateInterestTicks(cur, cruiseMax, barMax, maxFwd, maxBrake);
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
