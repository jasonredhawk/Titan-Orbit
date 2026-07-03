using TitanOrbit.Data;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class MapGenerationSettingsAuthoring : MonoBehaviour
    {
        public MapGenerationSettings Settings;

        class Baker : Baker<MapGenerationSettingsAuthoring>
        {
            public override void Bake(MapGenerationSettingsAuthoring authoring)
            {
                // GamePrefabsRegistryAuthoring on the same GameObject already bakes MapGenerationConfig.
                if (authoring.GetComponent<GamePrefabsRegistryAuthoring>() != null)
                    return;

                var entity = GetEntity(TransformUsageFlags.None);
                var s = authoring.Settings;
                AddComponent(entity, s != null
                    ? MapGenerationConfigUtility.FromSettings(s)
                    : MapGenerationConfigUtility.Default());
            }
        }
    }
}
