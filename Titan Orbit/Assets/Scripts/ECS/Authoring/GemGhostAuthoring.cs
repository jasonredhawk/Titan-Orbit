using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] MonoBehaviour authoring on gem pickup ghost prefabs. The Baker adds
    /// <see cref="GemTag"/>, default <see cref="GemState"/>, and <see cref="GemKinematics"/>.
    /// [TITAN-ORBIT] Gems use scripted motion — no Unity Physics hull collision with ships.
    /// Physics layer is set when spawned at runtime by gem economy systems. Baked into SubScenes;
    /// server instantiates from <see cref="GamePrefabs.Gem"/>.
    /// </summary>
    public class GemGhostAuthoring : MonoBehaviour
    {
        /// <summary>[ECS/DOTS] Nested Baker for gem ghost entity components.</summary>
        class Baker : Baker<GemGhostAuthoring>
        {
            /// <summary>
            /// [ECS/DOTS] Registers gem tag, default value/size, and kinematics for scripted motion.
            /// </summary>
            public override void Bake(GemGhostAuthoring authoring)
            {
                // --- Entity registration ---
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new GemTag());

                // --- Default gem state (overwritten at spawn with rolled value/size) ---
                AddComponent(entity, new GemState { Value = 1f, Size = 1f });

                // --- Scripted motion component (no PhysicsVelocity) ---
                AddComponent(entity, new GemKinematics());
            }
        }
    }
}
