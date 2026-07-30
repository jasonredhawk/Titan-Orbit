#if UNITY_EDITOR
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.EditorTools
{
    /// <summary>
    /// [EDITOR] Creates the default GemExplosionSettings asset under Assets/Resources so designers
    /// can tune min/max gem count and explosion feel without hunting Create menus.
    /// One file only — Editor Play Mode and player builds both <c>Resources.Load</c> it.
    /// </summary>
    public static class GemExplosionSettingsMenu
    {
        const string ResourcesPath = "Assets/Resources/GemExplosionSettings.asset";

        [MenuItem("TitanOrbit/Create Gem Explosion Settings Asset")]
        static void CreateAsset()
        {
            Directory.CreateDirectory("Assets/Resources");

            var existing = AssetDatabase.LoadAssetAtPath<GemExplosionSettings>(ResourcesPath);
            if (existing != null)
            {
                Selection.activeObject = existing;
                EditorGUIUtility.PingObject(existing);
                Debug.Log($"[GemExplosionSettings] Already exists at {ResourcesPath}");
                return;
            }

            var asset = ScriptableObject.CreateInstance<GemExplosionSettings>();
            asset.ClampCounts();
            AssetDatabase.CreateAsset(asset, ResourcesPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log(
                $"[GemExplosionSettings] Created {ResourcesPath}. " +
                "Add GemExplosionSettingsLoader on NceGameRoot (optional — Resources auto-loads). " +
                "Tune Min/Max Gem Count (1–10), speed 2.2, damping 0.5, tumble in the Inspector.");
        }
    }
}
#endif
