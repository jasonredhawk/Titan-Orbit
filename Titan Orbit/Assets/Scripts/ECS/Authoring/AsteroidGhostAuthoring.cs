using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class AsteroidGhostAuthoring : MonoBehaviour
    {
        class Baker : Baker<AsteroidGhostAuthoring>
        {
            public override void Bake(AsteroidGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new AsteroidTag());
                AddComponent(entity, new AsteroidState());
            }
        }
    }
}
