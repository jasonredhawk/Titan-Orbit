using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="AsteroidSettings"/> ScriptableObject.
    /// Set by <see cref="Game.AsteroidSettingsLoader"/> at boot; read by map generation and
    /// <see cref="ECS.AsteroidSpawning"/>. Null → code defaults (size 1–70, HP/gems per size = 1,
    /// visual scale 0.35–3.5, grind pulse 0.25s / 4 Hz).
    /// </summary>
    public static class AsteroidSettingsCache
    {
        /// <summary>Current asteroid balance asset, or null until loader runs.</summary>
        public static AsteroidSettings Settings { get; set; }

        /// <summary>Resolved settings, or a transient default instance when none assigned.</summary>
        public static AsteroidSettings ResolveOrDefault()
        {
            if (Settings != null)
                return Settings;

            var fallback = ScriptableObject.CreateInstance<AsteroidSettings>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.ClampValues();
            Settings = fallback;
            return Settings;
        }
    }
}
