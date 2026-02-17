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
        private float contributedGems;
        private Button[] itemButtons;
        private TextMeshProUGUI[] itemLabels;
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

        public void Show(Starship ship, HomePlanet homePlanet)
        {
            currentShip = ship;
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

            if (itemButtons == null) return;
            for (int i = 0; i < itemButtons.Length && i < System.Enum.GetValues(typeof(StoreItemType)).Length; i++)
            {
                var item = (StoreItemType)i;
                float price = StoreItemData.GetPrice(item);
                bool canAfford = contributedGems >= price;
                if (itemButtons[i] != null) itemButtons[i].interactable = canAfford && currentShip != null && currentHomePlanet != null;
                if (itemLabels != null && i < itemLabels.Length && itemLabels[i] != null)
                    itemLabels[i].text = $"{StoreItemData.GetDisplayName(item)} — {price:F0} gems";
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
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(380, 420);
                rect.anchoredPosition = new Vector2(0f, 0f);
                var img = storePanel.AddComponent<Image>();
                img.color = new Color(0.1f, 0.12f, 0.2f, 0.97f);

                CreateTMP(storePanel.transform, "Title", "Home Planet Store", 24, new Vector2(0, 195), new Vector2(-20, 32));
                gemsText = CreateTMP(storePanel.transform, "Gems", "Your contributed gems: 0", 20, new Vector2(0, 155), new Vector2(-20, 28));

                var itemTypes = (StoreItemType[])System.Enum.GetValues(typeof(StoreItemType));
                itemButtons = new Button[itemTypes.Length];
                itemLabels = new TextMeshProUGUI[itemTypes.Length];
                for (int i = 0; i < itemTypes.Length; i++)
                {
                    var item = itemTypes[i];
                    string label = $"{StoreItemData.GetDisplayName(item)} — {StoreItemData.GetPrice(item):F0} gems";
                    var btn = CreateButton(storePanel.transform, label, new Vector2(0, 105 - i * 38));
                    itemButtons[i] = btn;
                    itemLabels[i] = btn.GetComponentInChildren<TextMeshProUGUI>();
                    int index = i;
                    btn.onClick.AddListener(() => OnBuyItem((StoreItemType)index));
                }

                closeButton = CreateButton(storePanel.transform, "Close", new Vector2(0, -175));
                closeButton.onClick.AddListener(Close);
            }
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

        private static Button CreateButton(Transform parent, string label, Vector2 pos)
        {
            var go = new GameObject("Button");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(340, 32);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.35f, 0.7f);
            var btn = go.AddComponent<Button>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 18;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            return btn;
        }
    }
}
