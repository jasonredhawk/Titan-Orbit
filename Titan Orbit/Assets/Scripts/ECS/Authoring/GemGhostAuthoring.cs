using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// MonoBehaviour authoring on gem pickup prefabs. The Baker adds
    /// <see cref="GemTag"/>, default <see cref="GemState"/>, <see cref="GemKinematics"/>,
    /// and <see cref="GemMotionState"/>.
    /// Gems use scripted motion — no Unity Physics hull collision with ships.
    /// Server Instantiates from <see cref="GamePrefabs.Gem"/> as a local entity (not a ghost);
    /// clients hydrate the same prefab from spawn / burst RPCs.
    /// </summary>
    public class GemGhostAuthoring : MonoBehaviour
    {
        /// <summary>Nested Baker for gem entity components.</summary>
        class Baker : Baker<GemGhostAuthoring>
        {
            /// <summary>Registers gem tag, default value/size, kinematics, and motion/lock state.</summary>
            public override void Bake(GemGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new GemTag());
                AddComponent(entity, new MapBodyHybridVisualPending());
                AddComponent(entity, new GemState { Value = 1f, Size = 1f });
                AddComponent(entity, new GemKinematics());
                AddComponent(entity, new GemMotionState { Phase = GemMotionState.PhaseCoast });
            }
        }
    }
}
