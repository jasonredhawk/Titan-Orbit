using System;
using System.Collections.Generic;
using System.Linq;
using TitanOrbit.Entities;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Populates Starship.thrusterJetFlameBank from a folder containing JetFlame prefabs.
    /// Prefab names are expected to include a color (Blue, Green, Orange, Purple, Red, Yellow).
    /// </summary>
    public static class PopulateThrusterJetFlameBankFromFolder
    {
        private const string FolderPrefKey = "TitanOrbit.ThrusterJetFlameFolder";
        private const string DefaultFolder = "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Interactive/JetFlame/Soft";
        private static readonly string[] ColorOrder = { "Blue", "Green", "Orange", "Purple", "Red", "Yellow" };

        [MenuItem("Titan Orbit/Populate Thruster JetFlame Bank From Folder")]
        public static void PopulateFromFolder()
        {
            string startFolder = EditorPrefs.GetString(FolderPrefKey, DefaultFolder);
            string chosen = EditorUtility.OpenFolderPanel("Select JetFlame prefab folder", startFolder, string.Empty);
            if (string.IsNullOrEmpty(chosen)) return;

            string relative = ToProjectRelativePath(chosen);
            if (string.IsNullOrEmpty(relative))
            {
                Debug.LogWarning("Titan Orbit: Please select a folder under Assets.");
                return;
            }
            EditorPrefs.SetString(FolderPrefKey, relative);

            List<GameObject> prefabs = FindPrefabs(relative);
            if (prefabs.Count == 0)
            {
                Debug.LogWarning($"Titan Orbit: No prefab assets found in {relative}. Try selecting the exact folder that contains .prefab files.");
                return;
            }

            List<(string color, GameObject prefab)> bank = BuildColorBank(prefabs);
            if (bank.Count == 0)
            {
                Debug.LogWarning("Titan Orbit: No JetFlame prefabs with recognized color names found.");
                return;
            }

            Starship[] ships = UnityEngine.Object.FindObjectsOfType<Starship>(true);
            if (ships == null || ships.Length == 0)
            {
                Debug.LogWarning("Titan Orbit: No Starship found in the open scene.");
                return;
            }

            int updated = 0;
            foreach (Starship ship in ships)
            {
                if (ship == null) continue;
                if (ApplyBankToShip(ship, bank))
                    updated++;
            }

            Debug.Log($"Titan Orbit: Applied {bank.Count} JetFlame entries to {updated} Starship(s).");
        }

        private static bool ApplyBankToShip(Starship ship, List<(string color, GameObject prefab)> bank)
        {
            SerializedObject so = new SerializedObject(ship);
            SerializedProperty bankProp = so.FindProperty("thrusterJetFlameBank");
            if (bankProp == null)
                return false;

            bankProp.arraySize = bank.Count;
            for (int i = 0; i < bank.Count; i++)
            {
                SerializedProperty entry = bankProp.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("colorName").stringValue = bank[i].color;
                entry.FindPropertyRelative("prefab").objectReferenceValue = bank[i].prefab;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(ship);
            return true;
        }

        private static List<(string color, GameObject prefab)> BuildColorBank(List<GameObject> prefabs)
        {
            var byColor = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (GameObject prefab in prefabs)
            {
                if (prefab == null) continue;
                string color = ExtractColor(prefab.name);
                if (string.IsNullOrEmpty(color)) continue;
                if (!byColor.ContainsKey(color))
                    byColor[color] = prefab;
            }

            var result = new List<(string color, GameObject prefab)>();
            foreach (string color in ColorOrder)
            {
                if (byColor.TryGetValue(color, out GameObject prefab) && prefab != null)
                    result.Add((color, prefab));
            }
            return result;
        }

        private static string ExtractColor(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            for (int i = 0; i < ColorOrder.Length; i++)
            {
                if (value.IndexOf(ColorOrder[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return ColorOrder[i];
            }
            return null;
        }

        private static List<GameObject> FindPrefabs(string relativeFolder)
        {
            var result = new List<GameObject>();
            string normalizedRelative = relativeFolder.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(normalizedRelative))
                return result;

            // Use AssetDatabase search (Unity-native) instead of raw file IO so folder path differences do not miss assets.
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { normalizedRelative });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    result.Add(prefab);
            }
            return result.OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ToProjectRelativePath(string absolutePath)
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            string projectPath = dataPath.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase)
                ? dataPath.Substring(0, dataPath.Length - 7)
                : dataPath;
            string chosen = absolutePath.Replace('\\', '/').TrimEnd('/');
            if (!chosen.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                return null;

            string relative = chosen.Substring(projectPath.Length).Replace('\\', '/').TrimStart('/');
            if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return relative;
            if (string.Equals(relative, "Assets", StringComparison.OrdinalIgnoreCase))
                return "Assets";
            return "Assets/" + relative;
        }
    }
}
