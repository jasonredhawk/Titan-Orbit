using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="GemExplosionSettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as MapGenerationSettingsLoader).
    /// Sole asset: <c>Assets/Resources/GemExplosionSettings.asset</c> — Editor and player builds
    /// both use <see cref="Resources.Load"/> (no Data/ duplicate).
    /// </summary>
    public class GemExplosionSettingsLoader : MonoBehaviour
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = "GemExplosionSettings";

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
                $"Create one via TitanOrbit → Create Gem Explosion Settings Asset " +
                $"(expected at Assets/Resources/{ResourcesLoadName}.asset). Using code defaults until then.");
            GemExplosionSettingsCache.ResolveOrDefault();
        }

        /// <summary>
        /// [UNITY] Loads the single Resources asset — same path in Editor Play Mode and player builds.
        /// </summary>
        static GemExplosionSettings TryLoadDefaultAsset()
        {
            return Resources.Load<GemExplosionSettings>(ResourcesLoadName);
        }
    }
}
