using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class PlanetGhostAuthoring : MonoBehaviour
    {
        class Baker : Baker<PlanetGhostAuthoring>
        {
            public override void Bake(PlanetGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new PlanetTag());
                AddComponent(entity, new PlanetState());
            }
        }
    }
}
