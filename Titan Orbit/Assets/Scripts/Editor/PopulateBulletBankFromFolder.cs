using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using SciFiArsenal;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Populates CombatSystem's Bullet Prefab Bank from a folder containing prefabs with SciFiProjectileScript.
    /// Adds Bullet and NetworkObject to each prefab if missing, then registers them for networking.
    /// </summary>
    public static class PopulateBulletBankFromFolder
    {
        private const string DefaultFolderKey = "TitanOrbit.BulletBankFolder";
        private const string DefaultFolder = "Assets/Archanor/Sci-Fi Arsenal/InteractiveDemo/Demo Prefabs/Bullets";

        [MenuItem("Titan Orbit/Populate Bullet Bank From Folder")]
        public static void PopulateFromFolder()
        {
            string folder = EditorPrefs.GetString(DefaultFolderKey, DefaultFolder);
            string chosen = EditorUtility.OpenFolderPanel("Select folder containing bullet prefabs (with SciFiProjectileScript)", folder, "");
            if (string.IsNullOrEmpty(chosen)) return;

            // Convert to project-relative path (Application.dataPath is e.g. .../ProjectName/Assets)
            string dataPath = Application.dataPath.Replace('\\', '/');
            string projectPath = dataPath.EndsWith("/Assets") ? dataPath.Substring(0, dataPath.Length - 7) : dataPath;
            chosen = chosen.Replace('\\', '/');
            if (!chosen.StartsWith(projectPath))
            {
                Debug.LogWarning("Titan Orbit: Please select a folder inside the project (under Assets).");
                return;
            }
            string relativeFolder = "Assets" + chosen.Substring(projectPath.Length).Replace("\\", "/");
            EditorPrefs.SetString(DefaultFolderKey, relativeFolder);

            List<GameObject> prefabs = FindPrefabsWithSciFiProjectile(relativeFolder);
            if (prefabs == null || prefabs.Count == 0)
            {
                Debug.LogWarning($"Titan Orbit: No prefabs with SciFiProjectileScript found in {relativeFolder}");
                return;
            }

            int addedBullet = 0, addedNetworkObject = 0;
            foreach (GameObject prefab in prefabs)
            {
                string path = AssetDatabase.GetAssetPath(prefab);
                if (string.IsNullOrEmpty(path)) continue;
                AddBulletAndNetworkObjectToPrefab(path, ref addedBullet, ref addedNetworkObject);
            }

            // Reload prefabs after modifying (paths unchanged)
            prefabs.Clear();
            prefabs = FindPrefabsWithSciFiProjectile(relativeFolder);
            if (prefabs.Count == 0) return;

            // Assign to CombatSystem in the open scene
            CombatSystem combat = Object.FindObjectOfType<CombatSystem>();
            if (combat != null)
            {
                SerializedObject so = new SerializedObject(combat);
                SerializedProperty bankProp = so.FindProperty("bulletPrefabBank");
                if (bankProp != null)
                {
                    bankProp.ClearArray();
                    for (int i = 0; i < prefabs.Count; i++)
                    {
                        bankProp.InsertArrayElementAtIndex(i);
                        bankProp.GetArrayElementAtIndex(i).objectReferenceValue = prefabs[i];
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(combat);
                    Debug.Log($"Titan Orbit: CombatSystem.bulletPrefabBank set to {prefabs.Count} prefab(s).");
                }
                // Ensure default bullet prefab is set so bullets can spawn (CombatSystem always spawns this; bank is for visuals only)
                string bulletPrefabPath = "Assets/Prefabs/Bullet.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(bulletPrefabPath) != null)
                {
                    int a = 0, b = 0;
                    AddBulletAndNetworkObjectToPrefab(bulletPrefabPath, ref a, ref b);
                    GameObject defaultBullet = AssetDatabase.LoadAssetAtPath<GameObject>(bulletPrefabPath);
                    if (defaultBullet != null && defaultBullet.GetComponent<NetworkObject>() != null)
                    {
                        SerializedProperty defaultProp = so.FindProperty("defaultBulletPrefab");
                        if (defaultProp != null)
                        {
                            defaultProp.objectReferenceValue = defaultBullet;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            EditorUtility.SetDirty(combat);
                            Debug.Log("Titan Orbit: CombatSystem.defaultBulletPrefab set to Assets/Prefabs/Bullet.prefab.");
                        }
                        RegisterPrefabInDefaultList(defaultBullet);
                    }
                }
            }
            else
            {
                Debug.LogWarning("Titan Orbit: No CombatSystem found in the open scene. Open a game scene and run again, or assign the bullet prefab bank manually.");
            }

            // Register each prefab in DefaultNetworkPrefabs so they can spawn over the network
            var defaultList = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/DefaultNetworkPrefabs.asset");
            if (defaultList != null)
            {
                SerializedObject listSo = new SerializedObject(defaultList);
                SerializedProperty listProp = listSo.FindProperty("List");
                if (listProp != null)
                {
                    int registered = 0;
                    foreach (GameObject p in prefabs)
                    {
                        if (p.GetComponent<NetworkObject>() == null) continue;
                        bool found = false;
                        for (int i = 0; i < listProp.arraySize; i++)
                        {
                            var prefabRef = listProp.GetArrayElementAtIndex(i).FindPropertyRelative("Prefab");
                            if (prefabRef != null && prefabRef.objectReferenceValue == p) { found = true; break; }
                        }
                        if (!found)
                        {
                            listProp.arraySize++;
                            listProp.GetArrayElementAtIndex(listProp.arraySize - 1).FindPropertyRelative("Prefab").objectReferenceValue = p;
                            registered++;
                        }
                    }
                    listSo.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssets();
                    if (registered > 0)
                        Debug.Log($"Titan Orbit: Registered {registered} bullet prefab(s) in DefaultNetworkPrefabs.");
                }
            }
            else
            {
                Debug.LogWarning("Titan Orbit: DefaultNetworkPrefabs.asset not found at Assets/DefaultNetworkPrefabs.asset. Bullets may not sync over the network until prefabs are added to the network list.");
            }

            Debug.Log($"Titan Orbit: Populate Bullet Bank complete. Added Bullet to {addedBullet} prefab(s), NetworkObject to {addedNetworkObject} prefab(s). Total in bank: {prefabs.Count}.");
        }

        /// <summary>Find all prefabs with SciFiProjectileScript in the given folder and all subfolders recursively.</summary>
        private static List<GameObject> FindPrefabsWithSciFiProjectile(string relativeFolder)
        {
            List<GameObject> result = new List<GameObject>();
            string dataPath = Application.dataPath.Replace('\\', '/');
            string normalizedRelative = relativeFolder.Replace('\\', '/').TrimEnd('/');
            string folderWithoutAssets = normalizedRelative.StartsWith("Assets/") ? normalizedRelative.Substring(7) : normalizedRelative;
            string fullPath = Path.Combine(dataPath, folderWithoutAssets).Replace('\\', '/');
            if (!Directory.Exists(fullPath))
                return result;
            string[] files = Directory.GetFiles(fullPath, "*.prefab", SearchOption.AllDirectories);
            foreach (string file in files)
            {
                string path = file.Replace('\\', '/');
                if (!path.StartsWith(dataPath)) continue;
                path = "Assets" + path.Substring(dataPath.Length);
                if (!path.StartsWith("Assets/")) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<SciFiProjectileScript>() != null)
                    result.Add(prefab);
            }
            return result;
        }

        private static void RegisterPrefabInDefaultList(GameObject prefab)
        {
            if (prefab == null || prefab.GetComponent<NetworkObject>() == null) return;
            var defaultList = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/DefaultNetworkPrefabs.asset");
            if (defaultList == null) return;
            SerializedObject listSo = new SerializedObject(defaultList);
            SerializedProperty listProp = listSo.FindProperty("List");
            if (listProp == null) return;
            for (int i = 0; i < listProp.arraySize; i++)
            {
                var prefabRef = listProp.GetArrayElementAtIndex(i).FindPropertyRelative("Prefab");
                if (prefabRef != null && prefabRef.objectReferenceValue == prefab) return;
            }
            listProp.arraySize++;
            listProp.GetArrayElementAtIndex(listProp.arraySize - 1).FindPropertyRelative("Prefab").objectReferenceValue = prefab;
            listSo.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        private static void AddBulletAndNetworkObjectToPrefab(string assetPath, ref int addedBullet, ref int addedNetworkObject)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null) return;

            bool changed = false;
            if (root.GetComponent<Bullet>() == null)
            {
                if (root.GetComponent<Rigidbody>() == null)
                    root.AddComponent<Rigidbody>();
                root.AddComponent<Bullet>();
                addedBullet++;
                changed = true;
            }
            if (root.GetComponent<NetworkObject>() == null)
            {
                root.AddComponent<NetworkObject>();
                addedNetworkObject++;
                changed = true;
            }

            if (changed)
            {
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
