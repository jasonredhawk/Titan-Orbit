using System.Globalization;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using TMPro;
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
    /// [TITAN-ORBIT] <see cref="ShipMotorConfig"/> already includes the empty-hold capacity tax from
    /// <see cref="ShipMobilityResolution"/>. Prefer those post-tax motor fields; bake-default
    /// fallbacks re-apply the same tax so freighters never show untaxed chassis speeds.
    /// </para>
    /// <para>
    /// Master on/off lives on <see cref="GameManager.ShowSpeedometer"/> (NceGameRoot Inspector → HUD).
    /// When off, this component does not build UI and LateUpdate returns immediately — no ECS ship
    /// queries, no bar math, no TMP rebuilds. Presentation-only — never writes ECS.
    /// </para>
    /// Hidden during team select, death, and when the upgrade tree obscures HUD.
    /// </summary>
    public class ShipSpeedometerHUD : MonoBehaviour
    {
        const float HudLayoutScale = 1.6f;
        const float AsteroidCollisionNormalSpeedRetention = 0.93f;

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
        [SerializeField] Color textColor = new Color(0.92f, 0.95f, 1f, 1f);
        [SerializeField] Color accelPositiveColor = new Color(0.25f, 0.92f, 0.45f, 0.92f);
        [SerializeField] Color accelNegativeColor = new Color(0.95f, 0.28f, 0.28f, 0.92f);
        [SerializeField] Color tickLabelColor = new Color(0.78f, 0.82f, 0.9f, 0.72f);

        GameObject rootPanel;
        Slider speedSlider;
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

        /// <summary>Cached chassis id string — FixedString.ToString() every LateUpdate allocates.</summary>
        string _cachedChassisId;
        Entity _statsCacheShipEntity;
        int _statsCacheShipLevel = int.MinValue;
        int _statsCacheBranch = int.MinValue;
        ShipComponentAbilityStats _statsCacheEffective;
        ShipAttributeUpgradeState _statsCacheAttrs;

        /// <summary>
        /// Latched when GameManager turns the HUD off so we hide the panel once and clear samples.
        /// </summary>
        bool _idleBecauseDisabled;

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
            // [UNITY] No wrap — long telemetry lines stay on one row; wrapping used to climb into bars.
            speedLabel.enableWordWrapping = false;
            speedLabel.overflowMode = TextOverflowModes.Ellipsis;
            // [UNITY] Monospace keeps SPD/ACC columns from shifting when digits change.
            if (TMP_Settings.defaultFontAsset != null)
                speedLabel.font = TMP_Settings.defaultFontAsset;
            speedLabel.color = textColor;
            bool alignLeft = placement == SpeedometerPlacement.BottomLeft || placement == SpeedometerPlacement.TopLeft;
            speedLabel.alignment = alignLeft ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.TopRight;

            uiBuilt = true;
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

        /// <summary>Always one decimal, fixed character width (figure-space pad) so layout never jumps.</summary>
        static string FormatFixed1(float v, int width = 5)
        {
            string s = v.ToString("0.0", CultureInfo.InvariantCulture);
            return PadFigure(s, width);
        }

        /// <summary>Always signed (+/-) with one decimal — ACC never drops the sign glyph.</summary>
        static string FormatFixedSigned1(float v, int width = 6)
        {
            string s = (v >= 0f ? "+" : "-") + Mathf.Abs(v).ToString("0.0", CultureInfo.InvariantCulture);
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

            // Reuse cached chassis/effective stats when ship identity + level + branch + attrs unchanged.
            // ChassisId.ToString() + catalog lookups every LateUpdate were ~13KB GC (Profiler 5199).
            ShipAttributeUpgradeState attrs = default;
            bool hasAttrs = em.HasComponent<ShipAttributeUpgradeState>(shipEntity);
            if (hasAttrs)
                attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);

            if (_statsCacheShipEntity == shipEntity &&
                _statsCacheShipLevel == ship.ShipLevel &&
                _statsCacheBranch == branchIndex &&
                (!hasAttrs || AttrsEqual(attrs, _statsCacheAttrs)))
            {
                effectiveStats = _statsCacheEffective;
                return true;
            }

            string chassisId = null;
            if (em.HasComponent<ShipChassisState>(shipEntity))
            {
                var chassis = em.GetComponentData<ShipChassisState>(shipEntity);
                if (_statsCacheShipEntity == shipEntity && _cachedChassisId != null)
                    chassisId = _cachedChassisId;
                else
                {
                    chassisId = chassis.ChassisId.ToString();
                    _cachedChassisId = chassisId;
                }
            }

            if (string.IsNullOrEmpty(chassisId))
            {
                ShipStatApplyLogic.TryResolveChassisId(
                    ship.Team,
                    ship.ShipLevel,
                    branchIndex,
                    out chassisId,
                    allowFallback: true,
                    shipFamilyConfigIndex: ship.ShipFamilyConfigIndex);
            }

            if (!string.IsNullOrEmpty(chassisId) &&
                ShipStatApplyLogic.TryGetBaseStatsForChassis(chassisId, ship.ShipLevel, out ShipComponentAbilityStats summed))
            {
                effectiveStats = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(summed, ship.ShipLevel);
                if (hasAttrs)
                    ShipAttributeUpgradeLogic.ApplyMultipliers(ref effectiveStats, attrs);
            }

            _statsCacheShipEntity = shipEntity;
            _statsCacheShipLevel = ship.ShipLevel;
            _statsCacheBranch = branchIndex;
            _statsCacheAttrs = attrs;
            _statsCacheEffective = effectiveStats;
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
        /// Display MaxSpeed: prefer post-tax <see cref="ShipMotorConfig.MaxSpeed"/>.
        /// When motor still looks like bake defaults, rebuild from chassis moveSpeed and apply
        /// the same capacity tax so the bar matches freighter / fighter feel.
        /// Does <b>not</b> include territory boost — call <see cref="ResolveTerritoryMovementMult"/> after.
        /// </summary>
        static float ResolveDisplayMaxSpeed(in ShipMotorConfig motor, in ShipComponentAbilityStats effectiveStats)
        {
            float motorMax = Mathf.Max(0.01f, motor.MaxSpeed);
            float chassisMax = effectiveStats.moveSpeed > 0.1f ? effectiveStats.moveSpeed : 0f;

            // [TITAN-ORBIT] Before client apply runs (first frames), bake MaxSpeed=35 would empty the bar.
            // Capacity tax is already inside motorMax after ApplyToShip; only tax the chassis fallback.
            if (chassisMax > 0.1f && motorMax > chassisMax * 1.35f)
            {
                float untaxedThrust = Mathf.Max(0.1f, effectiveStats.accelerationCap > 0f
                    ? effectiveStats.accelerationCap
                    : chassisMax) * ShipPropulsionAggregation.EngineThrustVisibility;
                float untaxedTurn = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(
                    effectiveStats.turnSpeed);
                return ShipMobilityResolution.ApplyCapacityTax(
                    chassisMax,
                    untaxedThrust,
                    untaxedTurn,
                    effectiveStats.maxGems,
                    effectiveStats.maxPeople).MaxSpeed;
            }

            return motorMax;
        }

        /// <summary>
        /// Sticky friendly-triangle movement multiplier for the local owner (1 when outside).
        /// Same cache that grows engine/thruster meshes and that predicted drive publishes.
        /// </summary>
        static float ResolveTerritoryMovementMult() =>
            Mathf.Max(1f, PlanetConnectionGraphCache.LocalOwnerTerritoryMult);

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

        /// <summary>Estimates asteroid ram damage from current speed and effective ramming stats.</summary>
        static void GetRamDamageEstimate(
            in ShipState ship,
            in ShipMotorConfig motor,
            in ShipComponentAbilityStats effectiveStats,
            float inboundSpeed,
            out float asteroidDamage,
            out float selfDamage,
            out float ramRating,
            out float ramMass,
            out float massFactor)
        {
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float hullBaseline = ShipMassLogic.ComputeRammingHullMassBaseline(
                motor.HullMassReference,
                ship.MaxHealth,
                motor.ChassisReferenceHealth,
                baseMass);
            ramMass = ShipMassLogic.ComputeRammingMass(
                motor.HullMassReference,
                ship.MaxHealth,
                motor.ChassisReferenceHealth,
                ship.CurrentGems,
                baseMass,
                ship.CurrentPeople);

            // Prefer motor.RammingPower (server-applied) when present; else rebuild from family stats.
            float familyRammingPower = motor.RammingPower > 0f
                ? motor.RammingPower
                : (effectiveStats.rammingPower > 0f
                    ? effectiveStats.rammingPower
                    : ShipFamilyDefaultFallbackStats.CreateBaseline().rammingPower);
            ramRating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(familyRammingPower);
            massFactor = ShipComponentRammingSuggestions.ComputeMassDamageFactor(ramMass, hullBaseline);

            // [TITAN-ORBIT] Same helpers as ShipAsteroidRammingDamageSystem — HUD cannot drift.
            float restitution = Mathf.Approximately(AsteroidCollisionNormalSpeedRetention,
                ShipComponentRammingSuggestions.MaxAsteroidRestitutionForDamage)
                ? ShipComponentRammingSuggestions.MaxAsteroidRestitutionForDamage
                : AsteroidCollisionNormalSpeedRetention;
            asteroidDamage = ShipComponentRammingSuggestions.ComputeImpactDamage(
                ramRating, ramMass, hullBaseline, inboundSpeed, restitution);
            selfDamage = ShipComponentRammingSuggestions.ComputeImpactSelfDamage(
                ramRating, ramMass, hullBaseline, inboundSpeed, restitution);
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
                || speedTickLabels == null || accelTickLabels == null)
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
            float chassisMove = effectiveStats.moveSpeed;
            float maxSpd = ResolveDisplayMaxSpeed(motor, effectiveStats);
            // Bake default MaxSpeed=35 while chassis is ~13 — motor not applied yet this frame.
            bool motorLooksBaked = chassisMove > 0.1f && motor.MaxSpeed > chassisMove * 1.35f;

            // --- Friendly territory boost (same mult as ShipPhysicsDriveLogic) ---
            // [TITAN-ORBIT] Motor multiplies MaxSpeed / EngineThrust at drive time only; chassis
            // ShipMotorConfig stays unboosted. Without this, the bar saturates early and SPD
            // shows e.g. 13.5/13.5 "at max" while kinematics are still climbing past chassis cruise.
            float territoryMult = ResolveTerritoryMovementMult();
            maxSpd *= territoryMult;

            // --- Current-load MaxSpeed tax (same as ShipPhysicsDriveLogic each tick) ---
            // Capacity tax is already inside motor.MaxSpeed; collecting gems/people further
            // lowers the live cruise cap — HUD must match or the bar lies at "full" while slow.
            var loadMul = ShipMobilityResolution.ApplyCurrentLoadTax(ship.CurrentGems, ship.CurrentPeople);
            maxSpd *= loadMul.SpeedMultiplier;

            speedSlider.value = Mathf.Clamp01(cur / Mathf.Max(0.01f, maxSpd));

            float mass = GetMovementMass(ship, motor);
            // [TITAN-ORBIT] F/m — same a = (EngineThrust × territory)/mass the motor uses below MaxSpeed.
            // EngineThrust on motor is already capacity-taxed by ShipStatApplyLogic.
            float thrustForDisplay = motor.EngineThrust * territoryMult;
            float maxFwd = Mathf.Max(0.01f, thrustForDisplay / Mathf.Max(ShipMassLogic.MinMass, mass));
            if (motorLooksBaked && effectiveStats.accelerationCap > 0.1f)
            {
                // --- Bake fallback: tax chassis accel the same way ApplyToShip would ---
                float untaxedThrust = effectiveStats.accelerationCap
                    * ShipPropulsionAggregation.EngineThrustVisibility;
                float untaxedTurn = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(
                    effectiveStats.turnSpeed);
                float taxedThrust = ShipMobilityResolution.ApplyCapacityTax(
                    effectiveStats.moveSpeed,
                    untaxedThrust,
                    untaxedTurn,
                    effectiveStats.maxGems,
                    effectiveStats.maxPeople).EngineThrust;
                maxFwd = Mathf.Max(
                    0.01f,
                    taxedThrust * territoryMult / Mathf.Max(ShipMassLogic.MinMass, mass));
            }

            float maxBrake = Mathf.Max(0.01f, motor.BrakeDeceleration > 0f
                ? motor.BrakeDeceleration
                : ShipMassLogic.DefaultBrakeDeceleration);

            // --- Accel bar from frame-to-frame speed delta ---
            // [TITAN-ORBIT] Presentation-only. Editor ~30 FPS amplifies sample noise; dead-zone + cruise flatten.
            float sampleDt = Mathf.Max(Time.deltaTime, 0.001f);
            float speedDelta = hasLastHorizontalSpeed ? (cur - lastHorizontalSpeed) : 0f;
            float rawAccel = hasLastHorizontalSpeed ? speedDelta / sampleDt : 0f;
            lastHorizontalSpeed = cur;
            hasLastHorizontalSpeed = true;

            float speedNoiseFloor = maxSpd * 0.015f;
            if (Mathf.Abs(speedDelta) < speedNoiseFloor)
                rawAccel = 0f;

            bool atCruise = cur >= maxSpd * AtMaxSpeedFraction;
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

            // --- Tick labels: only when max speed / accel scale changes ---
            float skew = Mathf.Max(maxFwd, maxBrake, 0.01f);
            if (!Mathf.Approximately(lastTickMaxSpeed, maxSpd))
            {
                lastTickMaxSpeed = maxSpd;
                for (int i = 0; i < speedTickLabels.Length; i++)
                {
                    float t = speedTickLabels.Length <= 1 ? 0f : (float)i / (speedTickLabels.Length - 1);
                    float tickSpd = t * maxSpd;
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
            float displayCur = Mathf.Min(cur, maxSpd);
            if (atCruise)
                displayCur = maxSpd;

            // [TITAN-ORBIT] Brief territory tag so the raised max reads as a boost, not a chassis change.
            // ASCII "x" instead of "×" — wide unicode glyphs look crushed under <mspace>.
            string territoryTag = territoryMult > 1.001f
                ? $" <color=#AAEEDD>x{FormatFixed1(territoryMult, 4)}t</color>"
                : string.Empty;

            // --- Compact body lines (no wrap; must fit panel width) ---
            string spdLine;
            if (atCruise)
            {
                spdLine =
                    $"SPD {FormatFixed1(displayCur)}/{FormatFixed1(maxSpd)}  <color=#AAAAAA>max</color>{territoryTag}";
            }
            else
            {
                float remaining = Mathf.Max(0f, maxSpd - cur);
                float tMax = remaining / maxFwd;
                tMax = Mathf.Clamp(tMax, 0f, 99.9f);
                spdLine =
                    $"SPD {FormatFixed1(displayCur)}/{FormatFixed1(maxSpd)}  <color=#AAEEDD>{FormatFixed1(tMax, 4)}s</color> to max{territoryTag}";
            }

            string stopPart = cur > 0.35f
                ? $"  stop {FormatFixed1(cur / maxBrake, 4)}s"
                : "  stop —.−s";

            string line2 =
                $"ACC {FormatFixedSigned1(smoothedHorizontalAccel)}/{FormatFixed1(maxFwd)}  brk {FormatFixed1(maxBrake)}  MASS {FormatFixed1(mass)}";

            GetRamDamageEstimate(
                ship,
                motor,
                effectiveStats,
                cur,
                out float ramAst,
                out float ramSelf,
                out float ramRating,
                out float ramMass,
                out float massFactor);

            string line3 =
                $"RAM {FormatFixed1(ramRating, 4)} x m{FormatFixed1(massFactor, 4)}  ast {FormatFixed1(ramAst)}  hull {FormatFixed1(ramSelf)}  <color=#888888>m {FormatFixed1(ramMass)}</color>";

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

            // --- Drop transient sample state so re-enable does not flash a stale accel spike ---
            hasLastHorizontalSpeed = false;
            accelSampleShip = Entity.Null;
            smoothedHorizontalAccel = 0f;
            nextTextRebuildTime = 0f;
        }
    }
}
