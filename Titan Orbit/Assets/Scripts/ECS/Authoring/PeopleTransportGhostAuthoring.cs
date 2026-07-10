using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// Baker for people-transport projectile ghosts. Spawned at runtime by
    /// <see cref="PeopleTransportDispatchSystem"/> when ships load/unload population at planets.
    /// </summary>
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
