#if UNITY_EDITOR
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.EditorTools
{
    /// <summary>
    /// [EDITOR] Creates the default TractorBeamSettings asset under Assets/Resources so designers
    /// can tune sticky / multi-beam / range / pickup without hunting Create menus.
    /// One file only — Editor Play Mode and player builds both <c>Resources.Load</c> it.
    /// </summary>
    public static class TractorBeamSettingsMenu
    {
        const string ResourcesPath = "Assets/Resources/TractorBeamSettings.asset";

        /// <summary>
        /// Menu entry: create (or ping) the sole TractorBeamSettings asset under Resources.
        /// </summary>
        [MenuItem("TitanOrbit/Create Tractor Beam Settings Asset")]
        static void CreateAsset()
        {
            Directory.CreateDirectory("Assets/Resources");

            var existing = AssetDatabase.LoadAssetAtPath<TractorBeamSettings>(ResourcesPath);
            if (existing != null)
            {
                Selection.activeObject = existing;
                EditorGUIUtility.PingObject(existing);
                Debug.Log($"[TractorBeamSettings] Already exists at {ResourcesPath}");
                return;
            }

            var asset = ScriptableObject.CreateInstance<TractorBeamSettings>();
            asset.ClampValues();
            AssetDatabase.CreateAsset(asset, ResourcesPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log(
                $"[TractorBeamSettings] Created {ResourcesPath}. " +
                "Add TractorBeamSettingsLoader on NceGameRoot (optional — Resources auto-loads). " +
                "Tune PrimaryStickyOnly, MaxCooperatingBeams, Range/Power multipliers, and pickup radii.");
        }
    }
}
#endif
