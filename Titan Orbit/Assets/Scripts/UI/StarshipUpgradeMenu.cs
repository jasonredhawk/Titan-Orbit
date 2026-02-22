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
    /// Upgrade menu along the full bottom of the screen (minus minimap). Eight attribute buttons:
    /// Icon + title, 6 flat progression notches (when at max ship level), and Lvl + key row.
    /// </summary>
    public class StarshipUpgradeMenu : MonoBehaviour
    {
        [Header("Optional: Shift Sci-Fi panel/button styling")]
        [SerializeField] private Sprite panelSprite;
        [SerializeField] private Sprite buttonSprite;

        [Header("Optional: assign sprites for upgrade icons (otherwise uses coloured placeholders)")]
        [SerializeField] private Sprite iconMovementSpeed;
        [SerializeField] private Sprite iconEnergyCapacity;
        [SerializeField] private Sprite iconFirePower;
        [SerializeField] private Sprite iconBulletSpeed;
        [SerializeField] private Sprite iconMaxHealth;
        [SerializeField] private Sprite iconHealthRegen;
        [SerializeField] private Sprite iconRotationSpeed;
        [SerializeField] private Sprite iconEnergyRegen;

        // Brighter semi-transparent theme background colours (same theme = same tint)
        private static readonly Color ThemeMovement = new Color(0.35f, 0.65f, 0.9f, 0.5f);
        private static readonly Color ThemeEnergy = new Color(0.9f, 0.7f, 0.25f, 0.5f);
        private static readonly Color ThemeCombat = new Color(0.85f, 0.35f, 0.35f, 0.5f);
        private static readonly Color ThemeHealth = new Color(0.35f, 0.75f, 0.5f, 0.5f);

        // Order: 1 Move Speed, 2 Turn Speed, 3 Health Capacity, 4 Health Regen, 5 Energy Capacity, 6 Energy Regen, 7 Shot Power, 8 Shot Speed
        private static readonly (AttributeUpgradeSystem.ShipAttributeType Type, string Title, Color PlaceholderColor, Color ThemeBg)[] UpgradeTypes =
        {
            (AttributeUpgradeSystem.ShipAttributeType.MovementSpeed, "Move\nSpeed", new Color(0.4f, 0.8f, 1f), ThemeMovement),
            (AttributeUpgradeSystem.ShipAttributeType.RotationSpeed, "Turn\nSpeed", new Color(0.8f, 0.5f, 1f), ThemeMovement),
            (AttributeUpgradeSystem.ShipAttributeType.MaxHealth, "Health\nCapacity", new Color(0.4f, 1f, 0.4f), ThemeHealth),
            (AttributeUpgradeSystem.ShipAttributeType.HealthRegen, "Health\nRegen", new Color(0.3f, 1f, 0.6f), ThemeHealth),
            (AttributeUpgradeSystem.ShipAttributeType.EnergyCapacity, "Energy\nCapacity", new Color(1f, 0.7f, 0.2f), ThemeEnergy),
            (AttributeUpgradeSystem.ShipAttributeType.EnergyRegen, "Energy\nRegen", new Color(0.2f, 0.9f, 1f), ThemeEnergy),
            (AttributeUpgradeSystem.ShipAttributeType.FirePower, "Shot\nPower", new Color(1f, 0.3f, 0.3f), ThemeCombat),
            (AttributeUpgradeSystem.ShipAttributeType.BulletSpeed, "Shot\nSpeed", new Color(1f, 0.9f, 0.3f), ThemeCombat),
        };

        private const float MINIMAP_WIDTH = 384f;
        private const float BUTTON_WIDTH = 168f;
        private const float BUTTON_HEIGHT = 80f;
        private const float BUTTON_SPACING = 20f;
        private const float ICON_SIZE = 28f;
        private const int KEY_1_TO_8 = 1;
        private const int ATTRIBUTE_BUTTON_COUNT = 8;
        private const int NOTCH_COUNT = 6;

        private GameObject panel;
        private Button[] upgradeButtons = new Button[ATTRIBUTE_BUTTON_COUNT];
        private Image[] iconImages = new Image[ATTRIBUTE_BUTTON_COUNT];
        private TextMeshProUGUI[] titleTexts = new TextMeshProUGUI[ATTRIBUTE_BUTTON_COUNT];
        private TextMeshProUGUI[] keyTexts = new TextMeshProUGUI[ATTRIBUTE_BUTTON_COUNT];
        private Image[][] notchImages = new Image[ATTRIBUTE_BUTTON_COUNT][];
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
            // Start hidden; only show when in play mode (player ship exists)
            EnsurePanelExists();
            EnsureTopShipChoicePanelExists();
            Hide();
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

            // Only show upgrade menu when in play mode (local player ship exists)
            if (playerShip == null)
            {
                Hide();
                return;
            }

            Show();
            EnsureTopShipChoicePanelExists();
            RefreshTopShipChoiceButtons();
            if (panel != null && panel.activeSelf)
                RefreshAllButtons();
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
            float margin = 24f;
            float panelHeight = BUTTON_HEIGHT + 24f;
            // Full width at bottom minus minimap
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, panelHeight + 20f);
            rect.offsetMin = new Vector2(0f, 0f);
            rect.offsetMax = new Vector2(-MINIMAP_WIDTH, panelHeight);
            var img = panel.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            img.raycastTarget = false;

            for (int i = 0; i < ATTRIBUTE_BUTTON_COUNT; i++)
            {
                float x = margin + i * (BUTTON_WIDTH + BUTTON_SPACING);
                CreateUpgradeButton(i, x);
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
            if (panelSprite != null) { img.sprite = panelSprite; img.type = panelSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }

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
                if (buttonSprite != null) { btnImg.sprite = buttonSprite; btnImg.type = buttonSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }
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

        private void CreateUpgradeButton(int index, float x)
        {
            var type = UpgradeTypes[index];
            int keyNum = index + KEY_1_TO_8;
            var btnRoot = new GameObject($"Upgrade_{type.Title}");
            btnRoot.transform.SetParent(panel.transform, false);
            var btnRect = btnRoot.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0f, 0.5f);
            btnRect.anchorMax = new Vector2(0f, 0.5f);
            btnRect.pivot = new Vector2(0f, 0.5f);
            btnRect.anchoredPosition = new Vector2(x, 0f);
            btnRect.sizeDelta = new Vector2(BUTTON_WIDTH, BUTTON_HEIGHT);

            var btnImg = btnRoot.AddComponent<Image>();
            btnImg.color = type.ThemeBg;
            if (buttonSprite != null) { btnImg.sprite = buttonSprite; btnImg.type = buttonSprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple; }
            var outline = btnRoot.AddComponent<Outline>();
            outline.effectColor = new Color(0.35f, 0.4f, 0.55f, 0.4f);
            outline.effectDistance = new Vector2(1, -1);
            var btn = btnRoot.AddComponent<Button>();
            var colors = btn.colors;
            // Use white so the Image's theme colour (ThemeBg) shows; Button only adds hover/press tint
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.88f, 0.88f, 0.88f, 1f);
            colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.85f);
            btn.colors = colors;

            int idx = index;
            btn.onClick.AddListener(() => OnUpgradeClicked(idx));

            float pad = 6f;

            // Top row: Icon + Title only
            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(btnRoot.transform, false);
            var iconRect = iconGo.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 1f);
            iconRect.anchorMax = new Vector2(0f, 1f);
            iconRect.pivot = new Vector2(0f, 1f);
            iconRect.anchoredPosition = new Vector2(pad, -pad);
            iconRect.sizeDelta = new Vector2(ICON_SIZE, ICON_SIZE);
            var iconImg = iconGo.AddComponent<Image>();
            Sprite iconSprite = GetIconSprite(type.Type);
            iconImg.color = iconSprite != null ? Color.white : type.PlaceholderColor;
            if (iconSprite != null) iconImg.sprite = iconSprite;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;
            iconImages[index] = iconImg;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(btnRoot.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -pad);
            titleRect.offsetMin = new Vector2(pad + ICON_SIZE + 4f, -44f);
            titleRect.offsetMax = new Vector2(-pad, 0f);
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = type.Title;
            titleTmp.fontSize = 13;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.enableWordWrapping = true;
            titleTmp.overflowMode = TMPro.TextOverflowModes.Overflow;
            titleTmp.color = new Color(0.95f, 0.96f, 1f);
            titleTmp.raycastTarget = false;
            titleTexts[index] = titleTmp;

            // Vertical stack of 6 flat progression notches (left side of bottom area); key number to the right
            float notchBarW = 22f;
            float notchBarH = 4f;
            float notchGap = 2f;
            float stackHeight = NOTCH_COUNT * notchBarH + (NOTCH_COUNT - 1) * notchGap;
            var notchRoot = new GameObject("Notches");
            notchRoot.transform.SetParent(btnRoot.transform, false);
            var notchRootRect = notchRoot.AddComponent<RectTransform>();
            notchRootRect.anchorMin = new Vector2(0f, 0f);
            notchRootRect.anchorMax = new Vector2(0f, 0f);
            notchRootRect.pivot = new Vector2(0f, 0f);
            notchRootRect.anchoredPosition = new Vector2(pad, pad);
            notchRootRect.sizeDelta = new Vector2(notchBarW, stackHeight);

            notchImages[index] = new Image[NOTCH_COUNT];
            for (int n = 0; n < NOTCH_COUNT; n++)
            {
                var notchGo = new GameObject($"Notch_{n}");
                notchGo.transform.SetParent(notchRoot.transform, false);
                var notchRect = notchGo.AddComponent<RectTransform>();
                notchRect.anchorMin = new Vector2(0f, 0f);
                notchRect.anchorMax = new Vector2(0f, 0f);
                notchRect.pivot = new Vector2(0f, 0f);
                notchRect.anchoredPosition = new Vector2(0f, n * (notchBarH + notchGap));
                notchRect.sizeDelta = new Vector2(notchBarW, notchBarH);
                var notchImg = notchGo.AddComponent<Image>();
                notchImg.color = new Color(0.28f, 0.3f, 0.4f);
                notchImg.raycastTarget = false;
                notchImages[index][n] = notchImg;
            }

            // Key number: fills the space to the right of the notch stack (large, centered in that area)
            float keyAreaLeft = pad + notchBarW + 6f;
            var keyGo = new GameObject("Key");
            keyGo.transform.SetParent(btnRoot.transform, false);
            var keyRect = keyGo.AddComponent<RectTransform>();
            keyRect.anchorMin = new Vector2(0f, 0f);
            keyRect.anchorMax = new Vector2(1f, 0f);
            keyRect.pivot = new Vector2(0.5f, 0f);
            keyRect.anchoredPosition = new Vector2(0f, 0f);
            keyRect.offsetMin = new Vector2(keyAreaLeft, pad);
            keyRect.offsetMax = new Vector2(-pad, pad + stackHeight);
            var keyTmp = keyGo.AddComponent<TextMeshProUGUI>();
            keyTmp.text = keyNum.ToString();
            keyTmp.fontSize = 28;
            keyTmp.fontStyle = FontStyles.Bold;
            keyTmp.alignment = TextAlignmentOptions.Center;
            keyTmp.color = new Color(0.85f, 0.92f, 1f, 0.95f);
            keyTmp.raycastTarget = false;
            keyTexts[index] = keyTmp;

            upgradeButtons[index] = btn;
        }

        private Sprite GetIconSprite(AttributeUpgradeSystem.ShipAttributeType type)
        {
            switch (type)
            {
                case AttributeUpgradeSystem.ShipAttributeType.MovementSpeed: return iconMovementSpeed;
                case AttributeUpgradeSystem.ShipAttributeType.EnergyCapacity: return iconEnergyCapacity;
                case AttributeUpgradeSystem.ShipAttributeType.FirePower: return iconFirePower;
                case AttributeUpgradeSystem.ShipAttributeType.BulletSpeed: return iconBulletSpeed;
                case AttributeUpgradeSystem.ShipAttributeType.MaxHealth: return iconMaxHealth;
                case AttributeUpgradeSystem.ShipAttributeType.HealthRegen: return iconHealthRegen;
                case AttributeUpgradeSystem.ShipAttributeType.RotationSpeed: return iconRotationSpeed;
                case AttributeUpgradeSystem.ShipAttributeType.EnergyRegen: return iconEnergyRegen;
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

            for (int i = 0; i < ATTRIBUTE_BUTTON_COUNT; i++)
            {
                var type = UpgradeTypes[i].Type;
                int current = playerShip.GetAttributeLevel(type);
                int max = shipLevel;
                bool canUpgrade = current < max && playerShip.CurrentGems >= cost;
                if (upgradeButtons[i] != null)
                    upgradeButtons[i].interactable = canUpgrade;

                int keyNum = i + KEY_1_TO_8;
                if (keyTexts[i] != null)
                    keyTexts[i].text = keyNum.ToString();

                if (notchImages[i] != null)
                {
                    for (int n = 0; n < NOTCH_COUNT; n++)
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
