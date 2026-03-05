using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Data.Editor
{
    /// <summary>
    /// Editor utility that scans all USC modular example prefabs and writes out a simple
    /// JSON file describing which components each prefab instance contains and their
    /// local transforms. This is a one-time/offline tool to help populate ShipPartCatalog
    /// and preset layouts.
    /// </summary>
    public static class USCComponentMapGenerator
    {
        private const string PrefabsRoot = "Assets/UltimateSpaceshipsCreator/Prefabs/ModularExamples";

        [MenuItem("Titan Orbit/USC/Generate Component Map")]
        public static void GenerateComponentMap()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabsRoot });
            var allEntries = new List<ComponentMapEntry>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    CollectEntriesForPrefab(path, root.transform, allEntries);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            string json = JsonUtility.ToJson(new ComponentMapWrapper { entries = allEntries.ToArray() }, true);
            string outputPath = Path.Combine(Application.dataPath, "../USCComponentMap.json");
            File.WriteAllText(outputPath, json);
            Debug.Log($"USC Component map written to: {outputPath} ({allEntries.Count} entries).");
        }

        private static void CollectEntriesForPrefab(string prefabPath, Transform root, List<ComponentMapEntry> accumulator)
        {
            if (root == null) return;

            foreach (Transform child in root)
            {
                if (child == null) continue;

                string name = child.name;
                string family = ExtractFamilyName(name);
                string purpose = ExtractPurposeName(name);
                string key = string.IsNullOrEmpty(family) || string.IsNullOrEmpty(purpose)
                    ? name
                    : $"{family}_{purpose}";

                var entry = new ComponentMapEntry
                {
                    prefabPath = prefabPath,
                    objectName = name,
                    componentKey = key,
                    localPosition = child.localPosition,
                    localRotation = child.localRotation,
                    localScale = child.localScale
                };
                accumulator.Add(entry);

                // Recurse into children so nested modules are also recorded.
                CollectEntriesForPrefab(prefabPath, child, accumulator);
            }
        }

        private static string ExtractFamilyName(string objectName)
        {
            // Example names: "AstroEagle_Thruster", "GalacticLeopard_Parts_Cargo", "StarForce_Engine (2)"
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            int idx = objectName.IndexOf('_');
            if (idx <= 0) return string.Empty;
            return objectName.Substring(0, idx);
        }

        private static string ExtractPurposeName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            int idx = objectName.IndexOf('_');
            if (idx < 0 || idx + 1 >= objectName.Length) return string.Empty;
            string rest = objectName.Substring(idx + 1);
            // Strip Unity's "(Clone)" and instance suffixes like " (1)".
            int paren = rest.IndexOf('(');
            if (paren > 0)
                rest = rest.Substring(0, paren).Trim();
            return rest;
        }

        [System.Serializable]
        private class ComponentMapWrapper
        {
            public ComponentMapEntry[] entries;
        }

        [System.Serializable]
        private class ComponentMapEntry
        {
            public string prefabPath;
            public string objectName;
            public string componentKey;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
        }
    }
}

