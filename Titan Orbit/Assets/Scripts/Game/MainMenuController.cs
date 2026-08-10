using TitanOrbit.Core;
using TitanOrbit.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Minimal main menu for NetCode vertical slice: LAN host reload, dedicated Relay join by lobby id.
    /// Wires UGUI buttons to <see cref="TitanOrbitSessionManager"/>. Client-only scene UI — does not
    /// run on dedicated server builds. Quick join is a placeholder until browser/relay auto-join ships.
    /// </summary>
    public class MainMenuController : MonoBehaviour
    {
        [SerializeField] Button quickJoinButton;
        [SerializeField] Button lanHostButton;
        [SerializeField] InputField lobbyIdInput;
        [SerializeField] Button joinByIdButton;

        async void Start()
        {
            // --- Wire UGUI buttons to session actions ---
            if (quickJoinButton != null)
                quickJoinButton.onClick.AddListener(OnQuickJoin);
            if (lanHostButton != null)
                lanHostButton.onClick.AddListener(OnLanHost);
            if (joinByIdButton != null)
                joinByIdButton.onClick.AddListener(OnJoinById);
        }

        /// <summary>Placeholder until browser/quick-match Relay flow ships.</summary>
        async void OnQuickJoin()
        {
            // Placeholder: user pastes lobby id for slice testing.
            Debug.Log("[MainMenu] Use Join By Lobby Id for dedicated Relay join in this slice.");
        }

        /// <summary>[NETCODE] Reloads active scene with PendingLanHost — bootstrap starts listen host.</summary>
        void OnLanHost()
        {
            TitanOrbitSessionManager.PendingLanHost = true;
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
        }

        /// <summary>[NETCODE] Join dedicated server via Unity Lobby id + Relay allocation.</summary>
        async void OnJoinById()
        {
            if (TitanOrbitSessionManager.Instance == null || lobbyIdInput == null) return;
            bool ok = await TitanOrbitSessionManager.Instance.JoinDedicatedLobbyAsync(lobbyIdInput.text.Trim());
            Debug.Log("[MainMenu] Join result: " + ok);
        }
    }
}
