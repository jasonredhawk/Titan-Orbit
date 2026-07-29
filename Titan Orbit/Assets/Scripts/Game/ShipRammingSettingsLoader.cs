using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="ShipRammingSettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as GemExplosionSettingsLoader).
    /// Asset path: Assets/Data/ShipRammingSettings.asset
    /// </summary>
    public class ShipRammingSettingsLoader : MonoBehaviour
    {
        const string DefaultAssetPath = "Assets/Data/ShipRammingSettings.asset";

        /// <summary>[UNITY] Drag the ShipRammingSettings asset here, or leave empty to auto-load.</summary>
        [SerializeField] ShipRammingSettings settings;

        /// <summary>Assigned settings (may be null before Awake).</summary>
        public ShipRammingSettings Settings => settings;

        /// <summary>
        /// [UNITY] Publishes the asset into <see cref="ShipRammingSettingsCache"/> before gameplay systems run.
        /// </summary>
        void Awake()
        {
            if (settings == null)
                settings = TryLoadDefaultAsset();

            if (settings != null)
            {
                settings.ClampValues();
                ShipRammingSettingsCache.Settings = settings;
                return;
            }

            Debug.LogWarning(
                "[ShipRammingSettingsLoader] No ShipRammingSettings found. " +
                $"Create one via Assets → Create → Titan Orbit → Ship Ramming Settings " +
                $"(expected at {DefaultAssetPath}). Using code defaults until then.");
            ShipRammingSettingsCache.ResolveOrDefault();
        }

        /// <summary>Editor AssetDatabase path, or Resources load in player builds.</summary>
        static ShipRammingSettings TryLoadDefaultAsset()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<ShipRammingSettings>(DefaultAssetPath);
#else
            return Resources.Load<ShipRammingSettings>("ShipRammingSettings");
#endif
        }
    }
}
