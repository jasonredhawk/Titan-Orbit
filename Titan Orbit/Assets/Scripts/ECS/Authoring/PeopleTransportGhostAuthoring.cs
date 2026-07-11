using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] MonoBehaviour authoring on people-transport projectile ghost prefabs. Spawned at
    /// runtime by people transport dispatch systems when ships load or unload population at planets.
    /// Baker adds <see cref="PeopleTransportTag"/> and empty <see cref="PeopleTransportState"/> —
    /// dispatch systems fill state on spawn. [NETCODE] Ghost-replicated to all clients for VFX.
    /// </summary>
    public class PeopleTransportGhostAuthoring : MonoBehaviour
    {
        /// <summary>[ECS/DOTS] Nested Baker for transport projectile entity.</summary>
        class Baker : Baker<PeopleTransportGhostAuthoring>
        {
            /// <summary>[ECS/DOTS] Registers transport tag and initial empty state.</summary>
            public override void Bake(PeopleTransportGhostAuthoring authoring)
            {
                // --- Entity registration ---
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<PeopleTransportTag>(entity);
                AddComponent(entity, new PeopleTransportState());
            }
        }
    }
}
