using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Win/loss screen displayed at end of match
    /// </summary>
    public class WinLossScreen : MonoBehaviour
    {
        public static WinLossScreen Instance { get; private set; }
        [Header("UI References")]
        [SerializeField] private GameObject winLossPanel;
        [SerializeField] private TextMeshProUGUI resultText;
        [SerializeField] private TextMeshProUGUI winningTeamText;
        [SerializeField] private Button returnToMenuButton;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Start()
        {
            if (returnToMenuButton != null)
            {
                returnToMenuButton.onClick.AddListener(OnReturnToMenuClicked);
            }

            if (winLossPanel != null)
            {
                winLossPanel.SetActive(false);
            }
        }

        public void ShowWinScreen(TeamManager.Team winningTeam, TeamManager.Team playerTeam)
        {
            if (winLossPanel != null)
            {
                winLossPanel.SetActive(true);
            }

            bool isWinner = winningTeam == playerTeam;

            if (resultText != null)
            {
                resultText.text = isWinner ? "VICTORY!" : "DEFEAT";
                resultText.color = isWinner ? Color.green : Color.red;
            }

            if (winningTeamText != null)
            {
                winningTeamText.text = $"Team {winningTeam} Wins!";
            }
        }

        public void ShowTeamEliminatedScreen(TeamManager.Team eliminatedTeam, TeamManager.Team playerTeam)
        {
            if (winLossPanel != null)
            {
                winLossPanel.SetActive(true);
            }

            bool isEliminated = eliminatedTeam == playerTeam;

            if (resultText != null)
            {
                resultText.text = isEliminated ? "TEAM ELIMINATED" : $"Team {eliminatedTeam} Eliminated";
                resultText.color = Color.red;
            }

            if (winningTeamText != null)
            {
                winningTeamText.text = isEliminated ? "Your team has been eliminated!" : "Continue fighting!";
            }
        }

        private void OnReturnToMenuClicked()
        {
            // Return to main menu
            UnityEngine.SceneManagement.SceneManager.LoadScene(0);
        }
    }
}
