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
    /// One-click setup for the NetCode for Entities vertical slice in the active scene.
    /// Wires subscene, prefab registry, input actions, and default data assets for local MPPM testing.
    /// Editor-only — not compiled into player or dedicated server builds.
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
        const string DefaultPlanetMaterialPoolPath = "Assets/Resources/PlanetMaterialPool.asset";
        const string DefaultMapGenerationSettingsPath = "Assets/Resources/MapGenerationSettings.asset";
        const string DefaultBulletVfxBankPath = BulletVfxBank.ResourcesAssetPath;

        [MenuItem("Titan Orbit/Create Bullet VFX Bank")]
        public static void CreateBulletVfxBankMenu()
        {
            // --- Create instance ---
            var bank = BulletVfxBankSetup.EnsureAsset();
            if (bank != null)
                Debug.Log($"[BulletVfxBankSetup] Bullet VFX bank ready at {DefaultBulletVfxBankPath}");
        }

        [MenuItem("Titan Orbit/Setup NetCode Game (Full)")]
        public static void SetupActiveScene()
        {
            // --- SetupActiveScene ---
            EnsureGhostPrefabs();
            var subSceneAsset = EnsureGameplaySubScene();
            DisableLegacyNgoObjects();
            EnsureBootstrapObjects();
            TitanOrbitMultiplayerConfigEditor.EnsureAsset();
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
            // --- Menu wrapper ---
            // Same work as Game Manager → Multiplayer Mode → Test; dialog explains the result.
            ApplyTestMode();

            EditorUtility.DisplayDialog(
                "Titan Orbit — local play setup",
                "PlayMode Tools (NetCode) updated:\n\n" +
                "• PlayMode Type → Client & Server\n" +
                "• Auto-connect port → 0 (pick Local play or Join game on the menu)\n" +
                "• Num Thin Clients → 0\n\n" +
                "Press Play, then choose Local play, Join game, or Local host/client.\n\n" +
                "Tip: you can also flip Test / Production on NceGameRoot → Game Manager.\n\n" +
                "For a second human player: Titan Orbit > Configure Multiplayer For MPPM (2 Players), " +
                "then set Player 2 Role → Client in Window > Play Mode > Scenarios.",
                "OK");
        }

        [MenuItem("Titan Orbit/Configure Multiplayer For Dedicated Server")]
        public static void ConfigureMultiplayerForDedicatedServer()
        {
            // --- Menu wrapper ---
            // Same work as Game Manager → Multiplayer Mode → Production; dialog explains the result.
            ApplyProductionMode();

            EditorUtility.DisplayDialog(
                "Titan Orbit — dedicated server join setup",
                "PlayMode Tools (NetCode) updated:\n\n" +
                "• PlayMode Type → Client (no local ServerWorld)\n" +
                "• Auto-connect port → 0 (manual Relay join only)\n" +
                "• Local play menu buttons → hidden\n" +
                "• MPPM scenario Main + Player 2 → Multiplayer Role Client\n\n" +
                "Stop Play on ALL instances, then Play from the Main Editor only.\n" +
                "Player 2 console must show buildSubTarget=Player (not Server).\n\n" +
                "Then Join game → GCE Relay on both editors.\n\n" +
                "Tip: you can also flip Test / Production on NceGameRoot → Game Manager.",
                "OK");
        }

        /// <summary>
        /// [EDITOR] Test mode — local Client &amp; Server worlds + Local play menu buttons.
        /// Same outcome as <see cref="ConfigureMultiplayerForLocalPlay"/> without the dialog.
        /// Called from the Game Manager Inspector toggle and the Titan Orbit menu.
        /// </summary>
        public static void ApplyTestMode()
        {
            // --- Test = Local Play ---
            // [NETCODE] ClientAndServer so Editor Play hosts a ServerWorld for LAN / local host.
            // [TITAN-ORBIT] ShowLocalPlayOptions reveals Local play / Local client on the main menu.
            ApplyLocalPlayNetCodePrefs();
            TitanOrbitMultiplayerConfigEditor.SetLocalPlayUiEnabled(true);
            Debug.Log("[NetCodeGameSetup] Test mode applied (Client & Server, Local play UI on). " +
                      "Open Window > Multiplayer > PlayMode Tools to verify.");
        }

        /// <summary>
        /// [EDITOR] Production mode — Client-only Editor + UGS/Relay join (no local ServerWorld).
        /// Same outcome as <see cref="ConfigureMultiplayerForDedicatedServer"/> without the dialog.
        /// Called from the Game Manager Inspector toggle and the Titan Orbit menu.
        /// </summary>
        public static void ApplyProductionMode()
        {
            // --- Production = Dedicated Server join ---
            // [NETCODE] PlayType.Client — Editor is a thin client against GCE / Edgegap.
            // [TITAN-ORBIT] Hide Local play buttons so the menu matches a shipped WebGL client.
            ApplyDedicatedJoinNetCodePrefs();
            TitanOrbitMultiplayerConfigEditor.SetLocalPlayUiEnabled(false);
            // basics45: MPPM Player 2 Role=Server → ghost schema mismatch / jitter.
            ForceMppmScenarioClientRoles();
            Debug.Log("[NetCodeGameSetup] Production mode applied (Client world, Local play UI off, MPPM roles Client). " +
                      "Restart Play mode if already running.");
        }

        /// <summary>
        /// Reads current NetCode PlayMode prefs + Local play UI flag and maps them to Test vs Production.
        /// Used by the Game Manager Inspector so the toolbar matches reality after menu changes.
        /// </summary>
        /// <returns>True when prefs look like Test (Client &amp; Server + local UI); otherwise Production.</returns>
        public static bool IsCurrentModeTest()
        {
            // --- Infer mode from live Editor prefs ---
            bool clientAndServer =
                MultiplayerPlayModePreferences.RequestedPlayType == ClientServerBootstrap.PlayType.ClientAndServer;
            bool localUi = TitanOrbitMultiplayerConfig.ShowLocalPlayOptions;
            return clientAndServer && localUi;
        }

        [MenuItem("Titan Orbit/Configure Multiplayer For MPPM (2 Players)")]
        public static void ConfigureMultiplayerForMppmTwoPlayers()
        {
            ApplyDedicatedJoinNetCodePrefs();
            ForceMppmScenarioClientRoles();
            MppmBuildProfileSetup.CreateMppmClientBuildProfile();
        }

        const string MppmScenarioPath = "Assets/Settings/PlayMode/TitanOrbitServer.asset";

        /// <summary>
        /// Forces Main Editor + Player 2 Multiplayer Role = Client on the scenario asset AND
        /// MPPM runtime cache (<c>Library/VP/SystemData.json</c>).
        /// Server role clones use Dedicated Server buildSubTarget and break NetCode ghost schemas.
        /// </summary>
        public static void ForceMppmScenarioClientRoles()
        {
            // Runtime evidence (basics45/46): SystemData MultiplayerRole 1 → Player 2
            // launches with -standaloneBuildSubtarget Server (TitanOrbitDedicatedServer.log).
            // Role values: Server=1, Client=2, ClientAndServer=3.
            const int roleClient = 2;

            // --- Scenario asset (source checked into Assets/Settings/PlayMode) ---
            var asset = AssetDatabase.LoadMainAssetAtPath(MppmScenarioPath);
            if (asset == null)
            {
                Debug.LogWarning("[NetCodeGameSetup] MPPM scenario missing at " + MppmScenarioPath);
            }
            else
            {
                var so = new SerializedObject(asset);
                var mainRole = so.FindProperty("m_MainEditorInstance.m_Role");
                if (mainRole != null)
                    mainRole.intValue = roleClient;

                var editors = so.FindProperty("m_EditorInstances");
                if (editors != null && editors.isArray)
                {
                    for (int i = 0; i < editors.arraySize; i++)
                    {
                        var role = editors.GetArrayElementAtIndex(i).FindPropertyRelative("m_Role");
                        if (role != null)
                            role.intValue = roleClient;
                    }
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                Debug.Log("[NetCodeGameSetup] MPPM scenario asset roles → Client: " + MppmScenarioPath);
            }

            // --- Runtime cache (what MPPM actually launches clones with) ---
            // Editing the .asset alone does NOT update this file; stale Role=1 kept Player 2 on Server.
            ForceMppmSystemDataClientRoles(roleClient);
        }

        /// <summary>
        /// Patches <c>Library/VP/SystemData.json</c> so active Main Editor + Player 2 use Client role.
        /// </summary>
        /// <param name="roleClient">Integer for Client (Unity MultiplayerRoleFlags / scenario: 2).</param>
        static void ForceMppmSystemDataClientRoles(int roleClient)
        {
            // Library/VP lives next to Assets (project root), not in the git repo root.
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string systemDataPath = Path.Combine(projectRoot, "Library", "VP", "SystemData.json");
            if (!File.Exists(systemDataPath))
            {
                Debug.LogWarning("[NetCodeGameSetup] MPPM SystemData.json missing (open Play Mode Scenarios once): " +
                                 systemDataPath);
                return;
            }

            string json = File.ReadAllText(systemDataPath);
            // [STANDARD] Targeted replace: only Server (1) → Client (2). Leave ClientAndServer (3) alone.
            string patched = System.Text.RegularExpressions.Regex.Replace(
                json,
                "\"MultiplayerRole\"\\s*:\\s*1\\b",
                "\"MultiplayerRole\": " + roleClient);

            if (patched == json)
            {
                Debug.Log("[NetCodeGameSetup] MPPM SystemData.json already has no Server roles (no Role=1).");
                return;
            }

            File.WriteAllText(systemDataPath, patched);
            Debug.Log("[NetCodeGameSetup] MPPM SystemData.json MultiplayerRole Server(1) → Client(" +
                      roleClient + "): " + systemDataPath);
        }

        static void ApplyLocalPlayNetCodePrefs()
        {
            // --- Apply changes ---
            MultiplayerPlayModePreferences.RequestedPlayType = ClientServerBootstrap.PlayType.ClientAndServer;
            MultiplayerPlayModePreferences.SimulateDedicatedServer = false;
            MultiplayerPlayModePreferences.RequestedNumThinClients = 0;
            MultiplayerPlayModePreferences.SimulatorEnabled = false;
            MultiplayerPlayModePreferences.AutoConnectionAddress = "127.0.0.1";
            MultiplayerPlayModePreferences.AutoConnectionPort = 0;
        }

        /// <summary>Editor client-only: join remote dedicated matches via UGS + Relay (no local ServerWorld).</summary>
        public static void ApplyDedicatedJoinNetCodePrefs()
        {
            // --- Apply changes ---
            MultiplayerPlayModePreferences.RequestedPlayType = ClientServerBootstrap.PlayType.Client;
            MultiplayerPlayModePreferences.SimulateDedicatedServer = false;
            MultiplayerPlayModePreferences.RequestedNumThinClients = 0;
            MultiplayerPlayModePreferences.SimulatorEnabled = false;
            MultiplayerPlayModePreferences.AutoConnectionAddress = "127.0.0.1";
            MultiplayerPlayModePreferences.AutoConnectionPort = 0;
        }

        static void EnsureGhostPrefabs()
        {
            // --- Ensure setup ---
            if (!File.Exists(Path.Combine(Application.dataPath, "Prefabs/ECS/StarshipGhost.prefab")) ||
                !File.Exists(Path.Combine(Application.dataPath, "Prefabs/ECS/PeopleTransportGhost.prefab")))
                GhostPrefabCreator.CreateGhostPrefabs();
        }

        static SceneAsset EnsureGameplaySubScene()
        {
            // --- Ensure setup ---
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
            // --- Ensure setup ---
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
            // --- Ensure setup ---
            var root = GameObject.Find("NceGameRoot") ?? new GameObject("NceGameRoot");

            if (root.GetComponent<TitanOrbitSessionManager>() == null)
                root.AddComponent<TitanOrbitSessionManager>();

            if (root.GetComponent<NceGameFlowController>() == null)
                root.AddComponent<NceGameFlowController>();

            if (root.GetComponent<JoinGameBrowserController>() == null)
                root.AddComponent<JoinGameBrowserController>();

            if (root.GetComponent<ShipInputBridge>() == null)
                root.AddComponent<ShipInputBridge>();

            if (root.GetComponent<ClientLocalBulletVfxBridge>() == null)
                root.AddComponent<ClientLocalBulletVfxBridge>();

            if (root.GetComponent<EcsWorldVisualizer>() == null)
                root.AddComponent<EcsWorldVisualizer>();

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

            if (root.GetComponent<WorldFloatingCountManager>() == null)
                root.AddComponent<WorldFloatingCountManager>();

            if (root.GetComponent<EcsFloatingCountPresenter>() == null)
                root.AddComponent<EcsFloatingCountPresenter>();

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
            // --- DisableLegacyNgoObjects ---
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

            var canvas = GameObject.Find("Canvas");
            if (canvas != null)
            {
                foreach (var mb in canvas.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var typeName = mb.GetType().FullName;
                    if (typeName != null && typeName.Contains("TitanOrbit.UI.MainMenu"))
                        mb.enabled = false;
                }
            }
        }

        static void DisableIfExists(string objectName)
        {
            // --- DisableIfExists ---
            var go = GameObject.Find(objectName);
            if (go != null)
                go.SetActive(false);
        }

        static void WireEcsWorldVisualizer(GameObject root)
        {
            // --- WireEcsWorldVisualizer ---
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
            // --- WireMapGenerationSettingsLoader ---
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
            // --- WireCamera ---
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
            // --- WireUiFlow ---
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
            // --- AssignPanel ---
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
            // --- AssignButton ---
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
            // --- FindJoinButtonForTeam ---
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
