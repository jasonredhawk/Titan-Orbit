using Unity.Mathematics;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Snapshot of a ship's <c>PhysicsVelocity.Linear</c> taken after the motor drive tick and
    /// before Unity Physics integrates / solves contacts.
    /// <para>
    /// [TITAN-ORBIT] Custom bounce (<see cref="TitanOrbit.Simulation.ShipCollisionImpulseLogic"/>)
    /// must use pre-collision velocity because PhysX material restitution is 0 (inelastic solve
    /// would otherwise leave relative normal speed ≈ 0 and our impulse would see nothing to rebound).
    /// Written by <see cref="ShipPreCollisionVelocitySnapshotSystem"/>; read by
    /// <see cref="ShipCollisionBounceSystem"/> and toroidal seam resolve.
    /// </para>
    /// </summary>
    public struct ShipPreCollisionVelocity : IComponentData
    {
        /// <summary>Planar linear velocity captured before PhysicsSimulationGroup this fixed step.</summary>
        public float3 Linear;

        /// <summary>
        /// <see cref="Unity.Transforms.LocalTransform.Position"/> captured with <see cref="Linear"/>.
        /// MEGA asteroid plow reconstructs unconstrained motion (position + velocity × dt) so
        /// PhysX depenetration cannot shove the hull off its flight path.
        /// </summary>
        public float3 Position;

        /// <summary>Hull rotation captured with <see cref="Position"/> (Galaxy pose walk).</summary>
        public quaternion Rotation;
    }
}
