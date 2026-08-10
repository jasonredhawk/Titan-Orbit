using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads map generation settings at play time so asset edits apply without rebaking the ECS subscene.
    /// Sole asset: <c>Assets/Resources/MapGenerationSettings.asset</c> — Editor and player builds
    /// both use <see cref="Resources.Load"/> (no Data/ duplicate).
    /// </summary>
    public class MapGenerationSettingsLoader : MonoBehaviour
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        const string ResourcesLoadName = "MapGenerationSettings";

        [SerializeField] MapGenerationSettings settings;

        public MapGenerationSettings Settings => settings;

        /// <summary>
        /// [UNITY] Awake — loads <see cref="MapGenerationSettings"/> into static cache for ECS bootstrap.
        /// Lets designers tweak map asset without rebaking SubScene.
        /// </summary>
        void Awake()
        {
            // --- Unity lifecycle ---
            if (settings == null)
                settings = TryLoadDefaultAsset();

            if (settings != null)
            {
                MapGenerationSettingsCache.Settings = settings;
                return;
            }

            Debug.LogWarning(
                "[MapGenerationSettingsLoader] No MapGenerationSettings found. " +
                $"Create one via Titan Orbit > Create Map Generation Settings Asset " +
                $"(expected at Assets/Resources/{ResourcesLoadName}.asset), " +
                "or run Titan Orbit > Setup NetCode Game (Full).");
        }

        /// <summary>
        /// [UNITY] Loads the single Resources asset — same path in Editor Play Mode and player builds.
        /// </summary>
        static MapGenerationSettings TryLoadDefaultAsset()
        {
            return Resources.Load<MapGenerationSettings>(ResourcesLoadName);
        }
    }
}
