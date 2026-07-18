using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Strips missing MonoBehaviour slots from ship / UltimateSpaceships prefabs.
    /// Those broken components spam Console with "referenced script (Unknown)" when
    /// <see cref="TitanOrbit.Data.PlanetShipFamilyConfig"/> loads chassis prefab references —
    /// heavy Editor log I/O that can make Play Mode feel choppier than a Windows player build.
    /// </summary>
    public static class MissingScriptCleanupMenu
    {
        static readonly string[] SearchFolders =
        {
            "Assets/Prefabs",
            "Assets/Prefabs/Ships",
            "Assets/UltimateSpaceshipsCreator",
            "Assets/Resources",
        };

        /// <summary>
        /// Menu entry: scan prefabs under ship folders and remove missing-script components.
        /// </summary>
        [MenuItem("Titan Orbit/Cleanup/Remove Missing Scripts on Ship Prefabs")]
        public static void RemoveMissingScriptsOnShipPrefabs()
        {
            // --- Find prefabs ---
            string[] guids = AssetDatabase.FindAssets("t:Prefab", SearchFolders);
            int prefabsTouched = 0;
            int componentsRemoved = 0;

            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar(
                        "Remove missing scripts",
                        path,
                        guids.Length <= 1 ? 1f : (float)i / (guids.Length - 1));

                    var root = PrefabUtility.LoadPrefabContents(path);
                    if (root == null)
                        continue;

                    int removed = RemoveMissingRecursive(root);
                    if (removed > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        prefabsTouched++;
                        componentsRemoved += removed;
                    }

                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log(
                $"[TitanOrbit] Missing-script cleanup: removed {componentsRemoved} component(s) on {prefabsTouched} prefab(s). " +
                "Re-enter Play Mode — Instantiate of Asteroid/Planet/Gem proxies should stop logging missing scripts.");
        }

        /// <summary>
        /// Walks the hierarchy and deletes every missing MonoBehaviour (Unity "Unknown" script).
        /// </summary>
        static int RemoveMissingRecursive(GameObject go)
        {
            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                removed += RemoveMissingRecursive(t.GetChild(i).gameObject);
            return removed;
        }
    }
}
