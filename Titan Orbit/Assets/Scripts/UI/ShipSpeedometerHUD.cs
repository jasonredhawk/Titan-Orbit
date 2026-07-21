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
    /// Presentation-only — never writes ECS. Numbers use fixed-width formatting so TMP does not
    /// reflow when signs / decimals flicker (that "blinking" layout shift).
    /// </para>
    /// Hidden during team select, death, and when the upgrade tree obscures HUD.
    /// </summary>
    public class ShipSpeedometerHUD : MonoBehaviour
    {
        const float HudLayoutScale = 1.6f;
        const float AsteroidCollisionNormalSpeedRetention = 0.93f;

        /// <summary>Treat as "at max" when within this fraction of motor MaxSpeed (cruise lock / float noise).</summary>
        const float AtMaxSpeedFraction = 0.985f;

        /// <summary>Figure space — same advance as a digit in most fonts; pads without visible gaps jumping.</summary>
        const char FigureSpace = '\u2007';

        [Header("Enable")]
        [SerializeField] bool speedometerEnabled = true;

        [Header("Layout")]
        [SerializeField] SpeedometerPlacement placement = SpeedometerPlacement.BottomLeft;
        [SerializeField] float panelWidth = 380f * HudLayoutScale;
        [SerializeField] float panelHeight = 148f * HudLayoutScale;
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

        void Start()
        {
            if (speedometerEnabled)
                BuildUIIfNeeded();
        }

        void OnDisable()
        {
            if (rootPanel != null)
                rootPanel.SetActive(false);
        }

        void OnEnable()
        {
            if (speedometerEnabled && uiBuilt && rootPanel != null)
                rootPanel.SetActive(true);
        }

        void BuildUIIfNeeded()
        {
            // --- One-time procedural HUD build ---
            if (uiBuilt || !speedometerEnabled)
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

            float pad = 8f * HudLayoutScale;
            const float labelNormH = 0.46f;
            const float speedNormTop = 1f;
            const float speedNormBottom = 0.74f;
            const float speedTickNormBottom = 0.645f;
            const float speedTickNormTop = 0.72f;
            const float accelNormTop = 0.61f;
            const float accelNormBottom = 0.465f;
            const float accelTickNormBottom = 0.38f;
            const float accelTickNormTop = 0.445f;

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
            speedTickLabels = CreateTickLabelRow(speedTickStrip.transform, 5, 9f * HudLayoutScale);

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
            accelTickLabels = CreateTickLabelRow(accelTickStrip.transform, 5, 9f * HudLayoutScale);

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
            lr.anchorMax = new Vector2(1f, labelNormH);
            lr.offsetMin = new Vector2(10f * HudLayoutScale, 4f * HudLayoutScale);
            lr.offsetMax = new Vector2(-10f * HudLayoutScale, -2f * HudLayoutScale);
            speedLabel = labelGo.AddComponent<TextMeshProUGUI>();
            speedLabel.text = "—";
            speedLabel.fontSize = 12f * HudLayoutScale;
            speedLabel.lineSpacing = -2f * HudLayoutScale;
            speedLabel.richText = true;
            // [UNITY] Monospace keeps SPD/ACC columns from shifting when digits change.
            if (TMP_Settings.defaultFontAsset != null)
                speedLabel.font = TMP_Settings.defaultFontAsset;
            speedLabel.color = textColor;
            bool alignLeft = placement == SpeedometerPlacement.BottomLeft || placement == SpeedometerPlacement.TopLeft;
            speedLabel.alignment = alignLeft ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.TopRight;

            uiBuilt = true;
        }

        TextMeshProUGUI[] CreateTickLabelRow(Transform parent, int count, float fontSize)
        {
            var labels = new TextMeshProUGUI[count];
            for (int i = 0; i < count; i++)
            {
                GameObject go = new GameObject($"Tick{i}");
                go.transform.SetParent(parent, false);
                RectTransform rt = go.AddComponent<RectTransform>();
                float x = count <= 1 ? 0.5f : (float)i / (count - 1);
                rt.anchorMin = new Vector2(x, 0f);
                rt.anchorMax = new Vector2(x, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(52f * HudLayoutScale, 0f);
                rt.anchoredPosition = Vector2.zero;
                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.text = "—";
                tmp.fontSize = fontSize;
                tmp.enableAutoSizing = false;
                tmp.richText = false;
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
            var upgradeBar = Object.FindFirstObjectByType<ShipAttributeUpgradeHUD>();
            if (upgradeBar == null)
                return 0f;
            return upgradeBar.GetUpgradeStripReserveHeight() + stackGapAboveUpgradeBar;
        }

        void ApplyPlacement(RectTransform rootRect)
        {
            // --- Corner anchor + margin (stack above upgrade bar when bottom-left) ---
            float h = horizontalMargin;
            float v = verticalMargin + GetBottomLeftStackYBoost();
            switch (placement)
            {
                case SpeedometerPlacement.BottomLeft:
                    rootRect.anchorMin = new Vector2(0f, 0f);
                    rootRect.anchorMax = new Vector2(0f, 0f);
                    rootRect.pivot = new Vector2(0f, 0f);
                    rootRect.anchoredPosition = new Vector2(h, v);
                    break;
                case SpeedometerPlacement.BottomRight:
                    rootRect.anchorMin = new Vector2(1f, 0f);
                    rootRect.anchorMax = new Vector2(1f, 0f);
                    rootRect.pivot = new Vector2(1f, 0f);
                    rootRect.anchoredPosition = new Vector2(-h, v);
                    break;
                case SpeedometerPlacement.TopLeft:
                    rootRect.anchorMin = new Vector2(0f, 1f);
                    rootRect.anchorMax = new Vector2(0f, 1f);
                    rootRect.pivot = new Vector2(0f, 1f);
                    rootRect.anchoredPosition = new Vector2(h, -v);
                    break;
                case SpeedometerPlacement.TopRight:
                    rootRect.anchorMin = new Vector2(1f, 1f);
                    rootRect.anchorMax = new Vector2(1f, 1f);
                    rootRect.pivot = new Vector2(1f, 1f);
                    rootRect.anchoredPosition = new Vector2(-h, -v);
                    break;
            }
        }

        /// <summary>
        /// Gathers local ship ECS data for HUD display. Recomputes effective stats the same way
        /// ShipStatApplyLogic does (chassis sum + level curve + attribute multipliers) for ram rating
        /// and as a sanity fallback if motor MaxSpeed still looks like bake defaults.
        /// </summary>
        static bool TryGetLocalShipHudData(
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
            // [TITAN-ORBIT] TryGetLocalShipEntity scans all ships and is gated off during Instantiates.
            // LocalPlayerShipTag is a singleton-style tag — CalculateEntityCount/GetSingletonEntity only.
            using (var tagged = em.CreateEntityQuery(
                       typeof(LocalPlayerShipTag),
                       typeof(ShipState),
                       typeof(ShipMotorConfig),
                       typeof(ShipKinematics)))
            {
                if (tagged.CalculateEntityCount() == 1)
                {
                    shipEntity = tagged.GetSingletonEntity();
                    return FillHudDataFromEntity(
                        em, shipEntity, out ship, out motor, out kinematics, out weapon, out effectiveStats);
                }
            }

            // --- Broader resolve (skipped during Settling / GhostSpawnBacklog — Crash!!! risk) ---
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out shipEntity))
                return false;

            return FillHudDataFromEntity(
                em, shipEntity, out ship, out motor, out kinematics, out weapon, out effectiveStats);
        }

        /// <summary>Reads motor/kinematics/weapon/stats from a resolved ship entity.</summary>
        static bool FillHudDataFromEntity(
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

            string chassisId = null;
            if (em.HasComponent<ShipChassisState>(shipEntity))
                chassisId = em.GetComponentData<ShipChassisState>(shipEntity).ChassisId.ToString();

            if (string.IsNullOrEmpty(chassisId))
                ShipStatApplyLogic.TryResolveChassisId(ship.Team, ship.ShipLevel, branchIndex, out chassisId);

            if (!string.IsNullOrEmpty(chassisId) &&
                ShipStatApplyLogic.TryGetBaseStatsForChassis(chassisId, ship.ShipLevel, out ShipComponentAbilityStats summed))
            {
                effectiveStats = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(summed, ship.ShipLevel);
                if (em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
                {
                    var attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);
                    ShipAttributeUpgradeLogic.ApplyMultipliers(ref effectiveStats, attrs);
                }
            }

            return true;
        }

        /// <summary>
        /// Display MaxSpeed: prefer live motor (client ShipStatApply), fall back to chassis moveSpeed
        /// if motor still looks like StarshipGhostAuthoring bake (35) while chassis is ~13.
        /// </summary>
        static float ResolveDisplayMaxSpeed(in ShipMotorConfig motor, in ShipComponentAbilityStats effectiveStats)
        {
            float motorMax = Mathf.Max(0.01f, motor.MaxSpeed);
            float chassisMax = effectiveStats.moveSpeed > 0.1f ? effectiveStats.moveSpeed : 0f;

            // [TITAN-ORBIT] Before client apply runs (first frames), bake MaxSpeed=35 would empty the bar.
            if (chassisMax > 0.1f && motorMax > chassisMax * 1.35f)
                return chassisMax;

            return motorMax;
        }

        /// <summary>Planar speed magnitude — top-down game ignores Y velocity.</summary>
        static float GetHorizontalSpeed(in ShipKinematics kinematics)
        {
            float3 vel = kinematics.Velocity;
            vel.y = 0f;
            return math.length(vel);
        }

        /// <summary>
        /// Same movement mass the motor uses (hull bulk + gems) — not hull-reference alone.
        /// </summary>
        static float GetMovementMass(in ShipState ship, in ShipMotorConfig motor)
        {
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            return ShipMassLogic.ComputeMovementMass(
                motor.HullMassReference,
                ship.MaxHealth,
                motor.ChassisReferenceHealth,
                ship.CurrentGems,
                baseMass);
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
                baseMass);

            float familyRammingPower = effectiveStats.rammingPower > 0f
                ? effectiveStats.rammingPower
                : ShipFamilyDefaultFallbackStats.CreateBaseline().rammingPower;
            ramRating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(familyRammingPower);
            massFactor = ShipComponentRammingSuggestions.ComputeMassDamageFactor(ramMass, hullBaseline);
            float selfMassFactor = ShipComponentRammingSuggestions.ComputeSelfMassDamageFactor(ramMass, hullBaseline);

            float deltaNormalSpeed = (1f + AsteroidCollisionNormalSpeedRetention) * Mathf.Max(0f, inboundSpeed);
            float speedFactor = deltaNormalSpeed / ShipComponentRammingSuggestions.ReferenceImpactSpeed;

            asteroidDamage = Mathf.Max(0f, ramRating * massFactor * speedFactor);
            selfDamage = Mathf.Max(
                0f,
                ramRating * selfMassFactor * speedFactor * ShipComponentRammingSuggestions.SelfToAsteroidDamageRatio);

            float selfCap = ship.MaxHealth * ShipComponentRammingSuggestions.MaxSelfImpactDamageFractionOfMaxHealth;
            if (selfCap > 0f)
                selfDamage = Mathf.Min(selfDamage, selfCap);
        }

        void LateUpdate()
        {
            // --- Early out when disabled or UI not built ---
            if (!speedometerEnabled)
            {
                if (rootPanel != null)
                    rootPanel.SetActive(false);
                return;
            }

            if (!uiBuilt)
                BuildUIIfNeeded();
            if (rootPanel == null || speedSlider == null || speedLabel == null || accelGreenFill == null || accelRedFill == null
                || speedTickLabels == null || accelTickLabels == null)
                return;

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
                ApplyPlacement(rootPanel.GetComponent<RectTransform>());

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
            speedSlider.value = Mathf.Clamp01(cur / maxSpd);

            float mass = GetMovementMass(ship, motor);
            // [TITAN-ORBIT] F/m — same a = EngineThrust/mass the motor uses below MaxSpeed.
            float maxFwd = Mathf.Max(0.01f, motor.EngineThrust / Mathf.Max(ShipMassLogic.MinMass, mass));
            if (motorLooksBaked && effectiveStats.accelerationCap > 0.1f)
            {
                maxFwd = Mathf.Max(
                    0.01f,
                    effectiveStats.accelerationCap * ShipPropulsionAggregation.EngineThrustVisibility
                    / Mathf.Max(ShipMassLogic.MinMass, mass));
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
                return;
            nextTextRebuildTime = Time.unscaledTime + 0.1f;

            // Clamp displayed speed for text so we never show 13.6/13.5 from float noise.
            float displayCur = Mathf.Min(cur, maxSpd);
            if (atCruise)
                displayCur = maxSpd;

            string spdLine;
            if (atCruise)
            {
                spdLine = $"SPD {FormatFixed1(displayCur)}/{FormatFixed1(maxSpd)}  ·  <color=#AAAAAA>at max</color>";
            }
            else
            {
                float remaining = Mathf.Max(0f, maxSpd - cur);
                float tMax = remaining / maxFwd;
                tMax = Mathf.Clamp(tMax, 0f, 99.9f);
                spdLine =
                    $"SPD {FormatFixed1(displayCur)}/{FormatFixed1(maxSpd)}  ·  max in <color=#AAEEDD>{FormatFixed1(tMax, 4)}s</color>";
            }

            string stopPart = cur > 0.35f
                ? $"  ·  stop {FormatFixed1(cur / maxBrake, 4)}s"
                : "  ·  stop  —.−s";

            string line2 =
                $"ACC {FormatFixedSigned1(smoothedHorizontalAccel)}/{FormatFixed1(maxFwd)}  ·  brake {FormatFixed1(maxBrake)}  ·  MASS {FormatFixed1(mass)}";

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
                $"RAM {FormatFixed1(ramRating, 4)}×m{FormatFixed1(massFactor, 4)} →ast {FormatFixed1(ramAst)}  ·  hull {FormatFixed1(ramSelf)}  <color=#888888>(m {FormatFixed1(ramMass)})</color>";

            string line4;
            if (weapon.FireRate > 0.01f && weapon.BulletDamage > 0.01f)
            {
                float dps = weapon.BulletDamage * weapon.FireRate;
                line4 =
                    $"BUL {FormatFixed1(weapon.BulletDamage)}/hit  ·  {FormatFixed1(dps)}/s  <color=#888888>({FormatFixed1(weapon.FireRate)}/s)</color>";
            }
            else
                line4 = "BUL  —.−/hit  ·   —.−/s";

            // [UNITY] mspace keeps TMP digit columns stable even with proportional fonts.
            string body =
                "<mspace=0.55em>" + spdLine + stopPart + "\n" + line2 + "\n" + line3 + "\n" + line4 + "</mspace>";
            if (body != lastHudBodyText)
            {
                lastHudBodyText = body;
                speedLabel.text = body;
            }
        }
    }
}
