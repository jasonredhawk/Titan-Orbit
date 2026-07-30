using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="ShipCargoMobilitySettings"/> ScriptableObject.
    /// Set by <see cref="Game.ShipCargoMobilitySettingsLoader"/> at boot; read by
    /// <see cref="ShipMobilityResolution"/> so server motor apply and client HUD match.
    /// Null → try <c>Resources.Load</c>, then code defaults matching the ScriptableObject fields.
    /// </summary>
    public static class ShipCargoMobilitySettingsCache
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        public const string ResourcesLoadName = "ShipCargoMobilitySettings";

        /// <summary>Current cargo-mobility balance asset, or null until loader / resolve runs.</summary>
        public static ShipCargoMobilitySettings Settings { get; set; }

        /// <summary>
        /// Resolved settings: cached instance, Resources asset, or a transient default.
        /// Safe to call from ECS apply paths and MonoBehaviour HUD every frame.
        /// </summary>
        public static ShipCargoMobilitySettings ResolveOrDefault()
        {
            if (Settings != null)
                return Settings;

            // --- Prefer the Resources asset (Editor + player builds share one file) ---
            var fromResources = Resources.Load<ShipCargoMobilitySettings>(ResourcesLoadName);
            if (fromResources != null)
            {
                fromResources.ClampValues();
                Settings = fromResources;
                return Settings;
            }

            // --- Code defaults (same as ScriptableObject field defaults) ---
            var fallback = ScriptableObject.CreateInstance<ShipCargoMobilitySettings>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.ClampValues();
            Settings = fallback;
            return Settings;
        }
    }
}
