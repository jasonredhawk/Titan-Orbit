using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="GemExplosionSettings"/> ScriptableObject.
    /// Set by <see cref="Game.GemExplosionSettingsLoader"/> at boot; read by gem spawn/motion
    /// and client burst presentation. Null → code defaults matching NGO-era GemSpawner/Gem.
    /// </summary>
    public static class GemExplosionSettingsCache
    {
        /// <summary>Current gem explosion asset, or null until loader runs.</summary>
        public static GemExplosionSettings Settings { get; set; }

        /// <summary>Resolved settings, or a transient default instance when none assigned.</summary>
        public static GemExplosionSettings ResolveOrDefault()
        {
            if (Settings != null)
                return Settings;

            // --- Code defaults (same as ScriptableObject field defaults) ---
            var fallback = ScriptableObject.CreateInstance<GemExplosionSettings>();
            fallback.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            Settings = fallback;
            return Settings;
        }
    }
}
