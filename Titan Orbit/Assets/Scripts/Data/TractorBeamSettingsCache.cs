using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime pointer to the active <see cref="TractorBeamSettings"/> ScriptableObject.
    /// Set by <see cref="Game.TractorBeamSettingsLoader"/> at boot; also auto-loaded from
    /// <c>Resources/TractorBeamSettings</c> the first time a system asks — dedicated-server
    /// boot can spawn a bare NceGameRoot without the loader MonoBehaviour.
    /// Read by <c>GemTractorBeamSystem</c>, <c>GemTractorBeamClientLogic</c>, and <c>GemPickupSystem</c>.
    /// Missing asset → code defaults (primary sticky, max 8 cooperating beams, 1× range/power,
    /// wing collect 0.65, hull pickup 2.5 with hull scoop enabled).
    /// </summary>
    public static class TractorBeamSettingsCache
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = "TractorBeamSettings";

        /// <summary>Current tractor / pickup balance asset, or null until loader / first resolve.</summary>
        public static TractorBeamSettings Settings { get; set; }

        /// <summary>
        /// Resolved settings: assigned loader asset, then Resources, then a transient default.
        /// Safe to call from ECS systems every tick — Resources.Load / fallback run at most once.
        /// </summary>
        public static TractorBeamSettings ResolveOrDefault()
        {
            if (Settings != null)
                return Settings;

            // --- Dedicated server / missing loader ---
            // [UNITY] Headless boot may never run TractorBeamSettingsLoader. Resources.Load still
            // finds Assets/Resources/TractorBeamSettings.asset in player builds and Editor Play Mode.
            var fromResources = Resources.Load<TractorBeamSettings>(ResourcesLoadName);
            if (fromResources != null)
            {
                fromResources.ClampValues();
                Settings = fromResources;
                return Settings;
            }

            // --- Cold start / missing asset ---
            // [UNITY] HideAndDontSave — not written to disk; exists only for this Play session.
            var fallback = ScriptableObject.CreateInstance<TractorBeamSettings>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.ClampValues();
            Settings = fallback;
            return Settings;
        }

        /// <summary>
        /// Applies <see cref="TractorBeamSettings.RangeMultiplier"/> and
        /// <see cref="TractorBeamSettings.PowerMultiplier"/> to already-resolved wing reach/speed.
        /// Call after <c>GemTractorBeamMath.GetWingTractorParams</c> / max-gems fallback so
        /// designer multipliers stay in one place for server and client.
        /// </summary>
        /// <param name="searchRadius">In/out search reach (world units).</param>
        /// <param name="attractionSpeed">In/out gameplay pull speed (m/s).</param>
        public static void ApplyReachAndPower(ref float searchRadius, ref float attractionSpeed)
        {
            var s = ResolveOrDefault();
            searchRadius = Mathf.Max(0.5f, searchRadius * s.RangeMultiplier);
            attractionSpeed = Mathf.Max(0f, attractionSpeed * s.PowerMultiplier);
        }

        /// <summary>
        /// Applies only the range multiplier (when pull speed is resolved separately).
        /// </summary>
        /// <param name="searchRadius">In/out search reach (world units).</param>
        public static void ApplyReach(ref float searchRadius)
        {
            var s = ResolveOrDefault();
            searchRadius = Mathf.Max(0.5f, searchRadius * s.RangeMultiplier);
        }

        /// <summary>
        /// Applies only the power multiplier to a resolved attraction speed.
        /// </summary>
        /// <param name="attractionSpeed">In/out gameplay pull speed (m/s).</param>
        public static void ApplyPower(ref float attractionSpeed)
        {
            var s = ResolveOrDefault();
            attractionSpeed = Mathf.Max(0f, attractionSpeed * s.PowerMultiplier);
        }
    }
}
