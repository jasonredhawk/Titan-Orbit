#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    public static class BulletVfxBankSetup
    {
        const string AssetPath = "Assets/Data/BulletVfxBank.asset";
        const string ResourcesPath = "Assets/Resources/BulletVfxBank.asset";
        const string FallbackImpactPath = "Assets/Plugins/AllIn1VfxToolkit/Demo & Assets/Demo/Prefabs/Red Impact.prefab";
        const string LaserboltFolder = "Assets/Archanor/Sci-Fi Arsenal/InteractiveDemo/Demo Prefabs/Laserbolt";

        public static BulletVfxBank EnsureAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<BulletVfxBank>(AssetPath);
            if (existing != null)
            {
                PopulateIfEmpty(existing);
                ApplyLegacyVisualScale(existing);
                SyncResourcesCopy();
                return existing;
            }

            if (!Directory.Exists("Assets/Data"))
                Directory.CreateDirectory("Assets/Data");

            var bank = ScriptableObject.CreateInstance<BulletVfxBank>();
            PopulateIfEmpty(bank);
            AssetDatabase.CreateAsset(bank, AssetPath);
            AssetDatabase.SaveAssets();

            if (!Directory.Exists("Assets/Resources"))
                Directory.CreateDirectory("Assets/Resources");

            if (!File.Exists(ResourcesPath))
                AssetDatabase.CopyAsset(AssetPath, ResourcesPath);

            AssetDatabase.SaveAssets();
            ApplyLegacyVisualScale(bank);
            SyncResourcesCopy();
            return bank;
        }

        static void ApplyLegacyVisualScale(BulletVfxBank bank)
        {
            if (bank == null) return;
            var so = new SerializedObject(bank);
            so.FindProperty("visualScaleMultiplier").floatValue = 0.5f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bank);
        }

        static void SyncResourcesCopy()
        {
            if (!File.Exists(AssetPath)) return;
            if (!Directory.Exists("Assets/Resources"))
                Directory.CreateDirectory("Assets/Resources");
            AssetDatabase.CopyAsset(AssetPath, ResourcesPath);
            AssetDatabase.SaveAssets();
        }

        static void PopulateIfEmpty(BulletVfxBank bank)
        {
            var so = new SerializedObject(bank);
            var categories = so.FindProperty("categories");
            if (categories != null && categories.arraySize > 0)
            {
                ApplyLegacyVisualScale(bank);
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

            so.FindProperty("visualScaleMultiplier").floatValue = 0.5f;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bank);
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
