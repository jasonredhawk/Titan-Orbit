using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TitanOrbit.Data;
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
        /// <summary>Full Demo Prefabs root: all subfolders (Bullets, Sparkler, Shockwave, Sharp, Rockets, Ring, Ring2, Rift, Plasma, Liquid, Lightning, etc.) are searched.</summary>
        private const string DemoPrefabsFolder = "Assets/Archanor/Sci-Fi Arsenal/InteractiveDemo/Demo Prefabs";

        /// <summary>Color order for sorting within each category. Team A=Red, B=Blue, C=Green; others (Yellow, Purple, Orange) for variety.</summary>
        private static readonly string[] ColorOrder = { "Blue", "Green", "Orange", "Purple", "Red", "Yellow" };

        [MenuItem("Titan Orbit/Populate Bullet Bank From Demo Prefabs")]
        public static void PopulateFromDemoPrefabs()
        {
            PopulateFromDemoPrefabsInternal(DemoPrefabsFolder);
        }

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

            PopulateFromFolderInternal(relativeFolder);
        }

        private static void PopulateFromDemoPrefabsInternal(string relativeFolder)
        {
            List<BulletBankCategory> categories = FindPrefabsGroupedByFolderAndSortedByColor(relativeFolder);
            if (categories == null || categories.Count == 0)
            {
                Debug.LogWarning($"Titan Orbit: No prefabs with SciFiProjectileScript found in {relativeFolder}");
                return;
            }

            int addedBullet = 0, addedNetworkObject = 0;
            foreach (var cat in categories)
            {
                if (cat.prefabs == null) continue;
                foreach (GameObject prefab in cat.prefabs)
                {
                    string path = AssetDatabase.GetAssetPath(prefab);
                    if (string.IsNullOrEmpty(path)) continue;
                    AddBulletAndNetworkObjectToPrefab(path, ref addedBullet, ref addedNetworkObject);
                }
            }

            // Reload categories after modifying (paths unchanged)
            categories = FindPrefabsGroupedByFolderAndSortedByColor(relativeFolder);
            if (categories == null || categories.Count == 0) return;

            CombatSystem combat = UnityEngine.Object.FindObjectOfType<CombatSystem>();
            PreserveBulletBankProfilesFromExisting(combat, categories);
            if (combat != null)
            {
                SerializedObject so = new SerializedObject(combat);
                // Set categories via reflection so the list is definitely applied (avoids SerializedProperty path issues with nested lists)
                var categoriesField = typeof(CombatSystem).GetField("bulletBankCategories", BindingFlags.NonPublic | BindingFlags.Instance);
                if (categoriesField != null)
                {
                    categoriesField.SetValue(combat, new List<BulletBankCategory>(categories));
                    var bankField = typeof(CombatSystem).GetField("bulletPrefabBank", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (bankField != null)
                        bankField.SetValue(combat, new List<GameObject>());
                }
                so.Update();
                EditorUtility.SetDirty(combat);
                int totalPrefabs = categories.Sum(c => c.prefabs != null ? c.prefabs.Count : 0);
                Debug.Log($"Titan Orbit: CombatSystem.bulletBankCategories set to {categories.Count} categories, {totalPrefabs} prefab(s). B key cycles one per category; team color picks Red/Blue/Green.");
                EnsureDefaultBulletPrefab(so, combat);
            }
            else
            {
                Debug.LogWarning("Titan Orbit: No CombatSystem found in the open scene. Open a game scene and run again.");
            }

            // Register all prefabs in DefaultNetworkPrefabs
            var allPrefabs = new List<GameObject>();
            foreach (var cat in categories)
            {
                if (cat.prefabs != null) allPrefabs.AddRange(cat.prefabs);
            }
            RegisterPrefabsInDefaultList(allPrefabs);
            int total = categories.Sum(c => c.prefabs != null ? c.prefabs.Count : 0);
            Debug.Log($"Titan Orbit: Populate Bullet Bank (Demo Prefabs) complete. {categories.Count} categories, {total} prefab(s). Added Bullet to {addedBullet}, NetworkObject to {addedNetworkObject}.");
        }

        private static void EnsureDefaultBulletPrefab(SerializedObject so, CombatSystem combat)
        {
            string bulletPrefabPath = "Assets/Prefabs/Bullet.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(bulletPrefabPath) == null) return;
            int a = 0, b = 0;
            AddBulletAndNetworkObjectToPrefab(bulletPrefabPath, ref a, ref b);
            GameObject defaultBullet = AssetDatabase.LoadAssetAtPath<GameObject>(bulletPrefabPath);
            if (defaultBullet == null || defaultBullet.GetComponent<NetworkObject>() == null) return;
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

        /// <summary>Find prefabs with SciFiProjectileScript, group by parent folder name, sort each group by color (Blue, Green, Orange, Purple, Red, Yellow).</summary>
        private static List<BulletBankCategory> FindPrefabsGroupedByFolderAndSortedByColor(string relativeFolder)
        {
            var withPath = FindPrefabsWithSciFiProjectileAndPath(relativeFolder);
            if (withPath == null || withPath.Count == 0) return new List<BulletBankCategory>();

            var byFolder = new Dictionary<string, List<GameObject>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in withPath)
            {
                string folderName = kv.Key;
                if (!byFolder.TryGetValue(folderName, out List<GameObject> list))
                {
                    list = new List<GameObject>();
                    byFolder[folderName] = list;
                }
                list.Add(kv.Value);
            }

            var categories = new List<BulletBankCategory>();
            foreach (var kv in byFolder.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var sorted = kv.Value.OrderBy(p => GetColorSortOrder(p != null ? p.name : "")).ToList();
                categories.Add(new BulletBankCategory
                {
                    categoryName = kv.Key,
                    prefabs = sorted,
                    profile = new BulletBankProfile(),
                });
            }
            return categories;
        }

        /// <summary>Keeps authored stat modifiers and abilities when repopulating prefab lists from disk.</summary>
        private static void PreserveBulletBankProfilesFromExisting(CombatSystem combat, List<BulletBankCategory> newCategories)
        {
            if (combat == null || newCategories == null || newCategories.Count == 0) return;
            var categoriesField = typeof(CombatSystem).GetField("bulletBankCategories", BindingFlags.NonPublic | BindingFlags.Instance);
            if (categoriesField == null) return;
            var existing = categoriesField.GetValue(combat) as List<BulletBankCategory>;
            if (existing == null || existing.Count == 0) return;

            var byName = new Dictionary<string, BulletBankProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (BulletBankCategory cat in existing)
            {
                if (cat == null || string.IsNullOrEmpty(cat.categoryName) || cat.profile == null) continue;
                byName[cat.categoryName] = cat.profile;
            }

            foreach (BulletBankCategory cat in newCategories)
            {
                if (cat == null || string.IsNullOrEmpty(cat.categoryName)) continue;
                if (byName.TryGetValue(cat.categoryName, out BulletBankProfile preserved))
                    cat.profile = preserved;
            }
        }

        private static int GetColorSortOrder(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return ColorOrder.Length;
            for (int i = 0; i < ColorOrder.Length; i++)
            {
                if (prefabName.IndexOf(ColorOrder[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return ColorOrder.Length;
        }

        private static List<KeyValuePair<string, GameObject>> FindPrefabsWithSciFiProjectileAndPath(string relativeFolder)
        {
            var result = new List<KeyValuePair<string, GameObject>>();
            string dataPath = Application.dataPath.Replace('\\', '/');
            string normalizedRelative = relativeFolder.Replace('\\', '/').TrimEnd('/');
            string folderWithoutAssets = normalizedRelative.StartsWith("Assets/") ? normalizedRelative.Substring(7) : normalizedRelative;
            string fullPath = Path.Combine(dataPath, folderWithoutAssets).Replace('\\', '/');
            if (!Directory.Exists(fullPath)) return result;
            string[] files = Directory.GetFiles(fullPath, "*.prefab", SearchOption.AllDirectories);
            foreach (string file in files)
            {
                string path = file.Replace('\\', '/');
                if (!path.StartsWith(dataPath)) continue;
                path = "Assets" + path.Substring(dataPath.Length);
                if (!path.StartsWith("Assets/")) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponent<SciFiProjectileScript>() == null) continue;
                string parentFolder = Path.GetFileName(Path.GetDirectoryName(path));
                if (string.IsNullOrEmpty(parentFolder)) parentFolder = "Default";
                result.Add(new KeyValuePair<string, GameObject>(parentFolder, prefab));
            }
            return result;
        }

        private static void RegisterPrefabsInDefaultList(List<GameObject> prefabs)
        {
            var defaultList = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/DefaultNetworkPrefabs.asset");
            if (defaultList == null)
            {
                Debug.LogWarning("Titan Orbit: DefaultNetworkPrefabs.asset not found. Bullets may not sync over the network.");
                return;
            }
            SerializedObject listSo = new SerializedObject(defaultList);
            SerializedProperty listProp = listSo.FindProperty("List");
            if (listProp == null) return;
            int registered = 0;
            foreach (GameObject p in prefabs)
            {
                if (p == null || p.GetComponent<NetworkObject>() == null) continue;
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

        private static void PopulateFromFolderInternal(string relativeFolder)
        {
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

            // Assign to CombatSystem in the open scene (flat bank)
            CombatSystem combat = UnityEngine.Object.FindObjectOfType<CombatSystem>();
            if (combat != null)
            {
                SerializedObject so = new SerializedObject(combat);
                // Set flat bank and clear categories via reflection so it persists reliably
                var bankField = typeof(CombatSystem).GetField("bulletPrefabBank", BindingFlags.NonPublic | BindingFlags.Instance);
                var categoriesField = typeof(CombatSystem).GetField("bulletBankCategories", BindingFlags.NonPublic | BindingFlags.Instance);
                if (bankField != null)
                    bankField.SetValue(combat, new List<GameObject>(prefabs));
                if (categoriesField != null)
                    categoriesField.SetValue(combat, new List<BulletBankCategory>());
                so.Update();
                EditorUtility.SetDirty(combat);
                Debug.Log($"Titan Orbit: CombatSystem.bulletPrefabBank set to {prefabs.Count} prefab(s) (flat).");
                EnsureDefaultBulletPrefab(so, combat);
            }
            else
            {
                Debug.LogWarning("Titan Orbit: No CombatSystem found in the open scene. Open a game scene and run again, or assign the bullet prefab bank manually.");
            }

            RegisterPrefabsInDefaultList(prefabs);
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
