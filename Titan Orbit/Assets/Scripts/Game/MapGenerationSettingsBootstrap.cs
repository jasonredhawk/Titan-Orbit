using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [UNITY] Registers <see cref="MapGenerationSettings"/> into <see cref="MapGenerationSettingsCache"/>
    /// before any scene loads so dedicated server and client agree on map size, team count, and spawn
    /// rules when <see cref="ECS.Systems.GameBootstrapSystem"/> generates the world. Runs via
    /// RuntimeInitializeOnLoadMethod — no scene object required.
    /// </summary>
    static class MapGenerationSettingsBootstrap
    {
        /// <summary>Default designer asset when no <see cref="MapGenerationSettingsLoader"/> is in scene.</summary>
        const string DefaultAssetPath = "Assets/Data/MapGenerationSettings.asset";

        /// <summary>
        /// [UNITY] BeforeSceneLoad — first chance to populate the static cache for ECS bootstrap.
        /// Priority: existing cache → scene loader → editor default asset.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void RegisterSettings()
        {
            if (MapGenerationSettingsCache.Settings != null)
                return;

            // --- Optional MonoBehaviour loader in bootstrap scene ---
            var loader = Object.FindAnyObjectByType<MapGenerationSettingsLoader>();
            if (loader != null && loader.Settings != null)
            {
                MapGenerationSettingsCache.Settings = loader.Settings;
                return;
            }

#if UNITY_EDITOR
            // [EDITOR] Play Mode without loader still gets project default asset.
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<MapGenerationSettings>(DefaultAssetPath);
            if (asset != null)
                MapGenerationSettingsCache.Settings = asset;
#endif
        }
    }
}
