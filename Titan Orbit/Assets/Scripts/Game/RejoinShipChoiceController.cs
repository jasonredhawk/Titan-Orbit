using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen UI for dedicated-server rejoin flow: shows saved ship summary (team, level, HP,
    /// gems, energy) and lets the player resume that ship or abandon it and pick a new team.
    /// Calls TitanOrbitSessionManager.RequestResumeExistingShip / RequestAbandonShipForRejoin.
    /// Shown by session bootstrap when RejoinShipManagementSystem finds a persisted ship for the client.
    /// </summary>
    public class RejoinShipChoiceController : MonoBehaviour
    {
        const float PanelWidth = 560f;

        [SerializeField] GameObject mainMenuPanel;

        GameObject _screenRoot;
        TextMeshProUGUI _statusText;
        TextMeshProUGUI _shipSummaryText;
        bool _choiceInProgress;

        public bool IsVisible => _screenRoot != null && _screenRoot.activeSelf;

        /// <summary>Stores reference to main menu panel for hide/show coordination.</summary>
        public void Configure(GameObject menuPanel) => mainMenuPanel = menuPanel;

        /// <summary>Displays rejoin screen with ghost-serialized ShipState summary from saved ship.</summary>
        public void Show(ShipState shipState)
        {
            EnsureUi();
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);

            _choiceInProgress = false;
            if (_shipSummaryText != null)
            {
                string teamLabel = shipState.Team.ToString();
                _shipSummaryText.text =
                    $"Team {teamLabel}\n" +
                    $"Level {shipState.ShipLevel}  ·  HP {shipState.Health:0}/{shipState.MaxHealth:0}\n" +
                    $"Gems {shipState.CurrentGems:0}/{shipState.GemCapacity:0}  ·  Energy {shipState.CurrentEnergy:0}/{shipState.MaxEnergy:0}";
            }

            if (_statusText != null)
                _statusText.text = "Welcome back. Continue where you left off, or start fresh on a new team.";

            _screenRoot.SetActive(true);
            _screenRoot.transform.SetAsLastSibling();
        }

        public void Hide()
        {
            if (_screenRoot != null)
                _screenRoot.SetActive(false);
            _choiceInProgress = false;
        }

        /// <summary>[NETCODE] Resume RPC — reattach client to persisted ship entity on server.</summary>
        void OnResumeClicked()
        {
            if (_choiceInProgress || TitanOrbitSessionManager.Instance == null)
                return;

            _choiceInProgress = true;
            SetStatus("Resuming your ship...");
            TitanOrbitSessionManager.Instance.RequestResumeExistingShip();
        }

        /// <summary>Abandon saved ship and return to team selection flow.</summary>
        void OnStartFreshClicked()
        {
            if (_choiceInProgress || TitanOrbitSessionManager.Instance == null)
                return;

            _choiceInProgress = true;
            SetStatus("Releasing your saved ship...");
            TitanOrbitSessionManager.Instance.RequestAbandonShipForRejoin();
        }

        void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
        }

        void EnsureUi()
        {
            if (_screenRoot != null)
                return;

            Transform host = ResolveUiHost();
            _screenRoot = new GameObject("RejoinShipScreen", typeof(RectTransform), typeof(Image));
            _screenRoot.transform.SetParent(host, false);
            var screenRt = _screenRoot.GetComponent<RectTransform>();
            screenRt.anchorMin = Vector2.zero;
            screenRt.anchorMax = Vector2.one;
            screenRt.offsetMin = Vector2.zero;
            screenRt.offsetMax = Vector2.zero;
            _screenRoot.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.1f, 0.98f);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            panel.transform.SetParent(_screenRoot.transform, false);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(PanelWidth, 360f);
            panel.GetComponent<Image>().color = new Color(0.08f, 0.12f, 0.2f, 0.98f);
            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 24, 24);
            layout.spacing = 16f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;

            CreateLabel(panel.transform, "RejoinShipTitle", "Welcome back", 28f, FontStyles.Bold);
            _statusText = CreateLabel(panel.transform, "RejoinShipStatus", "", 18f, FontStyles.Normal);
            _shipSummaryText = CreateLabel(panel.transform, "RejoinShipSummary", "", 20f, FontStyles.Normal);

            var resume = CreateButton(panel.transform, "ResumeShipButton", "Continue with my ship", true);
            resume.onClick.AddListener(OnResumeClicked);
            var fresh = CreateButton(panel.transform, "StartFreshShipButton", "Start fresh (pick new team)", false);
            fresh.onClick.AddListener(OnStartFreshClicked);

            _screenRoot.SetActive(false);
        }

        static TextMeshProUGUI CreateLabel(Transform parent, string name, string text, float size, FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.enableWordWrapping = true;
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = size + 12f;
            return label;
        }

        static Button CreateButton(Transform parent, string name, string label, bool primary)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = primary
                ? new Color(0.18f, 0.38f, 0.62f, 0.98f)
                : new Color(0.11f, 0.17f, 0.28f, 0.98f);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 48f;
            le.preferredHeight = 48f;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(8f, 4f);
            textRt.offsetMax = new Vector2(-8f, -4f);
            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            tmp.text = label;
            tmp.fontSize = primary ? 22f : 20f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            return go.GetComponent<Button>();
        }

        Transform ResolveUiHost()
        {
            if (mainMenuPanel != null)
            {
                var canvas = mainMenuPanel.GetComponentInParent<Canvas>();
                if (canvas != null)
                    return canvas.transform;
                if (mainMenuPanel.transform.parent != null)
                    return mainMenuPanel.transform.parent;
            }

            var anyCanvas = Object.FindAnyObjectByType<Canvas>();
            return anyCanvas != null ? anyCanvas.transform : transform;
        }
    }
}
