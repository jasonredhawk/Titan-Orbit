using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="GemExplosionSettings"/> ScriptableObject.
    /// Set by <see cref="Game.GemExplosionSettingsLoader"/> when that component is in the
    /// scene; otherwise the first resolve loads <c>Resources/GemExplosionSettings</c>.
    /// Dedicated-server boot often has no loader, and a bare <c>CreateInstance</c> would
    /// keep the C# defaults (3s self-block, 4s first dibs) and ignore the asset.
    /// Missing asset → those same code defaults.
    /// </summary>
    public static class GemExplosionSettingsCache
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = "GemExplosionSettings";

        /// <summary>Current gem explosion asset, or null until loader / first resolve.</summary>
        public static GemExplosionSettings Settings { get; set; }

        /// <summary>
        /// Resolved settings: assigned loader asset, then Resources, then a transient default.
        /// Safe to call from ECS systems every tick — Resources.Load / fallback run at most once.
        /// Pickup lockouts (<see cref="GemExplosionSettings.SelfPickupBlockSeconds"/> and
        /// <see cref="GemExplosionSettings.DestroyClaimExclusiveSeconds"/>) read this instance.
        /// </summary>
        public static GemExplosionSettings ResolveOrDefault()
        {
            if (Settings != null)
                return Settings;

            // --- Dedicated server / missing scene loader ---
            // [UNITY] Headless boot may never run GemExplosionSettingsLoader. Resources.Load still
            // finds Assets/Resources/GemExplosionSettings.asset in player builds and Editor Play Mode.
            var fromResources = Resources.Load<GemExplosionSettings>(ResourcesLoadName);
            if (fromResources != null)
            {
                fromResources.ClampCounts();
                Settings = fromResources;
                return Settings;
            }

            // --- Cold start / missing asset ---
            // [UNITY] HideAndDontSave — not written to disk; exists only for this Play session.
            var fallback = ScriptableObject.CreateInstance<GemExplosionSettings>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.ClampCounts();
            Settings = fallback;
            return Settings;
        }
    }
}
