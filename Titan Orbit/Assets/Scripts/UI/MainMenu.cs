using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using TitanOrbit.Networking;

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
        [SerializeField] private Button startServerButton;
        [SerializeField] private Button startHostButton;
        [SerializeField] private Button startClientButton;
        [SerializeField] private TMP_InputField serverAddressInput;
        [SerializeField] private TextMeshProUGUI playerCountText;
        [SerializeField] private TextMeshProUGUI teamStatusText;

        private void Start()
        {
            if (startServerButton != null)
            {
                startServerButton.onClick.AddListener(OnStartServerClicked);
            }

            if (startHostButton != null)
            {
                startHostButton.onClick.AddListener(OnStartHostClicked);
            }

            if (startClientButton != null)
            {
                startClientButton.onClick.AddListener(OnStartClientClicked);
            }
        }

        private void Update()
        {
            UpdateLobbyInfo();
        }

        private void OnStartServerClicked()
        {
            if (NetworkGameManager.Instance != null)
            {
                NetworkGameManager.Instance.StartServer();
                ShowLobby();
            }
        }

        private void OnStartHostClicked()
        {
            if (NetworkGameManager.Instance != null)
            {
                NetworkGameManager.Instance.StartHost();
                ShowLobby();
            }
        }

        private void OnStartClientClicked()
        {
            if (NetworkGameManager.Instance != null)
            {
                NetworkGameManager.Instance.StartClient();
                ShowLobby();
            }
        }

        private void ShowLobby()
        {
            if (mainMenuPanel != null)
            {
                mainMenuPanel.SetActive(false);
            }

            if (lobbyPanel != null)
            {
                lobbyPanel.SetActive(true);
            }
        }

        private void UpdateLobbyInfo()
        {
            if (playerCountText != null)
            {
                // Update player count
                int playerCount = NetworkManager.Singleton != null ? 
                    NetworkManager.Singleton.ConnectedClients.Count : 0;
                playerCountText.text = $"Players: {playerCount}/60";
            }

            if (teamStatusText != null && Core.TeamManager.Instance != null)
            {
                // Update team status
                int teamACount = Core.TeamManager.Instance.GetTeamPlayerCount(Core.TeamManager.Team.TeamA);
                int teamBCount = Core.TeamManager.Instance.GetTeamPlayerCount(Core.TeamManager.Team.TeamB);
                int teamCCount = Core.TeamManager.Instance.GetTeamPlayerCount(Core.TeamManager.Team.TeamC);
                
                teamStatusText.text = $"Team A: {teamACount}/20 | Team B: {teamBCount}/20 | Team C: {teamCCount}/20";
            }
        }
    }
}
