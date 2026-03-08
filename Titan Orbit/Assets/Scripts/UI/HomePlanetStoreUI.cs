using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Data;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Home Planet store panel: shows contributed gems and buy buttons for drones, rockets, mines.
    /// </summary>
    public class HomePlanetStoreUI : MonoBehaviour
    {
        [Header("Panel")]
        [SerializeField] private GameObject storePanel;
        [SerializeField] private TextMeshProUGUI gemsText;
        [SerializeField] private Button closeButton;

        private Starship currentShip;
        private HomePlanet currentHomePlanet;
        private Planet currentStorePlanet;
        private float contributedGems;
        private Button[] itemButtons;
        private TextMeshProUGUI[] itemLabels;

        // Card section (simple list of available cards at home; planet-specific family cards handled later).
        private Button[] cardButtons;
        private TextMeshProUGUI[] cardLabels;
        private CardData[] cardEntries;

        // Chassis section (unlocked ships by home planet level).
        private Button[] chassisButtons;
        private TextMeshProUGUI[] chassisLabels;
        private ShipChassisDefinition[] chassisEntries;
        private static float lastReceivedGems;
        private static bool pendingGemsRequest;

        public static void OnContributedGemsReceived(float gems)
        {
            lastReceivedGems = gems;
            pendingGemsRequest = false;
            var ui = Object.FindFirstObjectByType<HomePlanetStoreUI>();
            if (ui != null) ui.RefreshFromReceivedGems();
        }

        private void Awake()
        {
            if (closeButton != null) closeButton.onClick.AddListener(Close);
        }

        private void Update()
        {
            if (storePanel != null && storePanel.activeSelf)
                RefreshLabels();
        }

        public void Show(Starship ship, Planet storePlanet, HomePlanet homePlanet)
        {
            currentShip = ship;
            currentStorePlanet = storePlanet;
            currentHomePlanet = homePlanet;
            contributedGems = 0f;
            EnsurePanelExists();
            if (storePanel != null) storePanel.SetActive(true);
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null)
                HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
            RefreshLabels();
        }

        public void Close()
        {
            currentShip = null;
            currentHomePlanet = null;
            currentStorePlanet = null;
            if (storePanel != null) storePanel.SetActive(false);
        }

        private void RefreshFromReceivedGems()
        {
            contributedGems = lastReceivedGems;
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            if (!pendingGemsRequest) contributedGems = lastReceivedGems;
            if (gemsText != null) gemsText.text = $"Your contributed gems: {contributedGems:F0}";

            // Legacy consumables (drones, rockets, mines) are being phased into the new card system.
            // Hide their buttons so the store presents a single, unified design.
            if (itemButtons != null)
            {
                for (int i = 0; i < itemButtons.Length; i++)
                {
                    if (itemButtons[i] != null)
                        itemButtons[i].gameObject.SetActive(false);
                }
            }

            // Simple card section at home and captured planets.
            if (cardButtons != null && currentShip != null && currentHomePlanet != null && currentStorePlanet != null && CardShopSystem.Instance != null)
            {
                int homeLevel = currentHomePlanet.HomePlanetLevel;
                bool isHomeStore = currentStorePlanet is HomePlanet;
                System.Collections.Generic.List<CardData> availableCards;
                if (isHomeStore)
                {
                    // Home planet: show global cards plus cards from any captured planets owned by this team.
                    var team = currentShip.ShipTeam;
                    availableCards = CardShopSystem.Instance.GetAvailableCardsForHomeStore(homeLevel, team);
                }
                else
                {
                    // Captured planet: only that planet's own family cards.
                    int originFilter = currentStorePlanet.PlanetId;
                    availableCards = CardShopSystem.Instance.GetAvailableCardsForPlanet(homeLevel, originFilter);
                }

                // Cache up to cardButtons.Length entries.
                if (cardEntries == null || cardEntries.Length != cardButtons.Length)
                    cardEntries = new CardData[cardButtons.Length];

                for (int i = 0; i < cardButtons.Length; i++)
                {
                    CardData card = (i < availableCards.Count) ? availableCards[i] : null;
                    cardEntries[i] = card;
                    bool show = card != null;
                    if (cardButtons[i] != null)
                    {
                        cardButtons[i].gameObject.SetActive(show);
                        if (show)
                        {
                            float price = Mathf.Max(card.gemCost, 1f);
                            bool canAfford = contributedGems >= price;
                            cardButtons[i].interactable = canAfford;
                        }
                    }
                    if (cardLabels != null && i < cardLabels.Length && cardLabels[i] != null)
                    {
                        if (show)
                        {
                            float price = Mathf.Max(card.gemCost, 1f);
                            cardLabels[i].text = $"{card.displayName} — {price:F0} gems";
                        }
                        else
                        {
                            cardLabels[i].text = string.Empty;
                        }
                    }
                }
            }

            // Chassis section: show unlocked chassis from CardShopSystem / ShipUnlockTable.
            // At home: show all unlocked chassis. At captured planets: only show chassis whose originPlanetId matches the store planet.
            if (chassisButtons != null && currentShip != null && currentHomePlanet != null && currentStorePlanet != null && CardShopSystem.Instance != null)
            {
                int homeLevel = currentHomePlanet.HomePlanetLevel;
                bool isHomeStore = currentStorePlanet is HomePlanet;
                int storePlanetId = currentStorePlanet.PlanetId;
                var unlocked = CardShopSystem.Instance.GetUnlockedChassisForStore(homeLevel, isHomeStore, storePlanetId);

                if (chassisEntries == null || chassisEntries.Length != chassisButtons.Length)
                    chassisEntries = new ShipChassisDefinition[chassisButtons.Length];

                for (int i = 0; i < chassisButtons.Length; i++)
                {
                    ShipChassisDefinition chassis = (i < unlocked.Count) ? unlocked[i] : null;
                    chassisEntries[i] = chassis;
                    bool show = chassis != null;

                    if (chassisButtons[i] != null)
                    {
                        chassisButtons[i].gameObject.SetActive(show);
                        if (show)
                        {
                            // Use minHomePlanetLevel as the notional tier level when computing cost.
                            int tierLevel = Mathf.Max(1, chassis.minHomePlanetLevel);
                            float price = ShipUnlockTable.GetTierCost(tierLevel);
                            bool canAfford = contributedGems >= price;
                            chassisButtons[i].interactable = canAfford;
                        }
                    }

                    if (chassisLabels != null && i < chassisLabels.Length && chassisLabels[i] != null)
                    {
                        if (show)
                        {
                            int tierLevel = Mathf.Max(1, chassis.minHomePlanetLevel);
                            float price = ShipUnlockTable.GetTierCost(tierLevel);
                            string family = string.IsNullOrEmpty(chassis.shipFamily) ? "Ship" : chassis.shipFamily;
                            chassisLabels[i].text = $"{chassis.displayName} ({family} • Tier {tierLevel}) — {price:F0} gems";
                        }
                        else
                        {
                            chassisLabels[i].text = string.Empty;
                        }
                    }
                }
            }
        }

        private void EnsurePanelExists()
        {
            if (storePanel != null && itemButtons != null) return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            if (storePanel == null)
            {
                storePanel = new GameObject("StorePanel");
                storePanel.transform.SetParent(canvas.transform, false);
                var rect = storePanel.AddComponent<RectTransform>();
                // Anchor to far left so center of screen stays clear (same side as orbit panel and ship grid).
                rect.anchorMin = new Vector2(0f, 0.5f);
                rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                float panelWidth = 400f;
                float panelHeight = 520f;
                rect.sizeDelta = new Vector2(panelWidth, panelHeight);
                const float leftMargin = 12f;
                rect.anchoredPosition = new Vector2(panelWidth * 0.5f + leftMargin, 0f);

                var img = storePanel.AddComponent<Image>();
                img.color = new Color(0.08f, 0.09f, 0.16f, 0.97f);

                var itemTypes = (StoreItemType[])System.Enum.GetValues(typeof(StoreItemType));
                itemButtons = new Button[itemTypes.Length];
                itemLabels = new TextMeshProUGUI[itemTypes.Length];

                float y = panelHeight * 0.5f - 24f;

                CreateTMP(storePanel.transform, "Title", "Planet Store", 26, new Vector2(panelWidth * 0.5f, y), new Vector2(panelWidth - 32f, 36));
                y -= 44f;
                gemsText = CreateTMP(storePanel.transform, "Gems", "Your contributed gems: 0", 18, new Vector2(panelWidth * 0.5f, y), new Vector2(panelWidth - 32f, 28));
                y -= 36f;

                // Section: Available Cards
                CreateSectionHeader(storePanel.transform, "CardsHeader", "Available Cards", panelWidth, ref y);
                int maxCards = 6;
                cardButtons = new Button[maxCards];
                cardLabels = new TextMeshProUGUI[maxCards];
                for (int i = 0; i < maxCards; i++)
                {
                    var btn = CreateButton(storePanel.transform, "Card", new Vector2(panelWidth * 0.5f, y), panelWidth - 40f);
                    cardButtons[i] = btn;
                    cardLabels[i] = btn.GetComponentInChildren<TextMeshProUGUI>();
                    int index = i;
                    btn.onClick.AddListener(() => OnBuyCard(index));
                    y -= 38f;
                }
                y -= 12f;

                // Section: Available Ships
                CreateSectionHeader(storePanel.transform, "ShipsHeader", "Available Ships", panelWidth, ref y);
                int maxChassis = 6;
                chassisButtons = new Button[maxChassis];
                chassisLabels = new TextMeshProUGUI[maxChassis];
                for (int i = 0; i < maxChassis; i++)
                {
                    var btn = CreateButton(storePanel.transform, "Chassis", new Vector2(panelWidth * 0.5f, y), panelWidth - 40f);
                    chassisButtons[i] = btn;
                    chassisLabels[i] = btn.GetComponentInChildren<TextMeshProUGUI>();
                    int index = i;
                    btn.onClick.AddListener(() => OnBuyChassis(index));
                    y -= 38f;
                }
                y -= 20f;

                closeButton = CreateButton(storePanel.transform, "Close", new Vector2(panelWidth * 0.5f, y), panelWidth - 40f);
                closeButton.onClick.AddListener(Close);
            }
        }

        private static void CreateSectionHeader(Transform parent, string name, string text, float panelWidth, ref float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(panelWidth * 0.5f, y);
            rect.sizeDelta = new Vector2(panelWidth - 24f, 28f);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.15f, 0.2f, 0.35f, 0.85f);
            y -= 32f;

            var label = new GameObject(name + "Label");
            label.transform.SetParent(go.transform, false);
            var labelRect = label.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            var tmp = label.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 18;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.9f, 0.92f, 1f, 1f);
        }

        private void OnBuyItem(StoreItemType item)
        {
            if (currentShip == null || currentHomePlanet == null || HomePlanetStoreSystem.Instance == null) return;
            var homeNo = currentHomePlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (homeNo == null || !homeNo.IsSpawned) return;
            HomePlanetStoreSystem.Instance.PurchaseItemServerRpc(homeNo.NetworkObjectId, currentShip.NetworkObjectId, item);
            pendingGemsRequest = true;
            HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private void OnBuyCard(int index)
        {
            if (currentShip == null || currentHomePlanet == null || CardShopSystem.Instance == null) return;
            if (cardEntries == null || index < 0 || index >= cardEntries.Length) return;
            CardData card = cardEntries[index];
            if (card == null) return;

            var homeNo = currentHomePlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (homeNo == null || !homeNo.IsSpawned) return;

            CardShopSystem.Instance.PurchaseCardServerRpc(homeNo.NetworkObjectId, currentShip.NetworkObjectId, card.cardId);

            // Refresh contributed gems after purchase.
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null)
                HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private void OnBuyChassis(int index)
        {
            if (currentShip == null || currentHomePlanet == null || CardShopSystem.Instance == null) return;
            if (chassisEntries == null || index < 0 || index >= chassisEntries.Length) return;

            ShipChassisDefinition chassis = chassisEntries[index];
            if (chassis == null) return;

            var homeNo = currentHomePlanet.GetComponent<Unity.Netcode.NetworkObject>();
            if (homeNo == null || !homeNo.IsSpawned) return;

            // Use minHomePlanetLevel as the tier level for pricing in PurchaseChassisServerRpc.
            int tierLevel = Mathf.Max(1, chassis.minHomePlanetLevel);
            CardShopSystem.Instance.PurchaseChassisServerRpc(homeNo.NetworkObjectId, currentShip.NetworkObjectId, chassis.chassisId, tierLevel);

            // Refresh contributed gems after purchase.
            pendingGemsRequest = true;
            if (HomePlanetStoreSystem.Instance != null)
                HomePlanetStoreSystem.Instance.RequestContributedGemsServerRpc();
        }

        private static TextMeshProUGUI CreateTMP(Transform parent, string name, string text, int fontSize, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = sizeDelta;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            return tmp;
        }

        private static Button CreateButton(Transform parent, string label, Vector2 pos, float width = 340f)
        {
            var go = new GameObject("Button");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(width, 34f);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.18f, 0.32f, 0.55f, 0.95f);
            var btn = go.AddComponent<Button>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 4f);
            textRect.offsetMax = new Vector2(-8f, -4f);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 16;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            return btn;
        }
    }
}
