#if UNITY_EDITOR
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.EditorTools
{
    /// <summary>
    /// [EDITOR] Creates the default GemExplosionSettings asset under Assets/Data so designers
    /// can tune min/max gem count and explosion feel without hunting Create menus.
    /// </summary>
    public static class GemExplosionSettingsMenu
    {
        const string AssetPath = "Assets/Data/GemExplosionSettings.asset";

        const string ResourcesPath = "Assets/Resources/GemExplosionSettings.asset";

        [MenuItem("TitanOrbit/Create Gem Explosion Settings Asset")]
        static void CreateAsset()
        {
            Directory.CreateDirectory("Assets/Data");
            Directory.CreateDirectory("Assets/Resources");

            var existing = AssetDatabase.LoadAssetAtPath<GemExplosionSettings>(AssetPath);
            if (existing != null)
            {
                Selection.activeObject = existing;
                EditorGUIUtility.PingObject(existing);
                EnsureResourcesCopy(existing);
                Debug.Log($"[GemExplosionSettings] Already exists at {AssetPath}");
                return;
            }

            var asset = ScriptableObject.CreateInstance<GemExplosionSettings>();
            asset.ClampCounts();
            AssetDatabase.CreateAsset(asset, AssetPath);
            EnsureResourcesCopy(asset);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log(
                $"[GemExplosionSettings] Created {AssetPath} (+ Resources copy for player builds). " +
                "Add GemExplosionSettingsLoader on NceGameRoot (optional — path auto-loads). " +
                "Tune Min/Max Gem Count (1–10), speed 2.2, damping 0.5, tumble in the Inspector.");
        }

        static void EnsureResourcesCopy(GemExplosionSettings source)
        {
            if (source == null)
                return;
            var copy = AssetDatabase.LoadAssetAtPath<GemExplosionSettings>(ResourcesPath);
            if (copy != null)
                return;
            AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), ResourcesPath);
        }
    }
}
#endif
