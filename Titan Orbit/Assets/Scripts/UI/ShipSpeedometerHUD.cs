using UnityEngine;
using Unity.Netcode;
using UnityEngine.Serialization;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    public enum SpeedometerPlacement
    {
        [Tooltip("Clear of bottom-right minimap; pair with attribute bar on wide layouts.")]
        BottomLeft = 0,
        BottomRight = 1,
        TopLeft = 2,
        TopRight = 3
    }

    /// <summary>
    /// Speed and mass HUD: speed/accel bars with tick labels, time-to-max-speed, asteroid ram estimate (speed × mass, head-on), and primary weapon damage/DPS.
    /// </summary>
    public class ShipSpeedometerHUD : MonoBehaviour
    {
        private const float HudLayoutScale = 1.6f;

        [Header("Enable")]
        [Tooltip("When off, no UI is created and nothing is shown. Toggle in the inspector without removing the component.")]
        [SerializeField] private bool speedometerEnabled = true;

        [Header("Layout")]
        [SerializeField] private SpeedometerPlacement placement = SpeedometerPlacement.BottomLeft;
        [SerializeField] private float panelWidth = 380f * HudLayoutScale;
        [SerializeField] private float panelHeight = 138f * HudLayoutScale;
        [Tooltip("How quickly the accelerometer value catches up to measured acceleration (lower = smoother bar and ACC text).")]
        [SerializeField, FormerlySerializedAs("accelerationDisplayResponsiveness")] private float accelerationBarSmoothing = 5f;
        [Tooltip("Inset from the left or right screen edge, depending on placement.")]
        [SerializeField, FormerlySerializedAs("rightMargin")] private float horizontalMargin = 20f;
        [Tooltip("Inset from the bottom or top screen edge, depending on placement.")]
        [SerializeField, FormerlySerializedAs("bottomMargin")] private float verticalMargin = 20f;
        [Tooltip("When placement is Bottom Left, extra space above the bottom ship upgrade strip before this panel.")]
        [SerializeField] private float stackGapAboveUpgradeBar = 8f;

        [Header("Colors")]
        [SerializeField] private Color backgroundColor = new Color(0f, 0f, 0f, 0.45f);
        [SerializeField] private Color fillColor = new Color(0.35f, 0.85f, 1f, 0.9f);
        [SerializeField] private Color trackColor = new Color(0.15f, 0.15f, 0.18f, 0.85f);
        [SerializeField] private Color textColor = new Color(0.92f, 0.95f, 1f, 1f);
        [SerializeField] private Color accelPositiveColor = new Color(0.25f, 0.92f, 0.45f, 0.92f);
        [SerializeField] private Color accelNegativeColor = new Color(0.95f, 0.28f, 0.28f, 0.92f);
        [SerializeField] private Color tickLabelColor = new Color(0.78f, 0.82f, 0.9f, 0.72f);

        private GameObject rootPanel;
        private Slider speedSlider;
        private RectTransform accelGreenFill;
        private RectTransform accelRedFill;
        private TextMeshProUGUI speedLabel;
        private TextMeshProUGUI[] speedTickLabels;
        private TextMeshProUGUI[] accelTickLabels;
        private Starship playerShip;
        private Starship accelSampleShip;
        private float lastHorizontalSpeed;
        private float smoothedHorizontalAccel;
        private bool hasLastHorizontalSpeed;
        private bool uiBuilt;

        private void Start()
        {
            if (speedometerEnabled)
                BuildUIIfNeeded();
        }

        private void OnDisable()
        {
            if (rootPanel != null)
                rootPanel.SetActive(false);
        }

        private void OnEnable()
        {
            if (speedometerEnabled && uiBuilt && rootPanel != null)
                rootPanel.SetActive(true);
        }

        private void BuildUIIfNeeded()
        {
            if (uiBuilt || !speedometerEnabled) return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

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
            if (TMP_Settings.defaultFontAsset != null) speedLabel.font = TMP_Settings.defaultFontAsset;
            speedLabel.color = textColor;
            bool alignLeft = placement == SpeedometerPlacement.BottomLeft || placement == SpeedometerPlacement.TopLeft;
            speedLabel.alignment = alignLeft ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.TopRight;

            uiBuilt = true;
        }

        private TextMeshProUGUI[] CreateTickLabelRow(Transform parent, int count, float fontSize)
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
                if (TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
                tmp.color = tickLabelColor;
                tmp.raycastTarget = false;
                labels[i] = tmp;
            }
            return labels;
        }

        private static string FormatHudNumber(float v, bool preferInteger)
        {
            if (preferInteger && v >= 10f) return v.ToString("0");
            if (preferInteger && v < 10f && Mathf.Abs(v - Mathf.Round(v)) < 0.05f) return Mathf.Round(v).ToString("0");
            return v.ToString("0.#");
        }

        /// <summary>
        /// Same as <see cref="FormatHudNumber"/> but always prefixes '+' or '-' so label width stays stable (no missing sign for 0 / small values).
        /// </summary>
        private static string FormatHudSignedNumber(float v, bool preferInteger)
        {
            if (v < 0f)
                return "-" + FormatHudNumber(-v, preferInteger);
            return "+" + FormatHudNumber(v, preferInteger);
        }

        private float GetBottomLeftStackYBoost()
        {
            if (placement != SpeedometerPlacement.BottomLeft) return 0f;
            var upgradeBar = Object.FindFirstObjectByType<ShipAttributeUpgradeHUD>();
            if (upgradeBar == null) return 0f;
            return upgradeBar.GetUpgradeBarCanvasHeight() + stackGapAboveUpgradeBar;
        }

        private void ApplyPlacement(RectTransform rootRect)
        {
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

        private Starship GetPlayerShip()
        {
            if (playerShip != null && playerShip.IsSpawned && !playerShip.IsDead)
                return playerShip;
            playerShip = null;

            if (NetworkManager.Singleton != null && NetworkManager.Singleton.SpawnManager != null)
            {
                NetworkObject local = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
                if (local != null)
                {
                    var s = local.GetComponent<Starship>();
                    if (s != null && !s.IsDead)
                    {
                        playerShip = s;
                        return playerShip;
                    }
                }
            }

            foreach (var s in Object.FindObjectsByType<Starship>(FindObjectsSortMode.None))
            {
                if (s != null && s.IsOwner && !s.IsDead)
                {
                    playerShip = s;
                    break;
                }
            }
            return playerShip;
        }

        private void LateUpdate()
        {
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

            Starship ship = GetPlayerShip();
            bool show = ship != null && ship.IsSpawned && !ship.IsDead && ship.ShipTeam != TeamManager.Team.None;
            if (HUDController.ShipUpgradeTreeObscuresHud)
                show = false;
            rootPanel.SetActive(show);
            if (placement == SpeedometerPlacement.BottomLeft)
                ApplyPlacement(rootPanel.GetComponent<RectTransform>());

            if (!show || ship == null)
            {
                hasLastHorizontalSpeed = false;
                accelSampleShip = null;
                smoothedHorizontalAccel = 0f;
                return;
            }

            if (accelSampleShip != ship)
            {
                accelSampleShip = ship;
                hasLastHorizontalSpeed = false;
                smoothedHorizontalAccel = 0f;
            }

            float cur = ship.CurrentHorizontalSpeed;
            float maxSpd = Mathf.Max(0.01f, ship.MaxMoveSpeed);
            speedSlider.value = Mathf.Clamp01(cur / maxSpd);

            float maxFwd = Mathf.Max(0.01f, ship.MaxHorizontalAcceleration);
            float maxBrake = Mathf.Max(0.01f, ship.MaxBrakingDeceleration);
            float mass = ship.CurrentMass;

            float dt = Mathf.Max(Time.deltaTime, 1e-5f);
            float rawAccel = hasLastHorizontalSpeed ? (cur - lastHorizontalSpeed) / dt : 0f;
            lastHorizontalSpeed = cur;
            hasLastHorizontalSpeed = true;
            float k = Mathf.Clamp01(dt * accelerationBarSmoothing);
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

            ship.GetHudAsteroidRamDamageEstimate(cur, out float ramAst, out float ramSelf);
            float ramRating = ship.GetHudRamDamageRating();
            float ramMass = ship.GetHudRamEffectiveMass();
            float massFactor = ship.GetHudRamMassDamageFactor();
            string line3 = $"RAM {ramRating:0.##}×m{massFactor:0.##} →ast {ramAst:0.#}  ·  hull {ramSelf:0.#}  <color=#888888>(mass {ramMass:0.#})</color>";

            string line4;
            if (ship.TryGetHudPrimaryBulletStats(out float dmgPerHit, out float sps, out float dps))
                line4 = $"BUL {dmgPerHit:0.#}/hit  ·  {dps:0.#}/s  <color=#888888>({sps:0.#}/s)</color>";
            else
                line4 = "BUL —";

            speedLabel.text = spdLine + stopPart + "\n" + line2 + "\n" + line3 + "\n" + line4;
        }
    }
}
