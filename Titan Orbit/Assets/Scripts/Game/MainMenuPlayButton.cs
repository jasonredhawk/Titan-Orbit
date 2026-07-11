using TitanOrbit.Data;
using TitanOrbit.NetCode;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Reliable Play click handler independent of NceGameFlowController wiring. [UNITY] MonoBehaviour
    /// on the main menu Play button — validates NetCode worlds exist before starting local or dedicated join.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class MainMenuPlayButton : MonoBehaviour, IPointerClickHandler
    {
        Button _button;

        void Awake()
        {
            // --- Cache button and fix child raycast stealing clicks ---
            _button = GetComponent<Button>();
            DisableChildRaycasts();
        }

        void OnEnable()
        {
            DisableChildRaycasts();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // --- Guard disabled button ---
            if (_button != null && !_button.interactable)
                return;

            Debug.Log("[MainMenuPlayButton] Play clicked.");

#if UNITY_SERVER
            // --- [NETCODE] Dedicated server window cannot host client UI play ---
            Debug.LogError(
                "[MainMenuPlayButton] Wrong window: this is the SERVER play instance. " +
                "In the Game view, click the 'Main Editor' tab (not Server / Player 2), then press Play.");
            return;
#endif

            // --- Validate NetCode worlds before session start ---
            if (!HasPlayableClientWorld())
            {
                Debug.LogError(
                    "[MainMenuPlayButton] ClientWorld is missing. " +
                    "Run Titan Orbit > Configure Multiplayer For Local Play, then press Unity Play using the Main Editor Game tab.");
                return;
            }

            if (!TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance() &&
                TitanOrbitMultiplayerConfig.ShowLocalPlayOptions &&
                !HasPlayableServerWorld())
            {
                Debug.LogError(
                    "[MainMenuPlayButton] ServerWorld is missing for local play. " +
                    "Run Titan Orbit > Configure Multiplayer For Local Play (Client+Server).");
                return;
            }

            if (TitanOrbitSessionManager.Instance == null)
            {
                Debug.LogError("[MainMenuPlayButton] TitanOrbitSessionManager missing on NceGameRoot.");
                return;
            }

            // --- Start local host/client or quick-join dedicated ---
            if (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions)
            {
                if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                    TitanOrbitSessionManager.Instance.StartLocalClientForLanTest();
                else
                    TitanOrbitSessionManager.Instance.StartLocalPlay();
            }
            else
                _ = TitanOrbitSessionManager.Instance.QuickJoinDedicatedAsync();
        }

        static bool HasPlayableClientWorld()
        {
            // --- [NETCODE] ClientWorld must exist with NetworkStreamDriver ---
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
                return false;
            return client.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
        }

        static bool HasPlayableServerWorld()
        {
            // --- [NETCODE] ServerWorld required for local Client+Server play ---
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;
            return server.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
        }

        void DisableChildRaycasts()
        {
            // --- [UNITY] Label graphics must not block Button raycasts ---
            foreach (var graphic in GetComponentsInChildren<Graphic>(true))
            {
                if (graphic.gameObject != gameObject)
                    graphic.raycastTarget = false;
            }
        }
    }
}
