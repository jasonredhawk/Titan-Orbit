using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Runtime toggle for local host/client UI. Load from Resources/TitanOrbitMultiplayerConfig.
    /// Disable <see cref="showLocalPlayOptions"/> before publishing production WebGL builds.
    /// </summary>
    [CreateAssetMenu(fileName = "TitanOrbitMultiplayerConfig", menuName = "Titan Orbit/Multiplayer Config")]
    public class TitanOrbitMultiplayerConfig : ScriptableObject
    {
        const string ResourcePath = "TitanOrbitMultiplayerConfig";

        [Tooltip("When enabled, the main menu shows Local Host / Local Client dev buttons.")]
        public bool showLocalPlayOptions;

        static TitanOrbitMultiplayerConfig s_Cached;

        public static TitanOrbitMultiplayerConfig Instance
        {
            get
            {
                if (s_Cached == null)
                    s_Cached = Resources.Load<TitanOrbitMultiplayerConfig>(ResourcePath);
                return s_Cached;
            }
        }

        public static bool ShowLocalPlayOptions => Instance != null && Instance.showLocalPlayOptions;

        public static void SetShowLocalPlayOptions(bool enabled)
        {
            var config = Instance;
            if (config == null)
                return;
            config.showLocalPlayOptions = enabled;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(config);
#endif
        }
    }
}
