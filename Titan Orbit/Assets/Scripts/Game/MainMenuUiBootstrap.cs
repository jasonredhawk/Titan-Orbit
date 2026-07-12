using System.Collections;
using TitanOrbit.Data;
using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Guarantees Join game / Local host / Local client buttons exist even when
    /// <see cref="NceGameFlowController"/> fails to initialize. Runs after scene load.
    /// </summary>
    public static class MainMenuUiBootstrap
    {
        const string BootstrapObjectName = "MainMenuUiBootstrapRunner";

        static bool s_Created;

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

            var playButton = FindSceneObjectByName("PlayButton")?.GetComponent<Button>();
            float y = playButton != null
                ? playButton.GetComponent<RectTransform>().anchoredPosition.y - 56f
                : -170f;

            CreateOrWireButton(panel.transform, "BrowseGamesButton", "Join game", y, OnJoinGameClicked);
            y -= 56f;

            if (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions)
            {
                if (!TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                {
                    CreateOrWireButton(panel.transform, "LocalHostButton", "Local host", y, OnLocalHostClicked);
                    y -= 48f;
                }

                CreateOrWireButton(panel.transform, "LocalClientButton", "Local client", y, OnLocalClientClicked);
            }

            EnsureStatusText(panel.transform);
            s_Created = true;
            Debug.Log("[MainMenuUiBootstrap] Main menu buttons ready.");
        }

        static void CreateOrWireButton(
            Transform parent,
            string name,
            string label,
            float y,
            UnityEngine.Events.UnityAction onClick)
        {
            Transform existing = parent.Find(name);
            GameObject go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            if (existing == null)
                go.transform.SetParent(parent, false);
            go.transform.SetAsLastSibling();

            var playButton = FindSceneObjectByName("PlayButton")?.GetComponent<RectTransform>();
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = playButton != null ? playButton.anchorMin : new Vector2(0.5f, 0.5f);
            rt.anchorMax = playButton != null ? playButton.anchorMax : new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(320f, 44f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.localScale = Vector3.one;

            var image = go.GetComponent<Image>();
            image.color = new Color(0.11f, 0.17f, 0.28f, 0.98f);

            var textGo = go.transform.Find("Text")?.gameObject;
            if (textGo == null)
            {
                textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                textGo.transform.SetParent(go.transform, false);
            }

            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 20f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;

            var button = go.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(onClick);
            button.interactable = true;
            go.SetActive(true);
        }

        static void EnsureStatusText(Transform panel)
        {
            var statusGo = panel.Find("MainMenuStatus");
            if (statusGo != null)
                return;

            var go = new GameObject("MainMenuStatus", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(panel, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.12f);
            rt.anchorMax = new Vector2(0.5f, 0.12f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(680f, 80f);
            rt.anchoredPosition = Vector2.zero;

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.fontSize = 16f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.75f, 0.85f, 0.95f, 0.95f);
            tmp.raycastTarget = false;
            tmp.text = "For Docker: Join game. For local dev: Local host.";
        }

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

        static void OnLocalHostClicked()
        {
            Debug.Log("[MainMenuUiBootstrap] Local host clicked.");
            if (TitanOrbitSessionManager.Instance == null)
            {
                Debug.LogError("[MainMenuUiBootstrap] TitanOrbitSessionManager missing.");
                return;
            }

            TitanOrbitSessionManager.Instance.StartLocalPlay();
        }

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

        sealed class Runner : MonoBehaviour
        {
            public void Begin() => StartCoroutine(Run());

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
                    Debug.LogError("[MainMenuUiBootstrap] Gave up creating main menu buttons after 30 frames.");
            }
        }
    }
}
