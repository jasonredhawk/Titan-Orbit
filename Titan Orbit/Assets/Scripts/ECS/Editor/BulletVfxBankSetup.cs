#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// Editor utility that creates or populates the single <see cref="BulletVfxBank"/> at
    /// <c>Assets/Resources/BulletVfxBank.asset</c> (no Data duplicate).
    /// </summary>
    public static class BulletVfxBankSetup
    {
        const string ResourcesPath = BulletVfxBank.ResourcesAssetPath;
        const string FallbackImpactPath = "Assets/Plugins/AllIn1VfxToolkit/Demo & Assets/Demo/Prefabs/Red Impact.prefab";
        const string LaserboltFolder = "Assets/Archanor/Sci-Fi Arsenal/InteractiveDemo/Demo Prefabs/Laserbolt";
        const string LegacyDataPath = "Assets/Data/BulletVfxBank.asset";

        /// <summary>
        /// Loads or creates the Resources bank. Removes a leftover Data copy if present.
        /// </summary>
        public static BulletVfxBank EnsureAsset()
        {
            // --- Prefer existing Resources bank ---
            var existing = AssetDatabase.LoadAssetAtPath<BulletVfxBank>(ResourcesPath);
            if (existing != null)
            {
                PopulateIfEmpty(existing);
                DeleteLegacyDataCopyIfPresent();
                return existing;
            }

            // --- Migrate Data → Resources once, then delete Data ---
            var legacy = AssetDatabase.LoadAssetAtPath<BulletVfxBank>(LegacyDataPath);
            if (legacy != null)
            {
                if (!Directory.Exists("Assets/Resources"))
                    Directory.CreateDirectory("Assets/Resources");
                AssetDatabase.CopyAsset(LegacyDataPath, ResourcesPath);
                AssetDatabase.DeleteAsset(LegacyDataPath);
                AssetDatabase.SaveAssets();
                existing = AssetDatabase.LoadAssetAtPath<BulletVfxBank>(ResourcesPath);
                if (existing != null)
                {
                    PopulateIfEmpty(existing);
                    return existing;
                }
            }

            if (!Directory.Exists("Assets/Resources"))
                Directory.CreateDirectory("Assets/Resources");

            var bank = ScriptableObject.CreateInstance<BulletVfxBank>();
            PopulateIfEmpty(bank);
            AssetDatabase.CreateAsset(bank, ResourcesPath);
            AssetDatabase.SaveAssets();
            return bank;
        }

        static void DeleteLegacyDataCopyIfPresent()
        {
            if (AssetDatabase.LoadAssetAtPath<BulletVfxBank>(LegacyDataPath) == null)
                return;
            AssetDatabase.DeleteAsset(LegacyDataPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[BulletVfxBankSetup] Removed legacy Assets/Data/BulletVfxBank.asset — use Resources only.");
        }

        static void PopulateIfEmpty(BulletVfxBank bank)
        {
            if (bank == null)
                return;

            var so = new SerializedObject(bank);
            var categories = so.FindProperty("categories");
            if (categories != null && categories.arraySize > 0)
            {
                // Ensure new scale fields exist with sensible defaults if missing from old YAML.
                EnsureScaleDefaults(so);
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(bank);
                return;
            }

            var category = new BulletVfxBank.Category
            {
                categoryName = "Laserbolt",
                prefabs = LoadLaserboltPrefabs(),
            };

            so.FindProperty("categories").arraySize = 1;
            var catElement = so.FindProperty("categories").GetArrayElementAtIndex(0);
            catElement.FindPropertyRelative("categoryName").stringValue = category.categoryName;
            var prefabsProp = catElement.FindPropertyRelative("prefabs");
            prefabsProp.arraySize = category.prefabs.Count;
            for (int i = 0; i < category.prefabs.Count; i++)
                prefabsProp.GetArrayElementAtIndex(i).objectReferenceValue = category.prefabs[i];

            var impact = AssetDatabase.LoadAssetAtPath<GameObject>(FallbackImpactPath);
            if (impact != null)
                so.FindProperty("fallbackImpactPrefab").objectReferenceValue = impact;

            EnsureScaleDefaults(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bank);
        }

        /// <summary>Does not overwrite designer values — only fills missing/zero upgrade field.</summary>
        static void EnsureScaleDefaults(SerializedObject so)
        {
            var globalProp = so.FindProperty("globalVisualScaleMultiplier");
            if (globalProp == null)
                globalProp = so.FindProperty("visualScaleMultiplier");
            // Leave global as-is when already set (designer may have chosen 0.25).

            var upgradeProp = so.FindProperty("upgradeVisualScaleMultiplier");
            if (upgradeProp != null && upgradeProp.floatValue <= 0.001f)
                upgradeProp.floatValue = 0.5f;
        }

        static List<GameObject> LoadLaserboltPrefabs()
        {
            var list = new List<GameObject>();
            string[] colors = { "Red", "Blue", "Green", "Yellow", "Purple" };
            foreach (string color in colors)
            {
                string path = $"{LaserboltFolder}/LaserBolt{color}OBJ.prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    list.Add(prefab);
            }

            return list;
        }
    }
}
#endif
