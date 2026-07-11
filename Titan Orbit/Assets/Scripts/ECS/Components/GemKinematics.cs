using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Scripted motion for gem pickup entities — gems are not Unity Physics bodies.
    /// Advanced each tick by gem motion systems (drag + position integration). Spawned with burst
    /// velocity from mining and asteroid destruction. Paired with <see cref="GemState"/> and
    /// <see cref="GemTag"/> on gem ghost entities baked by <see cref="Authoring.GemGhostAuthoring"/>.
    /// </summary>
    public struct GemKinematics : IComponentData
    {
        // --- Type members ---
        /// <summary>
        /// [TITAN-ORBIT] World-units-per-second velocity on the XZ plane; decays via drag each tick
        /// until the gem settles for tractor-beam pickup.
        /// </summary>
        public float3 Velocity;
    }
}
