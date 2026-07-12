using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
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
    /// Local-player speed and mass HUD: reads ShipMotorConfig, ShipKinematics, ShipWeaponConfig, and
    /// effective stats (via ShipStatApplyLogic + attribute upgrades) from the visualization ECS world.
    /// Displays speed/accel bars, time-to-max-speed, ram damage estimates (ShipMassLogic), and weapon DPS.
    /// Presentation-only — mirrors sim numbers for player feedback; does not write ECS components.
    /// Hidden during team select, death, and when upgrade tree obscures HUD.
    /// </summary>
    public class ShipSpeedometerHUD : MonoBehaviour
    {
        const float HudLayoutScale = 1.6f;
        const float AsteroidCollisionNormalSpeedRetention = 0.93f;

        [Header("Enable")]
        [SerializeField] bool speedometerEnabled = true;

        [Header("Layout")]
        [SerializeField] SpeedometerPlacement placement = SpeedometerPlacement.BottomLeft;
        [SerializeField] float panelWidth = 380f * HudLayoutScale;
        [SerializeField] float panelHeight = 138f * HudLayoutScale;
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
            const float labelNormH = 0.44f;
            const float speedNormTop = 1f;
            const float speedNormBottom = 0.73f;
            const float speedTickNormBottom = 0.635f;
            const float speedTickNormTop = 0.71f;
            const float accelNormTop = 0.60f;
            const float accelNormBottom = 0.455f;
            const float accelTickNormBottom = 0.37f;
            const float accelTickNormTop = 0.435f;

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
            speedLabel.lineSpacing = -3f * HudLayoutScale;
            speedLabel.richText = true;
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

        static string FormatHudNumber(float v, bool preferInteger)
        {
            if (preferInteger && v >= 10f)
                return v.ToString("0");
            if (preferInteger && v < 10f && Mathf.Abs(v - Mathf.Round(v)) < 0.05f)
                return Mathf.Round(v).ToString("0");
            return v.ToString("0.#");
        }

        static string FormatHudSignedNumber(float v, bool preferInteger)
        {
            if (v < 0f)
                return "-" + FormatHudNumber(-v, preferInteger);
            return "+" + FormatHudNumber(v, preferInteger);
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
        /// ShipStatApplyLogic does (chassis sum + level curve + attribute multipliers) for ram rating.
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
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out shipEntity))
                return false;

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

        /// <summary>Planar speed magnitude — top-down game ignores Y velocity.</summary>
        static float GetHorizontalSpeed(in ShipKinematics kinematics)
        {
            float3 vel = kinematics.Velocity;
            vel.y = 0f;
            return math.length(vel);
        }

        /// <summary>Physics hull mass from baked motor config (no gem-weight custom mass).</summary>
        static float GetMovementMass(in ShipState ship, in ShipMotorConfig motor)
        {
            if (motor.HullMassReference > 0f)
                return motor.HullMassReference;
            return motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
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
            float maxSpd = Mathf.Max(0.01f, motor.MaxSpeed);
            speedSlider.value = Mathf.Clamp01(cur / maxSpd);

            float mass = GetMovementMass(ship, motor);
            float maxFwd = Mathf.Max(0.01f, motor.EngineThrust / Mathf.Max(0.5f, mass));
            float maxBrake = Mathf.Max(0.01f, motor.BrakeDeceleration > 0f
                ? motor.BrakeDeceleration
                : ShipMassLogic.DefaultBrakeDeceleration);

            float sampleDt = Mathf.Max(Time.deltaTime, 0.001f);
            float rawAccel = hasLastHorizontalSpeed ? (cur - lastHorizontalSpeed) / sampleDt : 0f;
            lastHorizontalSpeed = cur;
            hasLastHorizontalSpeed = true;
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

            bool preferIntSpeed = maxSpd >= 12f;
            for (int i = 0; i < speedTickLabels.Length; i++)
            {
                float t = speedTickLabels.Length <= 1 ? 0f : (float)i / (speedTickLabels.Length - 1);
                float tickSpd = t * maxSpd;
                speedTickLabels[i].text = FormatHudNumber(tickSpd, preferIntSpeed);
                speedTickLabels[i].alignment = i == 0
                    ? TextAlignmentOptions.MidlineLeft
                    : (i == speedTickLabels.Length - 1 ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Midline);
            }

            float skew = Mathf.Max(maxFwd, maxBrake, 0.01f);
            bool preferIntAccel = skew >= 12f;
            for (int i = 0; i < accelTickLabels.Length; i++)
            {
                float t = accelTickLabels.Length <= 1 ? 0.5f : (float)i / (accelTickLabels.Length - 1);
                float v = Mathf.Lerp(-skew, skew, t);
                accelTickLabels[i].text = FormatHudSignedNumber(v, preferIntAccel);
                accelTickLabels[i].alignment = i == 0
                    ? TextAlignmentOptions.MidlineLeft
                    : (i == accelTickLabels.Length - 1 ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Midline);
            }

            string spdLine;
            if (cur >= maxSpd - 0.02f)
                spdLine = $"SPD {cur:0.0}/{maxSpd:0.0}  ·  <color=#AAAAAA>at max spd</color>";
            else
            {
                float tMax = (maxSpd - cur) / maxFwd;
                tMax = Mathf.Clamp(tMax, 0f, 999f);
                spdLine = $"SPD {cur:0.0}/{maxSpd:0.0}  ·  max spd in <color=#AAEEDD>{tMax:0.0}s</color>";
            }

            string stopPart = cur > 0.35f
                ? $"  ·  stop in {cur / maxBrake:0.0}s"
                : string.Empty;

            char accSign = smoothedHorizontalAccel < 0f ? '-' : '+';
            string line2 = $"ACC {accSign}{Mathf.Abs(smoothedHorizontalAccel):0.0}/{maxFwd:0.0}  ·  brake {maxBrake:0.0}  ·  MASS {mass:0.0}";

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

            // --- Compose multi-line HUD text ---
            string line3 = $"RAM {ramRating:0.##}×m{massFactor:0.##} →ast {ramAst:0.#}  ·  hull {ramSelf:0.#}  <color=#888888>(mass {ramMass:0.#})</color>";

            string line4;
            if (weapon.FireRate > 0.01f && weapon.BulletDamage > 0.01f)
            {
                float dps = weapon.BulletDamage * weapon.FireRate;
                line4 = $"BUL {weapon.BulletDamage:0.#}/hit  ·  {dps:0.#}/s  <color=#888888>({weapon.FireRate:0.#}/s)</color>";
            }
            else
                line4 = "BUL —";

            speedLabel.text = spdLine + stopPart + "\n" + line2 + "\n" + line3 + "\n" + line4;
        }
    }
}
