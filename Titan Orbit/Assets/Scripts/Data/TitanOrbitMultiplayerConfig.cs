using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime toggle for local host/client dev UI on the main menu. ScriptableObject loaded from
    /// Resources/TitanOrbitMultiplayerConfig at first access. Disable
    /// <see cref="showLocalPlayOptions"/> before shipping production WebGL builds so players only
    /// see relay/join flow. Editor can flip the flag via <see cref="SetShowLocalPlayOptions"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "TitanOrbitMultiplayerConfig", menuName = "Titan Orbit/Multiplayer Config")]
    public class TitanOrbitMultiplayerConfig : ScriptableObject
    {
        /// <summary>[UNITY] Resources folder path without extension — must match asset file name.</summary>
        const string ResourcePath = "TitanOrbitMultiplayerConfig";

        /// <summary>
        /// When true, main menu shows Local Host / Local Client buttons for MPPM-style dev testing.
        /// </summary>
        [Tooltip("When enabled, the main menu shows Local Host / Local Client dev buttons.")]
        public bool showLocalPlayOptions;

        /// <summary>Cached singleton instance after first <see cref="Instance"/> load.</summary>
        static TitanOrbitMultiplayerConfig s_Cached;

        /// <summary>
        /// Lazy-loaded config asset from Resources. Returns null if the asset is missing from the build.
        /// </summary>
        public static TitanOrbitMultiplayerConfig Instance
        {
            get
            {
                // --- Load once ---
                // [UNITY] Resources.Load — asset must live under Assets/Resources/TitanOrbitMultiplayerConfig.asset
                if (s_Cached == null)
                    s_Cached = Resources.Load<TitanOrbitMultiplayerConfig>(ResourcePath);
                return s_Cached;
            }
        }

        /// <summary>Convenience: true when config exists and dev local-play buttons should show.</summary>
        public static bool ShowLocalPlayOptions => Instance != null && Instance.showLocalPlayOptions;

        /// <summary>
        /// Sets the dev flag and marks the asset dirty in the Editor so the change persists to disk.
        /// No-op if the Resources asset is missing.
        /// </summary>
        /// <param name="enabled">New value for <see cref="showLocalPlayOptions"/>.</param>
        public static void SetShowLocalPlayOptions(bool enabled)
        {
            var config = Instance;
            if (config == null)
                return;

            config.showLocalPlayOptions = enabled;
#if UNITY_EDITOR
            // [EDITOR] Persist toggle when changed from a menu item or test harness.
            UnityEditor.EditorUtility.SetDirty(config);
#endif
        }
    }
}
