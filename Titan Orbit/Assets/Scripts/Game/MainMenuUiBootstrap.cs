using System.Collections;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Guarantees the Main Menu layout exists even when <see cref="NceGameFlowController"/> fails to
    /// initialize. Runs after scene load on client presentation processes only.
    /// Delegates visuals to <see cref="MainMenuPresenter"/> (logo, account bar, stacked buttons).
    /// </summary>
    public static class MainMenuUiBootstrap
    {
        /// <summary>Hidden DontDestroy runner that retries layout for a few frames.</summary>
        const string BootstrapObjectName = "MainMenuUiBootstrapRunner";

        /// <summary>True after a successful <see cref="EnsureButtonsCreated"/> pass.</summary>
        static bool s_Created;

        /// <summary>
        /// [UNITY] AfterSceneLoad — dedicated servers skip client UI entirely.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            Debug.Log("[MainMenuUiBootstrap] AfterSceneLoad (editor=" + Application.isEditor +
                      ", buildTarget=" + Application.platform + ")");
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
            {
                Debug.Log("[MainMenuUiBootstrap] Skipped — client presentation disabled for this process.");
                return;
            }

            var runner = new GameObject(BootstrapObjectName);
            Object.DontDestroyOnLoad(runner);
            runner.hideFlags = HideFlags.HideAndDontSave;
            runner.AddComponent<Runner>().Begin();
        }

        /// <summary>
        /// Builds logo / account / Join game / Local client via <see cref="MainMenuPresenter"/>.
        /// Idempotent — safe if <see cref="NceGameFlowController"/> already presented the menu.
        /// </summary>
        public static void EnsureButtonsCreated()
        {
            if (s_Created)
                return;

            var panel = FindSceneObjectByName("MainMenuPanel");
            if (panel == null)
            {
                Debug.LogError("[MainMenuUiBootstrap] MainMenuPanel not found in scene.");
                return;
            }

            var playButton = FindSceneObjectByName("PlayButton")?.GetComponent<UnityEngine.UI.Button>();

            MainMenuPresenter.Apply(
                panel,
                playButton,
                OnJoinGameClicked,
                OnLocalClientClicked,
                out _);

            s_Created = true;
            Debug.Log("[MainMenuUiBootstrap] Main menu layout ready.");
        }

        /// <summary>Opens the Join game browser overlay.</summary>
        static void OnJoinGameClicked()
        {
            Debug.Log("[MainMenuUiBootstrap] Join game clicked.");
            var root = TitanOrbitSessionManager.Instance != null
                ? TitanOrbitSessionManager.Instance.gameObject
                : FindSceneObjectByName("NceGameRoot");

            if (root == null)
            {
                Debug.LogError("[MainMenuUiBootstrap] NceGameRoot / SessionManager not found.");
                return;
            }

            var browser = root.GetComponent<JoinGameBrowserController>();
            if (browser == null)
                browser = root.AddComponent<JoinGameBrowserController>();

            var panel = FindSceneObjectByName("MainMenuPanel");
            browser.Configure(panel);
            browser.Show();
        }

        /// <summary>LAN / MPPM second-window client join.</summary>
        static void OnLocalClientClicked()
        {
            Debug.Log("[MainMenuUiBootstrap] Local client clicked.");
            if (TitanOrbitSessionManager.Instance == null)
            {
                Debug.LogError("[MainMenuUiBootstrap] TitanOrbitSessionManager missing.");
                return;
            }

            TitanOrbitSessionManager.Instance.StartLocalClientForLanTest();
        }

        /// <summary>
        /// Finds a loaded-scene object by exact name (includes inactive). Avoids DontDestroy orphans.
        /// </summary>
        static GameObject FindSceneObjectByName(string objectName)
        {
            var transforms = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < transforms.Length; i++)
            {
                var transform = transforms[i];
                if (transform.name != objectName)
                    continue;
                var scene = transform.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;
                return transform.gameObject;
            }

            return null;
        }

        /// <summary>Coroutine host that retries layout until MainMenuPanel exists.</summary>
        sealed class Runner : MonoBehaviour
        {
            /// <summary>Starts the retry loop.</summary>
            public void Begin() => StartCoroutine(Run());

            /// <summary>Up to 30 frames — covers late scene activation.</summary>
            IEnumerator Run()
            {
                for (int i = 0; i < 30 && !s_Created; i++)
                {
                    EnsureButtonsCreated();
                    if (s_Created)
                        yield break;
                    yield return null;
                }

                if (!s_Created)
                    Debug.LogError("[MainMenuUiBootstrap] Gave up creating main menu layout after 30 frames.");
            }
        }
    }
}
