using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] MonoBehaviour authoring on gem pickup ghost prefabs. The Baker adds
    /// <see cref="GemTag"/>, default <see cref="GemState"/>, <see cref="GemKinematics"/>,
    /// and <see cref="GemMotionState"/> (burst index + tractor lock for client presentation).
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
            /// [ECS/DOTS] Registers gem tag, default value/size, kinematics, and motion/lock state.
            /// </summary>
            public override void Bake(GemGhostAuthoring authoring)
            {
                // --- Entity registration ---
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new GemTag());

                // --- Client hybrid visual queue ---
                // [NETCODE] Pending is GhostPrefabType.Client only — see MapBodyHybridVisualPending.
                AddComponent(entity, new MapBodyHybridVisualPending());

                // --- Default gem state (overwritten at spawn with rolled value/size) ---
                AddComponent(entity, new GemState { Value = 1f, Size = 1f });

                // --- Scripted motion (no PhysicsVelocity) ---
                // [NETCODE] Velocity + AngularVelocity are GhostFields — client GemClientMotionApplier
                // follows interpolated pose / velocity (server still owns authority + pickup).
                AddComponent(entity, new GemKinematics());

                // --- Burst index + tractor lock (ghosted) ---
                // [NETCODE] Clients hand off local VFX by BurstIndex and time beam pull from lock tick.
                AddComponent(entity, new GemMotionState { Phase = GemMotionState.PhaseCoast });
            }
        }
    }
}
