using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using TitanOrbit.Networking;
using System.Threading.Tasks;

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
        [SerializeField] private TextMeshProUGUI teamALabel;
        [SerializeField] private TextMeshProUGUI teamBLabel;
        [SerializeField] private TextMeshProUGUI teamCLabel;
        [SerializeField] private TMP_InputField joinCodeInputField;
        [SerializeField] private TextMeshProUGUI joinCodeDisplayText;
        [SerializeField] private TMP_InputField serverAddressInput;
        [SerializeField] private TextMeshProUGUI playerCountText;
        [SerializeField] private TextMeshProUGUI teamStatusText;
        [SerializeField] private TextMeshProUGUI roomNameText;
        [SerializeField] private TMP_InputField playerNameInputField;

        [Header("WebGL Match Selection (Placeholder)")]
        [Tooltip("When true, WebGL Play shows open lobbies and joins based on the joinCode input (index or lobby id).")]
        [SerializeField] private bool paidPlaceholder = false;

        private void Start()
        {
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

            NetworkGameManager.OnTeamChosen += OnTeamChosen;

            if (teamAButton != null) teamAButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamA));
            if (teamBButton != null) teamBButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamB));
            if (teamCButton != null) teamCButton.onClick.AddListener(() => OnTeamClicked(Core.TeamManager.Team.TeamC));

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
        }

        private void OnDestroy()
        {
            NetworkGameManager.OnTeamChosen -= OnTeamChosen;
        }

        private void OnTeamChosen(Core.TeamManager.Team team)
        {
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
            if (teamSelectionPanel != null) teamSelectionPanel.SetActive(false);
        }

        private void Update()
        {
            if (lobbyPanel != null && lobbyPanel.activeSelf)
            {
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
            int a = Core.TeamManager.Instance.TeamACount;
            int b = Core.TeamManager.Instance.TeamBCount;
            int c = Core.TeamManager.Instance.TeamCCount;
            if (teamALabel != null) teamALabel.text = $"Team A ({a}/{max})";
            if (teamBLabel != null) teamBLabel.text = $"Team B ({b}/{max})";
            if (teamCLabel != null) teamCLabel.text = $"Team C ({c}/{max})";
            bool aOpen = a < max;
            bool bOpen = b < max;
            bool cOpen = c < max;
            if (teamAButton != null) teamAButton.interactable = aOpen;
            if (teamBButton != null) teamBButton.interactable = bOpen;
            if (teamCButton != null) teamCButton.interactable = cOpen;
        }

        private void OnTeamClicked(Core.TeamManager.Team team)
        {
            if (Core.TeamManager.Instance == null) return;
            Core.TeamManager.Instance.RequestTeamServerRpc(team);
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
                    // Free players are forced into the latest lobby that is open.
                    var latest = await NetworkGameManager.Instance.QueryWebGLOpenLobbiesAsync(latestOnly: true, count: 10);
                    if (latest == null || latest.Count == 0)
                    {
                        Debug.LogWarning("No open latest lobbies found for free users.");
                        ok = false;
                    }
                    else
                    {
                        ok = await NetworkGameManager.Instance.PlayWebGLJoinByLobbyIdAsync(latest[0].Id);
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
                    Debug.LogError("Play failed. Check console and Unity Services.");
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
                string joinCode = await NetworkGameManager.Instance.StartHostWithRelayAsync();
                if (!string.IsNullOrEmpty(joinCode))
                {
                    if (joinCodeDisplayText != null)
                    {
                        joinCodeDisplayText.gameObject.SetActive(true);
                        joinCodeDisplayText.text = "Join code: " + joinCode;
                    }
                    else
                    {
                        Debug.Log("Host (Online) started. Share this join code: " + joinCode);
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

        private void ShowLobby()
        {
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);

            string playerName = (playerNameInputField != null ? playerNameInputField.text : null) ?? "";
            playerName = playerName.Trim();
            NetworkGameManager.LocalPlayerDisplayName = string.IsNullOrEmpty(playerName)
                ? TitanOrbit.Data.GameNames.GetRandomPlayerName()
                : playerName;

            if (lobbyPanel != null)
                lobbyPanel.SetActive(true);

            if (teamSelectionPanel != null)
                teamSelectionPanel.SetActive(true);
        }

        /// <summary>Called by LoadingScreenController when loading is complete. Shows lobby and team selection (hides loading).</summary>
        public void ShowLobbyAndTeamSelection()
        {
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
                int teamACount = Core.TeamManager.Instance.GetTeamPlayerCount(Core.TeamManager.Team.TeamA);
                int teamBCount = Core.TeamManager.Instance.GetTeamPlayerCount(Core.TeamManager.Team.TeamB);
                int teamCCount = Core.TeamManager.Instance.GetTeamPlayerCount(Core.TeamManager.Team.TeamC);
                teamStatusText.text = $"Team A: {teamACount}/20 | Team B: {teamBCount}/20 | Team C: {teamCCount}/20";
            }
        }
    }
}
