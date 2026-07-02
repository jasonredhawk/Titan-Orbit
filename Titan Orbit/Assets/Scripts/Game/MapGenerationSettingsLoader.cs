using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads map generation settings at play time so asset edits apply without rebaking the ECS subscene.
    /// Asset path: Assets/Data/MapGenerationSettings.asset
    /// </summary>
    public class MapGenerationSettingsLoader : MonoBehaviour
    {
        const string DefaultAssetPath = "Assets/Data/MapGenerationSettings.asset";

        [SerializeField] MapGenerationSettings settings;

        public MapGenerationSettings Settings => settings;

        void Awake()
        {
            if (settings == null)
                settings = TryLoadDefaultAsset();

            if (settings != null)
            {
                MapGenerationSettingsCache.Settings = settings;
                return;
            }

            Debug.LogWarning(
                "[MapGenerationSettingsLoader] No MapGenerationSettings found. " +
                $"Create one via Titan Orbit > Create Map Generation Settings Asset (expected at {DefaultAssetPath}), " +
                "or run Titan Orbit > Setup NetCode Game (Full).");
        }

        static MapGenerationSettings TryLoadDefaultAsset()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<MapGenerationSettings>(DefaultAssetPath);
#else
            return null;
#endif
        }
    }
}
