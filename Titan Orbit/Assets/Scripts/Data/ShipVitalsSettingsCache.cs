using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="ShipVitalsSettings"/> ScriptableObject.
    /// Read by ship hull regen and stat apply so the post-damage heal pause matches
    /// the Resources asset.
    /// Null → <c>Resources.Load</c>, then a transient default of 1 second.
    /// </summary>
    public static class ShipVitalsSettingsCache
    {
        /// <summary>Current vitals asset, or null until resolve runs.</summary>
        public static ShipVitalsSettings Settings { get; set; }

        /// <summary>
        /// Seconds hull regen waits after damage. Live asset value when present.
        /// </summary>
        public static float HealthRegenDelayAfterDamage
        {
            get
            {
                ShipVitalsSettings settings = ResolveOrDefault();
                return settings != null
                    ? Mathf.Max(0f, settings.healthRegenDelayAfterDamage)
                    : ShipVitalsSettings.DefaultHealthRegenDelayAfterDamage;
            }
        }

        /// <summary>
        /// Resolved settings: cached instance, Resources asset, or a transient default.
        /// Safe from stat apply and the server regen tick (one cached load).
        /// </summary>
        public static ShipVitalsSettings ResolveOrDefault()
        {
            if (Settings != null)
                return Settings;

            // Dedicated / headless may not run a scene loader. Resources.Load keeps
            // the delay on the same asset as the Editor.
            ShipVitalsSettings loaded = Resources.Load<ShipVitalsSettings>(ShipVitalsSettings.ResourcesLoadName);
            if (loaded != null)
            {
                loaded.ClampValues();
                Settings = loaded;
                return Settings;
            }

            var fallback = ScriptableObject.CreateInstance<ShipVitalsSettings>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.ClampValues();
            Settings = fallback;
            return Settings;
        }
    }
}
