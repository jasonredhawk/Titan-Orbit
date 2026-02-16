using UnityEngine;
using UnityEditor;
using Unity.Netcode;
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
    /// Quick setup that creates everything in one go
    /// </summary>
    public class QuickSetup
    {
        [MenuItem("Titan Orbit/Quick Setup (All)")]
        public static void QuickSetupAll()
        {
            Debug.Log("Starting quick setup...");

            // First create prefabs
            GameSetup.CreateBasicPrefabs();

            // Then setup scene
            GameSetup.SetupGameScene();

            // Assign prefabs to components
            AssignPrefabs();

            Debug.Log("Quick setup complete! Check the scene and configure any remaining settings.");
        }

        private static void AssignPrefabs()
        {
            // Find MapGenerator and assign prefabs
            MapGenerator mapGenerator = Object.FindObjectOfType<MapGenerator>();
            if (mapGenerator != null)
            {
                GameObject homePlanetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/HomePlanet.prefab");
                GameObject planetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Planet.prefab");
                GameObject asteroidPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Asteroid.prefab");

                if (homePlanetPrefab != null)
                {
                    SerializedObject so = new SerializedObject(mapGenerator);
                    so.FindProperty("homePlanetPrefab").objectReferenceValue = homePlanetPrefab;
                    so.ApplyModifiedProperties();
                }

                if (planetPrefab != null)
                {
                    SerializedObject so = new SerializedObject(mapGenerator);
                    so.FindProperty("planetPrefab").objectReferenceValue = planetPrefab;
                    so.ApplyModifiedProperties();
                }

                if (asteroidPrefab != null)
                {
                    SerializedObject so = new SerializedObject(mapGenerator);
                    so.FindProperty("asteroidPrefab").objectReferenceValue = asteroidPrefab;
                    so.ApplyModifiedProperties();
                }
            }

            // Find CombatSystem and assign bullet prefab
            CombatSystem combatSystem = Object.FindObjectOfType<CombatSystem>();
            if (combatSystem != null)
            {
                GameObject bulletPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Bullet.prefab");
                if (bulletPrefab != null)
                {
                    SerializedObject so = new SerializedObject(combatSystem);
                    so.FindProperty("bulletPrefab").objectReferenceValue = bulletPrefab;
                    so.ApplyModifiedProperties();
                }
            }

            // Find NetworkManager and set player prefab (PlayerPrefab is on NetworkConfig, not NetworkManager)
            NetworkManager networkManager = Object.FindObjectOfType<NetworkManager>();
            if (networkManager != null)
            {
                GameObject starshipPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Starship.prefab");
                if (starshipPrefab != null)
                {
                    networkManager.NetworkConfig.PlayerPrefab = starshipPrefab;
                    EditorUtility.SetDirty(networkManager);
                }
            }
        }
    }
}
