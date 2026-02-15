using UnityEngine;
using Unity.Netcode;
using UnityEditor;
using TitanOrbit.Core;
using TitanOrbit.Networking;
using TitanOrbit.Camera;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using TitanOrbit.Generation;
using TitanOrbit.UI;
using TitanOrbit.Audio;
using TitanOrbit.Input;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using TMPro;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Editor script to set up the game scene with all necessary GameObjects
    /// </summary>
    public class GameSetup : EditorWindow
    {
        [MenuItem("Titan Orbit/Setup Game Scene")]
        public static void SetupGameScene()
        {
            // Create root objects
            GameObject networkManagerObj = CreateNetworkManager();
            GameObject gameManagersObj = CreateGameManagers();
            GameObject systemsObj = CreateSystems();
            GameObject mapGeneratorObj = CreateMapGenerator();
            GameObject cameraObj = CreateCamera();
            GameObject lightingObj = CreateLighting();
            GameObject uiObj = CreateUI();
            GameObject audioObj = CreateAudioManager();

            Debug.Log("Game scene setup complete! All GameObjects have been created.");
        }

        private static GameObject CreateNetworkManager()
        {
            GameObject obj = new GameObject("NetworkManager");
            Unity.Netcode.Transports.UTP.UnityTransport transport = obj.AddComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            NetworkManager networkManager = obj.AddComponent<NetworkManager>();
            networkManager.NetworkConfig.NetworkTransport = transport;

            // Assign Player Prefab so players spawn when connecting
            GameObject starshipPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Starship.prefab");
            if (starshipPrefab != null)
            {
                networkManager.NetworkConfig.PlayerPrefab = starshipPrefab;
            }

            return obj;
        }

        private static GameObject CreateGameManagers()
        {
            GameObject obj = new GameObject("GameManagers");
            obj.AddComponent<NetworkObject>(); // Required for NetworkBehaviour components
            
            // NetworkGameManager must be separate from NetworkManager GameObject
            obj.AddComponent<NetworkGameManager>();
            GameManager gameManager = obj.AddComponent<GameManager>();
            TeamManager teamManager = obj.AddComponent<TeamManager>();
            MatchManager matchManager = obj.AddComponent<MatchManager>();
            CrossPlatformManager crossPlatformManager = obj.AddComponent<CrossPlatformManager>();

            return obj;
        }

        private static GameObject CreateSystems()
        {
            GameObject obj = new GameObject("Systems");
            obj.AddComponent<NetworkObject>(); // Required for NetworkBehaviour components
            
            CombatSystem combatSystem = obj.AddComponent<CombatSystem>();
            GemSpawner gemSpawner = obj.AddComponent<GemSpawner>();
            AsteroidRespawnManager asteroidRespawn = obj.AddComponent<AsteroidRespawnManager>();
            MiningSystem miningSystem = obj.AddComponent<MiningSystem>();
            TransportSystem transportSystem = obj.AddComponent<TransportSystem>();
            CaptureSystem captureSystem = obj.AddComponent<CaptureSystem>();
            UpgradeSystem upgradeSystem = obj.AddComponent<UpgradeSystem>();
            AttributeUpgradeSystem attributeUpgradeSystem = obj.AddComponent<AttributeUpgradeSystem>();
            VisualEffectsManager visualEffectsManager = obj.AddComponent<VisualEffectsManager>();

            // Assign UpgradeTree so ship level-up menu and buttons work (full gems + home planet level)
            TitanOrbit.Data.UpgradeTree upgradeTree = AssetDatabase.LoadAssetAtPath<TitanOrbit.Data.UpgradeTree>("Assets/Data/UpgradeTree.asset");
            if (upgradeTree != null)
            {
                SerializedObject so = new SerializedObject(upgradeSystem);
                so.FindProperty("upgradeTree").objectReferenceValue = upgradeTree;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            return obj;
        }

        private static GameObject CreateMapGenerator()
        {
            GameObject obj = new GameObject("MapGenerator");
            obj.AddComponent<NetworkObject>(); // Required for NetworkBehaviour components
            MapGenerator mapGenerator = obj.AddComponent<MapGenerator>();

            return obj;
        }

        private static GameObject CreateCamera()
        {
            GameObject obj = new GameObject("Main Camera");
            UnityEngine.Camera cam = obj.AddComponent<UnityEngine.Camera>();
            CameraController cameraController = obj.AddComponent<CameraController>();

            // Set up camera for top-down 2D-style view of 3D scene
            obj.transform.position = new Vector3(0, 50, 0); // High above for top-down
            obj.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // Look straight down
            cam.orthographic = true;
            cam.orthographicSize = 12f; // Closer view of ship
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.02f, 0.05f); // Dark space background

            // Tag as MainCamera
            obj.tag = "MainCamera";

            // Add Audio Listener (required for audio playback)
            obj.AddComponent<AudioListener>();

            // Add LocalPlayerSetup to find and follow local player ship
            LocalPlayerSetup localPlayerSetup = obj.AddComponent<LocalPlayerSetup>();
            SerializedObject lpsSO = new SerializedObject(localPlayerSetup);
            lpsSO.FindProperty("cameraController").objectReferenceValue = cameraController;
            lpsSO.ApplyModifiedPropertiesWithoutUndo();

            return obj;
        }

        private static GameObject CreateLighting()
        {
            // Create directional light (main scene light) - brighter for better contrast
            GameObject lightObj = new GameObject("Directional Light");
            Light light = lightObj.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.98f, 0.95f); // Bright white
            light.intensity = 1.5f; // Increased intensity for better contrast
            light.shadows = LightShadows.Soft;

            // Position and rotate for top-down game (light from above)
            lightObj.transform.position = new Vector3(0, 20, 0);
            lightObj.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            
            // Add ambient light for better visibility
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.3f, 0.3f, 0.35f);
            RenderSettings.ambientEquatorColor = new Color(0.2f, 0.2f, 0.25f);
            RenderSettings.ambientGroundColor = new Color(0.1f, 0.1f, 0.15f);

            return lightObj;
        }

        private static GameObject CreateUI()
        {
            GameObject canvasObj = new GameObject("Canvas");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasObj.AddComponent<GraphicRaycaster>();

            // Create EventSystem (use InputSystemUIInputModule for new Input System)
            GameObject eventSystemObj = new GameObject("EventSystem");
            eventSystemObj.AddComponent<UnityEngine.EventSystems.EventSystem>();
            eventSystemObj.AddComponent<InputSystemUIInputModule>();

            Sprite uiSprite = CreateWhiteSprite();

            // Create Main Menu Panel
            GameObject mainMenuPanel = CreatePanel(canvasObj.transform, "MainMenuPanel", new Color(0.1f, 0.1f, 0.15f, 0.95f), uiSprite);
            RectTransform mainMenuRect = mainMenuPanel.GetComponent<RectTransform>();
            mainMenuRect.anchorMin = Vector2.zero;
            mainMenuRect.anchorMax = Vector2.one;
            mainMenuRect.offsetMin = Vector2.zero;
            mainMenuRect.offsetMax = Vector2.zero;

            // Title
            GameObject titleObj = CreateText(mainMenuPanel.transform, "Title", "TITAN ORBIT", 72, TextAnchor.MiddleCenter);
            RectTransform titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 0.8f);
            titleRect.anchorMax = new Vector2(0.5f, 0.9f);
            titleRect.pivot = new Vector2(0.5f, 0.5f);
            titleRect.anchoredPosition = Vector2.zero;
            titleRect.sizeDelta = new Vector2(800, 100);

            // Buttons
            GameObject startHostBtn = CreateButton(mainMenuPanel.transform, "StartHostButton", "Start Host", uiSprite);
            SetRect(startHostBtn, 0.5f, 0.5f, 0.5f, 0.5f, 0, 40, 250, 60);

            GameObject startServerBtn = CreateButton(mainMenuPanel.transform, "StartServerButton", "Start Server", uiSprite);
            SetRect(startServerBtn, 0.5f, 0.5f, 0.5f, 0.5f, 0, -30, 250, 60);

            GameObject startClientBtn = CreateButton(mainMenuPanel.transform, "StartClientButton", "Start Client", uiSprite);
            SetRect(startClientBtn, 0.5f, 0.5f, 0.5f, 0.5f, 0, -100, 250, 60);

            // Lobby Panel (hidden initially)
            GameObject lobbyPanel = CreatePanel(canvasObj.transform, "LobbyPanel", new Color(0.1f, 0.1f, 0.15f, 0.9f), uiSprite);
            RectTransform lobbyRect = lobbyPanel.GetComponent<RectTransform>();
            lobbyRect.anchorMin = Vector2.zero;
            lobbyRect.anchorMax = Vector2.one;
            lobbyRect.offsetMin = Vector2.zero;
            lobbyRect.offsetMax = Vector2.zero;
            lobbyPanel.SetActive(false);

            GameObject playerCountText = CreateText(lobbyPanel.transform, "PlayerCount", "Players: 0/60", 36, TextAnchor.MiddleCenter);
            SetRect(playerCountText, 0.5f, 0.7f, 0.5f, 0.7f, 0, 0, 400, 50);

            GameObject teamStatusText = CreateText(lobbyPanel.transform, "TeamStatus", "Team A: 0/20 | Team B: 0/20 | Team C: 0/20", 24, TextAnchor.MiddleCenter);
            SetRect(teamStatusText, 0.5f, 0.6f, 0.5f, 0.6f, 0, 0, 800, 40);

            // Main Menu component on Canvas (stays active when panel hidden)
            MainMenu mainMenu = canvasObj.AddComponent<MainMenu>();
            SerializedObject mainMenuSO = new SerializedObject(mainMenu);
            mainMenuSO.FindProperty("mainMenuPanel").objectReferenceValue = mainMenuPanel;
            mainMenuSO.FindProperty("lobbyPanel").objectReferenceValue = lobbyPanel;
            mainMenuSO.FindProperty("startServerButton").objectReferenceValue = startServerBtn.GetComponent<Button>();
            mainMenuSO.FindProperty("startHostButton").objectReferenceValue = startHostBtn.GetComponent<Button>();
            mainMenuSO.FindProperty("startClientButton").objectReferenceValue = startClientBtn.GetComponent<Button>();
            mainMenuSO.FindProperty("playerCountText").objectReferenceValue = playerCountText.GetComponent<TextMeshProUGUI>();
            mainMenuSO.FindProperty("teamStatusText").objectReferenceValue = teamStatusText.GetComponent<TextMeshProUGUI>();
            mainMenuSO.ApplyModifiedPropertiesWithoutUndo();

            // HUD (top-right corner)
            GameObject hudObj = CreatePanel(canvasObj.transform, "HUD", new Color(0, 0, 0, 0.3f), uiSprite);
            RectTransform hudRect = hudObj.GetComponent<RectTransform>();
            hudRect.anchorMin = new Vector2(1, 1);
            hudRect.anchorMax = new Vector2(1, 1);
            hudRect.pivot = new Vector2(1, 1);
            hudRect.anchoredPosition = new Vector2(-20, -20);
            hudRect.sizeDelta = new Vector2(250, 120);
            HUDController hudController = hudObj.AddComponent<HUDController>();

            GameObject hudText = CreateText(hudObj.transform, "HUDText", "Health: --\nGems: --\nPeople: --", 20, TextAnchor.UpperLeft);
            RectTransform hudTextRect = hudText.GetComponent<RectTransform>();
            hudTextRect.anchorMin = new Vector2(0, 1);
            hudTextRect.anchorMax = new Vector2(1, 1);
            hudTextRect.pivot = new Vector2(0.5f, 1);
            hudTextRect.anchoredPosition = new Vector2(0, -10);
            hudTextRect.offsetMin = new Vector2(15, 30);
            hudTextRect.offsetMax = new Vector2(-15, -10);

            // Minimap (bottom right corner, 20% of screen width, square)
            GameObject minimapObj = CreatePanel(canvasObj.transform, "Minimap", new Color(0, 0, 0, 0.4f), uiSprite);
            RectTransform minimapRect = minimapObj.GetComponent<RectTransform>();
            minimapRect.anchorMin = new Vector2(1, 0);
            minimapRect.anchorMax = new Vector2(1, 0);
            minimapRect.pivot = new Vector2(1, 0);
            // Position at bottom right with padding
            minimapRect.anchoredPosition = new Vector2(-20, 20);
            // Size: 20% of screen width, square aspect ratio
            // Use CanvasScaler to make it responsive - set size based on reference resolution
            // Assuming 1920x1080 reference, 20% = 384px
            float minimapSize = 384f; // Will scale with canvas
            minimapRect.sizeDelta = new Vector2(minimapSize, minimapSize);
            MinimapController minimapController = minimapObj.AddComponent<MinimapController>();
            
            // Set display size to match
            var minimapSO = new SerializedObject(minimapController);
            minimapSO.FindProperty("displaySize").floatValue = minimapSize;
            minimapSO.ApplyModifiedPropertiesWithoutUndo();

            GameObject minimapBorder = CreatePanel(minimapObj.transform, "Border", new Color(0.2f, 0.2f, 0.3f, 0.8f), uiSprite);
            RectTransform borderRect = minimapBorder.GetComponent<RectTransform>();
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = new Vector2(5, 5);
            borderRect.offsetMax = new Vector2(-5, -5);

            // Win/Loss Screen (hidden)
            GameObject winLossObj = CreatePanel(canvasObj.transform, "WinLossScreen", new Color(0, 0, 0, 0.9f), uiSprite);
            RectTransform winLossRect = winLossObj.GetComponent<RectTransform>();
            winLossRect.anchorMin = Vector2.zero;
            winLossRect.anchorMax = Vector2.one;
            winLossRect.offsetMin = Vector2.zero;
            winLossRect.offsetMax = Vector2.zero;
            winLossObj.SetActive(false);
            WinLossScreen winLossScreen = winLossObj.AddComponent<WinLossScreen>();
            SerializedObject winLossSO = new SerializedObject(winLossScreen);
            winLossSO.FindProperty("winLossPanel").objectReferenceValue = winLossObj;
            winLossSO.ApplyModifiedPropertiesWithoutUndo();

            // Mobile Controls (hidden on desktop)
            GameObject mobileControlsObj = new GameObject("MobileControls");
            mobileControlsObj.transform.SetParent(canvasObj.transform, false);
            mobileControlsObj.SetActive(Application.isMobilePlatform);
            mobileControlsObj.AddComponent<MobileControls>();

            // Orbit panel (shown when ship is in home planet orbit - deposit gems, load/unload people, future store)
            GameObject orbitPanelObj = CreatePanel(canvasObj.transform, "OrbitPanel", new Color(0.12f, 0.12f, 0.2f, 0.95f), uiSprite);
            RectTransform orbitRect = orbitPanelObj.GetComponent<RectTransform>();
            orbitRect.anchorMin = new Vector2(0.5f, 0.5f);
            orbitRect.anchorMax = new Vector2(0.5f, 0.5f);
            orbitRect.pivot = new Vector2(0.5f, 0.5f);
            orbitRect.anchoredPosition = new Vector2(-220f, 60f); // Left and up so it doesn't cover the starship
            orbitRect.sizeDelta = new Vector2(320, 280);
            orbitPanelObj.SetActive(false);
            HomePlanetOrbitUI orbitUI = orbitPanelObj.AddComponent<HomePlanetOrbitUI>();
            GameObject orbitTitle = CreateText(orbitPanelObj.transform, "Title", "Home Planet — In Orbit", 28, TextAnchor.MiddleCenter);
            RectTransform orbitTitleRect = orbitTitle.GetComponent<RectTransform>();
            orbitTitleRect.anchorMin = new Vector2(0, 1);
            orbitTitleRect.anchorMax = new Vector2(1, 1);
            orbitTitleRect.pivot = new Vector2(0.5f, 1);
            orbitTitleRect.anchoredPosition = new Vector2(0, -20);
            orbitTitleRect.sizeDelta = new Vector2(-30, 36);
            GameObject orbitInfo = CreateText(orbitPanelObj.transform, "PlanetInfo", "Level 1 | Gems: 0 | Pop: 0/100", 16, TextAnchor.MiddleCenter);
            RectTransform orbitInfoRect = orbitInfo.GetComponent<RectTransform>();
            orbitInfoRect.anchorMin = new Vector2(0, 1);
            orbitInfoRect.anchorMax = new Vector2(1, 1);
            orbitInfoRect.pivot = new Vector2(0.5f, 1);
            orbitInfoRect.anchoredPosition = new Vector2(0, -58);
            orbitInfoRect.sizeDelta = new Vector2(-30, 24);
            GameObject depositBtn = CreateButton(orbitPanelObj.transform, "DepositGemsButton", "Deposit Gems", uiSprite);
            SetRectAnchorTop(depositBtn, 90, 36);
            GameObject loadBtn = CreateButton(orbitPanelObj.transform, "LoadPeopleButton", "Load People", uiSprite);
            SetRectAnchorTop(loadBtn, 132, 36);
            GameObject unloadBtn = CreateButton(orbitPanelObj.transform, "UnloadPeopleButton", "Unload People", uiSprite);
            SetRectAnchorTop(unloadBtn, 174, 36);
            GameObject storeBtn = CreateButton(orbitPanelObj.transform, "StoreButton", "Store (coming soon)", uiSprite);
            SetRectAnchorTop(storeBtn, 216, 36);
            var orbitUISO = new SerializedObject(orbitUI);
            orbitUISO.FindProperty("orbitPanel").objectReferenceValue = orbitPanelObj;
            orbitUISO.FindProperty("titleText").objectReferenceValue = orbitTitle.GetComponent<TextMeshProUGUI>();
            orbitUISO.FindProperty("planetInfoText").objectReferenceValue = orbitInfo.GetComponent<TextMeshProUGUI>();
            orbitUISO.FindProperty("depositGemsButton").objectReferenceValue = depositBtn.GetComponent<Button>();
            orbitUISO.FindProperty("depositGemsLabel").objectReferenceValue = depositBtn.GetComponentInChildren<TextMeshProUGUI>();
            orbitUISO.FindProperty("loadPeopleButton").objectReferenceValue = loadBtn.GetComponent<Button>();
            orbitUISO.FindProperty("loadPeopleLabel").objectReferenceValue = loadBtn.GetComponentInChildren<TextMeshProUGUI>();
            orbitUISO.FindProperty("unloadPeopleButton").objectReferenceValue = unloadBtn.GetComponent<Button>();
            orbitUISO.FindProperty("unloadPeopleLabel").objectReferenceValue = unloadBtn.GetComponentInChildren<TextMeshProUGUI>();
            orbitUISO.FindProperty("storeButton").objectReferenceValue = storeBtn.GetComponent<Button>();
            orbitUISO.FindProperty("storeLabel").objectReferenceValue = storeBtn.GetComponentInChildren<TextMeshProUGUI>();
            orbitUISO.ApplyModifiedPropertiesWithoutUndo();

            // Starship upgrade menu (9 attribute buttons at bottom of screen)
            GameObject upgradeMenuObj = new GameObject("StarshipUpgradeMenu");
            upgradeMenuObj.transform.SetParent(canvasObj.transform, false);
            upgradeMenuObj.AddComponent<StarshipUpgradeMenu>();

            return canvasObj;
        }

        private static void SetRectAnchorTop(GameObject obj, float yFromTop, float height)
        {
            RectTransform rect = obj.GetComponent<RectTransform>();
            if (rect == null) return;
            rect.anchorMin = new Vector2(0.5f, 1);
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -yFromTop);
            rect.sizeDelta = new Vector2(260, height);
        }

        private static Sprite CreateWhiteSprite()
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        private static GameObject CreatePanel(Transform parent, string name, Color color, Sprite sprite)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            Image img = obj.AddComponent<Image>();
            img.color = color;
            img.sprite = sprite;
            return obj;
        }

        private static GameObject CreateText(Transform parent, string name, string content, int fontSize, TextAnchor anchor)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text = content;
            tmp.fontSize = fontSize;
            tmp.alignment = anchor == TextAnchor.MiddleCenter ? TextAlignmentOptions.Center : TextAlignmentOptions.TopLeft;
            tmp.color = Color.white;
            return obj;
        }

        private static GameObject CreateButton(Transform parent, string name, string label, Sprite sprite)
        {
            GameObject btnObj = new GameObject(name);
            btnObj.transform.SetParent(parent, false);
            Image img = btnObj.AddComponent<Image>();
            img.color = new Color(0.2f, 0.4f, 0.8f);
            img.sprite = sprite;

            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(btnObj.transform, false);
            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 24;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            Button btn = btnObj.AddComponent<Button>();
            ColorBlock colors = btn.colors;
            colors.highlightedColor = new Color(0.3f, 0.5f, 0.9f);
            colors.pressedColor = new Color(0.15f, 0.3f, 0.7f);
            btn.colors = colors;

            return btnObj;
        }

        private static void SetRect(GameObject obj, float anchorMinX, float anchorMinY, float anchorMaxX, float anchorMaxY, float posX, float posY, float width, float height)
        {
            RectTransform rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(anchorMinX, anchorMinY);
            rect.anchorMax = new Vector2(anchorMaxX, anchorMaxY);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(posX, posY);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static GameObject CreateAudioManager()
        {
            GameObject obj = new GameObject("AudioManager");
            AudioManager audioManager = obj.AddComponent<AudioManager>();

            return obj;
        }

        [MenuItem("Titan Orbit/Create Basic Prefabs")]
        public static void CreateBasicPrefabs()
        {
            CreateStarshipPrefab();
            CreatePlanetPrefab();
            CreateHomePlanetPrefab();
            CreateAsteroidPrefab();
            CreateBulletPrefab();
            CreateGemPrefab();

            Debug.Log("Basic prefabs created in Assets/Prefabs/");
        }

        private static void CreateStarshipPrefab()
        {
            // One custom hull mesh (arrowhead shape) + cockpit sphere. Single cohesive silhouette, no primitive soup.
            Mesh hullMesh = GetOrCreateStarshipHullMesh();
            GameObject ship = new GameObject("Starship");
            ship.transform.localPosition = Vector3.zero;
            ship.transform.localRotation = Quaternion.identity;
            ship.transform.localScale = Vector3.one;

            MeshFilter mf = ship.AddComponent<MeshFilter>();
            mf.sharedMesh = hullMesh;
            MeshRenderer mr = ship.AddComponent<MeshRenderer>();

            BoxCollider boxCol = ship.AddComponent<BoxCollider>();
            boxCol.size = new Vector3(0.5f, 0.12f, 1.1f);
            boxCol.center = new Vector3(0, 0, 0.02f);

            Rigidbody rb = ship.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.linearDamping = 5f;
            rb.angularDamping = 5f;

            ship.AddComponent<NetworkObject>();
            Starship starship = ship.AddComponent<Starship>();
            ShipTeamColor shipTeamColor = ship.AddComponent<ShipTeamColor>();
            ship.AddComponent<PlayerInputHandler>();

            Material bodyMat = CreateAndSaveMaterial("TitanOrbit_StarshipBody", new Color(0.18f, 0.18f, 0.2f), 2500);
            Material accentMat = CreateAndSaveMaterial("TitanOrbit_Starship", new Color(0.4f, 0.7f, 1f), 2500);
            var accentRenderersList = new System.Collections.Generic.List<Renderer>();
            const int sortBody = 10;
            const int sortAccent = 11;

            mr.sharedMaterial = bodyMat;
            mr.sortingOrder = sortBody;

            // Cockpit dome (team accent)
            GameObject cockpit = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cockpit.name = "Cockpit";
            cockpit.transform.SetParent(ship.transform);
            cockpit.transform.localPosition = new Vector3(0, 0.06f, 0.12f);
            cockpit.transform.localRotation = Quaternion.identity;
            cockpit.transform.localScale = new Vector3(0.28f, 0.16f, 0.28f);
            Object.DestroyImmediate(cockpit.GetComponent<Collider>());
            AddRenderer(cockpit.GetComponent<Renderer>(), accentMat, sortAccent, accentRenderersList);

            // Rear engine glow (team accent)
            GameObject engineL = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            engineL.name = "EngineL";
            engineL.transform.SetParent(ship.transform);
            engineL.transform.localPosition = new Vector3(-0.12f, 0, -0.42f);
            engineL.transform.localScale = new Vector3(0.12f, 0.12f, 0.12f);
            Object.DestroyImmediate(engineL.GetComponent<Collider>());
            AddRenderer(engineL.GetComponent<Renderer>(), accentMat, sortAccent, accentRenderersList);
            GameObject engineR = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            engineR.name = "EngineR";
            engineR.transform.SetParent(ship.transform);
            engineR.transform.localPosition = new Vector3(0.12f, 0, -0.42f);
            engineR.transform.localScale = new Vector3(0.12f, 0.12f, 0.12f);
            Object.DestroyImmediate(engineR.GetComponent<Collider>());
            AddRenderer(engineR.GetComponent<Renderer>(), accentMat, sortAccent, accentRenderersList);

            GameObject firePoint = new GameObject("FirePoint");
            firePoint.transform.SetParent(ship.transform);
            firePoint.transform.localPosition = new Vector3(0, 0, 0.55f);

            var starshipSO = new SerializedObject(starship);
            starshipSO.FindProperty("firePoint").objectReferenceValue = firePoint.transform;
            starshipSO.ApplyModifiedPropertiesWithoutUndo();

            var stcSO = new SerializedObject(shipTeamColor);
            SerializedProperty accentProp = stcSO.FindProperty("accentRenderers");
            accentProp.arraySize = accentRenderersList.Count;
            for (int i = 0; i < accentRenderersList.Count; i++)
                accentProp.GetArrayElementAtIndex(i).objectReferenceValue = accentRenderersList[i];
            stcSO.ApplyModifiedPropertiesWithoutUndo();

            string path = "Assets/Prefabs/Starship.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(ship, path);
            Object.DestroyImmediate(ship);

            Debug.Log($"Created prefab: {path}");
        }

        /// <summary>
        /// Creates a single hull mesh: smooth fighter silhouette (pointed nose, swept wings, narrow tail). Top-down view.
        /// </summary>
        private static Mesh GetOrCreateStarshipHullMesh()
        {
            const string meshPath = "Assets/Meshes/StarshipHull.asset";
            if (!AssetDatabase.IsValidFolder("Assets/Meshes"))
                AssetDatabase.CreateFolder("Assets", "Meshes");

            float halfThick = 0.05f;
            // Outline: nose -> right side -> tail -> left side -> back to nose. Z = forward, symmetric.
            // Smoother curve: sharp nose, flare to wing tips, taper to tail.
            Vector3[] topOutline = new Vector3[]
            {
                new Vector3(0, halfThick, 0.5f),       // 0 nose
                new Vector3(0.05f, halfThick, 0.4f),
                new Vector3(0.12f, halfThick, 0.25f),
                new Vector3(0.18f, halfThick, 0.08f),
                new Vector3(0.26f, halfThick, -0.06f),  // 4 wing tip
                new Vector3(0.24f, halfThick, -0.2f),
                new Vector3(0.18f, halfThick, -0.32f),
                new Vector3(0.1f, halfThick, -0.42f),
                new Vector3(0, halfThick, -0.5f),     // 8 tail
                new Vector3(-0.1f, halfThick, -0.42f),
                new Vector3(-0.18f, halfThick, -0.32f),
                new Vector3(-0.24f, halfThick, -0.2f),
                new Vector3(-0.26f, halfThick, -0.06f), // 12 wing tip
                new Vector3(-0.18f, halfThick, 0.08f),
                new Vector3(-0.12f, halfThick, 0.25f),
                new Vector3(-0.05f, halfThick, 0.4f),
            };
            int n = topOutline.Length;
            Vector3[] bottomOutline = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 t = topOutline[i];
                bottomOutline[i] = new Vector3(t.x, -halfThick, t.z);
            }

            Vector3[] verts = new Vector3[n * 2];
            for (int i = 0; i < n; i++) verts[i] = topOutline[i];
            for (int i = 0; i < n; i++) verts[n + i] = bottomOutline[i];

            var tris = new System.Collections.Generic.List<int>();
            // Top: fan from nose (0)
            for (int i = 1; i < n; i++)
            {
                int next = i + 1;
                if (next >= n) next = 1;
                tris.Add(0); tris.Add(i); tris.Add(next);
            }
            // Bottom: fan from tail (vertex 8). Order around bottom: 7,6,5,4,3,2,1,0,15,14,13,12,11,10,9
            int tailIdx = n + 8;
            int[] bottomOrder = { 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9 };
            for (int i = 0; i < bottomOrder.Length; i++)
            {
                int a = n + bottomOrder[i];
                int b = n + bottomOrder[(i + 1) % bottomOrder.Length];
                tris.Add(tailIdx); tris.Add(b); tris.Add(a);
            }
            // Sides
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                tris.Add(i); tris.Add(j); tris.Add(j + n);
                tris.Add(i); tris.Add(j + n); tris.Add(i + n);
            }

            Mesh mesh = new Mesh();
            mesh.name = "StarshipHull";
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();
            return mesh;
        }

        private static void AddRenderer(Renderer r, Material mat, int order, System.Collections.Generic.List<Renderer> accentList)
        {
            if (r == null) return;
            r.sharedMaterial = mat;
            r.sortingOrder = order;
            accentList.Add(r);
        }

        private static void CreatePlanetPrefab()
        {
            GameObject planet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            planet.name = "Planet";
            planet.transform.localScale = Vector3.one * 8f; // Much larger for orbit gameplay

            // Body collider = planet sphere (Unity default radius 0.5). Orbit zone = 0.5 to 0.6 (10% band)
            Object.DestroyImmediate(planet.GetComponent<Collider>());
            SphereCollider bodyCollider = planet.AddComponent<SphereCollider>();
            bodyCollider.isTrigger = false;
            bodyCollider.radius = 0.5f;

            // Add NetworkObject
            NetworkObject netObj = planet.AddComponent<NetworkObject>();

            // Add Planet script
            Planet planetScript = planet.AddComponent<Planet>();

            // Orbit zone: surface (0.5) to a bit farther (0.8 local)
            GameObject orbitZoneObj = new GameObject("OrbitZone");
            orbitZoneObj.transform.SetParent(planet.transform);
            orbitZoneObj.transform.localPosition = Vector3.zero;
            orbitZoneObj.transform.localScale = Vector3.one;
            SphereCollider orbitCollider = orbitZoneObj.AddComponent<SphereCollider>();
            orbitCollider.isTrigger = true;
            orbitCollider.radius = 0.8f;
            PlanetOrbitZone orbitZoneScript = orbitZoneObj.AddComponent<PlanetOrbitZone>();
            var orbitZoneSO = new SerializedObject(orbitZoneScript);
            orbitZoneSO.FindProperty("planet").objectReferenceValue = planetScript;
            orbitZoneSO.ApplyModifiedPropertiesWithoutUndo();

            // Add ToroidalRenderer for seamless map wrapping
            planet.AddComponent<ToroidalRenderer>();

            // Grey material for neutral planets - will change to team color when captured
            Renderer renderer = planet.GetComponent<Renderer>();
            Material neutralMat = CreateAndSaveMaterial("TitanOrbit_Planet", new Color(0.5f, 0.5f, 0.5f)); // Grey
            renderer.sharedMaterial = neutralMat;

            // Team materials (same names as HomePlanet so regular planets change colour when captured)
            Material teamAMat = CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamA", new Color(0.9f, 0.25f, 0.25f)); // Red
            Material teamBMat = CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamB", new Color(0.25f, 0.4f, 0.9f)); // Blue
            Material teamCMat = CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamC", new Color(0.25f, 0.85f, 0.35f)); // Green

            // Add ring - vertical orientation to differentiate from home planets (horizontal)
            GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "Ring";
            ring.transform.SetParent(planet.transform);
            ring.transform.localScale = new Vector3(1.2f, 0.05f, 1.2f);
            ring.transform.localRotation = Quaternion.Euler(0, 0, 0); // Vertical (standing up) instead of horizontal
            ring.transform.localPosition = Vector3.zero;
            Object.DestroyImmediate(ring.GetComponent<Collider>()); // Remove collider from ring
            ring.GetComponent<Renderer>().sharedMaterial = CreateAndSaveMaterial("TitanOrbit_PlanetRing", new Color(0f, 0.8f, 1f, 0.7f)); // Cyan ring

            // Population text: just above surface; negative X scale so text reads correctly (not mirrored)
            GameObject textObj = new GameObject("PopulationText");
            textObj.transform.SetParent(planet.transform);
            textObj.transform.localPosition = new Vector3(0f, 0.55f, 0f); // Just above sphere (radius 0.5)
            textObj.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            textObj.transform.localScale = new Vector3(0.04f, -0.04f, 0.04f); // +X: not mirrored, -Y: right-side up
            
            TextMeshPro textMesh = textObj.AddComponent<TextMeshPro>();
            textMesh.text = "0";
            textMesh.fontSize = 36;
            textMesh.color = Color.white;
            textMesh.alignment = TextAlignmentOptions.Center;
            textMesh.fontStyle = FontStyles.Bold;
            
            // Assign to planet script (renderer, materials for neutral + team colours when captured, population text)
            var planetSO = new SerializedObject(planetScript);
            planetSO.FindProperty("planetRenderer").objectReferenceValue = renderer;
            planetSO.FindProperty("neutralMaterial").objectReferenceValue = neutralMat;
            planetSO.FindProperty("teamAMaterial").objectReferenceValue = teamAMat;
            planetSO.FindProperty("teamBMaterial").objectReferenceValue = teamBMat;
            planetSO.FindProperty("teamCMaterial").objectReferenceValue = teamCMat;
            planetSO.FindProperty("populationText").objectReferenceValue = textMesh;
            planetSO.ApplyModifiedPropertiesWithoutUndo();

            // Save as prefab
            string path = "Assets/Prefabs/Planet.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(planet, path);
            Object.DestroyImmediate(planet);

            Debug.Log($"Created prefab: {path}");
        }

        private static void CreateHomePlanetPrefab()
        {
            GameObject homePlanet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            homePlanet.name = "HomePlanet";
            homePlanet.transform.localScale = Vector3.one * 20f; // Much larger - team base

            // Body collider = planet sphere (Unity default radius 0.5). Orbit zone = 0.5 to 0.6 (10% band)
            Object.DestroyImmediate(homePlanet.GetComponent<Collider>());
            SphereCollider bodyCollider = homePlanet.AddComponent<SphereCollider>();
            bodyCollider.isTrigger = false;
            bodyCollider.radius = 0.5f;

            // Add NetworkObject
            NetworkObject netObj = homePlanet.AddComponent<NetworkObject>();

            // Add HomePlanet script (which extends Planet)
            HomePlanet homePlanetScript = homePlanet.AddComponent<HomePlanet>();

            // Orbit zone: surface (0.5) to a bit farther (0.8 local)
            GameObject orbitZoneObj = new GameObject("OrbitZone");
            orbitZoneObj.transform.SetParent(homePlanet.transform);
            orbitZoneObj.transform.localPosition = Vector3.zero;
            orbitZoneObj.transform.localScale = Vector3.one;
            SphereCollider orbitCollider = orbitZoneObj.AddComponent<SphereCollider>();
            orbitCollider.isTrigger = true;
            orbitCollider.radius = 0.8f;
            PlanetOrbitZone orbitZoneScript = orbitZoneObj.AddComponent<PlanetOrbitZone>();
            var orbitZoneSO = new SerializedObject(orbitZoneScript);
            orbitZoneSO.FindProperty("planet").objectReferenceValue = homePlanetScript;
            orbitZoneSO.ApplyModifiedPropertiesWithoutUndo();

            // Add ToroidalRenderer for seamless map wrapping
            homePlanet.AddComponent<ToroidalRenderer>();

            // Default material - team color set at runtime by MapGenerator
            Renderer renderer = homePlanet.GetComponent<Renderer>();
            renderer.sharedMaterial = CreateAndSaveMaterial("TitanOrbit_HomePlanet", new Color(0.5f, 0.5f, 0.5f)); // Neutral default

            // Team-specific materials for home planets (A=red, B=blue, C=green)
            Material teamAMat = CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamA", new Color(0.9f, 0.25f, 0.25f)); // Red
            Material teamBMat = CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamB", new Color(0.25f, 0.4f, 0.9f)); // Blue
            Material teamCMat = CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamC", new Color(0.25f, 0.85f, 0.35f)); // Green

            var planetSO = new SerializedObject(homePlanetScript);
            planetSO.FindProperty("planetRenderer").objectReferenceValue = renderer;
            planetSO.FindProperty("neutralMaterial").objectReferenceValue = renderer.sharedMaterial;
            planetSO.FindProperty("teamAMaterial").objectReferenceValue = teamAMat;
            planetSO.FindProperty("teamBMaterial").objectReferenceValue = teamBMat;
            planetSO.FindProperty("teamCMaterial").objectReferenceValue = teamCMat;
            planetSO.ApplyModifiedPropertiesWithoutUndo();

            GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "Ring";
            ring.transform.SetParent(homePlanet.transform);
            ring.transform.localScale = new Vector3(1.2f, 0.05f, 1.2f);
            ring.transform.localRotation = Quaternion.Euler(90, 0, 0);
            ring.transform.localPosition = Vector3.zero;
            Object.DestroyImmediate(ring.GetComponent<Collider>());
            ring.GetComponent<Renderer>().sharedMaterial = CreateAndSaveMaterial("TitanOrbit_HomePlanetRing", new Color(1f, 1f, 1f, 0.6f)); // Ring color matches planet

            // Population text: just above surface; negative X scale so text reads correctly (not mirrored)
            GameObject textObj = new GameObject("PopulationText");
            textObj.transform.SetParent(homePlanet.transform);
            textObj.transform.localPosition = new Vector3(0f, 0.55f, 0f); // Just above sphere (radius 0.5)
            textObj.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            textObj.transform.localScale = new Vector3(0.04f, -0.04f, 0.04f); // +X: not mirrored, -Y: right-side up
            
            TextMeshPro textMesh = textObj.AddComponent<TextMeshPro>();
            textMesh.text = "0";
            textMesh.fontSize = 48;
            textMesh.color = Color.white;
            textMesh.alignment = TextAlignmentOptions.Center;
            textMesh.fontStyle = FontStyles.Bold;
            
            planetSO = new SerializedObject(homePlanetScript);
            planetSO.FindProperty("populationText").objectReferenceValue = textMesh;
            planetSO.ApplyModifiedPropertiesWithoutUndo();

            // Save as prefab
            string path = "Assets/Prefabs/HomePlanet.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(homePlanet, path);
            Object.DestroyImmediate(homePlanet);

            Debug.Log($"Created prefab: {path}");
        }

        /// <summary>
        /// Creates a lumpy, irregular asteroid mesh instead of a smooth sphere
        /// Saves the mesh as an asset so it persists in the prefab
        /// </summary>
        private static Mesh GetOrCreateLumpyAsteroidMesh()
        {
            // Check if mesh already exists
            string meshPath = "Assets/Meshes/LumpyAsteroid.asset";
            Mesh existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existingMesh != null)
            {
                return existingMesh;
            }
            
            // Create meshes folder if needed
            if (!AssetDatabase.IsValidFolder("Assets/Meshes"))
            {
                AssetDatabase.CreateFolder("Assets", "Meshes");
            }
            
            // Start with a sphere mesh and deform it
            GameObject tempSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh baseMesh = tempSphere.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tempSphere);
            
            // Create a new mesh with deformed vertices
            Mesh lumpyMesh = new Mesh();
            lumpyMesh.name = "LumpyAsteroid";
            
            Vector3[] vertices = baseMesh.vertices;
            Vector3[] normals = baseMesh.normals;
            Vector2[] uvs = baseMesh.uv;
            int[] triangles = baseMesh.triangles;
            
            // Deform vertices to create lumpy appearance
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 vertex = vertices[i];
                float distance = vertex.magnitude;
                
                // Use Perlin noise to create irregular bumps
                float noiseX = Mathf.PerlinNoise(vertex.x * 3f, vertex.y * 3f);
                float noiseY = Mathf.PerlinNoise(vertex.y * 3f + 100f, vertex.z * 3f);
                float noiseZ = Mathf.PerlinNoise(vertex.z * 3f + 200f, vertex.x * 3f);
                
                // Combine noise values
                float noise = (noiseX + noiseY + noiseZ) / 3f;
                
                // Create lumpy deformation (0.7 to 1.3 scale variation)
                float deformation = 0.7f + noise * 0.6f;
                
                // Apply deformation along the normal direction
                vertices[i] = vertex.normalized * (distance * deformation);
            }
            
            lumpyMesh.vertices = vertices;
            lumpyMesh.normals = normals;
            lumpyMesh.uv = uvs;
            lumpyMesh.triangles = triangles;
            lumpyMesh.RecalculateNormals();
            lumpyMesh.RecalculateBounds();
            
            // Save mesh as asset
            AssetDatabase.CreateAsset(lumpyMesh, meshPath);
            AssetDatabase.SaveAssets();
            
            return lumpyMesh;
        }
        
        private static void CreateAsteroidPrefab()
        {
            GameObject asteroid = new GameObject("Asteroid");
            
            // Get or create lumpy mesh (saved as asset)
            Mesh lumpyMesh = GetOrCreateLumpyAsteroidMesh();
            MeshFilter meshFilter = asteroid.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = lumpyMesh; // Use sharedMesh so it references the asset
            MeshRenderer meshRenderer = asteroid.AddComponent<MeshRenderer>();
            
            // Make it look more like an asteroid (randomize scale slightly)
            asteroid.transform.localScale = new Vector3(1.2f, 1.5f, 1.3f);

            // Non-trigger collider - use mesh collider for accurate lumpy shape
            MeshCollider collider = asteroid.AddComponent<MeshCollider>();
            collider.isTrigger = false;
            collider.sharedMesh = lumpyMesh;
            collider.convex = true; // Required for dynamic physics

            Rigidbody rb = asteroid.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // Detect fast-moving bullets/ships

            // Add NetworkObject
            NetworkObject netObj = asteroid.AddComponent<NetworkObject>();

            // Add Asteroid script
            Asteroid asteroidScript = asteroid.AddComponent<Asteroid>();

            // Add ToroidalRenderer for seamless map wrapping
            asteroid.AddComponent<ToroidalRenderer>();

            // Create crater-textured material for asteroids - darker brown with better contrast
            Material craterMat = CreateCraterMaterial();
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            {
                AssetDatabase.CreateFolder("Assets", "Materials");
            }
            string craterPath = "Assets/Materials/TitanOrbit_Asteroid_Crater.mat";
            AssetDatabase.CreateAsset(craterMat, craterPath);
            asteroid.GetComponent<Renderer>().sharedMaterial = craterMat;

            // Save as prefab
            string path = "Assets/Prefabs/Asteroid.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(asteroid, path);
            Object.DestroyImmediate(asteroid);

            Debug.Log($"Created prefab: {path}");
        }

        private static void CreateBulletPrefab()
        {
            GameObject bullet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bullet.name = "Bullet";
            bullet.transform.localScale = Vector3.one * 0.2f;

            // Remove default collider, add trigger collider
            Object.DestroyImmediate(bullet.GetComponent<Collider>());
            SphereCollider collider = bullet.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 0.5f; // Slightly larger for more reliable hits

            // Add Rigidbody with Continuous collision detection for fast bullets
            Rigidbody rb = bullet.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = false;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

            // Add NetworkObject
            NetworkObject netObj = bullet.AddComponent<NetworkObject>();

            // Add Bullet script
            Bullet bulletScript = bullet.AddComponent<Bullet>();

            bullet.GetComponent<Renderer>().sharedMaterial = CreateAndSaveMaterial("TitanOrbit_Bullet", Color.yellow);

            GameObject glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            glow.name = "Glow";
            glow.transform.SetParent(bullet.transform);
            glow.transform.localScale = Vector3.one * 1.5f;
            Object.DestroyImmediate(glow.GetComponent<Collider>()); // No collider - avoids interference with bullet hit detection
            glow.GetComponent<Renderer>().sharedMaterial = CreateAndSaveMaterial("TitanOrbit_BulletGlow", new Color(1f, 0.5f, 0f));

            // Save as prefab
            string path = "Assets/Prefabs/Bullet.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(bullet, path);
            Object.DestroyImmediate(bullet);

            Debug.Log($"Created prefab: {path}");
        }

        private static void CreateGemPrefab()
        {
            GameObject gem = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            gem.name = "Gem";
            gem.transform.localScale = Vector3.one * 0.5f;

            Object.DestroyImmediate(gem.GetComponent<Collider>());
            SphereCollider col = gem.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = 1f;

            Rigidbody rb = gem.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.linearDamping = 4f;
            rb.isKinematic = false;

            NetworkObject netObj = gem.AddComponent<NetworkObject>();
            Gem gemScript = gem.AddComponent<Gem>();

            gem.GetComponent<Renderer>().sharedMaterial = CreateAndSaveMaterial("TitanOrbit_Gem", new Color(0.2f, 0.9f, 0.5f));

            string path = "Assets/Prefabs/Gem.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(gem, path);
            Object.DestroyImmediate(gem);

            Debug.Log($"Created prefab: {path}");
        }

        private static void EnsurePrefabDirectory()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }
        }

        /// <summary>
        /// Creates a URP-compatible material (fixes purple/missing shader)
        /// Loads from URP package or creates from shader
        /// </summary>
        private static Material CreateURPMaterial(Color color)
        {
            // Try loading the default URP Lit material from package (most reliable)
            Material baseMat = AssetDatabase.LoadAssetAtPath<Material>(
                "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Lit.mat");
            if (baseMat != null)
            {
                Material mat = new Material(baseMat);
                mat.SetColor("_BaseColor", color);
                // Increase metallic and smoothness for better contrast
                mat.SetFloat("_Metallic", 0.3f);
                mat.SetFloat("_Smoothness", 0.6f);
                return mat;
            }
            // Fallback: create from shader
            Shader urpShader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Universal Render Pipeline/Simple Lit");
            if (urpShader != null)
            {
                Material mat = new Material(urpShader);
                mat.SetColor("_BaseColor", color);
                mat.SetFloat("_Metallic", 0.3f);
                mat.SetFloat("_Smoothness", 0.6f);
                return mat;
            }
            Debug.LogWarning("Could not find URP Lit material/shader. Objects may appear purple.");
            return new Material(Shader.Find("Sprites/Default")) { color = color };
        }
        
        /// <summary>
        /// Creates a procedural asteroid texture with crater-like patterns using Perlin noise
        /// Grey color scheme with highly visible craters
        /// </summary>
        private static Texture2D CreateAsteroidTexture(int width = 512, int height = 512)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, true);
            Color baseColor = new Color(0.5f, 0.5f, 0.5f); // Grey base
            Color darkColor = new Color(0.2f, 0.2f, 0.2f); // Very dark grey for deep craters
            Color lightColor = new Color(0.7f, 0.7f, 0.7f); // Light grey for highlights
            Color craterColor = new Color(0.15f, 0.15f, 0.15f); // Darkest for crater centers
            
            // Generate noise-based texture with prominent craters
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float nx = (float)x / width;
                    float ny = (float)y / height;
                    
                    // Multiple layers of Perlin noise for varied surface
                    float noise1 = Mathf.PerlinNoise(nx * 12f, ny * 12f); // Fine detail
                    float noise2 = Mathf.PerlinNoise(nx * 6f, ny * 6f); // Medium detail
                    float noise3 = Mathf.PerlinNoise(nx * 3f, ny * 3f); // Large detail
                    float noise4 = Mathf.PerlinNoise(nx * 24f, ny * 24f); // Very fine detail
                    
                    // Combine noise layers with different weights
                    float combinedNoise = (noise1 * 0.25f + noise2 * 0.35f + noise3 * 0.25f + noise4 * 0.15f);
                    
                    // Create prominent crater patterns using circular distance from noise centers
                    float craterPattern = 0f;
                    for (int i = 0; i < 8; i++)
                    {
                        float cx = Mathf.PerlinNoise((float)i * 50f, 0f);
                        float cy = Mathf.PerlinNoise(0f, (float)i * 50f);
                        float dx = nx - cx;
                        float dy = ny - cy;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        float craterSize = 0.08f + Mathf.PerlinNoise((float)i * 100f, 100f) * 0.05f;
                        float craterDepth = Mathf.SmoothStep(craterSize, craterSize * 0.3f, dist);
                        craterPattern = Mathf.Max(craterPattern, craterDepth);
                    }
                    
                    // Additional random crater noise
                    float craterNoise = Mathf.PerlinNoise(nx * 5f + 200f, ny * 5f + 200f);
                    float craterMask = Mathf.SmoothStep(0.2f, 0.5f, craterNoise);
                    
                    // Mix colors based on noise and craters
                    Color pixelColor;
                    float craterInfluence = craterPattern * 0.8f + (1f - craterMask) * 0.3f;
                    
                    if (craterInfluence > 0.6f)
                    {
                        // Deep crater centers - very dark
                        pixelColor = Color.Lerp(craterColor, darkColor, (craterInfluence - 0.6f) / 0.4f);
                    }
                    else if (craterInfluence > 0.3f)
                    {
                        // Crater edges - dark grey
                        pixelColor = Color.Lerp(darkColor, baseColor, (craterInfluence - 0.3f) / 0.3f);
                    }
                    else if (combinedNoise < 0.3f)
                    {
                        // Dark areas
                        pixelColor = Color.Lerp(darkColor, baseColor, combinedNoise / 0.3f);
                    }
                    else if (combinedNoise > 0.75f)
                    {
                        // Light highlights
                        pixelColor = Color.Lerp(baseColor, lightColor, (combinedNoise - 0.75f) / 0.25f);
                    }
                    else
                    {
                        // Base color with variation
                        pixelColor = Color.Lerp(baseColor, lightColor, (combinedNoise - 0.3f) / 0.45f * 0.4f);
                    }
                    
                    // Ensure good contrast
                    pixelColor.r = Mathf.Clamp01(pixelColor.r);
                    pixelColor.g = Mathf.Clamp01(pixelColor.g);
                    pixelColor.b = Mathf.Clamp01(pixelColor.b);
                    
                    texture.SetPixel(x, y, pixelColor);
                }
            }
            
            texture.Apply();
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            
            return texture;
        }
        
        /// <summary>
        /// Gets or creates the asteroid texture asset
        /// </summary>
        private static Texture2D GetOrCreateAsteroidTexture()
        {
            string texturePath = "Assets/Textures/AsteroidTexture.png";
            
            // Check if texture already exists
            Texture2D existingTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (existingTexture != null)
            {
                return existingTexture;
            }
            
            // Create texture folder if needed
            if (!AssetDatabase.IsValidFolder("Assets/Textures"))
            {
                AssetDatabase.CreateFolder("Assets", "Textures");
            }
            
            // Create procedural asteroid texture
            Texture2D asteroidTexture = CreateAsteroidTexture();
            
            // Save texture asset
            byte[] pngData = asteroidTexture.EncodeToPNG();
            System.IO.File.WriteAllBytes(texturePath, pngData);
            AssetDatabase.ImportAsset(texturePath);
            
            // Configure texture import settings
            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = true;
                importer.filterMode = FilterMode.Bilinear;
                importer.wrapMode = TextureWrapMode.Repeat;
                AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceUpdate);
            }
            
            // Load and return the saved texture
            return AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        }
        
        /// <summary>
        /// Creates a crater-textured material for asteroids using noise/procedural approach
        /// </summary>
        private static Material CreateCraterMaterial()
        {
            // Get or create the asteroid texture
            Texture2D asteroidTexture = GetOrCreateAsteroidTexture();
            
            // Create a darker brown material with texture
            Color baseColor = new Color(0.4f, 0.25f, 0.15f); // Dark brown
            
            Material baseMat = AssetDatabase.LoadAssetAtPath<Material>(
                "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Lit.mat");
            if (baseMat != null)
            {
                Material mat = new Material(baseMat);
                mat.SetColor("_BaseColor", Color.white); // Use white so texture shows properly
                if (asteroidTexture != null)
                {
                    mat.SetTexture("_BaseMap", asteroidTexture);
                }
                // Low metallic, high roughness for rocky/cratered look
                mat.SetFloat("_Metallic", 0.1f);
                mat.SetFloat("_Smoothness", 0.2f); // Rough surface = craters
                return mat;
            }
            
            Shader urpShader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Universal Render Pipeline/Simple Lit");
            if (urpShader != null)
            {
                Material mat = new Material(urpShader);
                mat.SetColor("_BaseColor", Color.white);
                if (asteroidTexture != null)
                {
                    mat.SetTexture("_BaseMap", asteroidTexture);
                }
                mat.SetFloat("_Metallic", 0.1f);
                mat.SetFloat("_Smoothness", 0.2f);
                return mat;
            }
            
            return CreateAndSaveMaterial("TitanOrbit_Asteroid", baseColor);
        }

        [MenuItem("Titan Orbit/Fix Duplicate Network Prefabs")]
        public static void FixDuplicatePrefabs()
        {
            var defaultList = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/DefaultNetworkPrefabs.asset");
            if (defaultList == null) return;

            var so = new SerializedObject(defaultList);
            var listProp = so.FindProperty("List");
            if (listProp == null) return;

            var seen = new System.Collections.Generic.List<Object>();
            for (int i = listProp.arraySize - 1; i >= 0; i--)
            {
                var prefabProp = listProp.GetArrayElementAtIndex(i).FindPropertyRelative("Prefab");
                var obj = prefabProp?.objectReferenceValue;
                if (obj != null)
                {
                    if (seen.Contains(obj))
                    {
                        listProp.DeleteArrayElementAtIndex(i);
                        Debug.Log($"Removed duplicate prefab: {obj.name}");
                    }
                    else
                    {
                        seen.Add(obj);
                    }
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log("Duplicate network prefabs removed.");
        }

        [MenuItem("Titan Orbit/Fix Player Prefab & Materials")]
        public static void FixSetup()
        {
            // Assign Player Prefab to NetworkManager
            NetworkManager nm = Object.FindObjectOfType<NetworkManager>();
            if (nm != null)
            {
                GameObject starshipPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Starship.prefab");
                if (starshipPrefab != null)
                {
                    nm.NetworkConfig.PlayerPrefab = starshipPrefab;
                    Debug.Log("Player Prefab assigned to NetworkManager.");
                }
                else
                {
                    Debug.LogWarning("Starship prefab not found. Run 'Create Basic Prefabs' first.");
                }

                // Add Gem prefab to Network Prefabs list (required for spawning gems)
                var gemPrefabObj = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Gem.prefab");
                if (gemPrefabObj != null)
                {
                    var netObj = gemPrefabObj.GetComponent<NetworkObject>();
                    var defaultList = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/DefaultNetworkPrefabs.asset");
                    if (netObj != null && defaultList != null)
                    {
                        var so = new SerializedObject(defaultList);
                        var listProp = so.FindProperty("List");
                        if (listProp != null)
                        {
                            bool found = false;
                            for (int i = 0; i < listProp.arraySize; i++)
                            {
                                if (listProp.GetArrayElementAtIndex(i).objectReferenceValue == netObj) { found = true; break; }
                            }
                            if (!found)
                            {
                                listProp.arraySize++;
                                listProp.GetArrayElementAtIndex(listProp.arraySize - 1).objectReferenceValue = netObj;
                                so.ApplyModifiedPropertiesWithoutUndo();
                                AssetDatabase.SaveAssets();
                                Debug.Log("Gem prefab added to DefaultNetworkPrefabs.");
                            }
                        }
                    }
                }
            }

            // Assign bullet prefab to CombatSystem
            CombatSystem combat = Object.FindObjectOfType<CombatSystem>();
            if (combat != null)
            {
                var bulletPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Bullet.prefab");
                if (bulletPrefab != null)
                {
                    var so = new SerializedObject(combat);
                    so.FindProperty("bulletPrefab").objectReferenceValue = bulletPrefab;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log("Bullet prefab assigned to CombatSystem.");
                }
            }

            // Assign gem prefab to GemSpawner
            GemSpawner gemSpawner = Object.FindObjectOfType<GemSpawner>();
            if (gemSpawner != null)
            {
                var gemPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Gem.prefab");
                if (gemPrefab != null)
                {
                    var so = new SerializedObject(gemSpawner);
                    so.FindProperty("gemPrefab").objectReferenceValue = gemPrefab;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log("Gem prefab assigned to GemSpawner.");
                }
            }

            // Assign asteroid prefab to AsteroidRespawnManager
            AsteroidRespawnManager asteroidRespawn = Object.FindObjectOfType<AsteroidRespawnManager>();
            if (asteroidRespawn != null)
            {
                var asteroidPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Asteroid.prefab");
                if (asteroidPrefab != null)
                {
                    var so = new SerializedObject(asteroidRespawn);
                    so.FindProperty("asteroidPrefab").objectReferenceValue = asteroidPrefab;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log("Asteroid prefab assigned to AsteroidRespawnManager.");
                }
            }

            FixPrefabMaterials();
        }

        [MenuItem("Titan Orbit/Update Asteroid Material with Texture")]
        public static void UpdateAsteroidMaterial()
        {
            // Get or create the asteroid texture
            Texture2D asteroidTexture = GetOrCreateAsteroidTexture();
            
            if (asteroidTexture == null)
            {
                Debug.LogError("Failed to create asteroid texture.");
                return;
            }
            
            // Update the existing asteroid material
            Material asteroidMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/TitanOrbit_Asteroid_Crater.mat");
            if (asteroidMat != null)
            {
                asteroidMat.SetColor("_BaseColor", Color.white);
                asteroidMat.SetTexture("_BaseMap", asteroidTexture);
                asteroidMat.SetFloat("_Metallic", 0.1f);
                asteroidMat.SetFloat("_Smoothness", 0.2f);
                EditorUtility.SetDirty(asteroidMat);
                AssetDatabase.SaveAssets();
                Debug.Log("Asteroid material updated with texture.");
            }
            else
            {
                Debug.LogWarning("Asteroid material not found. Run 'Create Basic Prefabs' first.");
            }
        }

        [MenuItem("Titan Orbit/Fix Prefab Materials (URP)")]
        public static void FixPrefabMaterials()
        {
            // Create Materials folder and save material assets (ensures they persist)
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            {
                AssetDatabase.CreateFolder("Assets", "Materials");
            }

            var materials = new System.Collections.Generic.Dictionary<string, Material>
            {
                { "TitanOrbit_Starship", CreateAndSaveMaterial("TitanOrbit_Starship", new Color(0.4f, 0.7f, 1f)) },
                { "TitanOrbit_StarshipBody", CreateAndSaveMaterial("TitanOrbit_StarshipBody", new Color(0.18f, 0.18f, 0.2f)) },
                { "TitanOrbit_Planet", CreateAndSaveMaterial("TitanOrbit_Planet", new Color(0.5f, 0.6f, 0.8f)) },
                { "TitanOrbit_HomePlanet", CreateAndSaveMaterial("TitanOrbit_HomePlanet", Color.yellow) },
                { "TitanOrbit_HomePlanet_TeamA", CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamA", new Color(0.9f, 0.25f, 0.25f)) },
                { "TitanOrbit_HomePlanet_TeamB", CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamB", new Color(0.25f, 0.4f, 0.9f)) },
                { "TitanOrbit_HomePlanet_TeamC", CreateAndSaveMaterial("TitanOrbit_HomePlanet_TeamC", new Color(0.25f, 0.85f, 0.35f)) },
                { "TitanOrbit_Asteroid", CreateAndSaveMaterial("TitanOrbit_Asteroid", new Color(0.5f, 0.35f, 0.2f)) },
                { "TitanOrbit_Bullet", CreateAndSaveMaterial("TitanOrbit_Bullet", Color.yellow) },
                { "TitanOrbit_Gem", CreateAndSaveMaterial("TitanOrbit_Gem", new Color(0.2f, 0.9f, 0.5f)) },
                { "TitanOrbit_Ring", CreateAndSaveMaterial("TitanOrbit_Ring", new Color(1f, 1f, 0f, 0.8f)) }
            };

            string[] paths = { "Assets/Prefabs/Starship.prefab", "Assets/Prefabs/Planet.prefab",
                "Assets/Prefabs/HomePlanet.prefab", "Assets/Prefabs/Asteroid.prefab", "Assets/Prefabs/Bullet.prefab", "Assets/Prefabs/Gem.prefab" };
            string[] matKeys = { "TitanOrbit_Starship", "TitanOrbit_Planet", "TitanOrbit_HomePlanet", "TitanOrbit_Asteroid", "TitanOrbit_Bullet", "TitanOrbit_Gem" };

            for (int i = 0; i < paths.Length; i++)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
                if (prefab != null && materials.TryGetValue(matKeys[i], out Material mat))
                {
                    GameObject instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                    if (instance != null)
                    {
                        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
                        bool isStarship = paths[i].Contains("Starship.prefab");
                        Material bodyMat = materials.TryGetValue("TitanOrbit_StarshipBody", out Material b) ? b : mat;
                        foreach (Renderer r in renderers)
                        {
                            if (r.gameObject.name == "Ring" && materials.TryGetValue("TitanOrbit_Ring", out Material ringMat))
                                r.sharedMaterial = ringMat;
                            else if (isStarship && r.gameObject.name == "Starship")
                                r.sharedMaterial = bodyMat; // Hull mesh = dark grey
                            else if (isStarship)
                                r.sharedMaterial = mat; // Cockpit, Nose, WingTipL/R, NozzleL/R, Dorsal = accent
                            else
                                r.sharedMaterial = mat;
                        }
                        // Regular Planet: set script material refs so captured planets change colour
                        if (paths[i].EndsWith("Planet.prefab") && !paths[i].Contains("HomePlanet"))
                        {
                            Planet planetScript = instance.GetComponent<Planet>();
                            if (planetScript != null)
                            {
                                var planetSO = new SerializedObject(planetScript);
                                Material neutralMat = materials["TitanOrbit_Planet"];
                                if (materials.TryGetValue("TitanOrbit_HomePlanet_TeamA", out Material ta) &&
                                    materials.TryGetValue("TitanOrbit_HomePlanet_TeamB", out Material tb) &&
                                    materials.TryGetValue("TitanOrbit_HomePlanet_TeamC", out Material tc))
                                {
                                    planetSO.FindProperty("neutralMaterial").objectReferenceValue = neutralMat;
                                    planetSO.FindProperty("teamAMaterial").objectReferenceValue = ta;
                                    planetSO.FindProperty("teamBMaterial").objectReferenceValue = tb;
                                    planetSO.FindProperty("teamCMaterial").objectReferenceValue = tc;
                                    planetSO.ApplyModifiedPropertiesWithoutUndo();
                                }
                            }
                        }
                        if (paths[i].Contains("HomePlanet.prefab"))
                        {
                            HomePlanet homeScript = instance.GetComponent<HomePlanet>();
                            if (homeScript != null)
                            {
                                var homeSO = new SerializedObject(homeScript);
                                Material neutralMat = materials["TitanOrbit_HomePlanet"];
                                if (materials.TryGetValue("TitanOrbit_HomePlanet_TeamA", out Material ta) &&
                                    materials.TryGetValue("TitanOrbit_HomePlanet_TeamB", out Material tb) &&
                                    materials.TryGetValue("TitanOrbit_HomePlanet_TeamC", out Material tc))
                                {
                                    homeSO.FindProperty("neutralMaterial").objectReferenceValue = neutralMat;
                                    homeSO.FindProperty("teamAMaterial").objectReferenceValue = ta;
                                    homeSO.FindProperty("teamBMaterial").objectReferenceValue = tb;
                                    homeSO.FindProperty("teamCMaterial").objectReferenceValue = tc;
                                    homeSO.ApplyModifiedPropertiesWithoutUndo();
                                }
                            }
                        }
                        PrefabUtility.SaveAsPrefabAsset(instance, paths[i]);
                        Object.DestroyImmediate(instance);
                    }
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Prefab materials fixed for URP.");
        }

        private static Material CreateAndSaveMaterial(string name, Color color, int renderQueue = -1)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            {
                AssetDatabase.CreateFolder("Assets", "Materials");
            }
            string path = $"Assets/Materials/{name}.mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.SetColor("_BaseColor", color);
                if (renderQueue >= 0)
                {
                    existing.renderQueue = renderQueue;
                }
                EditorUtility.SetDirty(existing);
                return existing;
            }
            Material mat = CreateURPMaterial(color);
            if (renderQueue >= 0)
            {
                mat.renderQueue = renderQueue;
            }
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
