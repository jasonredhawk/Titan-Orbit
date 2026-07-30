using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="ShipRammingSettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as GemExplosionSettingsLoader).
    /// Sole asset: <c>Assets/Resources/ShipRammingSettings.asset</c> — Editor and player builds
    /// both use <see cref="Resources.Load"/> (no Data/ duplicate).
    /// </summary>
    public class ShipRammingSettingsLoader : MonoBehaviour
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = "ShipRammingSettings";

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
                $"(expected at Assets/Resources/{ResourcesLoadName}.asset). Using code defaults until then.");
            ShipRammingSettingsCache.ResolveOrDefault();
        }

        /// <summary>
        /// [UNITY] Loads the single Resources asset — same path in Editor Play Mode and player builds.
        /// </summary>
        static ShipRammingSettings TryLoadDefaultAsset()
        {
            return Resources.Load<ShipRammingSettings>(ResourcesLoadName);
        }
    }
}
