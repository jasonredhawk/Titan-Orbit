using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="GemExplosionSettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as MapGenerationSettingsLoader).
    /// Asset path: Assets/Data/GemExplosionSettings.asset
    /// </summary>
    public class GemExplosionSettingsLoader : MonoBehaviour
    {
        const string DefaultAssetPath = "Assets/Data/GemExplosionSettings.asset";

        [SerializeField] GemExplosionSettings settings;

        /// <summary>Assigned settings (may be null before Awake).</summary>
        public GemExplosionSettings Settings => settings;

        void Awake()
        {
            if (settings == null)
                settings = TryLoadDefaultAsset();

            if (settings != null)
            {
                settings.ClampCounts();
                GemExplosionSettingsCache.Settings = settings;
                return;
            }

            Debug.LogWarning(
                "[GemExplosionSettingsLoader] No GemExplosionSettings found. " +
                $"Create one via Assets → Create → Titan Orbit → Gem Explosion Settings " +
                $"(expected at {DefaultAssetPath}). Using code defaults until then.");
            GemExplosionSettingsCache.ResolveOrDefault();
        }

        static GemExplosionSettings TryLoadDefaultAsset()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GemExplosionSettings>(DefaultAssetPath);
#else
            return Resources.Load<GemExplosionSettings>("GemExplosionSettings");
#endif
        }
    }
}
