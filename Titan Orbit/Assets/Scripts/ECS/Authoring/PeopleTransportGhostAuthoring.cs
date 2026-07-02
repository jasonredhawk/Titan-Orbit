using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class PeopleTransportGhostAuthoring : MonoBehaviour
    {
        class Baker : Baker<PeopleTransportGhostAuthoring>
        {
            public override void Bake(PeopleTransportGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<PeopleTransportTag>(entity);
                AddComponent(entity, new PeopleTransportState());
            }
        }
    }
}
