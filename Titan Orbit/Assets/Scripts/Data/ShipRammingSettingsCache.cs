using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="ShipRammingSettings"/> ScriptableObject.
    /// Set by <see cref="Game.ShipRammingSettingsLoader"/> at boot; read by
    /// <see cref="ShipComponentRammingSuggestions"/> so server damage and HUD estimates match.
    /// Null → code defaults (Global 1, MassReference 10, ram-double speed 10, SelfToAsteroid 2).
    /// </summary>
    public static class ShipRammingSettingsCache
    {
        /// <summary>Current ramming balance asset, or null until loader runs.</summary>
        public static ShipRammingSettings Settings { get; set; }

        /// <summary>Resolved settings, or a transient default instance when none assigned.</summary>
        public static ShipRammingSettings ResolveOrDefault()
        {
            if (Settings != null)
                return Settings;

            // Dedicated / headless may never run the NceGameRoot MonoBehaviour loader.
            // Resources.Load keeps server ram ratio / sliders on the same asset as the client.
            ShipRammingSettings loaded = Resources.Load<ShipRammingSettings>("ShipRammingSettings");
            if (loaded != null)
            {
                loaded.ClampValues();
                Settings = loaded;
                return Settings;
            }

            // --- Code defaults (same as ScriptableObject field defaults) ---
            var fallback = ScriptableObject.CreateInstance<ShipRammingSettings>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.GlobalDamageMultiplier = ShipComponentRammingSuggestions.DefaultGlobalDamageMultiplier;
            fallback.SelfToAsteroidDamageRatio = ShipComponentRammingSuggestions.DefaultSelfToAsteroidDamageRatio;
            fallback.MassReference = ShipComponentRammingSuggestions.DefaultMassReference;
            fallback.RamClosingSpeedForDouble = ShipComponentRammingSuggestions.DefaultRamClosingSpeedForDouble;
            Settings = fallback;
            return Settings;
        }
    }
}
