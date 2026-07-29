using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="AsteroidSettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as ShipRammingSettingsLoader).
    /// Asset path: Assets/Data/AsteroidSettings.asset
    /// </summary>
    public class AsteroidSettingsLoader : MonoBehaviour
    {
        const string DefaultAssetPath = "Assets/Data/AsteroidSettings.asset";

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
                $"(expected at {DefaultAssetPath}). Using code defaults until then.");
            AsteroidSettingsCache.ResolveOrDefault();
        }

        /// <summary>Editor AssetDatabase path, or Resources load in player builds.</summary>
        static AsteroidSettings TryLoadDefaultAsset()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<AsteroidSettings>(DefaultAssetPath);
#else
            return Resources.Load<AsteroidSettings>("AsteroidSettings");
#endif
        }
    }
}
