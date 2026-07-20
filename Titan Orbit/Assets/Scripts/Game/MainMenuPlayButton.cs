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
    /// Reliable Play click handler independent of NceGameFlowController wiring. Validates NetCode
    /// worlds before starting local play or dedicated quick-join, and surfaces errors on the menu.
    /// </summary>
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

#if UNITY_SERVER && !UNITY_EDITOR
            ReportMenuError(
                "Wrong window: use the main Editor Game tab, not the Server player window.");
            return;
#endif

            if (!HasPlayableClientWorld())
            {
                ReportMenuError(
                    "ClientWorld missing. Run Titan Orbit > Configure Multiplayer For Local Play, " +
                    "then press Play on the main Editor Game tab.");
                return;
            }

            // [TITAN-ORBIT] Editor starts ClientWorld-only; StartLocalPlay recreates ServerWorld.

            if (TitanOrbitSessionManager.Instance == null)
            {
                ReportMenuError("TitanOrbitSessionManager missing on NceGameRoot.");
                return;
            }

            if (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions)
            {
                if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                    TitanOrbitSessionManager.Instance.StartLocalClientForLanTest();
                else
                    TitanOrbitSessionManager.Instance.StartLocalPlay();
            }
            else
            {
                ReportMenuStatus("Finding a dedicated match...");
                _ = TitanOrbitSessionManager.Instance.QuickJoinDedicatedAsync();
            }
        }

        static void ReportMenuError(string message)
        {
            Debug.LogError("[MainMenuPlayButton] " + message);
            ReportMenuStatus(message);
        }

        static void ReportMenuStatus(string message)
        {
            var flow = Object.FindAnyObjectByType<NceGameFlowController>();
            if (flow != null)
                flow.SetMainMenuStatus(message);
        }

        static bool HasPlayableClientWorld()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
                return false;
            return client.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
        }

        static bool HasPlayableServerWorld()
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;
            return server.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
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
