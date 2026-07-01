using TitanOrbit.NetCode;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>Reliable Play click handler independent of NceGameFlowController wiring.</summary>
    [RequireComponent(typeof(Button))]
    public class MainMenuPlayButton : MonoBehaviour, IPointerClickHandler
    {
        Button _button;

        void Awake()
        {
            _button = GetComponent<Button>();
            DisableChildRaycasts();
        }

        void OnEnable()
        {
            DisableChildRaycasts();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_button != null && !_button.interactable)
                return;

            Debug.Log("[MainMenuPlayButton] Play clicked.");

#if UNITY_SERVER
            Debug.LogError(
                "[MainMenuPlayButton] Wrong window: this is the SERVER play instance. " +
                "In the Game view, click the 'Main Editor' tab (not Server / Player 2), then press Play.");
            return;
#endif

            if (!HasPlayableClientWorld())
            {
                Debug.LogError(
                    "[MainMenuPlayButton] ClientWorld is missing. " +
                    "Run Titan Orbit > Configure Multiplayer For Local Play, then press Unity Play using the Main Editor Game tab.");
                return;
            }

            if (TitanOrbitSessionManager.Instance == null)
            {
                Debug.LogError("[MainMenuPlayButton] TitanOrbitSessionManager missing on NceGameRoot.");
                return;
            }

            TitanOrbitSessionManager.Instance.StartLocalPlay();
        }

        static bool HasPlayableClientWorld()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
                return false;
            return client.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
        }

        void DisableChildRaycasts()
        {
            foreach (var graphic in GetComponentsInChildren<Graphic>(true))
            {
                if (graphic.gameObject != gameObject)
                    graphic.raycastTarget = false;
            }
        }
    }
}
