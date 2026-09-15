using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Scripted motion for gem pickup entities — gems are not Unity Physics bodies.
    /// Advanced each tick by <see cref="GemMotionSystem"/> / client hydrate motion.
    /// Not ghost-replicated; clients resolve the same launch from the spawn recipe.
    /// </summary>
    public struct GemKinematics : IComponentData
    {
        /// <summary>
        /// World-units-per-second velocity on the XZ plane (Y usually 0).
        /// Decays via linear damping each tick until below stop threshold.
        /// </summary>
        public float3 Velocity;

        /// <summary>
        /// Angular velocity in radians/sec (world space). Original GemSpawner set
        /// Rigidbody.angularVelocity to Random(±1.5) per axis so gems tumbled while exploding.
        /// </summary>
        public float3 AngularVelocity;
    }
}
