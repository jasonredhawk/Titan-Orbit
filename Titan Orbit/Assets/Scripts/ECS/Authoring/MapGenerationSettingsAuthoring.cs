using TitanOrbit.Data;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] Standalone baker for map generation settings when not combined with
    /// <see cref="GamePrefabsRegistryAuthoring"/> on the same GameObject. Writes
    /// <see cref="MapGenerationConfig"/> singleton at bake time for server map generation.
    /// Skips bake if GamePrefabsRegistryAuthoring is present (avoids duplicate singleton).
    /// </summary>
    public class MapGenerationSettingsAuthoring : MonoBehaviour
    {
        /// <summary>[TITAN-ORBIT] Designer-tunable map generation ScriptableObject.</summary>
        public MapGenerationSettings Settings;

        /// <summary>[ECS/DOTS] Nested Baker for MapGenerationConfig singleton.</summary>
        class Baker : Baker<MapGenerationSettingsAuthoring>
        {
            /// <summary>
            /// [ECS/DOTS] Bakes MapGenerationConfig unless GamePrefabsRegistryAuthoring already did.
            /// </summary>
            public override void Bake(MapGenerationSettingsAuthoring authoring)
            {
                // --- Avoid duplicate singleton ---
                // GamePrefabsRegistryAuthoring on the same GameObject already bakes MapGenerationConfig.
                if (authoring.GetComponent<GamePrefabsRegistryAuthoring>() != null)
                    return;

                // --- Standalone config entity ---
                var entity = GetEntity(TransformUsageFlags.None);
                var s = authoring.Settings;
                AddComponent(entity, s != null
                    ? MapGenerationConfigUtility.FromSettings(s)
                    : MapGenerationConfigUtility.Default());
            }
        }
    }
}
