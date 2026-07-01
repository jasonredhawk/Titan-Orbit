#if UNITY_EDITOR
using System.IO;
using TitanOrbit.Game;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using Unity.Entities;
using Unity.NetCode;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.InputSystem;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// One-click setup for the NCE vertical slice in the active scene.
    /// </summary>
    public static class NetCodeGameSetup
    {
        const string SubScenePath = "Assets/Scenes/GameplaySubScene.unity";
        const string RegistryPrefabPath = "Assets/Prefabs/ECS/GamePrefabsRegistry.prefab";
        const string InputActionsPath = "Assets/InputSystem_Actions.inputactions";

        [MenuItem("Titan Orbit/Setup NetCode Game (Full)")]
        public static void SetupActiveScene()
        {
            EnsureGhostPrefabs();
            var subSceneAsset = EnsureGameplaySubScene();
            DisableLegacyNgoObjects();
            EnsureBootstrapObjects();
            EnsureGameplaySubSceneReference(subSceneAsset);
            WireCamera();
            WireUiFlow();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[NetCodeGameSetup] Scene wired for NCE. Press Play (Client+Server), pick a team, fly with WASD + mouse.");
        }

        [MenuItem("Titan Orbit/Setup NetCode Scene")]
        public static void SetupSceneLegacy() => SetupActiveScene();

        [MenuItem("Titan Orbit/Configure Multiplayer For Local Play")]
        public static void ConfigureMultiplayerForLocalPlay()
        {
            MultiplayerPlayModePreferences.RequestedPlayType = ClientServerBootstrap.PlayType.ClientAndServer;
            MultiplayerPlayModePreferences.SimulateDedicatedServer = false;
            MultiplayerPlayModePreferences.RequestedNumThinClients = 0;
            MultiplayerPlayModePreferences.SimulatorEnabled = false;

            EditorUtility.DisplayDialog(
                "Titan Orbit — local play setup",
                "PlayMode Tools (NetCode) updated:\n\n" +
                "• PlayMode Type → Client & Server\n" +
                "• Server Emulation → Client Hosted Server\n" +
                "• Num Thin Clients → 0\n" +
                "• Client Network Emulation → off\n\n" +
                "Important — also check Unity's Play Mode dropdown:\n" +
                "Click the ▾ arrow next to the Play button (top centre). " +
                "Choose Default, or any scenario where the Main Editor is Client + Server (not Server-only).\n\n" +
                "Then press Play and use the main Editor Game tab.",
                "OK");

            Debug.Log("[NetCodeGameSetup] Local multiplayer prefs applied. Open Window > Multiplayer > PlayMode Tools to verify.");
        }

        static void EnsureGhostPrefabs()
        {
            if (!File.Exists(Path.Combine(Application.dataPath, "Prefabs/ECS/StarshipGhost.prefab")))
                GhostPrefabCreator.CreateGhostPrefabs();
        }

        static SceneAsset EnsureGameplaySubScene()
        {
            if (!File.Exists(SubScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                var registry = AssetDatabase.LoadAssetAtPath<GameObject>(RegistryPrefabPath);
                if (registry != null)
                    PrefabUtility.InstantiatePrefab(registry, scene);

                EditorSceneManager.SaveScene(scene, SubScenePath);
                EditorSceneManager.CloseScene(scene, true);
                AssetDatabase.Refresh();
            }

            return AssetDatabase.LoadAssetAtPath<SceneAsset>(SubScenePath);
        }

        static void EnsureGameplaySubSceneReference(SceneAsset subSceneAsset)
        {
            var existing = Object.FindAnyObjectByType<SubScene>();
            GameObject go;
            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = new GameObject("GameplaySubScene");
                existing = go.AddComponent<SubScene>();
            }

            existing.AutoLoadScene = true;
            if (subSceneAsset != null)
                existing.SceneAsset = subSceneAsset;
        }

        static void EnsureBootstrapObjects()
        {
            var root = GameObject.Find("NceGameRoot") ?? new GameObject("NceGameRoot");

            if (root.GetComponent<TitanOrbitSessionManager>() == null)
                root.AddComponent<TitanOrbitSessionManager>();

            if (root.GetComponent<NceGameFlowController>() == null)
                root.AddComponent<NceGameFlowController>();

            if (root.GetComponent<ShipInputBridge>() == null)
                root.AddComponent<ShipInputBridge>();

            var input = root.GetComponent<PlayerInputHandler>();
            if (input == null)
                input = root.AddComponent<PlayerInputHandler>();

            var inputAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
            if (inputAsset != null)
            {
                var so = new SerializedObject(input);
                so.FindProperty("inputActions").objectReferenceValue = inputAsset;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            if (root.GetComponent<EcsWorldVisualizer>() == null)
                root.AddComponent<EcsWorldVisualizer>();

            var duplicateSession = GameObject.Find("TitanOrbitSessionManager");
            if (duplicateSession != null && duplicateSession != root)
                Object.DestroyImmediate(duplicateSession);

            if (Object.FindAnyObjectByType<OverrideAutomaticNetcodeBootstrap>() == null)
            {
                var bootstrapGo = new GameObject("NetCodeBootstrapOverride");
                bootstrapGo.AddComponent<OverrideAutomaticNetcodeBootstrap>();
            }
        }

        static void DisableLegacyNgoObjects()
        {
            DisableIfExists("NetworkManager");
            DisableIfExists("MapGenerator");

            var gameManagers = GameObject.Find("GameManagers");
            if (gameManagers != null)
                gameManagers.SetActive(false);

            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                var typeName = mb.GetType().FullName;
                if (typeName == null) continue;
                if (typeName.Contains("NetworkGameManager") ||
                    typeName.Contains("TitanOrbit.UI.MainMenu") ||
                    typeName.Contains("TitanOrbit.UI.TeamSelectionUI") ||
                    typeName.Contains("TitanOrbit.UI.LoadingScreenController") ||
                    typeName.Contains("TitanOrbit.Camera.CameraController") ||
                    typeName.Contains("TitanOrbit.Core.LocalPlayerSetup"))
                {
                    mb.enabled = false;
                }
            }
        }

        static void DisableIfExists(string objectName)
        {
            var go = GameObject.Find(objectName);
            if (go != null)
                go.SetActive(false);
        }

        static void WireCamera()
        {
            var cam = UnityEngine.Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[NetCodeGameSetup] Main Camera not found.");
                return;
            }

            foreach (var mb in cam.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var n = mb.GetType().FullName;
                if (n != null && (n.Contains("CameraController") || n.Contains("LocalPlayerSetup")))
                    mb.enabled = false;
            }

            if (cam.GetComponent<CameraFollowEcs>() == null)
                cam.gameObject.AddComponent<CameraFollowEcs>();
        }

        static void WireUiFlow()
        {
            var flow = Object.FindAnyObjectByType<NceGameFlowController>();
            if (flow == null)
            {
                Debug.LogWarning("[NetCodeGameSetup] NceGameFlowController missing.");
                return;
            }

            var so = new SerializedObject(flow);

            AssignPanel(so, "mainMenuPanel", "MainMenuPanel");
            AssignPanel(so, "lobbyPanel", "LobbyPanel");
            AssignPanel(so, "teamSelectionPanel", "TeamSelectionPanel");
            AssignPanel(so, "loadingRoot", "LoadingScreenController");
            AssignPanel(so, "gameplayRoot", "HUD");
            AssignButton(so, "playButton", FindPlayButton());

            AssignButton(so, "teamAButton", FindJoinButtonForTeam("A"));
            AssignButton(so, "teamBButton", FindJoinButtonForTeam("B"));
            AssignButton(so, "teamCButton", FindJoinButtonForTeam("C"));

            so.ApplyModifiedPropertiesWithoutUndo();

            var loadingGo = GameObject.Find("LoadingScreenController");
            if (loadingGo != null)
            {
                var loadingNce = loadingGo.GetComponent<LoadingScreenControllerNce>();
                if (loadingNce == null)
                    loadingNce = loadingGo.AddComponent<LoadingScreenControllerNce>();

                var loadingSo = new SerializedObject(loadingNce);
                var loadingPanel = loadingGo.transform.Find("LoadingPanel")?.gameObject ?? loadingGo;
                var hud = GameObject.Find("HUD");
                var loadingProp = loadingSo.FindProperty("loadingRoot");
                var gameplayProp = loadingSo.FindProperty("gameplayRoot");
                if (loadingProp != null) loadingProp.objectReferenceValue = loadingPanel;
                if (gameplayProp != null && hud != null) gameplayProp.objectReferenceValue = hud;
                loadingSo.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void AssignPanel(SerializedObject so, string property, string objectName)
        {
            var go = GameObject.Find(objectName);
            if (go == null && objectName == "LobbyPanel")
                go = FindLobbyPanelRoot();
            if (go == null) return;
            var prop = so.FindProperty(property);
            if (prop != null)
                prop.objectReferenceValue = go;
        }

        static GameObject FindLobbyPanelRoot()
        {
            var teamPanel = GameObject.Find("TeamSelectionPanel");
            return teamPanel != null ? teamPanel.transform.parent?.gameObject : null;
        }

        static void AssignButton(SerializedObject so, string property, Button button)
        {
            if (button == null) return;
            var prop = so.FindProperty(property);
            if (prop != null)
                prop.objectReferenceValue = button;
        }

        static Button FindPlayButton()
        {
            var playGo = GameObject.Find("PlayButton");
            return playGo != null ? playGo.GetComponent<Button>() : null;
        }

        static Button FindJoinButtonForTeam(string letter)
        {
            var panels = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var t in panels)
            {
                if (t.name.Contains("Team" + letter) && t.name.Contains("Panel"))
                {
                    var join = t.Find("Content/JoinButton") ?? t.Find("JoinButton");
                    if (join != null && join.TryGetComponent(out Button btn))
                        return btn;

                    foreach (var button in t.GetComponentsInChildren<Button>(true))
                    {
                        if (button.gameObject.name == "JoinButton")
                            return button;
                    }
                }
            }

            var teamPanel = GameObject.Find("Team" + letter + "Panel");
            if (teamPanel != null)
            {
                var join = teamPanel.transform.Find("Content/JoinButton") ?? teamPanel.transform.Find("JoinButton");
                if (join != null && join.TryGetComponent(out Button btn))
                    return btn;
            }

            return null;
        }
    }
}
#endif
