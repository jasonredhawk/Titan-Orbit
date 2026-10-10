using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime toggle for local host/client dev UI on the main menu. ScriptableObject loaded from
    /// Resources/TitanOrbitMultiplayerConfig at first access. The Editor Test / Production switch
    /// writes <see cref="showLocalPlayOptions"/> for Play Mode. A published WebGL player ignores
    /// that asset and always hides Local play / Local client so the shipped menu is production.
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

        /// <summary>
        /// True when the main menu should show Local play / Local client.
        /// Published WebGL always returns false, even if the Editor was in Test when the build was made.
        /// </summary>
        public static bool ShowLocalPlayOptions
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                // [TITAN-ORBIT] Shipped WebGL is production: Quick join only.
                // The Resources flag stays free for Editor Test / MPPM.
                return false;
#else
                return Instance != null && Instance.showLocalPlayOptions;
#endif
            }
        }

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
