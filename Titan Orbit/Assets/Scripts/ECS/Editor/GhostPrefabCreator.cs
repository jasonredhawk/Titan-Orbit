#if UNITY_EDITOR
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;
using Unity.NetCode;
using Unity.Entities;
using Unity.Entities.Hybrid.Baking;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// Editor menu utility that creates ECS ghost prefabs (ship, planet, asteroid, gem, transport)
    /// and the GamePrefabsRegistry asset under Assets/Prefabs/ECS/. Run once per project setup.
    /// </summary>
    public static class GhostPrefabCreator
    {
        const string MapGenerationSettingsPath = "Assets/Data/MapGenerationSettings.asset";

        /// <summary>Menu entry — creates all ECS ghost prefabs and the registry asset.</summary>
        [MenuItem("Titan Orbit/Create Ghost Prefabs")]
        public static void CreateGhostPrefabs()
        {
            // --- Create instance ---
            EnsureDirectory("Assets/Prefabs/ECS");
            EnsureDirectory("Assets/Data");
            EnsureMapGenerationSettingsAsset();
            CreateShipPrefab();
            CreatePlanetPrefab();
            CreateAsteroidPrefab();
            CreateGemPrefab();
            CreatePeopleTransportPrefab();
            CreateRegistryPrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[GhostPrefabCreator] Created ECS ghost prefabs under Assets/Prefabs/ECS/");
        }

        /// <summary>Creates nested AssetDatabase folders if missing.</summary>
        static void EnsureDirectory(string path)
        {
            // --- Ensure setup ---
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parts = path.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }
        }

        static void AddGhostRootComponents(GameObject go, bool hasOwner = true)
        {
            // --- AddGhostRootComponents ---
            if (go.GetComponent<LinkedEntityGroupAuthoring>() == null)
                go.AddComponent<LinkedEntityGroupAuthoring>();
            var ghost = go.AddComponent<GhostAuthoringComponent>();
            ghost.HasOwner = hasOwner;
        }

        static void CreatePeopleTransportPrefab()
        {
            // --- Create instance ---
            var go = new GameObject("PeopleTransportGhost");
            AddGhostRootComponents(go, hasOwner: false);
            go.AddComponent<TitanOrbit.ECS.Authoring.PeopleTransportGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/PeopleTransportGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreateShipPrefab()
        {
            // --- Create instance ---
            var go = new GameObject("StarshipGhost");
            AddGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.StarshipGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/StarshipGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreatePlanetPrefab()
        {
            // --- Create instance ---
            var go = new GameObject("PlanetGhost");
            AddGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.PlanetGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/PlanetGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreateAsteroidPrefab()
        {
            // --- Create instance ---
            var go = new GameObject("AsteroidGhost");
            AddGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.AsteroidGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/AsteroidGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreateGemPrefab()
        {
            // --- Create instance ---
            var go = new GameObject("GemGhost");
            AddGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.GemGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/GemGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreateRegistryPrefab()
        {
            // --- Create instance ---
            var ship = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/StarshipGhost.prefab");
            var planet = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/PlanetGhost.prefab");
            var asteroid = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/AsteroidGhost.prefab");
            var gem = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/GemGhost.prefab");
            var peopleTransport = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ECS/PeopleTransportGhost.prefab");
            var go = new GameObject("GamePrefabsRegistry");
            var reg = go.AddComponent<TitanOrbit.ECS.Authoring.GamePrefabsRegistryAuthoring>();
            reg.ShipPrefab = ship;
            reg.PlanetPrefab = planet;
            reg.AsteroidPrefab = asteroid;
            reg.GemPrefab = gem;
            reg.PeopleTransportPrefab = peopleTransport;
            reg.MapGenerationSettings = EnsureMapGenerationSettingsAsset();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/GamePrefabsRegistry.prefab");
            Object.DestroyImmediate(go);
        }

        [MenuItem("Titan Orbit/Select Map Generation Settings Asset")]
        public static void SelectMapGenerationSettingsAsset()
        {
            // --- SelectMapGenerationSettingsAsset ---
            var asset = EnsureMapGenerationSettingsAsset();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        [MenuItem("Titan Orbit/Create Map Generation Settings Asset")]
        public static void CreateMapGenerationSettingsMenuItem()
        {
            // --- Create instance ---
            EnsureDirectory("Assets/Data");
            var asset = EnsureMapGenerationSettingsAsset();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log($"[GhostPrefabCreator] Map generation settings at {MapGenerationSettingsPath}");
        }

        public static MapGenerationSettings EnsureMapGenerationSettingsAsset()
        {
            // --- Ensure setup ---
            var existing = AssetDatabase.LoadAssetAtPath<MapGenerationSettings>(MapGenerationSettingsPath);
            if (existing != null)
                return existing;

            var asset = ScriptableObject.CreateInstance<MapGenerationSettings>();
            AssetDatabase.CreateAsset(asset, MapGenerationSettingsPath);
            AssetDatabase.SaveAssets();
            return asset;
        }
    }
}
#endif
