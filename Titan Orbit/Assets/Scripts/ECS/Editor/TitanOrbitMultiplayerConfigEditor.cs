#if UNITY_EDITOR
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    public static class TitanOrbitMultiplayerConfigEditor
    {
        const string AssetPath = "Assets/Resources/TitanOrbitMultiplayerConfig.asset";

        [MenuItem("Titan Orbit/Multiplayer/Enable Local Play UI")]
        public static void EnableLocalPlayUi() => SetLocalPlayUiEnabled(true);

        [MenuItem("Titan Orbit/Multiplayer/Disable Local Play UI (production)")]
        public static void DisableLocalPlayUi() => SetLocalPlayUiEnabled(false);

        public static void SetLocalPlayUiEnabled(bool enabled)
        {
            var config = EnsureAsset();
            config.showLocalPlayOptions = enabled;
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            Debug.Log("[TitanOrbit] Local play UI " + (enabled ? "enabled" : "disabled") + " on " + AssetPath +
                      (enabled ? "" : " (production-style menu)."));
        }

        public static TitanOrbitMultiplayerConfig EnsureAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TitanOrbitMultiplayerConfig>(AssetPath);
            if (existing != null)
                return existing;

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Resources"));
            var asset = ScriptableObject.CreateInstance<TitanOrbitMultiplayerConfig>();
            asset.showLocalPlayOptions = true;
            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();
            return asset;
        }
    }
}
#endif
