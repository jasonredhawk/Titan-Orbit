using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Data;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace TitanOrbit.UI
{
    /// <summary>
    /// UI shown when the player's starship is in orbit around any planet.
    /// Home planets: deposit gems, load/unload people, store (planned). Regular planets: load/unload people.
    /// </summary>
    public class HomePlanetOrbitUI : MonoBehaviour
    {
        [Header("Panel")]
        [SerializeField] private GameObject orbitPanel;
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI planetInfoText;

        [Header("Actions")]
        [SerializeField] private Button depositGemsButton;
        [SerializeField] private TextMeshProUGUI depositGemsLabel;
        [SerializeField] private Button loadPeopleButton;
        [SerializeField] private TextMeshProUGUI loadPeopleLabel;
        [SerializeField] private Button unloadPeopleButton;
        [SerializeField] private TextMeshProUGUI unloadPeopleLabel;
        [SerializeField] private Button storeButton;
        [SerializeField] private TextMeshProUGUI storeLabel;
        [SerializeField] private Button upgradeShipButton;
        [SerializeField] private TextMeshProUGUI upgradeShipLabel;

        private Starship currentShip;
        private Planet currentPlanet;
        private GameObject shipUpgradeChoicePanel;
        private Button[] shipChoiceButtons = new Button[2];
        private TextMeshProUGUI[] shipChoiceLabels = new TextMeshProUGUI[2];

        /// <summary>Find existing orbit UI or create one so the popup always appears when orbiting.</summary>
        public static HomePlanetOrbitUI GetOrCreate()
        {
            var existing = Object.FindFirstObjectByType<HomePlanetOrbitUI>();
            if (existing != null) return existing;

            Canvas canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                GameObject canvasObj = new GameObject("Canvas");
                canvas = canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvasObj.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvasObj.AddComponent<GraphicRaycaster>();
            }

            // Ensure EventSystem exists so UI buttons are clickable
            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                GameObject esObj = new GameObject("EventSystem");
#if ENABLE_INPUT_SYSTEM
                esObj.AddComponent<EventSystem>();
                esObj.AddComponent<InputSystemUIInputModule>();
#else
                esObj.AddComponent<EventSystem>();
                esObj.AddComponent<StandaloneInputModule>();
#endif
            }

            GameObject uiObj = new GameObject("OrbitUI");
            uiObj.transform.SetParent(canvas.transform, false);
            return uiObj.AddComponent<HomePlanetOrbitUI>();
        }

        private void Awake()
        {
            if (orbitPanel != null) orbitPanel.SetActive(false);
            if (depositGemsButton != null) depositGemsButton.onClick.AddListener(OnDepositGems);
            if (loadPeopleButton != null) loadPeopleButton.onClick.AddListener(OnLoadPeople);
            if (unloadPeopleButton != null) unloadPeopleButton.onClick.AddListener(OnUnloadPeople);
            if (storeButton != null)
            {
                storeButton.onClick.AddListener(OnStore);
                storeButton.interactable = true;
            }
            if (upgradeShipButton != null) upgradeShipButton.onClick.AddListener(OnUpgradeShip);
        }

        private void Update()
        {
            if (orbitPanel != null && orbitPanel.activeSelf && currentShip != null && currentPlanet != null)
                RefreshLabels();
        }

        public void Show(Starship ship, Planet planet)
        {
            currentShip = ship;
            currentPlanet = planet;
            EnsurePanelExists();
            if (orbitPanel != null) orbitPanel.SetActive(true);
            RefreshLabels();
        }

        public void Hide()
        {
            currentShip = null;
            currentPlanet = null;
            if (orbitPanel != null) orbitPanel.SetActive(false);
            if (shipUpgradeChoicePanel != null) shipUpgradeChoicePanel.SetActive(false);
        }

        private void EnsurePanelExists()
        {
            if (orbitPanel != null) return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            orbitPanel = new GameObject("OrbitPanel");
            orbitPanel.transform.SetParent(canvas.transform, false);
            var rect = orbitPanel.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(320, 280);
            rect.anchoredPosition = new Vector2(-220f, 60f); // Left and up so it doesn't cover the starship
            var img = orbitPanel.AddComponent<Image>();
            img.color = new Color(0.12f, 0.12f, 0.2f, 0.95f);

            titleText = CreateTMP(orbitPanel.transform, "Title", "In Orbit", 28, new Vector2(0, -20), new Vector2(-30, 36));
            planetInfoText = CreateTMP(orbitPanel.transform, "PlanetInfo", "Pop: 0/100", 16, new Vector2(0, -58), new Vector2(-30, 24));

            loadPeopleButton = CreateButton(orbitPanel.transform, "Load People", new Vector2(0, -90));
            loadPeopleLabel = loadPeopleButton.GetComponentInChildren<TextMeshProUGUI>();
            unloadPeopleButton = CreateButton(orbitPanel.transform, "Unload People", new Vector2(0, -132));
            unloadPeopleLabel = unloadPeopleButton.GetComponentInChildren<TextMeshProUGUI>();
            depositGemsButton = CreateButton(orbitPanel.transform, "Deposit Gems", new Vector2(0, -174));
            depositGemsLabel = depositGemsButton.GetComponentInChildren<TextMeshProUGUI>();
            upgradeShipButton = CreateButton(orbitPanel.transform, "Upgrade Ship", new Vector2(0, -216));
            upgradeShipLabel = upgradeShipButton.GetComponentInChildren<TextMeshProUGUI>();
            storeButton = CreateButton(orbitPanel.transform, "Store", new Vector2(0, -258));
            storeLabel = storeButton.GetComponentInChildren<TextMeshProUGUI>();
            storeButton.interactable = true;

            loadPeopleButton.onClick.AddListener(OnLoadPeople);
            unloadPeopleButton.onClick.AddListener(OnUnloadPeople);
            depositGemsButton.onClick.AddListener(OnDepositGems);
            upgradeShipButton.onClick.AddListener(OnUpgradeShip);
            storeButton.onClick.AddListener(OnStore);
        }

        private void EnsureShipUpgradeChoicePanelExists()
        {
            if (shipUpgradeChoicePanel != null) return;
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            shipUpgradeChoicePanel = new GameObject("ShipUpgradeChoicePanel");
            shipUpgradeChoicePanel.transform.SetParent(canvas.transform, false);
            var rect = shipUpgradeChoicePanel.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var img = shipUpgradeChoicePanel.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.6f);

            var center = new GameObject("Center");
            center.transform.SetParent(shipUpgradeChoicePanel.transform, false);
            var centerRect = center.AddComponent<RectTransform>();
            centerRect.anchorMin = new Vector2(0.5f, 0.5f);
            centerRect.anchorMax = new Vector2(0.5f, 0.5f);
            centerRect.sizeDelta = new Vector2(340, 165);
            centerRect.anchoredPosition = Vector2.zero;
            var centerImg = center.AddComponent<Image>();
            centerImg.color = new Color(0.14f, 0.14f, 0.22f, 0.98f);

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(center.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.pivot = new Vector2(0.5f, 1);
            titleRect.anchoredPosition = new Vector2(0, -16);
            titleRect.sizeDelta = new Vector2(-20, 32);
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "Choose next ship";
            titleTmp.fontSize = 22;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.color = Color.white;

            shipChoiceLabels = new TextMeshProUGUI[2];
            for (int i = 0; i < 2; i++)
            {
                var btn = CreateButton(center.transform, "Ship " + (i + 1), new Vector2(0, -56 - i * 48));
                shipChoiceButtons[i] = btn;
                shipChoiceLabels[i] = btn.GetComponentInChildren<TextMeshProUGUI>();
                int index = i;
                btn.onClick.AddListener(() => OnShipChoiceClicked(index));
            }

            shipUpgradeChoicePanel.SetActive(false);
        }

        private static TextMeshProUGUI CreateTMP(Transform parent, string name, string text, int fontSize, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
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
            rect.anchorMin = new Vector2(0.5f, 1);
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(260, 36);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.4f, 0.8f);
            var btn = go.AddComponent<Button>();

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 24;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            return btn;
        }

        private void RefreshLabels()
        {
            if (currentShip == null || currentPlanet == null) return;

            bool isHomePlanet = currentPlanet is HomePlanet;
            bool sameTeam = currentPlanet.TeamOwnership != TeamManager.Team.None && currentPlanet.TeamOwnership == currentShip.ShipTeam;
            if (currentPlanet is HomePlanet hpTeam)
                sameTeam = hpTeam.AssignedTeam != TeamManager.Team.None && hpTeam.AssignedTeam == currentShip.ShipTeam;

            // Full menu only for home planet or captured (same-team) planet; neutral/enemy = unload only
            bool isFriendly = isHomePlanet && sameTeam || (!isHomePlanet && sameTeam);

            if (titleText != null)
                titleText.text = isHomePlanet ? "Home Planet — In Orbit" : "Planet — In Orbit";
            if (planetInfoText != null)
            {
                if (currentPlanet is HomePlanet hp)
                    planetInfoText.text = $"Level {hp.HomePlanetLevel} | Gems: {hp.CurrentGems:F0}/{hp.MaxGems:F0} | Pop: {currentPlanet.CurrentPopulation:F0}/{currentPlanet.MaxPopulation:F0}";
                else
                    planetInfoText.text = $"Pop: {currentPlanet.CurrentPopulation:F0}/{currentPlanet.MaxPopulation:F0}";
            }

            float shipGems = currentShip.CurrentGems;
            float shipPeople = currentShip.CurrentPeople;
            float shipPeopleCap = currentShip.PeopleCapacity;
            float planetPop = currentPlanet.CurrentPopulation;
            float planetMaxPop = currentPlanet.MaxPopulation;

            // Deposit: same team only (home or regular planets); gradual at shipLevel gems per 0.5s (same pattern as load/unload people)
            bool isDepositing = currentShip.WantToDepositGems;
            int gemsPerHalfSec = Mathf.Max(1, currentShip.ShipLevel);
            if (depositGemsButton != null)
            {
                depositGemsButton.gameObject.SetActive(isFriendly && sameTeam);
                if (depositGemsLabel != null)
                    depositGemsLabel.text = isDepositing ? $"Stop Depositing" : $"Deposit Gems ({gemsPerHalfSec}/0.5s)";
                depositGemsButton.interactable = sameTeam && (isDepositing || shipGems > 0);
            }

            // Load: friendly only (home or captured); continuous at shipLevel/sec. Disable when ship at max or planet empty.
            const float peopleEpsilon = 0.001f;
            bool shipHasRoom = shipPeople < shipPeopleCap - peopleEpsilon;
            bool planetHasPeople = planetPop > peopleEpsilon;
            bool isLoading = currentShip.WantToLoadPeople;
            if (loadPeopleButton != null)
            {
                loadPeopleButton.gameObject.SetActive(isFriendly);
                int rate = Mathf.Max(1, currentShip.ShipLevel);
                if (loadPeopleLabel != null)
                    loadPeopleLabel.text = isLoading ? $"Stop Loading" : $"Load People ({rate}/s)";
                // Enabled when friendly and either: currently loading (so can stop), or can load (planet has people and ship has room)
                loadPeopleButton.interactable = isFriendly && (isLoading || (planetHasPeople && shipHasRoom));
            }

            // Unload: always; to enemy/neutral decreases their population (capture). Disable when ship empty or (friendly) planet full.
            bool shipHasPeople = shipPeople > peopleEpsilon;
            bool planetHasRoom = !isFriendly || planetPop < planetMaxPop - peopleEpsilon;
            bool isUnloading = currentShip.WantToUnloadPeople;
            if (unloadPeopleButton != null)
            {
                unloadPeopleButton.gameObject.SetActive(true);
                int rate = Mathf.Max(1, currentShip.ShipLevel);
                if (unloadPeopleLabel != null)
                    unloadPeopleLabel.text = isUnloading ? $"Stop Unloading" : $"Unload People ({rate}/s)";
                // Enabled when ship has people and either: currently unloading (so can stop), or can unload (planet has room if friendly)
                unloadPeopleButton.interactable = shipHasPeople && (isUnloading || planetHasRoom);
            }

            // Upgrade Ship: home planet, same team, full gem capacity, and home planet level allows next ship level
            bool canUpgradeShip = isHomePlanet && sameTeam && currentShip.ShipLevel < 6
                && UpgradeSystem.Instance != null && UpgradeSystem.Instance.CanUpgradeStarshipLevel(currentShip);
            if (upgradeShipButton != null)
            {
                upgradeShipButton.gameObject.SetActive(isFriendly && isHomePlanet);
                upgradeShipButton.interactable = canUpgradeShip;
            }
            if (upgradeShipLabel != null)
            {
                int next = currentShip.ShipLevel + 1;
                upgradeShipLabel.text = canUpgradeShip ? $"Upgrade Ship (Lv{currentShip.ShipLevel} → Lv{next})" : "Upgrade Ship (full gems + planet level)";
            }

            // Store: home only
            if (storeButton != null)
                storeButton.gameObject.SetActive(isFriendly && isHomePlanet);
            if (storeLabel != null)
                storeLabel.text = "Store";
        }

        private void OnDepositGems()
        {
            if (currentShip == null) return;
            // Toggle continuous deposit: shipLevel gems per 0.5s (handled on server)
            bool currentlyDepositing = currentShip.WantToDepositGems;
            currentShip.SetWantToDepositGemsServerRpc(!currentlyDepositing);
        }

        private void OnLoadPeople()
        {
            if (currentShip == null) return;
            // Toggle continuous load: 1 per second per ship level (handled on server)
            bool currentlyLoading = currentShip.WantToLoadPeople;
            currentShip.SetWantToLoadPeopleServerRpc(!currentlyLoading);
            if (!currentlyLoading) currentShip.SetWantToUnloadPeopleServerRpc(false);
        }

        private void OnUnloadPeople()
        {
            if (currentShip == null) return;
            // Toggle continuous unload; to enemy/neutral this decreases their population (capture)
            bool currentlyUnloading = currentShip.WantToUnloadPeople;
            currentShip.SetWantToUnloadPeopleServerRpc(!currentlyUnloading);
            if (!currentlyUnloading) currentShip.SetWantToLoadPeopleServerRpc(false);
        }

        private void OnStore()
        {
            if (currentShip == null || currentPlanet == null || !(currentPlanet is HomePlanet home)) return;
            var storeUI = Object.FindFirstObjectByType<HomePlanetStoreUI>();
            if (storeUI == null)
            {
                Canvas canvas = GetComponentInParent<Canvas>();
                if (canvas == null) canvas = Object.FindFirstObjectByType<Canvas>();
                if (canvas != null)
                {
                    var go = new GameObject("HomePlanetStoreUI");
                    go.transform.SetParent(canvas.transform, false);
                    storeUI = go.AddComponent<HomePlanetStoreUI>();
                }
            }
            if (storeUI != null) storeUI.Show(currentShip, home);
        }

        /// <summary>Show ship-level upgrade choice (2 options). Call from anywhere when player can upgrade; does not require being in orbit.</summary>
        public void ShowShipUpgradeChoice(Starship ship)
        {
            if (ship == null || UpgradeSystem.Instance == null || UpgradeSystem.Instance.UpgradeTree == null) return;
            currentShip = ship;
            EnsureShipUpgradeChoicePanelExists();
            var available = UpgradeSystem.Instance.UpgradeTree.GetAvailableUpgrades(currentShip.ShipLevel, currentShip.BranchIndex);
            for (int i = 0; i < 2; i++)
            {
                bool show = i < available.Count;
                if (shipChoiceButtons[i] != null)
                {
                    shipChoiceButtons[i].gameObject.SetActive(show);
                    if (show && shipChoiceLabels[i] != null)
                        shipChoiceLabels[i].text = available[i].shipName ?? available[i].focusType.ToString();
                }
            }
            shipUpgradeChoicePanel.SetActive(true);
        }

        private void OnUpgradeShip()
        {
            if (currentShip != null) ShowShipUpgradeChoice(currentShip);
        }

        private void OnShipChoiceClicked(int index)
        {
            if (currentShip == null || UpgradeSystem.Instance == null) return;
            var tree = UpgradeSystem.Instance.UpgradeTree;
            if (tree == null) return;
            var available = tree.GetAvailableUpgrades(currentShip.ShipLevel, currentShip.BranchIndex);
            if (index < 0 || index >= available.Count) return;

            int nextLevel = currentShip.ShipLevel + 1;
            var node = available[index];
            UpgradeSystem.Instance.UpgradeShipServerRpc(currentShip.NetworkObjectId, nextLevel, node.focusType, index);
            if (shipUpgradeChoicePanel != null) shipUpgradeChoicePanel.SetActive(false);
        }
    }
}
