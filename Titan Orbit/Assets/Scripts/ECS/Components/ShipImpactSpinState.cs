using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Motor-owned planar spin from glancing collisions and leftover-firepower wreck hits.
    /// Ghosted so owner prediction and remotes see the same yaw / wreck window.
    /// <see cref="WreckExpiresAt"/> 0 = not wrecked. Bake on the starship ghost.
    /// </summary>
    public struct ShipImpactSpinState : IComponentData
    {
        /// <summary>Signed yaw rate the motor integrates each tick (deg/sec).</summary>
        [GhostField(Quantization = 100)]
        public float YawRateDegPerSec;

        /// <summary>
        /// ECS elapsed seconds when the 0-HP wreck window ends. 0 = not wrecked.
        /// </summary>
        [GhostField(Quantization = 100)]
        public float WreckExpiresAt;
    }
}
