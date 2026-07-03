#if UNITY_EDITOR
using System.IO;
using TitanOrbit.Data;
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
        const string DefaultShipFamilyPath = "Assets/Prefabs/Ships/AstroEagle/AstroEagleShipFamily.asset";
        const string DefaultHomePlanetPath = "Assets/Prefabs/HomePlanet.prefab";
        const string DefaultNeutralPlanetPath = "Assets/Prefabs/Planet.prefab";
        const string DefaultAsteroidPath = "Assets/Prefabs/Asteroid.prefab";
        const string DefaultGemPath = "Assets/Prefabs/Gem.prefab";
        const string DefaultPeopleTransportPath = "Assets/Prefabs/PeopleTransport.prefab";
        const string DefaultPlanetMaterialPoolPath = "Assets/Data/PlanetMaterialPool.asset";
        const string DefaultMapGenerationSettingsPath = "Assets/Data/MapGenerationSettings.asset";
        const string DefaultBulletVfxBankPath = "Assets/Data/BulletVfxBank.asset";

        [MenuItem("Titan Orbit/Create Bullet VFX Bank")]
        public static void CreateBulletVfxBankMenu()
        {
            var bank = BulletVfxBankSetup.EnsureAsset();
            if (bank != null)
                Debug.Log($"[BulletVfxBankSetup] Bullet VFX bank ready at {DefaultBulletVfxBankPath}");
        }

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
            ApplyLocalPlayNetCodePrefs();

            EditorUtility.DisplayDialog(
                "Titan Orbit — local play setup",
                "PlayMode Tools (NetCode) updated:\n\n" +
                "• PlayMode Type → Client & Server\n" +
                "• Auto-connect → 127.0.0.1:7777\n" +
                "• Num Thin Clients → 0\n\n" +
                "Use the main Editor Game tab and press Play on the main menu.\n\n" +
                "For a second human player: Titan Orbit > Configure Multiplayer For MPPM (2 Players), " +
                "then set Player 2 Role → Client in Window > Play Mode > Scenarios.",
                "OK");

            Debug.Log("[NetCodeGameSetup] Local multiplayer prefs applied. Open Window > Multiplayer > PlayMode Tools to verify.");
        }

        [MenuItem("Titan Orbit/Configure Multiplayer For MPPM (2 Players)")]
        public static void ConfigureMultiplayerForMppmTwoPlayers()
        {
            ApplyLocalPlayNetCodePrefs();
            MppmBuildProfileSetup.CreateMppmClientBuildProfile();
        }

        static void ApplyLocalPlayNetCodePrefs()
        {
            MultiplayerPlayModePreferences.RequestedPlayType = ClientServerBootstrap.PlayType.ClientAndServer;
            MultiplayerPlayModePreferences.SimulateDedicatedServer = false;
            MultiplayerPlayModePreferences.RequestedNumThinClients = 0;
            MultiplayerPlayModePreferences.SimulatorEnabled = false;
            MultiplayerPlayModePreferences.AutoConnectionAddress = "127.0.0.1";
            MultiplayerPlayModePreferences.AutoConnectionPort = 7777;
        }

        static void EnsureGhostPrefabs()
        {
            if (!File.Exists(Path.Combine(Application.dataPath, "Prefabs/ECS/StarshipGhost.prefab")) ||
                !File.Exists(Path.Combine(Application.dataPath, "Prefabs/ECS/PeopleTransportGhost.prefab")))
                GhostPrefabCreator.CreateGhostPrefabs();
        }

        static SceneAsset EnsureGameplaySubScene()
        {
            GhostPrefabCreator.EnsureMapGenerationSettingsAsset();

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

            if (root.GetComponent<PeopleTransferPopupPresenter>() == null)
                root.AddComponent<PeopleTransferPopupPresenter>();

            if (root.GetComponent<MatchEndScreenController>() == null)
                root.AddComponent<MatchEndScreenController>();

            if (root.GetComponent<DeathScreenController>() == null)
                root.AddComponent<DeathScreenController>();

            WireEcsWorldVisualizer(root);
            WireMapGenerationSettingsLoader(root);

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

        static void WireEcsWorldVisualizer(GameObject root)
        {
            var visualizer = root.GetComponent<EcsWorldVisualizer>();
            if (visualizer == null)
                return;

            var so = new SerializedObject(visualizer);

            var family = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(DefaultShipFamilyPath);
            if (family != null)
                so.FindProperty("shipFamily").objectReferenceValue = family;
            else
                Debug.LogWarning($"[NetCodeGameSetup] Ship family not found at {DefaultShipFamilyPath}.");

            var homePlanet = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultHomePlanetPath);
            if (homePlanet != null)
                so.FindProperty("homePlanetVisualPrefab").objectReferenceValue = homePlanet;
            else
                Debug.LogWarning($"[NetCodeGameSetup] Home planet prefab not found at {DefaultHomePlanetPath}.");

            var neutralPlanet = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultNeutralPlanetPath);
            if (neutralPlanet != null)
                so.FindProperty("neutralPlanetVisualPrefab").objectReferenceValue = neutralPlanet;
            else
                Debug.LogWarning($"[NetCodeGameSetup] Planet prefab not found at {DefaultNeutralPlanetPath}.");

            var asteroid = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultAsteroidPath);
            if (asteroid != null)
                so.FindProperty("asteroidVisualPrefab").objectReferenceValue = asteroid;
            else
                Debug.LogWarning($"[NetCodeGameSetup] Asteroid prefab not found at {DefaultAsteroidPath}.");

            var gem = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultGemPath);
            if (gem != null)
                so.FindProperty("gemVisualPrefab").objectReferenceValue = gem;
            else
                Debug.LogWarning($"[NetCodeGameSetup] Gem prefab not found at {DefaultGemPath}.");

            var peopleTransport = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultPeopleTransportPath);
            if (peopleTransport != null)
                so.FindProperty("peopleTransportVisualPrefab").objectReferenceValue = peopleTransport;
            else
                Debug.LogWarning($"[NetCodeGameSetup] People transport prefab not found at {DefaultPeopleTransportPath}.");

            var materialPool = AssetDatabase.LoadAssetAtPath<PlanetMaterialPool>(DefaultPlanetMaterialPoolPath);
            if (materialPool != null)
                so.FindProperty("planetMaterialPool").objectReferenceValue = materialPool;
            else
                Debug.LogWarning($"[NetCodeGameSetup] Planet material pool not found at {DefaultPlanetMaterialPoolPath}.");

            var bulletBank = BulletVfxBankSetup.EnsureAsset()
                ?? AssetDatabase.LoadAssetAtPath<BulletVfxBank>(DefaultBulletVfxBankPath);
            if (bulletBank != null)
                so.FindProperty("bulletVfxBank").objectReferenceValue = bulletBank;
            else
                Debug.LogWarning($"[NetCodeGameSetup] Bullet VFX bank not found at {DefaultBulletVfxBankPath}.");

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void WireMapGenerationSettingsLoader(GameObject root)
        {
            var loader = root.GetComponent<MapGenerationSettingsLoader>();
            if (loader == null)
                loader = root.AddComponent<MapGenerationSettingsLoader>();

            var settings = GhostPrefabCreator.EnsureMapGenerationSettingsAsset();
            if (settings == null)
            {
                Debug.LogWarning($"[NetCodeGameSetup] Map generation settings not found at {DefaultMapGenerationSettingsPath}.");
                return;
            }

            var so = new SerializedObject(loader);
            so.FindProperty("settings").objectReferenceValue = settings;
            so.ApplyModifiedPropertiesWithoutUndo();
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
            AssignButton(so, "teamDButton", FindJoinButtonForTeam("D"));
            AssignButton(so, "teamEButton", FindJoinButtonForTeam("E"));

            AssignPanel(so, "teamAPanel", "TeamAPanel");
            AssignPanel(so, "teamBPanel", "TeamBPanel");
            AssignPanel(so, "teamCPanel", "TeamCPanel");
            AssignPanel(so, "teamDPanel", "TeamDPanel");
            AssignPanel(so, "teamEPanel", "TeamEPanel");

            var autoPickProp = so.FindProperty("autoPickTeamAInEditor");
            if (autoPickProp != null)
                autoPickProp.boolValue = false;

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
