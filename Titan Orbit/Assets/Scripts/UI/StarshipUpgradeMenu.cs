using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using TitanOrbit.Data;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Upgrade menu at the bottom of the screen with 8 attribute upgrade buttons.
    /// Full width minus minimap. Each button: icon, title, notches. Cost = 5 gems × ship level per upgrade.
    /// </summary>
    public class StarshipUpgradeMenu : MonoBehaviour
    {
        [Header("Optional: assign sprites for upgrade icons (otherwise uses colored placeholders)")]
        [SerializeField] private Sprite iconMovementSpeed;
        [SerializeField] private Sprite iconEnergyCapacity;
        [SerializeField] private Sprite iconFirePower;
        [SerializeField] private Sprite iconBulletSpeed;
        [SerializeField] private Sprite iconMaxHealth;
        [SerializeField] private Sprite iconHealthRegen;
        [SerializeField] private Sprite iconRotationSpeed;
        [SerializeField] private Sprite iconEnergyRegen;

        private static readonly (AttributeUpgradeSystem.ShipAttributeType Type, string Title, Color PlaceholderColor)[] UpgradeTypes =
        {
            (AttributeUpgradeSystem.ShipAttributeType.MovementSpeed, "Speed", new Color(0.4f, 0.8f, 1f)),
            (AttributeUpgradeSystem.ShipAttributeType.EnergyCapacity, "Energy Cap", new Color(1f, 0.7f, 0.2f)),
            (AttributeUpgradeSystem.ShipAttributeType.FirePower, "Power", new Color(1f, 0.3f, 0.3f)),
            (AttributeUpgradeSystem.ShipAttributeType.BulletSpeed, "Shot Spd", new Color(1f, 0.9f, 0.3f)),
            (AttributeUpgradeSystem.ShipAttributeType.MaxHealth, "Health", new Color(0.4f, 1f, 0.4f)),
            (AttributeUpgradeSystem.ShipAttributeType.HealthRegen, "Regen", new Color(0.3f, 1f, 0.6f)),
            (AttributeUpgradeSystem.ShipAttributeType.RotationSpeed, "Turn", new Color(0.8f, 0.5f, 1f)),
            (AttributeUpgradeSystem.ShipAttributeType.EnergyRegen, "Energy Regen", new Color(0.2f, 0.9f, 1f)),
        };

        private const float MINIMAP_WIDTH = 404f; // 384px + 20px padding (matches GameSetup)

        private const int SHIP_LEVEL_BUTTON_INDEX = 0;
        private const int ATTRIBUTE_BUTTON_COUNT = 8;

        private GameObject panel;
        private Button shipLevelUpgradeButton;
        private TextMeshProUGUI shipLevelUpgradeLabel;
        private Button[] upgradeButtons = new Button[ATTRIBUTE_BUTTON_COUNT];
        private Image[] iconImages = new Image[ATTRIBUTE_BUTTON_COUNT];
        private TextMeshProUGUI[] titleTexts = new TextMeshProUGUI[ATTRIBUTE_BUTTON_COUNT];
        private Image[][] notchImages = new Image[ATTRIBUTE_BUTTON_COUNT][];
        private TextMeshProUGUI costHintText;
        private Starship playerShip;

        // Top-center strip: two ship upgrade choice buttons (shown when criteria met). Keys 9 and 0.
        private GameObject topShipChoicePanel;
        private Button[] shipChoiceTopButtons = new Button[2];
        private TextMeshProUGUI[] shipChoiceTopLabels = new TextMeshProUGUI[2];

        public static StarshipUpgradeMenu GetOrCreate()
        {
            var existing = Object.FindFirstObjectByType<StarshipUpgradeMenu>();
            if (existing != null) return existing;

            Canvas canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                GameObject canvasObj = new GameObject("Canvas");
                canvas = canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasObj.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                canvasObj.AddComponent<GraphicRaycaster>();
            }

            GameObject uiObj = new GameObject("StarshipUpgradeMenu");
            uiObj.transform.SetParent(canvas.transform, false);
            var menu = uiObj.AddComponent<StarshipUpgradeMenu>();
            menu.Show();
            return menu;
        }

        private void Awake()
        {
            Show();
        }

        private void Update()
        {
            if (playerShip == null)
            {
                foreach (var ship in Object.FindObjectsByType<Starship>(FindObjectsSortMode.None))
                {
                    if (ship.IsOwner) { playerShip = ship; break; }
                }
            }
            if (playerShip != null)
            {
                EnsureTopShipChoicePanelExists();
                RefreshTopShipChoiceButtons();
                if (panel != null && panel.activeSelf)
                    RefreshAllButtons();
            }
            HandleKeyboardShortcuts();
        }

        public void Show()
        {
            EnsurePanelExists();
            EnsureTopShipChoicePanelExists();
            if (panel != null) panel.SetActive(true);
        }

        public void Hide()
        {
            if (panel != null) panel.SetActive(false);
            if (topShipChoicePanel != null) topShipChoicePanel.SetActive(false);
        }

        private void EnsurePanelExists()
        {
            if (panel != null) return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            panel = new GameObject("UpgradePanel");
            panel.transform.SetParent(canvas.transform, false);
            var rect = panel.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.offsetMin = new Vector2(0f, 0f);
            rect.offsetMax = new Vector2(-MINIMAP_WIDTH, 160f); // Leave space for minimap, 160px tall
            var img = panel.AddComponent<Image>();
            img.color = new Color(0.06f, 0.07f, 0.12f, 0.94f);

            // Cost hint at top of bar
            costHintText = CreateTMP(panel.transform, "CostHint", "5 gems × ship level per upgrade", 16,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -10), new Vector2(800, 28));

            float buttonWidth = 130f;
            float spacing = 12f;
            int totalCount = 1 + ATTRIBUTE_BUTTON_COUNT; // Ship level + 8 attributes
            float totalWidth = totalCount * buttonWidth + (totalCount - 1) * spacing;
            float startX = -totalWidth / 2f + buttonWidth / 2f;

            CreateShipLevelUpgradeButton(startX, buttonWidth);
            for (int i = 0; i < ATTRIBUTE_BUTTON_COUNT; i++)
            {
                float x = startX + (1 + i) * (buttonWidth + spacing);
                CreateUpgradeButton(i, x, buttonWidth);
            }
        }

        private void EnsureTopShipChoicePanelExists()
        {
            if (topShipChoicePanel != null) return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            topShipChoicePanel = new GameObject("TopShipChoicePanel");
            topShipChoicePanel.transform.SetParent(canvas.transform, false);
            var rect = topShipChoicePanel.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -24f);
            rect.sizeDelta = new Vector2(420f, 72f);
            var img = topShipChoicePanel.AddComponent<Image>();
            img.color = new Color(0.08f, 0.09f, 0.14f, 0.92f);

            float btnWidth = 190f;
            float spacing = 20f;
            for (int i = 0; i < 2; i++)
            {
                float x = (i == 0) ? -(btnWidth / 2f + spacing / 2f) : (btnWidth / 2f + spacing / 2f);
                var btnRoot = new GameObject($"ShipChoice_{i}");
                btnRoot.transform.SetParent(topShipChoicePanel.transform, false);
                var btnRect = btnRoot.AddComponent<RectTransform>();
                btnRect.anchorMin = new Vector2(0.5f, 0.5f);
                btnRect.anchorMax = new Vector2(0.5f, 0.5f);
                btnRect.pivot = new Vector2(0.5f, 0.5f);
                btnRect.anchoredPosition = new Vector2(x, 0f);
                btnRect.sizeDelta = new Vector2(btnWidth, 52f);
                var btnImg = btnRoot.AddComponent<Image>();
                btnImg.color = new Color(0.2f, 0.45f, 0.85f, 0.95f);
                var btn = btnRoot.AddComponent<Button>();
                var colors = btn.colors;
                colors.normalColor = new Color(0.2f, 0.45f, 0.85f);
                colors.highlightedColor = new Color(0.35f, 0.55f, 0.95f);
                colors.pressedColor = new Color(0.15f, 0.35f, 0.75f);
                colors.disabledColor = new Color(0.15f, 0.2f, 0.3f, 0.7f);
                btn.colors = colors;
                int index = i;
                btn.onClick.AddListener(() => OnTopShipChoiceClicked(index));

                var labelGo = new GameObject("Label");
                labelGo.transform.SetParent(btnRoot.transform, false);
                var labelRect = labelGo.AddComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(6, 4);
                labelRect.offsetMax = new Vector2(-6, -4);
                var tmp = labelGo.AddComponent<TextMeshProUGUI>();
                tmp.text = "Ship " + (i + 1);
                tmp.fontSize = 18;
                tmp.fontStyle = FontStyles.Bold;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white;
                tmp.raycastTarget = false;
                shipChoiceTopButtons[i] = btn;
                shipChoiceTopLabels[i] = tmp;
            }

            topShipChoicePanel.SetActive(false);
            topShipChoicePanel.transform.SetAsLastSibling(); // Draw on top of other UI
        }

        private void CreateShipLevelUpgradeButton(float x, float width)
        {
            var btnRoot = new GameObject("Upgrade_ShipLevel");
            btnRoot.transform.SetParent(panel.transform, false);
            var btnRect = btnRoot.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0f);
            btnRect.anchorMax = new Vector2(0.5f, 0f);
            btnRect.pivot = new Vector2(0.5f, 0f);
            btnRect.anchoredPosition = new Vector2(x, 20f);
            btnRect.sizeDelta = new Vector2(width, 120f);
            var btnImg = btnRoot.AddComponent<Image>();
            btnImg.color = new Color(0.14f, 0.16f, 0.24f, 0.95f);
            var outline = btnRoot.AddComponent<Outline>();
            outline.effectColor = new Color(0.35f, 0.4f, 0.55f, 0.6f);
            outline.effectDistance = new Vector2(1, 1);
            shipLevelUpgradeButton = btnRoot.AddComponent<Button>();
            var colors = shipLevelUpgradeButton.colors;
            colors.normalColor = new Color(0.14f, 0.16f, 0.24f);
            colors.highlightedColor = new Color(0.22f, 0.26f, 0.38f);
            colors.pressedColor = new Color(0.1f, 0.12f, 0.18f);
            colors.disabledColor = new Color(0.1f, 0.11f, 0.16f, 0.7f);
            shipLevelUpgradeButton.colors = colors;
            shipLevelUpgradeButton.onClick.AddListener(OnShipLevelUpgradeClicked);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(btnRoot.transform, false);
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(4, 4);
            labelRect.offsetMax = new Vector2(-4, -4);
            shipLevelUpgradeLabel = labelGo.AddComponent<TextMeshProUGUI>();
            shipLevelUpgradeLabel.text = "Ship Lv1";
            shipLevelUpgradeLabel.fontSize = 14;
            shipLevelUpgradeLabel.alignment = TextAlignmentOptions.Center;
            shipLevelUpgradeLabel.color = new Color(0.95f, 0.96f, 1f);
            shipLevelUpgradeLabel.raycastTarget = false;
        }

        private void CreateUpgradeButton(int index, float x, float width)
        {
            var type = UpgradeTypes[index];
            var btnRoot = new GameObject($"Upgrade_{type.Title}");
            btnRoot.transform.SetParent(panel.transform, false);
            var btnRect = btnRoot.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0f);
            btnRect.anchorMax = new Vector2(0.5f, 0f);
            btnRect.pivot = new Vector2(0.5f, 0f);
            btnRect.anchoredPosition = new Vector2(x, 20f);
            btnRect.sizeDelta = new Vector2(width, 120f);

            // Button background with outline
            var btnImg = btnRoot.AddComponent<Image>();
            btnImg.color = new Color(0.14f, 0.16f, 0.24f, 0.95f);
            var outline = btnRoot.AddComponent<Outline>();
            outline.effectColor = new Color(0.35f, 0.4f, 0.55f, 0.4f);
            outline.effectDistance = new Vector2(2, -2);
            var btn = btnRoot.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = new Color(0.14f, 0.16f, 0.24f);
            colors.highlightedColor = new Color(0.22f, 0.26f, 0.38f);
            colors.pressedColor = new Color(0.1f, 0.12f, 0.18f);
            colors.disabledColor = new Color(0.1f, 0.11f, 0.16f, 0.7f);
            btn.colors = colors;

            int idx = index;
            btn.onClick.AddListener(() => OnUpgradeClicked(idx));

            // Icon at top-left with padding and subtle frame
            const float iconSize = 42f;
            const float iconPadding = 8f;
            var iconFrame = new GameObject("IconFrame");
            iconFrame.transform.SetParent(btnRoot.transform, false);
            var iconFrameRect = iconFrame.AddComponent<RectTransform>();
            iconFrameRect.anchorMin = new Vector2(0f, 1f);
            iconFrameRect.anchorMax = new Vector2(0f, 1f);
            iconFrameRect.pivot = new Vector2(0f, 1f);
            iconFrameRect.anchoredPosition = new Vector2(iconPadding, -iconPadding);
            iconFrameRect.sizeDelta = new Vector2(iconSize, iconSize);
            var iconFrameImg = iconFrame.AddComponent<Image>();
            iconFrameImg.color = new Color(0.08f, 0.1f, 0.16f);
            iconFrameImg.raycastTarget = false;

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(iconFrame.transform, false);
            var iconRect = iconGo.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = Vector2.zero;
            iconRect.sizeDelta = new Vector2(iconSize - 6, iconSize - 6);
            var iconImg = iconGo.AddComponent<Image>();
            iconImg.color = type.PlaceholderColor;
            if (GetIconSprite(index) != null)
                iconImg.sprite = GetIconSprite(index);
            iconImg.raycastTarget = false;
            iconImages[index] = iconImg;

            // Title: top-middle, in the space to the right of icon (centered in remaining area)
            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(btnRoot.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 0.55f);
            titleRect.anchorMax = new Vector2(1f, 0.92f);
            titleRect.offsetMin = new Vector2(iconPadding + iconSize + 6, 4f);
            titleRect.offsetMax = new Vector2(-iconPadding, -4f);
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = type.Title;
            titleTmp.fontSize = 15;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.color = new Color(0.95f, 0.96f, 1f);
            titleTmp.raycastTarget = false;
            titleTexts[index] = titleTmp;

            // Notches row at bottom
            var notchRoot = new GameObject("Notches");
            notchRoot.transform.SetParent(btnRoot.transform, false);
            var notchRootRect = notchRoot.AddComponent<RectTransform>();
            notchRootRect.anchorMin = new Vector2(0.5f, 0f);
            notchRootRect.anchorMax = new Vector2(0.5f, 0f);
            notchRootRect.pivot = new Vector2(0.5f, 0f);
            notchRootRect.anchoredPosition = new Vector2(0, 12f);
            notchRootRect.sizeDelta = new Vector2(width - 16, 22);

            notchImages[index] = new Image[6];
            float notchSize = 12f;
            float notchSpacing = 5f;
            for (int n = 0; n < 6; n++)
            {
                var notchGo = new GameObject($"Notch_{n}");
                notchGo.transform.SetParent(notchRoot.transform, false);
                var notchRect = notchGo.AddComponent<RectTransform>();
                notchRect.anchorMin = new Vector2(0.5f, 0.5f);
                notchRect.anchorMax = new Vector2(0.5f, 0.5f);
                notchRect.pivot = new Vector2(0.5f, 0.5f);
                float totalW = 6f * notchSize + 5f * notchSpacing;
                float nStartX = -totalW / 2f + notchSize / 2f;
                float nx = nStartX + n * (notchSize + notchSpacing);
                notchRect.anchoredPosition = new Vector2(nx, 0);
                notchRect.sizeDelta = new Vector2(notchSize, notchSize);
                var notchImg = notchGo.AddComponent<Image>();
                notchImg.color = new Color(0.28f, 0.3f, 0.4f);
                notchImg.raycastTarget = false;
                notchImages[index][n] = notchImg;
            }

            upgradeButtons[index] = btn;
        }

        private Sprite GetIconSprite(int index)
        {
            switch (index)
            {
                case 0: return iconMovementSpeed;
                case 1: return iconEnergyCapacity;
                case 2: return iconFirePower;
                case 3: return iconBulletSpeed;
                case 4: return iconMaxHealth;
                case 5: return iconHealthRegen;
                case 6: return iconRotationSpeed;
                case 7: return iconEnergyRegen;
                default: return null;
            }
        }

        /// <summary>Updates the top-center two ship upgrade buttons. Called every frame when playerShip exists so they show whenever upgrade is available.</summary>
        private void RefreshTopShipChoiceButtons()
        {
            if (topShipChoicePanel == null || playerShip == null) return;
            bool canUpgradeShipLevel = UpgradeSystem.Instance != null && UpgradeSystem.Instance.CanUpgradeStarshipLevel(playerShip);
            topShipChoicePanel.SetActive(canUpgradeShipLevel);
            if (!canUpgradeShipLevel) return;
            if (UpgradeSystem.Instance == null || UpgradeSystem.Instance.UpgradeTree == null) return;
            var available = UpgradeSystem.Instance.UpgradeTree.GetAvailableUpgrades(playerShip.ShipLevel, playerShip.BranchIndex);
            for (int i = 0; i < 2; i++)
            {
                bool show = i < available.Count;
                if (shipChoiceTopButtons != null && i < shipChoiceTopButtons.Length && shipChoiceTopButtons[i] != null)
                {
                    shipChoiceTopButtons[i].gameObject.SetActive(show);
                    shipChoiceTopButtons[i].interactable = show;
                }
                if (shipChoiceTopLabels != null && i < shipChoiceTopLabels.Length && shipChoiceTopLabels[i] != null && show)
                    shipChoiceTopLabels[i].text = (i == 0 ? "[9] " : "[0] ") + (available[i].shipName ?? available[i].focusType.ToString());
            }
        }

        private static TextMeshProUGUI CreateTMP(Transform parent, string name, string text, int fontSize,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = sizeDelta;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.8f, 0.8f, 0.9f);
            return tmp;
        }

        private void RefreshAllButtons()
        {
            if (playerShip == null) return;
            int shipLevel = playerShip.ShipLevel;
            float cost = AttributeUpgradeSystem.Instance != null
                ? AttributeUpgradeSystem.Instance.GetUpgradeCost(shipLevel)
                : 5f * shipLevel;
            if (costHintText != null)
                costHintText.text = $"Cost: {cost:F0} gems per upgrade  |  Keys 1-8: abilities, 9/0: new ship";

            bool canUpgradeShipLevel = UpgradeSystem.Instance != null && UpgradeSystem.Instance.CanUpgradeStarshipLevel(playerShip);
            if (shipLevelUpgradeButton != null)
            {
                shipLevelUpgradeButton.interactable = canUpgradeShipLevel;
                shipLevelUpgradeButton.gameObject.SetActive(true);
            }
            if (shipLevelUpgradeLabel != null)
                shipLevelUpgradeLabel.text = canUpgradeShipLevel ? $"Upgrade Ship\n(Lv{shipLevel}→Lv{shipLevel + 1})" : $"Ship Lv{shipLevel}";

            for (int i = 0; i < ATTRIBUTE_BUTTON_COUNT; i++)
            {
                var type = UpgradeTypes[i].Type;
                int current = playerShip.GetAttributeLevel(type);
                int max = shipLevel;
                bool canUpgrade = current < max && playerShip.CurrentGems >= cost;
                if (upgradeButtons[i] != null)
                    upgradeButtons[i].interactable = canUpgrade;

                if (notchImages[i] != null)
                {
                    for (int n = 0; n < 6; n++)
                    {
                        if (n >= max)
                            notchImages[i][n].gameObject.SetActive(false);
                        else
                        {
                            notchImages[i][n].gameObject.SetActive(true);
                            notchImages[i][n].color = n < current
                                ? new Color(0.45f, 0.85f, 0.55f)
                                : new Color(0.28f, 0.3f, 0.4f);
                        }
                    }
                }
            }
        }

        private void OnShipLevelUpgradeClicked()
        {
            if (playerShip == null) return;
            HomePlanetOrbitUI.GetOrCreate().ShowShipUpgradeChoice(playerShip);
        }

        private void OnTopShipChoiceClicked(int index)
        {
            if (playerShip == null || UpgradeSystem.Instance == null) return;
            var tree = UpgradeSystem.Instance.UpgradeTree;
            if (tree == null) return;
            var available = tree.GetAvailableUpgrades(playerShip.ShipLevel, playerShip.BranchIndex);
            if (index < 0 || index >= available.Count) return;
            int nextLevel = playerShip.ShipLevel + 1;
            var node = available[index];
            UpgradeSystem.Instance.UpgradeShipServerRpc(playerShip.NetworkObjectId, nextLevel, node.focusType, index);
        }

        private void HandleKeyboardShortcuts()
        {
            if (playerShip == null || panel == null || !panel.activeSelf) return;

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            // 1-8: attribute upgrades (only when that button is interactable)
            float cost = AttributeUpgradeSystem.Instance != null
                ? AttributeUpgradeSystem.Instance.GetUpgradeCost(playerShip.ShipLevel)
                : 5f * playerShip.ShipLevel;
            var digitKeys = new[] { keyboard.digit1Key, keyboard.digit2Key, keyboard.digit3Key, keyboard.digit4Key,
                keyboard.digit5Key, keyboard.digit6Key, keyboard.digit7Key, keyboard.digit8Key };
            for (int i = 0; i < ATTRIBUTE_BUTTON_COUNT; i++)
            {
                if (digitKeys[i].wasPressedThisFrame)
                {
                    var type = UpgradeTypes[i].Type;
                    int current = playerShip.GetAttributeLevel(type);
                    int max = playerShip.ShipLevel;
                    bool canUpgrade = current < max && playerShip.CurrentGems >= cost;
                    if (canUpgrade && AttributeUpgradeSystem.Instance != null)
                    {
                        AttributeUpgradeSystem.Instance.UpgradeAttributeServerRpc(playerShip.NetworkObjectId, type);
                    }
                    return;
                }
            }

            // 9 and 0: ship upgrade choice 0 and 1 (only when criteria met)
            bool canUpgradeShip = UpgradeSystem.Instance != null && UpgradeSystem.Instance.CanUpgradeStarshipLevel(playerShip);
            if (canUpgradeShip && UpgradeSystem.Instance?.UpgradeTree != null)
            {
                var available = UpgradeSystem.Instance.UpgradeTree.GetAvailableUpgrades(playerShip.ShipLevel, playerShip.BranchIndex);
                if (keyboard.digit9Key.wasPressedThisFrame && available.Count > 0)
                {
                    OnTopShipChoiceClicked(0);
                    return;
                }
                if (keyboard.digit0Key.wasPressedThisFrame && available.Count > 1)
                {
                    OnTopShipChoiceClicked(1);
                }
            }
        }

        private void OnUpgradeClicked(int index)
        {
            if (playerShip == null || AttributeUpgradeSystem.Instance == null) return;
            var type = UpgradeTypes[index].Type;
            AttributeUpgradeSystem.Instance.UpgradeAttributeServerRpc(playerShip.NetworkObjectId, type);
        }
    }
}
