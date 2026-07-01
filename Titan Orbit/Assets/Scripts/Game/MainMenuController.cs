using TitanOrbit.Core;
using TitanOrbit.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>Minimal main menu for NCE vertical slice.</summary>
    public class MainMenuController : MonoBehaviour
    {
        [SerializeField] Button quickJoinButton;
        [SerializeField] Button lanHostButton;
        [SerializeField] InputField lobbyIdInput;
        [SerializeField] Button joinByIdButton;

        async void Start()
        {
            if (quickJoinButton != null)
                quickJoinButton.onClick.AddListener(OnQuickJoin);
            if (lanHostButton != null)
                lanHostButton.onClick.AddListener(OnLanHost);
            if (joinByIdButton != null)
                joinByIdButton.onClick.AddListener(OnJoinById);
        }

        async void OnQuickJoin()
        {
            // Placeholder: user pastes lobby id for slice testing.
            Debug.Log("[MainMenu] Use Join By Lobby Id for dedicated Relay join in this slice.");
        }

        void OnLanHost()
        {
            TitanOrbitSessionManager.PendingLanHost = true;
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
        }

        async void OnJoinById()
        {
            if (TitanOrbitSessionManager.Instance == null || lobbyIdInput == null) return;
            bool ok = await TitanOrbitSessionManager.Instance.JoinDedicatedLobbyAsync(lobbyIdInput.text.Trim());
            Debug.Log("[MainMenu] Join result: " + ok);
        }
    }
}
