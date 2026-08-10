using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="TractorBeamSettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as AsteroidSettingsLoader /
    /// GemExplosionSettingsLoader).
    /// Sole asset: <c>Assets/Resources/TractorBeamSettings.asset</c> — Editor and player builds
    /// both use <see cref="Resources.Load"/> (no Data/ duplicate).
    /// </summary>
    public class TractorBeamSettingsLoader : MonoBehaviour
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = "TractorBeamSettings";

        /// <summary>[UNITY] Drag the TractorBeamSettings asset here, or leave empty to auto-load.</summary>
        [SerializeField] TractorBeamSettings settings;

        /// <summary>Assigned settings (may be null before Awake).</summary>
        public TractorBeamSettings Settings => settings;

        /// <summary>
        /// [UNITY] Publishes the asset into <see cref="TractorBeamSettingsCache"/> before
        /// tractor / pickup systems run. Falls back to code defaults if the asset is missing.
        /// </summary>
        void Awake()
        {
            // --- Resolve asset ---
            if (settings == null)
                settings = TryLoadDefaultAsset();

            if (settings != null)
            {
                settings.ClampValues();
                TractorBeamSettingsCache.Settings = settings;
                return;
            }

            Debug.LogWarning(
                "[TractorBeamSettingsLoader] No TractorBeamSettings found. " +
                $"Create one via TitanOrbit → Create Tractor Beam Settings Asset " +
                $"(expected at Assets/Resources/{ResourcesLoadName}.asset). Using code defaults until then.");
            TractorBeamSettingsCache.ResolveOrDefault();
        }

        /// <summary>
        /// [UNITY] Loads the single Resources asset — same path in Editor Play Mode and player builds.
        /// </summary>
        static TractorBeamSettings TryLoadDefaultAsset()
        {
            return Resources.Load<TractorBeamSettings>(ResourcesLoadName);
        }
    }
}
