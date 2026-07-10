using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// Baker for gem pickup ghosts. Gems use scripted motion (<see cref="GemKinematics"/>) — no
    /// ship collision. Layer is set when spawned at runtime; prefab bakes tag + state only.
    /// </summary>
    public class GemGhostAuthoring : MonoBehaviour
    {
        class Baker : Baker<GemGhostAuthoring>
        {
            public override void Bake(GemGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new GemTag());
                AddComponent(entity, new GemState { Value = 1f, Size = 1f });
                AddComponent(entity, new GemKinematics());
            }
        }
    }
}
