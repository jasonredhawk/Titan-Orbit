#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Unity.NetCode;
using Unity.Entities;
using Unity.Entities.Hybrid.Baking;

namespace TitanOrbit.ECS.Editor
{
    public static class GhostPrefabCreator
    {
        [MenuItem("Titan Orbit/Create Ghost Prefabs")]
        public static void CreateGhostPrefabs()
        {
            EnsureDirectory("Assets/Prefabs/ECS");
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

        static void EnsureDirectory(string path)
        {
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
            if (go.GetComponent<LinkedEntityGroupAuthoring>() == null)
                go.AddComponent<LinkedEntityGroupAuthoring>();
            var ghost = go.AddComponent<GhostAuthoringComponent>();
            ghost.HasOwner = hasOwner;
        }

        static void CreatePeopleTransportPrefab()
        {
            var go = new GameObject("PeopleTransportGhost");
            AddGhostRootComponents(go, hasOwner: false);
            go.AddComponent<TitanOrbit.ECS.Authoring.PeopleTransportGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/PeopleTransportGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreateShipPrefab()
        {
            var go = new GameObject("StarshipGhost");
            AddGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.StarshipGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/StarshipGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreatePlanetPrefab()
        {
            var go = new GameObject("PlanetGhost");
            AddGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.PlanetGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/PlanetGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreateAsteroidPrefab()
        {
            var go = new GameObject("AsteroidGhost");
            AddGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.AsteroidGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/AsteroidGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreateGemPrefab()
        {
            var go = new GameObject("GemGhost");
            AddGhostRootComponents(go);
            go.AddComponent<TitanOrbit.ECS.Authoring.GemGhostAuthoring>();
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/GemGhost.prefab");
            Object.DestroyImmediate(go);
        }

        static void CreateRegistryPrefab()
        {
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
            PrefabUtility.SaveAsPrefabAsset(go, "Assets/Prefabs/ECS/GamePrefabsRegistry.prefab");
            Object.DestroyImmediate(go);
        }
    }
}
#endif
