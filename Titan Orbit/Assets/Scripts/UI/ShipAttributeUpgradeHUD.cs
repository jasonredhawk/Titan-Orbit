using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Ship Upgrade Menu at bottom of screen. 10 abilities bound to keys 1-9 and 0.
    /// Each upgrade costs ShipLevel * 5 gems. Max upgrades per ability = ShipLevel.
    /// </summary>
    public class ShipAttributeUpgradeHUD : MonoBehaviour
    {
        [Header("Enable")]
        [Tooltip("Uncheck to disable this HUD (e.g. if it causes crashes).")]
        [SerializeField] private bool upgradeBarEnabled = true;

        [Header("Layout")]
        [SerializeField] private float barHeight = 68f;
        [SerializeField] private float buttonWidth = 136f;
        [SerializeField] private float buttonSpacing = 10f;
        [SerializeField] private float bottomPadding = 8f;
        [Header("Mobile / touch")]
        [Tooltip("Multiplies bar height, button width, fonts, ticks, and padding on phones/tablets so the bottom upgrade strip is easier to read and tap.")]
        [SerializeField] private float mobileHudScale = 1.48f;
        [Tooltip("Vertical position of the ability title (negative = down from top edge). Increase to move title up.")]
        [SerializeField] private float titleFromTop = -7f;
        [Tooltip("Vertical offset of the upgrade tick squares (center anchor). Increase to move ticks up.")]
        [SerializeField] private float ticksCenterYOffset = -3f;

        [Header("Visual Styling")]
        [SerializeField] private Color buttonFrameColor = new Color(0.95f, 0.98f, 1f, 0.42f);
        [SerializeField] private Color buttonInnerShadeColor = new Color(0f, 0f, 0f, 0.22f);
        [SerializeField] private Color buttonAccentColor = new Color(0.75f, 0.88f, 1f, 0.28f);
        [SerializeField] private Color buttonShadowColor = new Color(0f, 0f, 0f, 0.45f);

        [Header("Category Colors")]
        [SerializeField] private Color weaponColor = ShipAbilityCategoryColors.WeaponForHud;
        [SerializeField] private Color healthColor = ShipAbilityCategoryColors.HealthForHud;
        [SerializeField] private Color energyColor = ShipAbilityCategoryColors.EnergyForHud;
        [SerializeField] private Color shipColor = ShipAbilityCategoryColors.ShipForHud;
        [SerializeField] private Color cargoColor = ShipAbilityCategoryColors.CargoForHud;

        [Header("Cost icon (assign in Inspector)")]
        [Tooltip("Shown next to the gem cost number on each upgrade slot. Leave empty until you have a sprite.")]
        [SerializeField] private Sprite gemCostIconSprite;
        [SerializeField] private float gemIconSize = 14f;

        private static readonly string[] Titles = new[]
        {
            "Fire Power", "Bullet Speed",
            "Max Health", "Health Regen",
            "Energy Cap", "Energy Regen",
            "Move Speed", "Turn Speed",
            "Max Gems", "Max People"
        };

        private static readonly int[] CategoryIndices = new[] { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4 };

        private Starship playerShip;
        private float lastShipLookupTime = -999f;
        private const float ShipLookupInterval = 0.3f;

        private GameObject rootPanel;
        private Button[] buttons = new Button[10];
        private TextMeshProUGUI[] titleTexts = new TextMeshProUGUI[10];
        private GameObject[] tickContainers = new GameObject[10];
        private Image[] buttonImages = new Image[10];
        private TextMeshProUGUI[] keyLabels = new TextMeshProUGUI[10];
        private TextMeshProUGUI[] costLabels = new TextMeshProUGUI[10];
        private Image[] costGemIcons = new Image[10];

        /// <summary>1 on desktop; <see cref="mobileHudScale"/> on mobile, clamped for safety.</summary>
        private float _layoutScale = 1f;

        private float S(float v) => v * _layoutScale;

        private void Start()
        {
            if (!upgradeBarEnabled) return;
            BuildUI();
        }

        private void BuildUI()
        {
            Canvas canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            _layoutScale = Application.isMobilePlatform ? Mathf.Clamp(mobileHudScale, 1f, 2.25f) : 1f;

            rootPanel = new GameObject("ShipAttributeUpgradeBar");
            rootPanel.transform.SetParent(canvas.transform, false);

            RectTransform rootRect = rootPanel.AddComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0f);
            rootRect.anchorMax = new Vector2(0.5f, 0f);
            rootRect.pivot = new Vector2(0.5f, 0f);
            rootRect.anchoredPosition = new Vector2(0f, S(bottomPadding));
            float bw = S(buttonWidth);
            float sp = S(buttonSpacing);
            float bh = S(barHeight);
            float totalWidth = 10 * bw + 9 * sp;
            rootRect.sizeDelta = new Vector2(totalWidth, bh);

            Image bgImage = rootPanel.AddComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0f);
            bgImage.raycastTarget = false;

            Color[] categoryColors = { weaponColor, healthColor, energyColor, shipColor, cargoColor };
            string[] keyStrings = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

            for (int i = 0; i < 10; i++)
            {
                float x = -totalWidth / 2f + bw / 2f + i * (bw + sp);
                var btn = CreateUpgradeButton(rootPanel.transform, x, i, categoryColors[CategoryIndices[i]], keyStrings[i], bw, bh);
                buttons[i] = btn.button;
                titleTexts[i] = btn.titleText;
                tickContainers[i] = btn.tickContainer;
                buttonImages[i] = btn.bgImage;
                keyLabels[i] = btn.keyLabel;
                costLabels[i] = btn.costLabel;
                costGemIcons[i] = btn.costGemIcon;
            }
        }

        private (Button button, TextMeshProUGUI titleText, GameObject tickContainer, Image bgImage, TextMeshProUGUI keyLabel, TextMeshProUGUI costLabel, Image costGemIcon) CreateUpgradeButton(Transform parent, float x, int index, Color categoryColor, string keyStr, float scaledButtonWidth, float scaledBarHeight)
        {
            GameObject btnObj = new GameObject($"UpgradeBtn_{index}");
            btnObj.transform.SetParent(parent, false);

            RectTransform btnRect = btnObj.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0.5f);
            btnRect.anchorMax = new Vector2(0.5f, 0.5f);
            btnRect.pivot = new Vector2(0.5f, 0.5f);
            btnRect.anchoredPosition = new Vector2(x, 0f);
            btnRect.sizeDelta = new Vector2(scaledButtonWidth, scaledBarHeight - S(6f));

            Image bgImage = btnObj.AddComponent<Image>();
            bgImage.color = categoryColor;
            bgImage.raycastTarget = true;
            var buttonOutline = btnObj.AddComponent<Outline>();
            buttonOutline.effectColor = buttonFrameColor;
            buttonOutline.effectDistance = new Vector2(S(1f), S(1f));
            var buttonShadow = btnObj.AddComponent<Shadow>();
            buttonShadow.effectColor = buttonShadowColor;
            buttonShadow.effectDistance = new Vector2(0f, S(-2f));

            GameObject innerShade = new GameObject("InnerShade");
            innerShade.transform.SetParent(btnObj.transform, false);
            RectTransform shadeRect = innerShade.AddComponent<RectTransform>();
            shadeRect.anchorMin = Vector2.zero;
            shadeRect.anchorMax = Vector2.one;
            shadeRect.offsetMin = new Vector2(S(3f), S(3f));
            shadeRect.offsetMax = new Vector2(S(-3f), S(-3f));
            Image shadeImage = innerShade.AddComponent<Image>();
            shadeImage.color = buttonInnerShadeColor;
            shadeImage.raycastTarget = false;

            GameObject accentLine = new GameObject("AccentLine");
            accentLine.transform.SetParent(btnObj.transform, false);
            RectTransform accentRect = accentLine.AddComponent<RectTransform>();
            accentRect.anchorMin = new Vector2(0f, 1f);
            accentRect.anchorMax = new Vector2(1f, 1f);
            accentRect.pivot = new Vector2(0.5f, 1f);
            accentRect.offsetMin = new Vector2(S(5f), S(-3f));
            accentRect.offsetMax = new Vector2(S(-5f), S(-1f));
            Image accentImage = accentLine.AddComponent<Image>();
            accentImage.color = buttonAccentColor;
            accentImage.raycastTarget = false;

            Button button = btnObj.AddComponent<Button>();
            button.targetGraphic = bgImage;
            int capturedIndex = index;
            button.onClick.AddListener(() => TryUpgrade(capturedIndex));

            // Key label (top-left)
            GameObject keyObj = new GameObject("KeyLabel");
            keyObj.transform.SetParent(btnObj.transform, false);
            RectTransform keyRect = keyObj.AddComponent<RectTransform>();
            keyRect.anchorMin = new Vector2(0f, 1f);
            keyRect.anchorMax = new Vector2(0f, 1f);
            keyRect.pivot = new Vector2(0f, 1f);
            keyRect.anchoredPosition = new Vector2(S(4f), S(-4f));
            keyRect.sizeDelta = new Vector2(S(20f), S(16f));
            TextMeshProUGUI keyLabel = keyObj.AddComponent<TextMeshProUGUI>();
            keyLabel.text = keyStr;
            keyLabel.fontSize = S(13f);
            if (TMP_Settings.defaultFontAsset != null) keyLabel.font = TMP_Settings.defaultFontAsset;
            keyLabel.color = new Color(1f, 1f, 1f, 0.9f);
            keyLabel.alignment = TextAlignmentOptions.TopLeft;

            // Title (center-top)
            GameObject titleObj = new GameObject("Title");
            titleObj.transform.SetParent(btnObj.transform, false);
            RectTransform titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 1f);
            titleRect.anchorMax = new Vector2(0.5f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, S(titleFromTop));
            float titleH = S(12f);
            titleRect.sizeDelta = new Vector2(scaledButtonWidth - S(10f), titleH);
            TextMeshProUGUI titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = Titles[index];
            titleText.fontSize = S(8.5f);
            if (TMP_Settings.defaultFontAsset != null) titleText.font = TMP_Settings.defaultFontAsset;
            titleText.color = Color.white;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.enableWordWrapping = true;

            // Tick container (vertical stack, center)
            GameObject tickContainer = new GameObject("Ticks");
            tickContainer.transform.SetParent(btnObj.transform, false);
            RectTransform tickRect = tickContainer.AddComponent<RectTransform>();
            tickRect.anchorMin = new Vector2(0.5f, 0.5f);
            tickRect.anchorMax = new Vector2(0.5f, 0.5f);
            tickRect.pivot = new Vector2(0.5f, 0.5f);
            tickRect.anchoredPosition = new Vector2(0f, S(ticksCenterYOffset));
            tickRect.sizeDelta = new Vector2(Mathf.Min(scaledButtonWidth - S(16f), S(78f)), S(10f));

            HorizontalLayoutGroup tickLayout = tickContainer.AddComponent<HorizontalLayoutGroup>();
            tickLayout.spacing = S(2f);
            tickLayout.childAlignment = TextAnchor.MiddleCenter;
            tickLayout.childControlWidth = true;
            tickLayout.childControlHeight = true;
            tickLayout.childForceExpandWidth = false;
            tickLayout.childForceExpandHeight = false;

            // Cost row: number + gem icon (assign gem sprite on ShipAttributeUpgradeHUD in Inspector)
            GameObject costRow = new GameObject("CostRow");
            costRow.transform.SetParent(btnObj.transform, false);
            RectTransform costRowRect = costRow.AddComponent<RectTransform>();
            costRowRect.anchorMin = new Vector2(0.5f, 0f);
            costRowRect.anchorMax = new Vector2(0.5f, 0f);
            costRowRect.pivot = new Vector2(0.5f, 0f);
            costRowRect.anchoredPosition = new Vector2(0f, S(3f));
            float scaledGem = S(gemIconSize);
            costRowRect.sizeDelta = new Vector2(scaledButtonWidth - S(6f), Mathf.Max(S(14f), scaledGem + S(2f)));

            HorizontalLayoutGroup costRowLayout = costRow.AddComponent<HorizontalLayoutGroup>();
            costRowLayout.childAlignment = TextAnchor.MiddleCenter;
            costRowLayout.spacing = S(1f);
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
            costLabel.fontSize = S(11f);
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
            gemRect.sizeDelta = new Vector2(gemIconSize, gemIconSize);
            Image costGemIcon = gemObj.AddComponent<Image>();
            costGemIcon.raycastTarget = false;
            costGemIcon.preserveAspect = true;
            costGemIcon.enabled = gemCostIconSprite != null;
            if (gemCostIconSprite != null) costGemIcon.sprite = gemCostIconSprite;
            LayoutElement gemLe = gemObj.AddComponent<LayoutElement>();
            gemLe.preferredWidth = gemIconSize;
            gemLe.preferredHeight = gemIconSize;
            gemLe.flexibleWidth = 0f;

            return (button, titleText, tickContainer, bgImage, keyLabel, costLabel, costGemIcon);
        }

        private void CreateTickMarks(GameObject container, int maxCount)
        {
            maxCount = Mathf.Clamp(maxCount, 0, 7);
            for (int i = 0; i < maxCount; i++)
            {
                GameObject tick = new GameObject($"Tick_{i}");
                tick.transform.SetParent(container.transform, false);
                Image img = tick.AddComponent<Image>();
                img.color = new Color(0.3f, 0.3f, 0.35f, 0.8f);
                LayoutElement le = tick.AddComponent<LayoutElement>();
                le.preferredWidth = S(7f);
                le.preferredHeight = S(7f);
            }
        }

        private void UpdateTickMarks(int index, int currentLevel, int maxLevel)
        {
            if (tickContainers == null || index < 0 || index >= tickContainers.Length || tickContainers[index] == null) return;
            maxLevel = Mathf.Clamp(maxLevel, 0, 7);
            Transform container = tickContainers[index].transform;
            int childCount = container.childCount;

            if (childCount != maxLevel)
            {
                for (int i = container.childCount - 1; i >= 0; i--)
                    Destroy(container.GetChild(i).gameObject);
                CreateTickMarks(tickContainers[index], maxLevel);
                childCount = maxLevel;
            }

            for (int i = 0; i < childCount; i++)
            {
                Image img = container.GetChild(i).GetComponent<Image>();
                if (img != null)
                    img.color = i < currentLevel ? new Color(1f, 1f, 0.9f, 1f) : new Color(0.3f, 0.3f, 0.35f, 0.8f);
            }
        }

        private void Update()
        {
            if (!upgradeBarEnabled || rootPanel == null) return;

            if (playerShip == null || !playerShip.IsSpawned)
            {
                if (Time.time - lastShipLookupTime >= ShipLookupInterval)
                {
                    lastShipLookupTime = Time.time;
                    foreach (var ship in Object.FindObjectsByType<Starship>(FindObjectsSortMode.None))
                    {
                        if (ship.IsOwner)
                        {
                            playerShip = ship;
                            break;
                        }
                    }
                }
            }

            bool show = playerShip != null && playerShip.IsSpawned && !playerShip.IsDead && playerShip.ShipTeam != TeamManager.Team.None;
            if (HUDController.ShipUpgradeTreeObscuresHud)
                show = false;
            rootPanel.SetActive(show);

            if (!show || playerShip == null) return;

            int maxUpgrades = playerShip.MaxAttributeUpgrades;
            int cost = playerShip.AttributeUpgradeCost;

            for (int i = 0; i < 10; i++)
            {
                int current = playerShip.GetAttributeLevel(i);
                UpdateTickMarks(i, current, maxUpgrades);

                bool canUpgrade = current < maxUpgrades && playerShip.CurrentGems >= cost - 0.01f;
                if (buttons[i] != null)
                    buttons[i].interactable = canUpgrade;

                if (costLabels[i] != null)
                {
                    if (current >= maxUpgrades)
                    {
                        costLabels[i].text = "MAX";
                        if (costGemIcons[i] != null) costGemIcons[i].enabled = false;
                    }
                    else
                    {
                        costLabels[i].text = $"{cost}";
                        if (costGemIcons[i] != null)
                            costGemIcons[i].enabled = gemCostIconSprite != null;
                    }
                }
            }
        }

        private void LateUpdate()
        {
            if (!upgradeBarEnabled) return;
            if (HUDController.ShipUpgradeTreeObscuresHud) return;
            var keyboard = Keyboard.current;
            if (keyboard == null || playerShip == null || !playerShip.IsSpawned) return;

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

        private void TryUpgrade(int index)
        {
            if (playerShip == null || !playerShip.IsSpawned || !playerShip.IsOwner) return;
            if (index < 0 || index > 9) return;

            int current = playerShip.GetAttributeLevel(index);
            if (current >= playerShip.MaxAttributeUpgrades) return;
            if (playerShip.CurrentGems < playerShip.AttributeUpgradeCost - 0.01f) return;

            playerShip.UpgradeAttributeServerRpc(index);
        }
    }
}
