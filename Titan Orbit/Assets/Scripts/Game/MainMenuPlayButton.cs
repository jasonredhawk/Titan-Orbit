using TitanOrbit.Data;
using TitanOrbit.NetCode;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Reliable Play click handler for the main menu. Validates NetCode ClientWorld before starting
    /// local play or dedicated quick-join, and surfaces errors on the menu status line.
    ///
    /// Uses <see cref="Button.onClick"/> only (not <c>IPointerClickHandler</c>) so we never double-fire
    /// with a second listener from <see cref="NceGameFlowController"/>.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class MainMenuPlayButton : MonoBehaviour
    {
        /// <summary>[UNITY] UGUI Button on this GameObject — sole click entry point.</summary>
        Button _button;

        /// <summary>[UNITY] Cache button and wire a single onClick listener.</summary>
        void Awake()
        {
            _button = GetComponent<Button>();
            DisableChildRaycasts();
            WireClick();
        }

        /// <summary>Re-wire if the component is re-enabled after a panel hide/show cycle.</summary>
        void OnEnable()
        {
            DisableChildRaycasts();
            WireClick();
        }

        /// <summary>Removes then adds our handler so Enable cycles do not stack listeners.</summary>
        void WireClick()
        {
            if (_button == null)
                _button = GetComponent<Button>();
            if (_button == null)
                return;

            // [STANDARD] One listener only — NceGameFlowController must NOT also AddListener(OnPlayClicked).
            _button.onClick.RemoveListener(OnPlayClicked);
            _button.onClick.AddListener(OnPlayClicked);
        }

        /// <summary>Unhook so destroyed menus do not keep callbacks.</summary>
        void OnDisable()
        {
            if (_button != null)
                _button.onClick.RemoveListener(OnPlayClicked);
        }

        /// <summary>
        /// Primary Play action: Local play / Local client (MPPM) / Quick join dedicated.
        /// </summary>
        void OnPlayClicked()
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

        /// <summary>Logs and mirrors the error onto the main menu status TMP.</summary>
        static void ReportMenuError(string message)
        {
            Debug.LogError("[MainMenuPlayButton] " + message);
            ReportMenuStatus(message);
        }

        /// <summary>Finds the live flow controller and updates its status line.</summary>
        static void ReportMenuStatus(string message)
        {
            var flow = Object.FindAnyObjectByType<NceGameFlowController>();
            if (flow != null)
                flow.SetMainMenuStatus(message);
        }

        /// <summary>
        /// [NETCODE] True when ClientWorld exists and already has a NetworkStreamDriver singleton.
        /// </summary>
        static bool HasPlayableClientWorld()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
                return false;
            return client.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0;
        }

        /// <summary>
        /// Child graphics must not steal raycasts from the Button Image — otherwise clicks miss.
        /// </summary>
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
