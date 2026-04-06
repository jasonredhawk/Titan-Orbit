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
    /// Speed readout: current horizontal speed vs effective max. Default placement is bottom-left so it does not cover the minimap.
    /// </summary>
    public class ShipSpeedometerHUD : MonoBehaviour
    {
        [Header("Enable")]
        [Tooltip("When off, no UI is created and nothing is shown. Toggle in the inspector without removing the component.")]
        [SerializeField] private bool speedometerEnabled = true;

        [Header("Layout")]
        [SerializeField] private SpeedometerPlacement placement = SpeedometerPlacement.BottomLeft;
        [SerializeField] private float panelWidth = 240f;
        [SerializeField] private float panelHeight = 40f;
        [Tooltip("Inset from the left or right screen edge, depending on placement.")]
        [SerializeField, FormerlySerializedAs("rightMargin")] private float horizontalMargin = 20f;
        [Tooltip("Inset from the bottom or top screen edge, depending on placement.")]
        [SerializeField, FormerlySerializedAs("bottomMargin")] private float verticalMargin = 20f;

        [Header("Colors")]
        [SerializeField] private Color backgroundColor = new Color(0f, 0f, 0f, 0.45f);
        [SerializeField] private Color fillColor = new Color(0.35f, 0.85f, 1f, 0.9f);
        [SerializeField] private Color trackColor = new Color(0.15f, 0.15f, 0.18f, 0.85f);
        [SerializeField] private Color textColor = new Color(0.92f, 0.95f, 1f, 1f);

        private GameObject rootPanel;
        private Slider speedSlider;
        private TextMeshProUGUI speedLabel;
        private Starship playerShip;
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

            GameObject sliderGo = new GameObject("SpeedBar");
            sliderGo.transform.SetParent(rootPanel.transform, false);
            RectTransform sliderRect = sliderGo.AddComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, 0.5f);
            sliderRect.anchorMax = new Vector2(1f, 1f);
            sliderRect.offsetMin = new Vector2(8f, 4f);
            sliderRect.offsetMax = new Vector2(-8f, -4f);

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

            GameObject labelGo = new GameObject("SpeedText");
            labelGo.transform.SetParent(rootPanel.transform, false);
            RectTransform lr = labelGo.AddComponent<RectTransform>();
            lr.anchorMin = new Vector2(0f, 0f);
            lr.anchorMax = new Vector2(1f, 0.5f);
            lr.offsetMin = new Vector2(10f, 2f);
            lr.offsetMax = new Vector2(-10f, -2f);
            speedLabel = labelGo.AddComponent<TextMeshProUGUI>();
            speedLabel.text = "— / —";
            speedLabel.fontSize = 16f;
            if (TMP_Settings.defaultFontAsset != null) speedLabel.font = TMP_Settings.defaultFontAsset;
            speedLabel.color = textColor;
            bool alignLeft = placement == SpeedometerPlacement.BottomLeft || placement == SpeedometerPlacement.TopLeft;
            speedLabel.alignment = alignLeft ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.MidlineRight;

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
            if (rootPanel == null || speedSlider == null || speedLabel == null)
                return;

            Starship ship = GetPlayerShip();
            bool show = ship != null && ship.IsSpawned && !ship.IsDead && ship.ShipTeam != TeamManager.Team.None;
            if (HUDController.ShipUpgradeTreeObscuresHud)
                show = false;
            rootPanel.SetActive(show);
            if (!show || ship == null)
                return;

            float cur = ship.CurrentHorizontalSpeed;
            float max = Mathf.Max(0.01f, ship.MaxMoveSpeed);
            speedSlider.value = Mathf.Clamp01(cur / max);
            float mass = ship.CurrentMass;
            speedLabel.text = $"SPD {cur:0.0}/{max:0.0}   MASS {mass:0.0}";
        }
    }
}
