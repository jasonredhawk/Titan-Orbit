using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Scripted motion for gem pickups — gems are not Unity Physics bodies. Advanced each tick by
    /// <see cref="GemMotionSystem"/> (drag + position integration). Spawned with burst velocity from
    /// mining and asteroid destruction.
    /// </summary>
    public struct GemKinematics : IComponentData
    {
        /// <summary>World-units-per-second velocity; decays via drag in GemMotionSystem.</summary>
        public float3 Velocity;
    }
}
