using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Registers map generation settings before the server generates the map.</summary>
    static class MapGenerationSettingsBootstrap
    {
        const string DefaultAssetPath = "Assets/Data/MapGenerationSettings.asset";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void RegisterSettings()
        {
            if (MapGenerationSettingsCache.Settings != null)
                return;

            var loader = Object.FindAnyObjectByType<MapGenerationSettingsLoader>();
            if (loader != null && loader.Settings != null)
            {
                MapGenerationSettingsCache.Settings = loader.Settings;
                return;
            }

#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<MapGenerationSettings>(DefaultAssetPath);
            if (asset != null)
                MapGenerationSettingsCache.Settings = asset;
#endif
        }
    }
}
