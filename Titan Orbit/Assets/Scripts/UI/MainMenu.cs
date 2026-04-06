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
        [SerializeField] private Button backToMainMenuButton;
        [SerializeField] private Transform lobbyListContainer;
        [SerializeField] private GameObject lobbyListRowPrefab;
        [SerializeField] private TextMeshProUGUI lobbyBrowserStatusText;
        [SerializeField] private bool latestOnlyFilter = false;

        [Header("WebGL Match Selection (Placeholder)")]
        [Tooltip("When true, WebGL Play shows open lobbies and joins based on the joinCode input (index or lobby id).")]
        [SerializeField] private bool paidPlaceholder = false;

        private readonly List<NetworkGameManager.LobbySummary> cachedLobbySummaries = new List<NetworkGameManager.LobbySummary>();
        private readonly List<Button> lobbyRowButtons = new List<Button>();
        private readonly List<Image> lobbyRowBackgrounds = new List<Image>();
        private string selectedLobbyId;
        private int selectedLobbyRowIndex = -1;
        private GameObject lobbyBrowserRoot;
        private string pendingTeamJoinError;
        /// <summary>When <see cref="ShowLobby"/> runs without a loading screen, team panel is shown only after Netcode is in a client/host session.</summary>
        private bool deferTeamPanelUntilNetworkReady;

        private void Start()
        {
            EnsureRuntimeLobbyBrowserUI();
            DeemphasizeLocalPlayButton();

            if (hostOnlineButton != null)
                hostOnlineButton.onClick.AddListener(OnHostOnlineClicked);

            if (joinOnlineButton != null)
            {
                joinOnlineButton.onClick.AddListener(OnJoinOnlineClicked);
            }

            if (playButton != null)
            {
                playButton.onClick.AddListener(OnPlayClicked);
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

        private async void OnPlayClicked()
        {
            if (NetworkGameManager.Instance == null) return;
            if (playButton != null) playButton.interactable = false;
            try
            {
                string playerName = (playerNameInputField != null ? playerNameInputField.text : null) ?? "";
                playerName = playerName.Trim();
                if (!string.IsNullOrEmpty(playerName))
                {
                    PlayerPrefs.SetString("TitanOrbit_PlayerName", playerName);
                    PlayerPrefs.Save();
                }
                NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(playerName)
                    ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                    : playerName;

                bool ok;
#if UNITY_WEBGL && !UNITY_EDITOR
                bool isPaid = PlayerPrefs.GetInt("TitanOrbit_WebPaid", paidPlaceholder ? 1 : 0) != 0;

                if (!isPaid)
                {
                    // WebGL cannot start a local host. Try Quick Join (any open lobby), then fall back to a "latest" lobby.
                    ok = await NetworkGameManager.Instance.PlayWebGLJoinAsync();
                    if (!ok)
                    {
                        var latest = await NetworkGameManager.Instance.QueryWebGLOpenLobbiesAsync(latestOnly: true, count: 10);
                        if (latest == null || latest.Count == 0)
                        {
                            Debug.LogWarning("No open lobbies found (Quick Join and latest). Run a headless/server build with Lobby+Relay, or use Host Online / join code from another client.");
                            ok = false;
                        }
                        else
                        {
                            ok = await NetworkGameManager.Instance.PlayWebGLJoinByLobbyIdAsync(latest[0].Id);
                        }
                    }
                }
                else
                {
                    // Paid users can pick any open lobby (placeholder selection via joinCodeInputField).
                    var openLobbies = await NetworkGameManager.Instance.QueryWebGLOpenLobbiesAsync(latestOnly: false, count: 20);
                    if (openLobbies == null || openLobbies.Count == 0)
                    {
                        Debug.LogWarning("No open lobbies found for paid users.");
                        ok = false;
                    }
                    else
                    {
                        // Show a small list in the existing display text so you can choose an index/lobby id.
                        if (joinCodeDisplayText != null)
                        {
                            joinCodeDisplayText.gameObject.SetActive(true);
                            int maxDisplay = Mathf.Min(8, openLobbies.Count);
                            string listText = "Open lobbies (paid):\n";
                            for (int i = 0; i < maxDisplay; i++)
                            {
                                var lob = openLobbies[i];
                                int playerCount = lob.Players != null ? lob.Players.Count : 0;
                                listText += $"{i}: {lob.Name} ({playerCount}/{lob.MaxPlayers}) id={lob.Id}\n";
                            }
                            listText += "Enter joinCodeInput as index (0..N) or lobby id.\n";
                            joinCodeDisplayText.text = listText;
                        }

                        string selection = joinCodeInputField != null ? joinCodeInputField.text : null;
                        string trimmed = string.IsNullOrWhiteSpace(selection) ? null : selection.Trim();

                        string lobbyIdToJoin = null;
                        if (string.IsNullOrWhiteSpace(trimmed))
                        {
                            lobbyIdToJoin = openLobbies[0].Id;
                        }
                        else if (int.TryParse(trimmed, out int index))
                        {
                            if (index >= 0 && index < openLobbies.Count)
                                lobbyIdToJoin = openLobbies[index].Id;
                        }
                        else
                        {
                            // Treat as a lobby id.
                            lobbyIdToJoin = trimmed;
                        }

                        if (string.IsNullOrWhiteSpace(lobbyIdToJoin))
                        {
                            Debug.LogWarning("Paid selection was invalid. Join failed.");
                            ok = false;
                        }
                        else
                        {
                            ok = await NetworkGameManager.Instance.PlayWebGLJoinByLobbyIdAsync(lobbyIdToJoin);
                        }
                    }
                }
#else
                ok = await NetworkGameManager.Instance.PlayQuickJoinOrCreateAsync();
#endif
                if (ok)
                {
                    if (mainMenuPanel != null) mainMenuPanel.SetActive(false);
                    if (loadingScreenController != null)
                        loadingScreenController.ShowLoading();
                    else
                        ShowLobby();
                }
                else
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    SetLobbyBrowserStatus("Join failed: no open match, or Unity Services blocked. Host a game from desktop/server, then try again.");
                    Debug.LogError("WebGL Play failed: need an open Lobby+Relay match (headless host or another player). Check Unity Dashboard and browser console.");
#else
                    SetLobbyBrowserStatus("Local Test failed. Check console and Unity Services.");
                    Debug.LogError("Play failed. Check console and Unity Services.");
#endif
                }
            }
            finally
            {
                if (playButton != null) playButton.interactable = true;
            }
        }

        private async void OnHostOnlineClicked()
        {
            if (NetworkGameManager.Instance == null) return;
            if (hostOnlineButton != null) hostOnlineButton.interactable = false;
            try
            {
                string pname = playerNameInputField != null ? (playerNameInputField.text ?? "").Trim() : "";
                string lobbyName = string.IsNullOrEmpty(pname) ? null : pname + "'s game";
                string joinCode = await NetworkGameManager.Instance.StartHostWithRelayAsync(lobbyName);
                if (!string.IsNullOrEmpty(joinCode))
                {
                    if (joinCodeDisplayText != null)
                    {
                        joinCodeDisplayText.gameObject.SetActive(true);
                        joinCodeDisplayText.text = "Match is listed under Browse Open Matches.\nRelay code: " + joinCode;
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

        private async void OnBrowseLobbiesClicked()
        {
            WireLobbyBrowserListeners();
            SetLobbyBrowserVisible(true);
            await RefreshLobbyListAsync();
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

        private void OnBackToMainMenuClicked()
        {
            ClearLobbyListRows();
            selectedLobbyId = null;
            selectedLobbyRowIndex = -1;
            if (joinSelectedLobbyButton != null)
                joinSelectedLobbyButton.interactable = false;
            SetLobbyBrowserStatus("Select a lobby to join.");
            SetLobbyBrowserVisible(false);
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
            {
                lobbyBrowserStatusText.text = text;
                lobbyBrowserStatusText.transform.SetAsLastSibling();
            }
        }

        private void DeemphasizeLocalPlayButton()
        {
            if (playButton == null)
                return;

            var playRect = playButton.GetComponent<RectTransform>();
            if (playRect != null)
                playRect.sizeDelta = new Vector2(190f, 46f);

            var playImage = playButton.GetComponent<Image>();
            if (playImage != null)
                playImage.color = new Color(0.22f, 0.33f, 0.42f, 0.75f);

            var playLabel = playButton.GetComponentInChildren<TextMeshProUGUI>();
            if (playLabel != null)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                playLabel.text = "Play";
#else
                playLabel.text = "Local Test";
#endif
            }
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

            if (browseLobbiesButton == null)
            {
                // Below Create match, or player name if host button is assigned in the scene
                browseLobbiesButton = CreateMenuButton("BrowseLobbiesButton", "Browse Open Matches", new Vector2(0f, -112f), new Vector2(320f, 52f), mainRect);
                if (hostOnlineButton != null)
                    browseLobbiesButton.transform.SetSiblingIndex(hostOnlineButton.transform.GetSiblingIndex() + 1);
                else if (playerNameInputField != null)
                    browseLobbiesButton.transform.SetSiblingIndex(playerNameInputField.transform.GetSiblingIndex() + 1);
            }

            if (lobbyBrowserRoot == null)
                BuildLobbyBrowserPanel(mainRect);

            SetLobbyBrowserVisible(false);
        }

        /// <summary>
        /// Places Create match, Browse, and Play below the player name field with consistent gaps so controls never overlap.
        /// </summary>
        private void LayoutMainMenuActionStack()
        {
            if (mainMenuPanel == null)
                return;

            const float paddingBelowName = 16f;
            const float gapBetweenButtons = 12f;

            float nameCenterY = 0f;
            float nameHalfH = 36f;
            if (playerNameInputField != null)
            {
                var nameRt = playerNameInputField.GetComponent<RectTransform>();
                if (nameRt != null)
                {
                    nameCenterY = nameRt.anchoredPosition.y;
                    nameHalfH = nameRt.rect.height * 0.5f;
                }
            }

            float rowTopY = nameCenterY - nameHalfH - paddingBelowName;

            void PlaceButton(Button btn)
            {
                if (btn == null)
                    return;
                var r = btn.GetComponent<RectTransform>();
                if (r == null)
                    return;
                float half = r.sizeDelta.y * 0.5f;
                r.anchoredPosition = new Vector2(r.anchoredPosition.x, rowTopY - half);
                rowTopY -= r.sizeDelta.y + gapBetweenButtons;
            }

            PlaceButton(hostOnlineButton);
            PlaceButton(browseLobbiesButton);
            PlaceButton(playButton);
        }

        /// <summary>
        /// Ensures listeners are bound after runtime UI is built (and avoids missing clicks if scene refs were reassigned).
        /// </summary>
        private void WireLobbyBrowserListeners()
        {
            if (browseLobbiesButton != null)
            {
                browseLobbiesButton.onClick.RemoveListener(OnBrowseLobbiesClicked);
                browseLobbiesButton.onClick.AddListener(OnBrowseLobbiesClicked);
            }
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
            if (backToMainMenuButton != null)
            {
                backToMainMenuButton.onClick.RemoveListener(OnBackToMainMenuClicked);
                backToMainMenuButton.onClick.AddListener(OnBackToMainMenuClicked);
            }
        }

        private void BuildLobbyBrowserPanel(RectTransform parent)
        {
            lobbyBrowserRoot = new GameObject("LobbyBrowserRoot", typeof(RectTransform), typeof(Image));
            lobbyBrowserRoot.transform.SetParent(parent, false);
            var rootRect = lobbyBrowserRoot.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(1040f, 640f);
            rootRect.anchoredPosition = new Vector2(0f, -12f);
            var rootImage = lobbyBrowserRoot.GetComponent<Image>();
            rootImage.color = new Color(0.035f, 0.065f, 0.11f, 0.98f);
            // Root must not steal hits from child controls (footer/scroll). Blocker handles modal background.
            rootImage.raycastTarget = false;

            var blocker = new GameObject("LobbyBrowserBlocker", typeof(RectTransform), typeof(Image));
            blocker.transform.SetParent(lobbyBrowserRoot.transform, false);
            var blockerRt = blocker.GetComponent<RectTransform>();
            blockerRt.anchorMin = Vector2.zero;
            blockerRt.anchorMax = Vector2.one;
            blockerRt.offsetMin = Vector2.zero;
            blockerRt.offsetMax = Vector2.zero;
            blockerRt.SetAsFirstSibling();
            var blockerImg = blocker.GetComponent<Image>();
            blockerImg.color = new Color(0.02f, 0.04f, 0.08f, 0.55f);
            blockerImg.raycastTarget = true;

            var titleObj = CreateLabel("LobbyBrowserTitle", "Orbital Matches", Vector2.zero, 40f, lobbyBrowserRoot.transform, raycastTarget: false);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 1f);
            titleRect.anchorMax = new Vector2(0.5f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -28f);
            titleRect.sizeDelta = new Vector2(900f, 56f);
            var titleTmp = titleObj.GetComponent<TextMeshProUGUI>();
            titleTmp.enableWordWrapping = false;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = new Color(0.95f, 0.97f, 1f, 1f);
            titleTmp.outlineWidth = 0.15f;
            titleTmp.outlineColor = new Color32(20, 40, 70, 200);

            var statusObj = CreateLabel("LobbyBrowserStatusText", "Select a lobby to join.", Vector2.zero, 22f, lobbyBrowserRoot.transform, raycastTarget: false);
            var statusRect = statusObj.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0.5f, 1f);
            statusRect.anchorMax = new Vector2(0.5f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.anchoredPosition = new Vector2(0f, -88f);
            statusRect.sizeDelta = new Vector2(900f, 56f);
            lobbyBrowserStatusText = statusObj.GetComponent<TextMeshProUGUI>();
            lobbyBrowserStatusText.enableWordWrapping = true;
            lobbyBrowserStatusText.color = new Color(0.75f, 0.86f, 0.98f, 0.95f);
            lobbyBrowserStatusText.overflowMode = TextOverflowModes.Ellipsis;

            // ScrollRect must own the viewport as a child so scrolling and raycasts work reliably.
            var scrollRootObj = new GameObject("LobbyListScrollRect", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollRootObj.transform.SetParent(lobbyBrowserRoot.transform, false);
            var scrollRootRect = scrollRootObj.GetComponent<RectTransform>();
            scrollRootRect.anchorMin = new Vector2(0.5f, 0.5f);
            scrollRootRect.anchorMax = new Vector2(0.5f, 0.5f);
            scrollRootRect.pivot = new Vector2(0.5f, 0.5f);
            scrollRootRect.anchoredPosition = new Vector2(0f, -28f);
            scrollRootRect.sizeDelta = new Vector2(900f, 300f);
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
            vlg.spacing = 12f;
            vlg.padding = new RectOffset(14, 14, 14, 14);
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

            var footerObj = new GameObject("LobbyBrowserFooter", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(Canvas), typeof(GraphicRaycaster));
            footerObj.transform.SetParent(lobbyBrowserRoot.transform, false);
            var footerRect = footerObj.GetComponent<RectTransform>();
            footerRect.anchorMin = new Vector2(0.5f, 0f);
            footerRect.anchorMax = new Vector2(0.5f, 0f);
            footerRect.pivot = new Vector2(0.5f, 0f);
            footerRect.anchoredPosition = new Vector2(0f, 28f);
            footerRect.sizeDelta = new Vector2(900f, 76f);

            var footerCanvas = footerObj.GetComponent<Canvas>();
            footerCanvas.overrideSorting = true;
            footerCanvas.sortingOrder = 50;
            footerObj.GetComponent<GraphicRaycaster>().blockingObjects = GraphicRaycaster.BlockingObjects.None;

            var hlg = footerObj.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = 14f;
            hlg.padding = new RectOffset(8, 8, 0, 0);
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlHeight = true;
            hlg.childControlWidth = true;
            hlg.childForceExpandHeight = false;
            hlg.childForceExpandWidth = true;

            refreshLobbiesButton = CreateMenuButton("RefreshLobbiesButton", "Refresh List", Vector2.zero, new Vector2(268f, 56f), footerRect, isPrimary: false);
            joinSelectedLobbyButton = CreateMenuButton("JoinSelectedLobbyButton", "Join Selected", Vector2.zero, new Vector2(268f, 56f), footerRect, isPrimary: true);
            backToMainMenuButton = CreateMenuButton("BackToMainMenuButton", "Back", Vector2.zero, new Vector2(200f, 56f), footerRect, isPrimary: false);

            lobbyListRowPrefab = CreateLobbyRowPrefab();
            if (lobbyListRowPrefab != null)
                lobbyListRowPrefab.SetActive(false);

            // Footer must draw and raycast above list/scroll; title/status on top for readability.
            titleObj.transform.SetAsLastSibling();
            statusObj.transform.SetAsLastSibling();
            footerObj.transform.SetAsLastSibling();
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

        private void SetLobbyBrowserVisible(bool visible)
        {
            if (lobbyBrowserRoot != null)
                lobbyBrowserRoot.SetActive(visible);
            if (browseLobbiesButton != null)
                browseLobbiesButton.gameObject.SetActive(!visible);
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
