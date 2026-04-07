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
    /// Speed and mass HUD: horizontal speed bar, bidirectional accelerometer (green accel / red decel), and time-to-max-speed estimate (uses thrust/mass, so it changes with gem load).
    /// </summary>
    public class ShipSpeedometerHUD : MonoBehaviour
    {
        [Header("Enable")]
        [Tooltip("When off, no UI is created and nothing is shown. Toggle in the inspector without removing the component.")]
        [SerializeField] private bool speedometerEnabled = true;

        [Header("Layout")]
        [SerializeField] private SpeedometerPlacement placement = SpeedometerPlacement.BottomLeft;
        [SerializeField] private float panelWidth = 340f;
        [SerializeField] private float panelHeight = 100f;
        [Tooltip("How quickly the accelerometer value catches up to measured acceleration (lower = smoother bar and ACC text).")]
        [SerializeField, FormerlySerializedAs("accelerationDisplayResponsiveness")] private float accelerationBarSmoothing = 5f;
        [Tooltip("Inset from the left or right screen edge, depending on placement.")]
        [SerializeField, FormerlySerializedAs("rightMargin")] private float horizontalMargin = 20f;
        [Tooltip("Inset from the bottom or top screen edge, depending on placement.")]
        [SerializeField, FormerlySerializedAs("bottomMargin")] private float verticalMargin = 20f;

        [Header("Colors")]
        [SerializeField] private Color backgroundColor = new Color(0f, 0f, 0f, 0.45f);
        [SerializeField] private Color fillColor = new Color(0.35f, 0.85f, 1f, 0.9f);
        [SerializeField] private Color trackColor = new Color(0.15f, 0.15f, 0.18f, 0.85f);
        [SerializeField] private Color textColor = new Color(0.92f, 0.95f, 1f, 1f);
        [SerializeField] private Color accelPositiveColor = new Color(0.25f, 0.92f, 0.45f, 0.92f);
        [SerializeField] private Color accelNegativeColor = new Color(0.95f, 0.28f, 0.28f, 0.92f);

        private GameObject rootPanel;
        private Slider speedSlider;
        private RectTransform accelGreenFill;
        private RectTransform accelRedFill;
        private TextMeshProUGUI speedLabel;
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

            const float pad = 8f;
            const float labelNormH = 0.34f;
            const float speedNormTop = 1f;
            const float speedNormBottom = 0.62f;
            const float accelNormTop = 0.56f;
            const float accelNormBottom = 0.38f;

            GameObject sliderGo = new GameObject("SpeedBar");
            sliderGo.transform.SetParent(rootPanel.transform, false);
            RectTransform sliderRect = sliderGo.AddComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, speedNormBottom);
            sliderRect.anchorMax = new Vector2(1f, speedNormTop);
            sliderRect.offsetMin = new Vector2(pad, 2f);
            sliderRect.offsetMax = new Vector2(-pad, -4f);

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

            GameObject accelRoot = new GameObject("AccelBar");
            accelRoot.transform.SetParent(rootPanel.transform, false);
            RectTransform accelRootRect = accelRoot.AddComponent<RectTransform>();
            accelRootRect.anchorMin = new Vector2(0f, accelNormBottom);
            accelRootRect.anchorMax = new Vector2(1f, accelNormTop);
            accelRootRect.offsetMin = new Vector2(pad, 0f);
            accelRootRect.offsetMax = new Vector2(-pad, 0f);

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
            cl.sizeDelta = new Vector2(1.5f, 0f);
            Image cli = centerLine.AddComponent<Image>();
            cli.color = new Color(1f, 1f, 1f, 0.28f);
            cli.raycastTarget = false;

            GameObject labelGo = new GameObject("HudText");
            labelGo.transform.SetParent(rootPanel.transform, false);
            RectTransform lr = labelGo.AddComponent<RectTransform>();
            lr.anchorMin = new Vector2(0f, 0f);
            lr.anchorMax = new Vector2(1f, labelNormH);
            lr.offsetMin = new Vector2(10f, 4f);
            lr.offsetMax = new Vector2(-10f, -2f);
            speedLabel = labelGo.AddComponent<TextMeshProUGUI>();
            speedLabel.text = "—";
            speedLabel.fontSize = 13f;
            speedLabel.lineSpacing = -2f;
            speedLabel.richText = true;
            if (TMP_Settings.defaultFontAsset != null) speedLabel.font = TMP_Settings.defaultFontAsset;
            speedLabel.color = textColor;
            bool alignLeft = placement == SpeedometerPlacement.BottomLeft || placement == SpeedometerPlacement.TopLeft;
            speedLabel.alignment = alignLeft ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.TopRight;

            uiBuilt = true;
        }

        private void ApplyPlacement(RectTransform rootRect)
        {
            float h = horizontalMargin;
            float v = verticalMargin;
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
            if (rootPanel == null || speedSlider == null || speedLabel == null || accelGreenFill == null || accelRedFill == null)
                return;

            Starship ship = GetPlayerShip();
            bool show = ship != null && ship.IsSpawned && !ship.IsDead && ship.ShipTeam != TeamManager.Team.None;
            if (HUDController.ShipUpgradeTreeObscuresHud)
                show = false;
            rootPanel.SetActive(show);
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

            string accSign = smoothedHorizontalAccel >= 0f ? "+" : "";
            string line2 = $"ACC {accSign}{smoothedHorizontalAccel:0.0}/{maxFwd:0.0}  ·  brake {maxBrake:0.0}  ·  MASS {mass:0.0}";

            speedLabel.text = spdLine + stopPart + "\n" + line2;
        }
    }
}
