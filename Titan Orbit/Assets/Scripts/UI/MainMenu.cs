using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using TitanOrbit.Networking;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Main menu for lobby creation/joining and team selection
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private GameObject mainMenuPanel;
        [SerializeField] private GameObject lobbyPanel;
        [SerializeField] private GameObject teamSelectionPanel;
        [SerializeField] private LoadingScreenController loadingScreenController;
        [SerializeField] private Button playButton;
        [SerializeField] private Button hostOnlineButton;
        [SerializeField] private Button joinOnlineButton;
        [SerializeField] private Button teamAButton;
        [SerializeField] private Button teamBButton;
        [SerializeField] private Button teamCButton;
        [SerializeField] private Button teamDButton;
        [SerializeField] private Button teamEButton;
        [SerializeField] private TextMeshProUGUI teamALabel;
        [SerializeField] private TextMeshProUGUI teamBLabel;
        [SerializeField] private TextMeshProUGUI teamCLabel;
        [SerializeField] private TextMeshProUGUI teamDLabel;
        [SerializeField] private TextMeshProUGUI teamELabel;
        [SerializeField] private TMP_InputField joinCodeInputField;
        [SerializeField] private TextMeshProUGUI joinCodeDisplayText;
        [SerializeField] private TMP_InputField serverAddressInput;
        [SerializeField] private TextMeshProUGUI playerCountText;
        [SerializeField] private TextMeshProUGUI teamStatusText;
        [SerializeField] private TextMeshProUGUI roomNameText;
        [SerializeField] private TMP_InputField playerNameInputField;
        [SerializeField] private Button browseLobbiesButton;
        [SerializeField] private Button refreshLobbiesButton;
        [SerializeField] private Button joinSelectedLobbyButton;
        [SerializeField] private Transform lobbyListContainer;
        [SerializeField] private GameObject lobbyListRowPrefab;
        [SerializeField] private TextMeshProUGUI lobbyBrowserStatusText;
        [SerializeField] private bool latestOnlyFilter = false;

        private readonly List<NetworkGameManager.LobbySummary> cachedLobbySummaries = new List<NetworkGameManager.LobbySummary>();
        private readonly List<Button> lobbyRowButtons = new List<Button>();
        private readonly List<Image> lobbyRowBackgrounds = new List<Image>();
        private string selectedLobbyId;
        private int selectedLobbyRowIndex = -1;
        private GameObject lobbyBrowserRoot;
        private Vector2 _lastMainMenuPanelSize = Vector2.negativeInfinity;
        private string pendingTeamJoinError;
        /// <summary>When <see cref="ShowLobby"/> runs without a loading screen, team panel is shown only after Netcode is in a client/host session.</summary>
        private bool deferTeamPanelUntilNetworkReady;

        private void Start()
        {
            EnsureRuntimeLobbyBrowserUI();
            HideMainMenuPlayButton();

            if (hostOnlineButton != null)
                hostOnlineButton.onClick.AddListener(OnHostOnlineClicked);

            if (joinOnlineButton != null)
            {
                joinOnlineButton.onClick.AddListener(OnJoinOnlineClicked);
            }

            WireLobbyBrowserListeners();

            NetworkGameManager.OnTeamChosen += OnTeamChosen;
            NetworkGameManager.OnTeamChoiceFailed += OnTeamChoiceFailed;

            if (teamAButton != null) teamAButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamA));
            if (teamBButton != null) teamBButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamB));
            if (teamCButton != null) teamCButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamC));
            if (teamDButton != null) teamDButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamD));
            if (teamEButton != null) teamEButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamE));

            const string playerNameKey = "TitanOrbit_PlayerName";
            if (playerNameInputField != null)
            {
                playerNameInputField.text = PlayerPrefs.GetString(playerNameKey, "");
                playerNameInputField.onEndEdit.AddListener(s => { PlayerPrefs.SetString(playerNameKey, s ?? ""); PlayerPrefs.Save(); });
                playerNameInputField.textComponent.fontSize = 60;
                playerNameInputField.textComponent.alignment = TMPro.TextAlignmentOptions.Center;
                if (playerNameInputField.placeholder as TextMeshProUGUI != null)
                {
                    (playerNameInputField.placeholder as TextMeshProUGUI).fontSize = 36;
                    (playerNameInputField.placeholder as TextMeshProUGUI).alignment = TMPro.TextAlignmentOptions.Center;
                }
            }

            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = false;
            SetLobbyBrowserStatus("Select a lobby to join.");

            LayoutMainMenuActionStack();
            if (mainMenuPanel != null)
            {
                var pr = mainMenuPanel.GetComponent<RectTransform>();
                if (pr != null)
                    _lastMainMenuPanelSize = pr.rect.size;
            }
            _ = RefreshLobbyListAsync();
        }

        private void OnDestroy()
        {
            NetworkGameManager.OnTeamChosen -= OnTeamChosen;
            NetworkGameManager.OnTeamChoiceFailed -= OnTeamChoiceFailed;
        }

        private void OnTeamChoiceFailed(string message)
        {
            pendingTeamJoinError = message ?? "";
            Debug.LogWarning("[MainMenu] " + message);
        }

        private void OnTeamChosen(Core.TeamManager.Team team)
        {
            pendingTeamJoinError = null;
            deferTeamPanelUntilNetworkReady = false;
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
            if (teamSelectionPanel != null) teamSelectionPanel.SetActive(false);
        }

        private void LateUpdate()
        {
            if (mainMenuPanel == null || !mainMenuPanel.activeSelf || lobbyBrowserRoot == null)
                return;
            var pr = mainMenuPanel.GetComponent<RectTransform>();
            if (pr == null)
                return;
            Vector2 sz = pr.rect.size;
            if (sz.x > 1f && sz.y > 1f && (sz - _lastMainMenuPanelSize).sqrMagnitude > 4f)
            {
                _lastMainMenuPanelSize = sz;
                LayoutMainMenuActionStack();
            }
        }

        private void Update()
        {
            if (lobbyPanel != null && lobbyPanel.activeSelf)
            {
                if (deferTeamPanelUntilNetworkReady && teamSelectionPanel != null)
                {
                    var nm = NetworkManager.Singleton;
                    if (nm != null && (nm.IsClient || nm.IsServer))
                    {
                        teamSelectionPanel.SetActive(true);
                        deferTeamPanelUntilNetworkReady = false;
                    }
                }
                UpdateLobbyInfo();
                if (teamSelectionPanel != null && teamSelectionPanel.GetComponent<TeamSelectionUI>() != null)
                    ; // TeamSelectionUI refreshes itself
                else
                    RefreshTeamSelectionUI();
            }
        }

        private void RefreshTeamSelectionUI()
        {
            if (Core.TeamManager.Instance == null) return;
            int max = Core.TeamManager.Instance.MaxPlayersPerTeam;
            int active = Core.TeamManager.Instance.GetEffectiveTeamCountForUI();
            int a = Core.TeamManager.Instance.TeamACount;
            int b = Core.TeamManager.Instance.TeamBCount;
            int c = Core.TeamManager.Instance.TeamCCount;
            int d = Core.TeamManager.Instance.TeamDCount;
            int e = Core.TeamManager.Instance.TeamECount;

            int minCount = int.MaxValue;
            for (int i = 0; i < active; i++)
            {
                Core.TeamManager.Team t = (Core.TeamManager.Team)(i + 1);
                minCount = Mathf.Min(minCount, Core.TeamManager.Instance.GetTeamPlayerCount(t));
            }

            ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
            bool localHasNoTeam = Core.TeamManager.Instance.GetPlayerTeam(localId) == Core.TeamManager.Team.None;

            void Row(int ord, TextMeshProUGUI label, Button btn, int count)
            {
                bool inMatch = ord <= active;
                if (label != null)
                {
                    label.gameObject.SetActive(inMatch);
                    if (inMatch) label.text = $"Team {(char)('A' + ord - 1)} ({count}/{max})";
                }
                if (btn != null)
                {
                    btn.gameObject.SetActive(inMatch);
                    if (inMatch) btn.interactable = count < max && (localHasNoTeam || count <= minCount + 1);
                }
            }

            Row(1, teamALabel, teamAButton, a);
            Row(2, teamBLabel, teamBButton, b);
            Row(3, teamCLabel, teamCButton, c);
            Row(4, teamDLabel, teamDButton, d);
            Row(5, teamELabel, teamEButton, e);
        }

        private void OnTeamClicked(Core.TeamManager.Team team)
        {
            NetworkGameManager.RequestTeamFromLocalPlayer(team);
        }

        private async void OnHostOnlineClicked()
        {
            if (NetworkGameManager.Instance == null) return;
            if (hostOnlineButton != null) hostOnlineButton.interactable = false;
            try
            {
                string pname = playerNameInputField != null ? (playerNameInputField.text ?? "").Trim() : "";
                string lobbyName = string.IsNullOrEmpty(pname) ? null : pname + "'s game";
                // Must run before StartHost — PlayerDisplayNames reads LocalPlayerDisplayName on network spawn (same frame as StartHost).
                if (!string.IsNullOrEmpty(pname))
                {
                    PlayerPrefs.SetString("TitanOrbit_PlayerName", pname);
                    PlayerPrefs.Save();
                }
                NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(pname)
                    ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                    : pname;

                string joinCode = await NetworkGameManager.Instance.StartHostWithRelayAsync(lobbyName);
                if (!string.IsNullOrEmpty(joinCode))
                {
                    if (joinCodeDisplayText != null)
                    {
                        joinCodeDisplayText.gameObject.SetActive(true);
                        joinCodeDisplayText.text = "Your match appears in Open matches below.\nRelay code: " + joinCode;
                    }
                    else
                    {
                        Debug.Log("Host started. Listed in lobby browser. Relay code: " + joinCode);
                    }
                    ShowLobby();
                }
                else
                {
                    Debug.LogError("Failed to start host with Relay. Check console and Unity Services setup.");
                }
            }
            finally
            {
                if (hostOnlineButton != null) hostOnlineButton.interactable = true;
            }
        }

        private async void OnJoinOnlineClicked()
        {
            if (NetworkGameManager.Instance == null) return;
            string code = joinCodeInputField != null ? joinCodeInputField.text : (serverAddressInput != null ? serverAddressInput.text : null);
            if (string.IsNullOrWhiteSpace(code))
            {
                Debug.LogWarning("Enter a join code first (or use the server address field).");
                return;
            }
            if (joinOnlineButton != null) joinOnlineButton.interactable = false;
            try
            {
                bool ok = await NetworkGameManager.Instance.StartClientWithRelayAsync(code);
                if (ok)
                    ShowLobby();
                else
                    Debug.LogError("Failed to join with Relay. Check the join code and connection.");
            }
            finally
            {
                if (joinOnlineButton != null) joinOnlineButton.interactable = true;
            }
        }

        private async void OnRefreshLobbiesClicked()
        {
            await RefreshLobbyListAsync();
        }

        private async void OnJoinSelectedLobbyClicked()
        {
            if (NetworkGameManager.Instance == null)
                return;
            if (string.IsNullOrWhiteSpace(selectedLobbyId))
            {
                SetLobbyBrowserStatus("Select a lobby first.");
                return;
            }

            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = false;

            try
            {
                SetLobbyBrowserStatus("Joining selected lobby...");
                bool ok = await NetworkGameManager.Instance.JoinLobbyByIdAsync(selectedLobbyId);
                if (ok)
                {
                    SetLobbyBrowserStatus("Connected.");
                    ShowLobby();
                }
                else
                {
                    SetLobbyBrowserStatus("Join failed. Try refreshing the list.");
                }
            }
            finally
            {
                if (joinSelectedLobbyButton != null)
                    joinSelectedLobbyButton.interactable = !string.IsNullOrWhiteSpace(selectedLobbyId);
            }
        }

        private async Task RefreshLobbyListAsync()
        {
            if (NetworkGameManager.Instance == null)
                return;

            SetLobbyBrowserStatus("Loading lobbies...");
            if (refreshLobbiesButton != null)
                refreshLobbiesButton.interactable = false;
            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = false;

            try
            {
                selectedLobbyId = null;
                selectedLobbyRowIndex = -1;
                cachedLobbySummaries.Clear();
                cachedLobbySummaries.AddRange(await NetworkGameManager.Instance.QueryOpenLobbiesAsync(latestOnlyFilter, 40));
                RenderLobbyList();
            }
            finally
            {
                if (refreshLobbiesButton != null)
                    refreshLobbiesButton.interactable = true;
            }
        }

        private void RenderLobbyList()
        {
            ClearLobbyListRows();

            if (cachedLobbySummaries.Count == 0)
            {
                SetLobbyBrowserStatus("No open lobbies found.");
                return;
            }

            for (int i = 0; i < cachedLobbySummaries.Count; i++)
            {
                var summary = cachedLobbySummaries[i];
                if (lobbyListRowPrefab == null || lobbyListContainer == null)
                    continue;

                GameObject row = Instantiate(lobbyListRowPrefab, lobbyListContainer);
                row.SetActive(true);
                var button = row.GetComponent<Button>();
                var label = row.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                {
                    string latestTag = summary.IsLatest ? "  ·  Latest" : "";
                    label.text = $"<b>{summary.Name}</b>{latestTag}\n<size=85%><color=#9ec4e8>{summary.CurrentPlayers} / {summary.MaxPlayers} players</color></size>";
                }
                if (button != null)
                {
                    int capturedIndex = i;
                    button.onClick.AddListener(() => OnLobbyRowSelected(capturedIndex));
                    lobbyRowButtons.Add(button);
                    lobbyRowBackgrounds.Add(button.GetComponent<Image>());
                }
            }

            SetLobbyBrowserStatus("Select a lobby to join.");
        }

        private void OnLobbyRowSelected(int index)
        {
            if (index < 0 || index >= cachedLobbySummaries.Count)
                return;
            selectedLobbyId = cachedLobbySummaries[index].LobbyId;
            selectedLobbyRowIndex = index;
            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = true;
            SetLobbyBrowserStatus($"Selected: {cachedLobbySummaries[index].Name}");
            ApplyLobbyRowSelectionVisuals();
        }

        private void ApplyLobbyRowSelectionVisuals()
        {
            Color normal = new Color(0.11f, 0.17f, 0.28f, 0.98f);
            Color selected = new Color(0.18f, 0.38f, 0.62f, 0.98f);
            for (int i = 0; i < lobbyRowBackgrounds.Count; i++)
            {
                if (lobbyRowBackgrounds[i] == null)
                    continue;
                lobbyRowBackgrounds[i].color = i == selectedLobbyRowIndex ? selected : normal;
            }
        }

        private void ClearLobbyListRows()
        {
            lobbyRowButtons.Clear();
            lobbyRowBackgrounds.Clear();
            if (lobbyListContainer == null)
                return;
            for (int i = lobbyListContainer.childCount - 1; i >= 0; i--)
            {
                var child = lobbyListContainer.GetChild(i);
                if (child == null)
                    continue;
                Destroy(child.gameObject);
            }
        }

        private void SetLobbyBrowserStatus(string text)
        {
            if (lobbyBrowserStatusText != null)
                lobbyBrowserStatusText.text = text;
        }

        private void HideMainMenuPlayButton()
        {
            if (playButton != null)
                playButton.gameObject.SetActive(false);
        }

        private void EnsureRuntimeLobbyBrowserUI()
        {
            if (mainMenuPanel == null)
                return;

            var mainRect = mainMenuPanel.GetComponent<RectTransform>();
            if (mainRect == null)
                return;

            EnsureEventSystemExists();

            // Relay + Unity Lobby listing; WebGL uses WSS (see NetworkGameManager.ConfigureUnityTransportRelay).
            if (hostOnlineButton == null)
            {
                hostOnlineButton = CreateMenuButton("CreateMatchButton", "Create match", new Vector2(0f, -48f), new Vector2(320f, 52f), mainRect, isPrimary: true);
                if (playerNameInputField != null)
                    hostOnlineButton.transform.SetSiblingIndex(playerNameInputField.transform.GetSiblingIndex() + 1);
            }

            if (browseLobbiesButton != null)
                browseLobbiesButton.gameObject.SetActive(false);

            if (lobbyBrowserRoot == null)
                BuildLobbyBrowserPanel(mainRect);

            if (lobbyBrowserRoot != null)
                lobbyBrowserRoot.SetActive(true);
        }

        /// <summary>
        /// Top-justifies title (if present), player name label, input, and Create match; Open matches fills space below (fixed max width, centered).
        /// </summary>
        private void LayoutMainMenuActionStack()
        {
            if (mainMenuPanel == null)
                return;

            var mainRect = mainMenuPanel.GetComponent<RectTransform>();
            float panelW = mainRect != null && mainRect.rect.width > 1f ? mainRect.rect.width : 1920f;
            float contentW = Mathf.Max(280f, panelW - 64f);

            const float topPadding = 24f;
            const float bottomPadding = 28f;
            const float gapAfterTitle = 18f;
            const float gapLabelToInput = 14f;
            const float gapInputToCreate = 16f;
            const float gapBeforeLobby = 22f;

            float y = topPadding;

            void PlaceTopDown(RectTransform rt, float height, float width)
            {
                if (rt == null)
                    return;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(width, height);
                rt.anchoredPosition = new Vector2(0f, -y);
                y += height;
            }

            Transform titleTf = mainMenuPanel.transform.Find("Title");
            var titleRt = titleTf != null ? titleTf.GetComponent<RectTransform>() : null;
            if (titleRt != null)
            {
                float th = titleRt.sizeDelta.y > 1f ? titleRt.sizeDelta.y : 88f;
                float tw = Mathf.Min(800f, contentW);
                PlaceTopDown(titleRt, th, tw);
                y += gapAfterTitle;
            }

            Transform labelTf = mainMenuPanel.transform.Find("PlayerNameLabel");
            var labelRt = labelTf != null ? labelTf.GetComponent<RectTransform>() : null;
            if (labelRt != null)
            {
                float lh = labelRt.sizeDelta.y > 1f ? labelRt.sizeDelta.y : 28f;
                PlaceTopDown(labelRt, lh, Mathf.Min(400f, contentW));
                y += gapLabelToInput;
            }

            if (playerNameInputField != null)
            {
                var inputRt = playerNameInputField.GetComponent<RectTransform>();
                if (inputRt != null)
                {
                    float ih = inputRt.sizeDelta.y > 1f ? inputRt.sizeDelta.y : 72f;
                    float iw = inputRt.sizeDelta.x > 1f ? inputRt.sizeDelta.x : Mathf.Min(440f, contentW);
                    PlaceTopDown(inputRt, ih, iw);
                    y += gapInputToCreate;
                }
            }

            if (hostOnlineButton != null)
            {
                var hostRt = hostOnlineButton.GetComponent<RectTransform>();
                if (hostRt != null)
                {
                    float ch = hostRt.sizeDelta.y > 1f ? hostRt.sizeDelta.y : 52f;
                    float cw = hostRt.sizeDelta.x > 1f ? hostRt.sizeDelta.x : Mathf.Min(360f, contentW);
                    PlaceTopDown(hostRt, ch, cw);
                }
            }

            y += gapBeforeLobby;
            ApplyLobbyBrowserStretch(mainRect, y, bottomPadding);
            if (lobbyBrowserRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(lobbyBrowserRoot.GetComponent<RectTransform>());
        }

        /// <summary>
        /// Vertically stretches the Open matches card between the header and bottom inset; horizontally uses a fixed max width (centered), like the earlier layout.
        /// </summary>
        private void ApplyLobbyBrowserStretch(RectTransform mainMenuRect, float topInsetPixels, float bottomPadding)
        {
            if (lobbyBrowserRoot == null || mainMenuRect == null)
                return;

            float panelW = mainMenuRect.rect.width > 1f ? mainMenuRect.rect.width : 1920f;
            const float maxLobbyWidth = 960f;
            const float minHorizontalPad = 48f;
            float fixedW = Mathf.Min(maxLobbyWidth, Mathf.Max(280f, panelW - minHorizontalPad));
            float sideMargin = Mathf.Max(0f, (panelW - fixedW) * 0.5f);

            var rt = lobbyBrowserRoot.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(sideMargin, bottomPadding);
            rt.offsetMax = new Vector2(-sideMargin, -topInsetPixels);
        }

        /// <summary>
        /// Ensures listeners are bound after runtime UI is built (and avoids missing clicks if scene refs were reassigned).
        /// </summary>
        private void WireLobbyBrowserListeners()
        {
            if (refreshLobbiesButton != null)
            {
                refreshLobbiesButton.onClick.RemoveListener(OnRefreshLobbiesClicked);
                refreshLobbiesButton.onClick.AddListener(OnRefreshLobbiesClicked);
            }
            if (joinSelectedLobbyButton != null)
            {
                joinSelectedLobbyButton.onClick.RemoveListener(OnJoinSelectedLobbyClicked);
                joinSelectedLobbyButton.onClick.AddListener(OnJoinSelectedLobbyClicked);
            }
        }

        private void BuildLobbyBrowserPanel(RectTransform parent)
        {
            lobbyBrowserRoot = new GameObject("LobbyBrowserRoot", typeof(RectTransform), typeof(Image));
            lobbyBrowserRoot.transform.SetParent(parent, false);
            var rootRect = lobbyBrowserRoot.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootRect.pivot = new Vector2(0.5f, 0.5f);

            var rootImage = lobbyBrowserRoot.GetComponent<Image>();
            rootImage.color = new Color(0.035f, 0.065f, 0.11f, 0.98f);
            rootImage.raycastTarget = false;

            var rootVlg = lobbyBrowserRoot.AddComponent<VerticalLayoutGroup>();
            rootVlg.spacing = 12f;
            rootVlg.padding = new RectOffset(16, 16, 14, 16);
            rootVlg.childAlignment = TextAnchor.UpperCenter;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = true;
            rootVlg.childForceExpandHeight = false;

            var titleObj = CreateLabel("LobbyBrowserTitle", "Open matches", Vector2.zero, 32f, lobbyBrowserRoot.transform, raycastTarget: false);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = Vector2.zero;
            titleRect.anchoredPosition = Vector2.zero;
            var titleTmp = titleObj.GetComponent<TextMeshProUGUI>();
            titleTmp.enableWordWrapping = false;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = new Color(0.95f, 0.97f, 1f, 1f);
            titleTmp.outlineWidth = 0.15f;
            titleTmp.outlineColor = new Color32(20, 40, 70, 200);
            var titleLe = titleObj.AddComponent<LayoutElement>();
            titleLe.minHeight = 36f;
            titleLe.preferredHeight = 40f;

            var statusObj = CreateLabel("LobbyBrowserStatusText", "Select a lobby to join.", Vector2.zero, 20f, lobbyBrowserRoot.transform, raycastTarget: false);
            var statusRect = statusObj.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = Vector2.zero;
            statusRect.anchoredPosition = Vector2.zero;
            lobbyBrowserStatusText = statusObj.GetComponent<TextMeshProUGUI>();
            lobbyBrowserStatusText.enableWordWrapping = true;
            lobbyBrowserStatusText.color = new Color(0.75f, 0.86f, 0.98f, 0.95f);
            lobbyBrowserStatusText.overflowMode = TextOverflowModes.Ellipsis;
            var statusLe = statusObj.AddComponent<LayoutElement>();
            statusLe.minHeight = 32f;
            statusLe.preferredHeight = 40f;

            var scrollRootObj = new GameObject("LobbyListScrollRect", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollRootObj.transform.SetParent(lobbyBrowserRoot.transform, false);
            var scrollRootRect = scrollRootObj.GetComponent<RectTransform>();
            scrollRootRect.anchorMin = Vector2.zero;
            scrollRootRect.anchorMax = Vector2.one;
            scrollRootRect.offsetMin = Vector2.zero;
            scrollRootRect.offsetMax = Vector2.zero;
            scrollRootRect.pivot = new Vector2(0.5f, 0.5f);
            var scrollLe = scrollRootObj.AddComponent<LayoutElement>();
            scrollLe.minHeight = 120f;
            scrollLe.preferredHeight = 200f;
            scrollLe.flexibleHeight = 1f;

            var scrollBg = scrollRootObj.GetComponent<Image>();
            scrollBg.color = new Color(0.055f, 0.09f, 0.145f, 0.98f);
            scrollBg.raycastTarget = true;

            var viewportObj = new GameObject("LobbyListViewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewportObj.transform.SetParent(scrollRootObj.transform, false);
            var viewportRect = viewportObj.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            viewportRect.pivot = new Vector2(0.5f, 0.5f);
            var viewportImage = viewportObj.GetComponent<Image>();
            viewportImage.color = new Color(0.07f, 0.11f, 0.17f, 1f);
            viewportImage.raycastTarget = true;
            var mask = viewportObj.GetComponent<Mask>();
            mask.showMaskGraphic = true;

            var contentObj = new GameObject("LobbyListContainer", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObj.transform.SetParent(viewportObj.transform, false);
            var contentRect = contentObj.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 0f);
            lobbyListContainer = contentObj.transform;

            var vlg = contentObj.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = 14f;
            vlg.padding = new RectOffset(16, 16, 16, 16);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;

            var fitter = contentObj.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollRect = scrollRootObj.GetComponent<ScrollRect>();
            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 28f;
            scrollRect.inertia = true;
            scrollRect.decelerationRate = 0.12f;

            var footerObj = new GameObject("LobbyBrowserFooter", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            footerObj.transform.SetParent(lobbyBrowserRoot.transform, false);
            var footerRect = footerObj.GetComponent<RectTransform>();
            footerRect.anchorMin = new Vector2(0f, 1f);
            footerRect.anchorMax = new Vector2(1f, 1f);
            footerRect.pivot = new Vector2(0.5f, 1f);
            footerRect.sizeDelta = new Vector2(0f, 56f);
            footerRect.anchoredPosition = Vector2.zero;
            var footerLe = footerObj.AddComponent<LayoutElement>();
            footerLe.minHeight = 52f;
            footerLe.preferredHeight = 56f;

            var hlg = footerObj.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = 16f;
            hlg.padding = new RectOffset(8, 8, 6, 8);
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlHeight = true;
            hlg.childControlWidth = true;
            hlg.childForceExpandHeight = false;
            hlg.childForceExpandWidth = true;

            refreshLobbiesButton = CreateMenuButton("RefreshLobbiesButton", "Refresh list", Vector2.zero, new Vector2(360f, 48f), footerRect, isPrimary: false);
            joinSelectedLobbyButton = CreateMenuButton("JoinSelectedLobbyButton", "Join selected", Vector2.zero, new Vector2(360f, 48f), footerRect, isPrimary: true);

            lobbyListRowPrefab = CreateLobbyRowPrefab();
            if (lobbyListRowPrefab != null)
                lobbyListRowPrefab.SetActive(false);
        }

        private Button CreateMenuButton(string name, string label, Vector2 anchoredPosition, Vector2 size, RectTransform parent, bool isPrimary = true)
        {
            if (parent == null)
                return null;

            var buttonObj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObj.transform.SetParent(parent, false);
            var rect = buttonObj.GetComponent<RectTransform>();
            var inLayoutGroup = parent.GetComponent<HorizontalLayoutGroup>() != null || parent.GetComponent<VerticalLayoutGroup>() != null;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            if (inLayoutGroup)
            {
                var le = buttonObj.AddComponent<LayoutElement>();
                le.preferredWidth = size.x;
                le.preferredHeight = size.y;
                le.minWidth = Mathf.Max(80f, size.x * 0.5f);
                le.minHeight = size.y;
            }

            var image = buttonObj.GetComponent<Image>();
            image.color = isPrimary
                ? new Color(0.22f, 0.52f, 0.88f, 0.95f)
                : new Color(0.16f, 0.22f, 0.32f, 0.92f);
            image.raycastTarget = true;

            var btn = buttonObj.GetComponent<Button>();
            btn.targetGraphic = image;
            var colors = btn.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = isPrimary ? new Color(0.32f, 0.62f, 0.98f, 1f) : new Color(0.24f, 0.32f, 0.44f, 1f);
            colors.pressedColor = isPrimary ? new Color(0.14f, 0.38f, 0.72f, 1f) : new Color(0.12f, 0.2f, 0.32f, 1f);
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = new Color(0.2f, 0.22f, 0.28f, 0.55f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            btn.colors = colors;

            var textObj = CreateLabel(name + "_Label", label, Vector2.zero, 26f, buttonObj.transform, raycastTarget: false);
            if (textObj != null)
            {
                var textRect = textObj.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
                var tmp = textObj.GetComponent<TextMeshProUGUI>();
                tmp.fontStyle = FontStyles.Bold;
                tmp.color = new Color(0.96f, 0.98f, 1f, 1f);
            }

            return btn;
        }

        private GameObject CreateLabel(string name, string text, Vector2 anchoredPosition, float fontSize, Transform parent = null, bool raycastTarget = true)
        {
            Transform targetParent = parent != null ? parent : mainMenuPanel.transform;
            var labelObj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObj.transform.SetParent(targetParent, false);
            var rect = labelObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(760f, 42f);

            var tmp = labelObj.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.92f, 0.95f, 1f, 1f);
            tmp.raycastTarget = raycastTarget;
            return labelObj;
        }

        private GameObject CreateLobbyRowPrefab()
        {
            if (lobbyBrowserRoot == null)
                return null;

            var rowObj = new GameObject("LobbyListRowPrefab", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            rowObj.transform.SetParent(lobbyBrowserRoot.transform, false);

            var rect = rowObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(860f, 72f);

            var image = rowObj.GetComponent<Image>();
            image.color = new Color(0.11f, 0.17f, 0.28f, 0.98f);
            image.raycastTarget = true;

            var btn = rowObj.GetComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(0.16f, 0.26f, 0.42f, 1f);
            colors.pressedColor = new Color(0.09f, 0.14f, 0.24f, 1f);
            colors.selectedColor = new Color(0.18f, 0.38f, 0.62f, 0.98f);
            colors.disabledColor = new Color(0.12f, 0.2f, 0.32f, 0.95f);
            btn.colors = colors;
            btn.transition = Selectable.Transition.ColorTint;

            var layoutElement = rowObj.GetComponent<LayoutElement>();
            layoutElement.minHeight = 72f;
            layoutElement.preferredHeight = 72f;

            var textObj = CreateLabel("LobbyRowLabel", "Lobby", Vector2.zero, 30f, rowObj.transform, raycastTarget: false);
            if (textObj != null)
            {
                var textRect = textObj.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(20f, 10f);
                textRect.offsetMax = new Vector2(-20f, -10f);
                var tmp = textObj.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.alignment = TextAlignmentOptions.MidlineLeft;
                    tmp.enableWordWrapping = true;
                    tmp.richText = true;
                }
            }

            return rowObj;
        }

        private static void EnsureEventSystemExists()
        {
            if (FindFirstObjectByType<EventSystem>() != null)
                return;
            var eventSystemObj = new GameObject("EventSystem", typeof(EventSystem));
            eventSystemObj.AddComponent<InputSystemUIInputModule>();
            DontDestroyOnLoad(eventSystemObj);
        }

        private void ShowLobby()
        {
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);

            string playerName = (playerNameInputField != null ? playerNameInputField.text : null) ?? "";
            playerName = playerName.Trim();
            NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(playerName)
                ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                : playerName;

            if (loadingScreenController != null)
            {
                deferTeamPanelUntilNetworkReady = false;
                if (lobbyPanel != null)
                    lobbyPanel.SetActive(false);
                if (teamSelectionPanel != null)
                    teamSelectionPanel.SetActive(false);
                loadingScreenController.ShowLoading();
                return;
            }

            deferTeamPanelUntilNetworkReady = true;
            if (lobbyPanel != null)
                lobbyPanel.SetActive(true);

            if (teamSelectionPanel != null)
                teamSelectionPanel.SetActive(false);
        }

        /// <summary>Called by LoadingScreenController when loading is complete. Shows lobby and team selection (hides loading).</summary>
        public void ShowLobbyAndTeamSelection()
        {
            deferTeamPanelUntilNetworkReady = false;
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);

            string playerName = (playerNameInputField != null ? playerNameInputField.text : null) ?? "";
            playerName = playerName.Trim();
            NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(playerName)
                ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                : playerName;

            if (lobbyPanel != null)
            {
                lobbyPanel.SetActive(true);
            }

            if (teamSelectionPanel != null)
            {
                teamSelectionPanel.SetActive(true);
            }
        }

        private void UpdateLobbyInfo()
        {
            if (roomNameText != null && NetworkGameManager.Instance != null)
                roomNameText.text = "Room: " + (string.IsNullOrEmpty(NetworkGameManager.Instance.CurrentLobbyName) ? "—" : NetworkGameManager.Instance.CurrentLobbyName);

            if (playerCountText != null)
            {
                int playerCount = 0;
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                    playerCount = NetworkManager.Singleton.ConnectedClients.Count;
                playerCountText.text = $"Players: {playerCount}/60";
            }

            if (teamStatusText != null && Core.TeamManager.Instance != null)
            {
                int active = Core.TeamManager.Instance.GetEffectiveTeamCountForUI();
                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < active; i++)
                {
                    var t = (Core.TeamManager.Team)(i + 1);
                    int n = Core.TeamManager.Instance.GetTeamPlayerCount(t);
                    parts.Add($"Team {(char)('A' + i)}: {n}/20");
                }
                string countsLine = string.Join(" | ", parts);
                teamStatusText.text = string.IsNullOrEmpty(pendingTeamJoinError)
                    ? countsLine
                    : pendingTeamJoinError + "\n" + countsLine;
            }
        }
    }
}
