#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// [EDITOR] Creates or re-populates the single <see cref="BulletVfxBank"/> at
    /// <c>Assets/Resources/BulletVfxBank.asset</c> from Sci-Fi Arsenal Demo Prefabs.
    /// <para>
    /// Each immediate subfolder under Demo Prefabs becomes one category (team-colored OBJ
    /// prefabs with <c>SciFiProjectileScript</c>). Fireballs/V1 and V2 become
    /// <c>Fireballs</c> / <c>FireballsV2</c> for readable B-key cycle names.
    /// </para>
    /// Menu: <b>Titan Orbit → Create Bullet VFX Bank</b> (ensure) and
    /// <b>Titan Orbit → Populate Bullet VFX Bank From Demo Prefabs</b> (force refill).
    /// </summary>
    public static class BulletVfxBankSetup
    {
        const string ResourcesPath = BulletVfxBank.ResourcesAssetPath;
        const string FallbackImpactPath = "Assets/Plugins/AllIn1VfxToolkit/Demo & Assets/Demo/Prefabs/Red Impact.prefab";
        const string DemoPrefabsFolder = "Assets/Archanor/Sci-Fi Arsenal/InteractiveDemo/Demo Prefabs";
        const string LegacyDataPath = "Assets/Data/BulletVfxBank.asset";

        /// <summary>Color sort within a category — Red first so team A matches the first slot visually in the Inspector.</summary>
        static readonly string[] ColorOrder = { "Red", "Blue", "Green", "Yellow", "Purple", "Orange" };

        /// <summary>
        /// Loads or creates the Resources bank. Removes a leftover Data copy if present.
        /// Populates from Demo Prefabs only when the categories list is empty.
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
            PopulateFromDemoPrefabs(bank, force: true);
            AssetDatabase.CreateAsset(bank, ResourcesPath);
            AssetDatabase.SaveAssets();
            return bank;
        }

        /// <summary>
        /// [EDITOR] Menu: wipe and refill every Demo Prefabs category (keeps scale / impact fields).
        /// Use after adding new Sci-Fi Arsenal folders, or if the asset only has Laserbolt.
        /// </summary>
        [MenuItem("Titan Orbit/Populate Bullet VFX Bank From Demo Prefabs")]
        public static void PopulateFromDemoPrefabsMenu()
        {
            var bank = EnsureAsset();
            if (bank == null)
            {
                Debug.LogError("[BulletVfxBankSetup] Could not create or load BulletVfxBank.");
                return;
            }

            PopulateFromDemoPrefabs(bank, force: true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[BulletVfxBankSetup] Populated {bank.CategoryCount} categories from {DemoPrefabsFolder}.");
        }

        static void DeleteLegacyDataCopyIfPresent()
        {
            if (AssetDatabase.LoadAssetAtPath<BulletVfxBank>(LegacyDataPath) == null)
                return;
            AssetDatabase.DeleteAsset(LegacyDataPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[BulletVfxBankSetup] Removed legacy Assets/Data/BulletVfxBank.asset — use Resources only.");
        }

        /// <summary>Fill categories only when the list is empty (first create / empty asset).</summary>
        static void PopulateIfEmpty(BulletVfxBank bank)
        {
            if (bank == null)
                return;

            var so = new SerializedObject(bank);
            var categories = so.FindProperty("categories");
            if (categories != null && categories.arraySize > 0)
            {
                EnsureScaleDefaults(so);
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(bank);
                return;
            }

            PopulateFromDemoPrefabs(bank, force: true);
        }

        /// <summary>
        /// Scans Demo Prefabs for <c>SciFiProjectileScript</c> OBJ prefabs, groups by folder,
        /// and writes the categories list. Laserbolt stays first so family default index 0 is unchanged.
        /// </summary>
        /// <param name="force">When true, replaces existing categories (menu refill).</param>
        static void PopulateFromDemoPrefabs(BulletVfxBank bank, bool force)
        {
            if (bank == null)
                return;

            var so = new SerializedObject(bank);
            var categoriesProp = so.FindProperty("categories");
            if (!force && categoriesProp != null && categoriesProp.arraySize > 0)
            {
                EnsureScaleDefaults(so);
                so.ApplyModifiedPropertiesWithoutUndo();
                return;
            }

            List<BulletVfxBank.Category> built = FindPrefabsGroupedByFolderAndSortedByColor(DemoPrefabsFolder);
            if (built.Count == 0)
            {
                // Fallback: Laserbolt-only if Demo Prefabs missing (partial checkout).
                built.Add(new BulletVfxBank.Category
                {
                    categoryName = "Laserbolt",
                    prefabs = LoadLaserboltPrefabsFallback(),
                });
            }

            // --- Prefer Laserbolt as index 0 (ShipFamilyDefinition.bulletPrefabIndex default) ---
            built = OrderWithLaserboltFirst(built);

            categoriesProp.arraySize = built.Count;
            for (int c = 0; c < built.Count; c++)
            {
                var cat = built[c];
                var catElement = categoriesProp.GetArrayElementAtIndex(c);
                catElement.FindPropertyRelative("categoryName").stringValue = cat.categoryName;
                var prefabsProp = catElement.FindPropertyRelative("prefabs");
                int prefabCount = cat.prefabs != null ? cat.prefabs.Count : 0;
                prefabsProp.arraySize = prefabCount;
                for (int i = 0; i < prefabCount; i++)
                    prefabsProp.GetArrayElementAtIndex(i).objectReferenceValue = cat.prefabs[i];
            }

            var impact = AssetDatabase.LoadAssetAtPath<GameObject>(FallbackImpactPath);
            if (impact != null)
                so.FindProperty("fallbackImpactPrefab").objectReferenceValue = impact;

            EnsureScaleDefaults(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bank);
        }

        /// <summary>
        /// Does not overwrite designer values — only fills missing/zero scale fields.
        /// Per-category Global/Upgrade default to 1 (100% of bank knobs).
        /// </summary>
        static void EnsureScaleDefaults(SerializedObject so)
        {
            var globalProp = so.FindProperty("globalVisualScaleMultiplier");
            if (globalProp == null)
                globalProp = so.FindProperty("visualScaleMultiplier");
            // Leave bank global as-is when already set (designer may have chosen 0.25).

            var upgradeProp = so.FindProperty("upgradeVisualScaleMultiplier");
            if (upgradeProp != null && upgradeProp.floatValue <= 0.001f)
                upgradeProp.floatValue = 0.5f;

            // --- Per-category overrides (new fields → 0 until migrated) ---
            var categoriesProp = so.FindProperty("categories");
            if (categoriesProp == null || !categoriesProp.isArray)
                return;

            for (int i = 0; i < categoriesProp.arraySize; i++)
            {
                var cat = categoriesProp.GetArrayElementAtIndex(i);
                var catGlobal = cat.FindPropertyRelative("globalVisualScaleMultiplier");
                if (catGlobal != null && catGlobal.floatValue <= 0.001f)
                    catGlobal.floatValue = 1f;
                var catUpgrade = cat.FindPropertyRelative("upgradeVisualScaleMultiplier");
                if (catUpgrade != null && catUpgrade.floatValue <= 0.001f)
                    catUpgrade.floatValue = 1f;
            }
        }

        /// <summary>
        /// Find prefabs with SciFiProjectileScript, group by parent folder name, sort each group by color.
        /// Fireballs/V1 → category "Fireballs"; Fireballs/V2 → "FireballsV2".
        /// </summary>
        static List<BulletVfxBank.Category> FindPrefabsGroupedByFolderAndSortedByColor(string relativeFolder)
        {
            var withPath = FindPrefabsWithSciFiProjectileAndPath(relativeFolder);
            if (withPath == null || withPath.Count == 0)
                return new List<BulletVfxBank.Category>();

            var byFolder = new Dictionary<string, List<GameObject>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in withPath)
            {
                if (!byFolder.TryGetValue(kv.Key, out List<GameObject> list))
                {
                    list = new List<GameObject>();
                    byFolder[kv.Key] = list;
                }
                list.Add(kv.Value);
            }

            var categories = new List<BulletVfxBank.Category>();
            foreach (var kv in byFolder.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var sorted = kv.Value.OrderBy(p => GetColorSortOrder(p != null ? p.name : "")).ToList();
                categories.Add(new BulletVfxBank.Category
                {
                    categoryName = kv.Key,
                    prefabs = sorted,
                });
            }

            return categories;
        }

        static int GetColorSortOrder(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return ColorOrder.Length;
            for (int i = 0; i < ColorOrder.Length; i++)
            {
                if (prefabName.IndexOf(ColorOrder[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return ColorOrder.Length;
        }

        /// <summary>
        /// Returns (displayCategoryName, prefab) for every Demo Prefabs OBJ that has SciFiProjectileScript.
        /// </summary>
        static List<KeyValuePair<string, GameObject>> FindPrefabsWithSciFiProjectileAndPath(string relativeFolder)
        {
            var result = new List<KeyValuePair<string, GameObject>>();
            string dataPath = Application.dataPath.Replace('\\', '/');
            string normalizedRelative = relativeFolder.Replace('\\', '/').TrimEnd('/');
            string folderWithoutAssets = normalizedRelative.StartsWith("Assets/")
                ? normalizedRelative.Substring(7)
                : normalizedRelative;
            string fullPath = Path.Combine(dataPath, folderWithoutAssets).Replace('\\', '/');
            if (!Directory.Exists(fullPath))
                return result;

            string[] files = Directory.GetFiles(fullPath, "*.prefab", SearchOption.AllDirectories);
            foreach (string file in files)
            {
                string path = file.Replace('\\', '/');
                if (!path.StartsWith(dataPath))
                    continue;
                path = "Assets" + path.Substring(dataPath.Length);
                if (!path.StartsWith("Assets/"))
                    continue;

                // Skip non-OBJ particle children — bank rows are the *OBJ launcher prefabs.
                if (!path.EndsWith("OBJ.prefab", StringComparison.OrdinalIgnoreCase))
                    continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || !HasSciFiProjectileScript(prefab))
                    continue;

                string categoryName = ResolveCategoryNameFromAssetPath(path);
                result.Add(new KeyValuePair<string, GameObject>(categoryName, prefab));
            }

            return result;
        }

        /// <summary>
        /// Parent folder name, with Fireballs/V1 → Fireballs and Fireballs/V2 → FireballsV2
        /// so B-key floating text is readable.
        /// </summary>
        static string ResolveCategoryNameFromAssetPath(string assetPath)
        {
            string parent = Path.GetFileName(Path.GetDirectoryName(assetPath));
            if (string.IsNullOrEmpty(parent))
                return "Default";

            // Fireballs live in V1 / V2 subfolders — rename for display.
            string grand = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(assetPath)));
            if (string.Equals(grand, "Fireballs", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(parent, "V1", StringComparison.OrdinalIgnoreCase))
                    return "Fireballs";
                if (string.Equals(parent, "V2", StringComparison.OrdinalIgnoreCase))
                    return "FireballsV2";
            }

            return parent;
        }

        /// <summary>
        /// Sci-Fi Arsenal lives in Assembly-CSharp; Editor assembly may not reference it.
        /// Match by type name like runtime <see cref="BulletVfxBank"/>.
        /// </summary>
        static bool HasSciFiProjectileScript(GameObject prefab)
        {
            foreach (MonoBehaviour script in prefab.GetComponents<MonoBehaviour>())
            {
                if (script != null && script.GetType().Name == "SciFiProjectileScript")
                    return true;
            }
            return false;
        }

        static List<BulletVfxBank.Category> OrderWithLaserboltFirst(List<BulletVfxBank.Category> built)
        {
            var ordered = new List<BulletVfxBank.Category>();
            BulletVfxBank.Category laser = null;
            foreach (var cat in built)
            {
                if (laser == null && string.Equals(cat.categoryName, "Laserbolt", StringComparison.OrdinalIgnoreCase))
                    laser = cat;
            }

            if (laser != null)
                ordered.Add(laser);

            foreach (var cat in built.OrderBy(c => c.categoryName, StringComparer.OrdinalIgnoreCase))
            {
                if (laser != null && ReferenceEquals(cat, laser))
                    continue;
                if (laser != null && string.Equals(cat.categoryName, "Laserbolt", StringComparison.OrdinalIgnoreCase))
                    continue;
                ordered.Add(cat);
            }

            return ordered;
        }

        static List<GameObject> LoadLaserboltPrefabsFallback()
        {
            var list = new List<GameObject>();
            const string laserboltFolder = DemoPrefabsFolder + "/Laserbolt";
            string[] colors = { "Red", "Blue", "Green", "Yellow", "Purple" };
            foreach (string color in colors)
            {
                string path = $"{laserboltFolder}/LaserBolt{color}OBJ.prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    list.Add(prefab);
            }

            return list;
        }
    }
}
#endif
