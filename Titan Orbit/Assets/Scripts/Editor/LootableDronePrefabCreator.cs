#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using Unity.Netcode.Components;
using TitanOrbit.Entities;

namespace TitanOrbit.Editor
{
    public static class LootableDronePrefabCreator
    {
        private const string PrefabPath = "Assets/Prefabs/LootableDrone.prefab";

        [MenuItem("Titan Orbit/Create Lootable Drone Prefab")]
        public static void CreateOrUpdatePrefab()
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null)
            {
                Debug.Log($"LootableDrone prefab already exists at {PrefabPath}. Assign it on HomePlanetStoreSystem and register with NetworkManager.");
                Selection.activeObject = existing;
                return;
            }

            var go = new GameObject("LootableDrone");
            go.AddComponent<NetworkObject>();
            go.AddComponent<NetworkTransform>();

            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = 0.5f;
            rb.constraints = RigidbodyConstraints.FreezePositionY;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var col = go.AddComponent<SphereCollider>();
            col.radius = 0.5f;
            col.center = Vector3.zero;

            go.AddComponent<LootableDrone>();
            go.AddComponent<DroneBody>();

            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                AssetDatabase.CreateFolder("Assets", "Prefabs");

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);

            Debug.Log($"Created {PrefabPath}. Assign to HomePlanetStoreSystem.lootableDroneNetworkPrefab and add to NetworkManager prefab list.");
            Selection.activeObject = prefab;
        }
    }
}
#endif
