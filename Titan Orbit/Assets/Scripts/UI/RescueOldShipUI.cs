using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Core;
using TitanOrbit.Networking;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Shown to returning players before team selection. Offers rescuing their saved ship or starting fresh.
    /// </summary>
    public class RescueOldShipUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI summaryText;
        [SerializeField] private Button rescueButton;
        [SerializeField] private Button startAnewButton;

        private NetworkGameManager.ReturningShipInfo pendingInfo;
        private bool uiBuilt;

        public bool IsVisible => panelRoot != null && panelRoot.activeSelf;

        private void Awake()
        {
            EnsureUiBuilt();
            Hide();
        }

        private void OnEnable()
        {
            if (rescueButton != null)
            {
                rescueButton.onClick.RemoveListener(OnRescueClicked);
                rescueButton.onClick.AddListener(OnRescueClicked);
            }
            if (startAnewButton != null)
            {
                startAnewButton.onClick.RemoveListener(OnStartAnewClicked);
                startAnewButton.onClick.AddListener(OnStartAnewClicked);
            }
        }

        public void Show(NetworkGameManager.ReturningShipInfo info)
        {
            EnsureUiBuilt();
            pendingInfo = info;
            if (panelRoot != null)
                panelRoot.SetActive(true);

            if (titleText != null)
                titleText.text = "RESCUE OLD SHIP";

            if (summaryText != null)
            {
                string teamLabel = TeamLabel(info.Team);
                Color teamColor = TeamManager.GetTeamColor(info.Team);
                string shipName = string.IsNullOrEmpty(info.ChassisDisplayName) ? "Your ship" : info.ChassisDisplayName;
                summaryText.text =
                    $"Welcome back.\n\n" +
                    $"<color=#{ColorUtility.ToHtmlStringRGB(teamColor)}>Team {teamLabel}</color> — " +
                    $"Lv.{Mathf.Max(1, info.ShipLevel)} {shipName}\n" +
                    $"Gems carried: {info.CurrentGems:F0}";
            }

            if (rescueButton != null)
            {
                var label = rescueButton.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                    label.text = $"Rescue ship (Team {TeamLabel(info.Team)})";
            }
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }

        private void OnRescueClicked()
        {
            if (pendingInfo.Team == TeamManager.Team.None)
                return;

            NetworkGameManager.PendingRestoreChoice = NetworkGameManager.ShipRestoreChoice.Rescue;
            Hide();
            NetworkGameManager.RequestTeamFromLocalPlayer(pendingInfo.Team);
        }

        private void OnStartAnewClicked()
        {
            NetworkGameManager.AbandonOldShipFromLocalPlayer();
            Hide();
            var mainMenu = FindFirstObjectByType<MainMenu>(FindObjectsInactive.Include);
            if (mainMenu != null)
                mainMenu.ShowTeamSelectionAfterRescueChoice();
        }

        private static string TeamLabel(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return "A";
                case TeamManager.Team.TeamB: return "B";
                case TeamManager.Team.TeamC: return "C";
                case TeamManager.Team.TeamD: return "D";
                case TeamManager.Team.TeamE: return "E";
                default: return "?";
            }
        }

        private void EnsureUiBuilt()
        {
            if (uiBuilt && panelRoot != null)
                return;

            Transform parent = transform;
            if (panelRoot == null)
            {
                panelRoot = new GameObject("RescueOldShipPanel");
                panelRoot.transform.SetParent(parent, false);

                var rect = panelRoot.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(520f, 340f);

                var bg = panelRoot.AddComponent<Image>();
                bg.color = new Color(0.08f, 0.1f, 0.18f, 0.95f);
            }

            if (titleText == null)
                titleText = CreateLabel(panelRoot.transform, "Title", 32, new Vector2(0.5f, 0.82f), new Vector2(480f, 48f));

            if (summaryText == null)
            {
                summaryText = CreateLabel(panelRoot.transform, "Summary", 22, new Vector2(0.5f, 0.52f), new Vector2(460f, 120f));
                summaryText.alignment = TextAlignmentOptions.Center;
            }

            if (rescueButton == null)
                rescueButton = CreateButton(panelRoot.transform, "RescueButton", "Rescue old ship", new Vector2(0.5f, 0.22f), new Color(0.2f, 0.55f, 0.35f, 1f));

            if (startAnewButton == null)
                startAnewButton = CreateButton(panelRoot.transform, "StartAnewButton", "Abandon ship & start anew", new Vector2(0.5f, 0.08f), new Color(0.35f, 0.38f, 0.48f, 1f));

            uiBuilt = true;
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string name, int fontSize, Vector2 anchorY, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, anchorY.y);
            rect.anchorMax = new Vector2(0.5f, anchorY.y);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = fontSize;
            tmp.color = new Color(0.85f, 0.92f, 1f, 1f);
            tmp.alignment = TextAlignmentOptions.Center;
            return tmp;
        }

        private static Button CreateButton(Transform parent, string name, string label, Vector2 anchorY, Color bgColor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, anchorY.y);
            rect.anchorMax = new Vector2(0.5f, anchorY.y);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(420f, 44f);

            var img = go.AddComponent<Image>();
            img.color = bgColor;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 20;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;

            return btn;
        }
    }
}
