using UnityEngine;
using UnityEditor;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Unity menu items for bulk import/export of <see cref="WeaponConfig"/> assets via
    /// <see cref="WeaponConfigCsv"/>. Lets designers edit cannon tables in Excel and round-trip
    /// into Assets/Data/WeaponConfigs. Not included in player or server builds.
    /// </summary>
    public static class WeaponConfigEditor
    {
        /// <summary>Finds every WeaponConfig in the project and writes one combined CSV file.</summary>
        [MenuItem("Titan Orbit/Weapon Configs/Export All to CSV")]
        public static void ExportAll()
        {
            string path = EditorUtility.SaveFilePanel("Export Weapon Configs CSV", "Assets", "WeaponConfigs", "csv");
            if (string.IsNullOrEmpty(path)) return;

            // --- Collect all WeaponConfig assets ---
            var guids = AssetDatabase.FindAssets("t:WeaponConfig");
            var configs = new System.Collections.Generic.List<WeaponConfig>();
            foreach (string g in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<WeaponConfig>(AssetDatabase.GUIDToAssetPath(g));
                if (asset != null) configs.Add(asset);
            }
            WeaponConfigCsv.ExportAllToCsv(configs, path);
            AssetDatabase.Refresh();
        }

        /// <summary>Creates new WeaponConfig .asset files from a CSV under Assets/Data/WeaponConfigs.</summary>
        [MenuItem("Titan Orbit/Weapon Configs/Import from CSV")]
        public static void Import()
        {
            string path = EditorUtility.OpenFilePanel("Import Weapon Configs CSV", "Assets", "csv");
            if (string.IsNullOrEmpty(path)) return;
            var configs = WeaponConfigCsv.ImportFromCsv(path);
            string saveDir = "Assets/Data/WeaponConfigs";

            // --- Ensure target folder exists ---
            if (!AssetDatabase.IsValidFolder("Assets/Data"))
            {
                if (!AssetDatabase.IsValidFolder("Assets")) AssetDatabase.CreateFolder("", "Assets");
                AssetDatabase.CreateFolder("Assets", "Data");
            }
            if (!AssetDatabase.IsValidFolder("Assets/Data/WeaponConfigs"))
                AssetDatabase.CreateFolder("Assets/Data", "WeaponConfigs");

            foreach (var config in configs)
            {
                string safeName = config.displayName.Replace("/", "_").Replace("\\", "_");
                string assetPath = $"{saveDir}/{safeName}.asset";
                AssetDatabase.CreateAsset(config, assetPath);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Imported {configs.Count} weapon configs into {saveDir}");
        }

        /// <summary>Exports only the WeaponConfig currently selected in the Project window.</summary>
        [MenuItem("Titan Orbit/Weapon Configs/Export Selected to CSV")]
        public static void ExportSelected()
        {
            var config = Selection.activeObject as WeaponConfig;
            if (config == null)
            {
                EditorUtility.DisplayDialog("Export", "Select a WeaponConfig asset first.", "OK");
                return;
            }
            string path = EditorUtility.SaveFilePanel("Export Weapon Config CSV", "Assets", config.displayName + ".csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            WeaponConfigCsv.ExportToCsv(config, path);
        }
    }
}
