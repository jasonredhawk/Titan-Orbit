using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class GemGhostAuthoring : MonoBehaviour
    {
        class Baker : Baker<GemGhostAuthoring>
        {
            public override void Bake(GemGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new GemTag());
                AddComponent(entity, new GemState { Value = 1f, Size = 1f });
            }
        }
    }
}
