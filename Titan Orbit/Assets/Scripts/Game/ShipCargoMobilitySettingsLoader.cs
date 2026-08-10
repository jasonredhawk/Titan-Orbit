using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="ShipCargoMobilitySettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as ShipRammingSettingsLoader).
    /// Sole asset: <c>Assets/Resources/ShipCargoMobilitySettings.asset</c> — Editor and player builds
    /// both use <see cref="Resources.Load"/> (no Data/ duplicate).
    /// </summary>
    public class ShipCargoMobilitySettingsLoader : MonoBehaviour
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = ShipCargoMobilitySettingsCache.ResourcesLoadName;

        /// <summary>[UNITY] Drag the ShipCargoMobilitySettings asset here, or leave empty to auto-load.</summary>
        [SerializeField] ShipCargoMobilitySettings settings;

        /// <summary>Assigned settings (may be null before Awake).</summary>
        public ShipCargoMobilitySettings Settings => settings;

        /// <summary>
        /// [UNITY] Publishes the asset into <see cref="ShipCargoMobilitySettingsCache"/> before gameplay systems run.
        /// </summary>
        void Awake()
        {
            if (settings == null)
                settings = TryLoadDefaultAsset();

            if (settings != null)
            {
                settings.ClampValues();
                ShipCargoMobilitySettingsCache.Settings = settings;
                return;
            }

            Debug.LogWarning(
                "[ShipCargoMobilitySettingsLoader] No ShipCargoMobilitySettings found. " +
                $"Create one via Assets → Create → Titan Orbit → Ship Cargo Mobility Settings " +
                $"(expected at Assets/Resources/{ResourcesLoadName}.asset). Using code defaults until then.");
            ShipCargoMobilitySettingsCache.ResolveOrDefault();
        }

        /// <summary>
        /// [UNITY] Loads the single Resources asset — same path in Editor Play Mode and player builds.
        /// </summary>
        static ShipCargoMobilitySettings TryLoadDefaultAsset()
        {
            return Resources.Load<ShipCargoMobilitySettings>(ResourcesLoadName);
        }
    }
}
