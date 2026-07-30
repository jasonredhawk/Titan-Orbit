using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="AsteroidSettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as ShipRammingSettingsLoader).
    /// Sole asset: <c>Assets/Resources/AsteroidSettings.asset</c> — Editor and player builds
    /// both use <see cref="Resources.Load"/> (no Data/ duplicate).
    /// </summary>
    public class AsteroidSettingsLoader : MonoBehaviour
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = "AsteroidSettings";

        /// <summary>[UNITY] Drag the AsteroidSettings asset here, or leave empty to auto-load.</summary>
        [SerializeField] AsteroidSettings settings;

        /// <summary>Assigned settings (may be null before Awake).</summary>
        public AsteroidSettings Settings => settings;

        /// <summary>
        /// [UNITY] Publishes the asset into <see cref="AsteroidSettingsCache"/> before map gen runs.
        /// </summary>
        void Awake()
        {
            if (settings == null)
                settings = TryLoadDefaultAsset();

            if (settings != null)
            {
                settings.ClampValues();
                AsteroidSettingsCache.Settings = settings;
                return;
            }

            Debug.LogWarning(
                "[AsteroidSettingsLoader] No AsteroidSettings found. " +
                $"Create one via Assets → Create → Titan Orbit → Asteroid Settings " +
                $"(expected at Assets/Resources/{ResourcesLoadName}.asset). Using code defaults until then.");
            AsteroidSettingsCache.ResolveOrDefault();
        }

        /// <summary>
        /// [UNITY] Loads the single Resources asset — same path in Editor Play Mode and player builds.
        /// </summary>
        static AsteroidSettings TryLoadDefaultAsset()
        {
            return Resources.Load<AsteroidSettings>(ResourcesLoadName);
        }
    }
}
