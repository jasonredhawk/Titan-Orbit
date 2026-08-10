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
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = "MapGenerationSettings";

        /// <summary>
        /// [UNITY] BeforeSceneLoad — first chance to populate the static cache for ECS bootstrap.
        /// Priority: existing cache → scene loader → Resources default asset.
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

            // [UNITY] Same Resources asset for Editor Play Mode, Windows player, and headless server.
            var asset = Resources.Load<MapGenerationSettings>(ResourcesLoadName);
            if (asset != null)
                MapGenerationSettingsCache.Settings = asset;
        }
    }
}
