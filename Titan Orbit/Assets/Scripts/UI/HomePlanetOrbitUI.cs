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
    /// UI shown when the player's starship is in orbit. Delegates to OrbitStationUI (combined loadout + orbit + store).
    /// Retains the legacy ship-level upgrade choice popup only.
    /// </summary>
    public class HomePlanetOrbitUI : MonoBehaviour
    {
        private Starship currentShip;
        private Planet currentPlanet;
        private GameObject shipUpgradeChoicePanel;
        private Button[] shipChoiceButtons = new Button[2];
        private TextMeshProUGUI[] shipChoiceLabels = new TextMeshProUGUI[2];

        private Starship _cachedLocalShip;
        private float _lastLocalShipLookupTime = -999f;
        private const float LocalShipLookupInterval = 0.3f;

        /// <summary>Find existing orbit UI or create one (with OrbitStationUI) so the popup appears when orbiting.</summary>
        public static HomePlanetOrbitUI GetOrCreate()
        {
            var existing = Object.FindFirstObjectByType<HomePlanetOrbitUI>();
            if (existing != null) return existing;

            Canvas canvas = null;
            var hud = Object.FindFirstObjectByType<HUDController>();
            if (hud != null) canvas = hud.GetComponentInParent<Canvas>(true);
            if (canvas == null) canvas = Object.FindFirstObjectByType<Canvas>();
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
            uiObj.AddComponent<OrbitStationUI>();
            return uiObj.AddComponent<HomePlanetOrbitUI>();
        }

        private void Awake()
        {
            // Orbit panel and actions are handled by OrbitStationUI on the same GameObject.
        }

        private void Update()
        {
            if (Time.time - _lastLocalShipLookupTime >= LocalShipLookupInterval)
            {
                _lastLocalShipLookupTime = Time.time;
                _cachedLocalShip = null;
                foreach (var ship in Starship.AllStarships)
                {
                    if (ship != null && ship.IsOwner) { _cachedLocalShip = ship; break; }
                }
            }
            Starship localShip = _cachedLocalShip;
            // If we're currently showing the orbit menu (Starship called Show), keep active until Hide() is called.
            if (currentShip != null)
            {
                gameObject.SetActive(true);
                return;
            }
            if (localShip == null || localShip.ShipTeam == TeamManager.Team.None)
            {
                gameObject.SetActive(false);
                return;
            }
            gameObject.SetActive(true);
        }

        public void Show(Starship ship, Planet planet)
        {
            // Avoid redundant work if already showing for this ship+planet.
            if (currentShip == ship && currentPlanet == planet && gameObject.activeSelf)
                return;

            currentShip = ship;
            currentPlanet = planet;
            gameObject.SetActive(true); // Ensure orbit UI GameObject is active so panel can show (e.g. if it was disabled by Update when team was None)
            var orbitStation = GetComponent<OrbitStationUI>();
            if (orbitStation == null) orbitStation = gameObject.AddComponent<OrbitStationUI>();
            orbitStation.Show(ship, planet);
        }

        public void Hide()
        {
            currentShip = null;
            currentPlanet = null;
            var orbitStation = GetComponent<OrbitStationUI>();
            if (orbitStation != null) orbitStation.Hide();
            if (shipUpgradeChoicePanel != null) shipUpgradeChoicePanel.SetActive(false);
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
