using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="ShipImpactSpinSettings"/> ScriptableObject.
    /// Set by <see cref="Game.ShipImpactSpinSettingsLoader"/> at boot; Burst jobs copy
    /// fields via <c>ShipImpactSpinLogic.FromSettings</c> on the main thread.
    /// </summary>
    public static class ShipImpactSpinSettingsCache
    {
        const string ResourcesLoadName = "ShipImpactSpinSettings";

        /// <summary>Current spin balance asset, or null until loader / first resolve.</summary>
        public static ShipImpactSpinSettings Settings { get; set; }

        /// <summary>Resolved settings, or a transient default instance when none assigned.</summary>
        public static ShipImpactSpinSettings ResolveOrDefault()
        {
            if (Settings != null)
                return Settings;

            var loaded = Resources.Load<ShipImpactSpinSettings>(ResourcesLoadName);
            if (loaded != null)
            {
                loaded.ClampValues();
                Settings = loaded;
                return Settings;
            }

            var fallback = ScriptableObject.CreateInstance<ShipImpactSpinSettings>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.ClampValues();
            Settings = fallback;
            return Settings;
        }

    }
}
