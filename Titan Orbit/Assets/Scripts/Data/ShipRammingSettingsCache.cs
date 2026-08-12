using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="ShipRammingSettings"/> ScriptableObject.
    /// Set by <see cref="Game.ShipRammingSettingsLoader"/> at boot; read by
    /// <see cref="ShipComponentRammingSuggestions"/> so server damage and HUD estimates match.
    /// Null → code defaults (Global 0.5, SelfToAsteroid 2).
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

            // --- Code defaults (same as ScriptableObject field defaults) ---
            var fallback = ScriptableObject.CreateInstance<ShipRammingSettings>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.GlobalDamageMultiplier = 0.5f;
            fallback.SelfToAsteroidDamageRatio = 2f;
            fallback.GrindPulseIntervalSeconds = 0.5f;
            Settings = fallback;
            return Settings;
        }
    }
}
