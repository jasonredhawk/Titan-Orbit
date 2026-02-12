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
            GameObject uiObj = CreateUI();
            GameObject audioObj = CreateAudioManager();

            Debug.Log("Game scene setup complete! All GameObjects have been created.");
        }

        private static GameObject CreateNetworkManager()
        {
            GameObject obj = new GameObject("NetworkManager");
            NetworkManager networkManager = obj.AddComponent<NetworkManager>();
            
            // Add NetworkGameManager
            NetworkGameManager netGameManager = obj.AddComponent<NetworkGameManager>();

            return obj;
        }

        private static GameObject CreateGameManagers()
        {
            GameObject obj = new GameObject("GameManagers");
            
            GameManager gameManager = obj.AddComponent<GameManager>();
            TeamManager teamManager = obj.AddComponent<TeamManager>();
            MatchManager matchManager = obj.AddComponent<MatchManager>();
            CrossPlatformManager crossPlatformManager = obj.AddComponent<CrossPlatformManager>();

            return obj;
        }

        private static GameObject CreateSystems()
        {
            GameObject obj = new GameObject("Systems");
            
            CombatSystem combatSystem = obj.AddComponent<CombatSystem>();
            MiningSystem miningSystem = obj.AddComponent<MiningSystem>();
            TransportSystem transportSystem = obj.AddComponent<TransportSystem>();
            CaptureSystem captureSystem = obj.AddComponent<CaptureSystem>();
            UpgradeSystem upgradeSystem = obj.AddComponent<UpgradeSystem>();
            AttributeUpgradeSystem attributeUpgradeSystem = obj.AddComponent<AttributeUpgradeSystem>();
            VisualEffectsManager visualEffectsManager = obj.AddComponent<VisualEffectsManager>();

            return obj;
        }

        private static GameObject CreateMapGenerator()
        {
            GameObject obj = new GameObject("MapGenerator");
            MapGenerator mapGenerator = obj.AddComponent<MapGenerator>();

            return obj;
        }

        private static GameObject CreateCamera()
        {
            GameObject obj = new GameObject("Main Camera");
            UnityEngine.Camera cam = obj.AddComponent<UnityEngine.Camera>();
            CameraController cameraController = obj.AddComponent<CameraController>();

            // Set up camera for top-down view
            obj.transform.position = new Vector3(0, 10, 0);
            obj.transform.rotation = Quaternion.Euler(90, 0, 0);
            cam.orthographic = true;
            cam.orthographicSize = 20f;

            // Tag as MainCamera
            obj.tag = "MainCamera";

            return obj;
        }

        private static GameObject CreateUI()
        {
            GameObject canvasObj = new GameObject("Canvas");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
            canvasObj.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            // Create EventSystem
            GameObject eventSystemObj = new GameObject("EventSystem");
            eventSystemObj.AddComponent<UnityEngine.EventSystems.EventSystem>();
            eventSystemObj.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();

            // Create HUD
            GameObject hudObj = new GameObject("HUD");
            hudObj.transform.SetParent(canvasObj.transform, false);
            HUDController hudController = hudObj.AddComponent<HUDController>();

            // Create Main Menu
            GameObject menuObj = new GameObject("MainMenu");
            menuObj.transform.SetParent(canvasObj.transform, false);
            MainMenu mainMenu = menuObj.AddComponent<MainMenu>();

            // Create Win/Loss Screen
            GameObject winLossObj = new GameObject("WinLossScreen");
            winLossObj.transform.SetParent(canvasObj.transform, false);
            WinLossScreen winLossScreen = winLossObj.AddComponent<WinLossScreen>();

            // Create Mobile Controls
            GameObject mobileControlsObj = new GameObject("MobileControls");
            mobileControlsObj.transform.SetParent(canvasObj.transform, false);
            MobileControls mobileControls = mobileControlsObj.AddComponent<MobileControls>();

            return canvasObj;
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

            Debug.Log("Basic prefabs created in Assets/Prefabs/");
        }

        private static void CreateStarshipPrefab()
        {
            GameObject ship = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            ship.name = "Starship";
            ship.transform.localScale = new Vector3(1f, 0.5f, 1f);

            // Remove default collider, add Rigidbody
            Object.DestroyImmediate(ship.GetComponent<Collider>());
            Rigidbody rb = ship.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.linearDamping = 5f;
            rb.angularDamping = 5f;

            // Add NetworkObject
            NetworkObject netObj = ship.AddComponent<NetworkObject>();

            // Add Starship script
            Starship starship = ship.AddComponent<Starship>();

            // Add Input Handler
            PlayerInputHandler inputHandler = ship.AddComponent<PlayerInputHandler>();

            // Create fire point
            GameObject firePoint = new GameObject("FirePoint");
            firePoint.transform.SetParent(ship.transform);
            firePoint.transform.localPosition = new Vector3(0, 0, 0.5f);

            // Set color
            Renderer renderer = ship.GetComponent<Renderer>();
            renderer.material.color = Color.cyan;

            // Save as prefab
            string path = "Assets/Prefabs/Starship.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(ship, path);
            Object.DestroyImmediate(ship);

            Debug.Log($"Created prefab: {path}");
        }

        private static void CreatePlanetPrefab()
        {
            GameObject planet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            planet.name = "Planet";
            planet.transform.localScale = Vector3.one * 2f;

            // Remove default collider, add trigger collider
            Object.DestroyImmediate(planet.GetComponent<Collider>());
            SphereCollider collider = planet.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 1f;

            // Add NetworkObject
            NetworkObject netObj = planet.AddComponent<NetworkObject>();

            // Add Planet script
            Planet planetScript = planet.AddComponent<Planet>();

            // Set color
            Renderer renderer = planet.GetComponent<Renderer>();
            renderer.material.color = Color.gray;

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
            homePlanet.transform.localScale = Vector3.one * 3f;

            // Remove default collider, add trigger collider
            Object.DestroyImmediate(homePlanet.GetComponent<Collider>());
            SphereCollider collider = homePlanet.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 1f;

            // Add NetworkObject
            NetworkObject netObj = homePlanet.AddComponent<NetworkObject>();

            // Add HomePlanet script (which extends Planet)
            HomePlanet homePlanetScript = homePlanet.AddComponent<HomePlanet>();

            // Set color (brighter to distinguish from regular planets)
            Renderer renderer = homePlanet.GetComponent<Renderer>();
            renderer.material.color = Color.yellow;

            // Add a ring or indicator (using cylinder scaled to look like a ring)
            GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "Ring";
            ring.transform.SetParent(homePlanet.transform);
            ring.transform.localScale = new Vector3(1.2f, 0.05f, 1.2f); // Thin and wide like a ring
            ring.transform.localRotation = Quaternion.Euler(90, 0, 0);
            ring.transform.localPosition = Vector3.zero;
            Renderer ringRenderer = ring.GetComponent<Renderer>();
            Material ringMaterial = new Material(ringRenderer.material);
            ringMaterial.color = new Color(1f, 1f, 0f, 0.5f);
            ringRenderer.material = ringMaterial;

            // Save as prefab
            string path = "Assets/Prefabs/HomePlanet.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(homePlanet, path);
            Object.DestroyImmediate(homePlanet);

            Debug.Log($"Created prefab: {path}");
        }

        private static void CreateAsteroidPrefab()
        {
            GameObject asteroid = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            asteroid.name = "Asteroid";
            asteroid.transform.localScale = Vector3.one * 1.5f;

            // Make it look more like an asteroid (randomize scale slightly)
            asteroid.transform.localScale = new Vector3(1.2f, 1.5f, 1.3f);

            // Remove default collider, add trigger collider
            Object.DestroyImmediate(asteroid.GetComponent<Collider>());
            SphereCollider collider = asteroid.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 1f;

            // Add NetworkObject
            NetworkObject netObj = asteroid.AddComponent<NetworkObject>();

            // Add Asteroid script
            Asteroid asteroidScript = asteroid.AddComponent<Asteroid>();

            // Set color (brown/gray)
            Renderer renderer = asteroid.GetComponent<Renderer>();
            renderer.material.color = new Color(0.4f, 0.3f, 0.2f);

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
            collider.radius = 0.2f;

            // Add Rigidbody
            Rigidbody rb = bullet.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = false;

            // Add NetworkObject
            NetworkObject netObj = bullet.AddComponent<NetworkObject>();

            // Add Bullet script
            Bullet bulletScript = bullet.AddComponent<Bullet>();

            // Set color (bright yellow/orange)
            Renderer renderer = bullet.GetComponent<Renderer>();
            renderer.material.color = Color.yellow;

            // Add a trail effect (simple glow)
            GameObject glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            glow.name = "Glow";
            glow.transform.SetParent(bullet.transform);
            glow.transform.localScale = Vector3.one * 1.5f;
            glow.GetComponent<Renderer>().material.color = new Color(1f, 0.5f, 0f, 0.3f);

            // Save as prefab
            string path = "Assets/Prefabs/Bullet.prefab";
            EnsurePrefabDirectory();
            PrefabUtility.SaveAsPrefabAsset(bullet, path);
            Object.DestroyImmediate(bullet);

            Debug.Log($"Created prefab: {path}");
        }

        private static void EnsurePrefabDirectory()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }
        }
    }
}
