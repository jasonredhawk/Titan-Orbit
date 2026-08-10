using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Scripted motion for gem pickup entities — gems are not Unity Physics bodies.
    /// Advanced each tick by <see cref="GemMotionSystem"/> (linear damping + position + tumble).
    /// Burst spawn sets Velocity and AngularVelocity like the old NGO Rigidbody launch.
    /// [NETCODE] Both fields are ghosted so clients can present smooth glide/tumble.
    /// </summary>
    public struct GemKinematics : IComponentData
    {
        /// <summary>
        /// [TITAN-ORBIT] World-units-per-second velocity on the XZ plane (Y usually 0).
        /// Decays via linear damping each tick until below stop threshold.
        /// </summary>
        [GhostField] public float3 Velocity;

        /// <summary>
        /// [TITAN-ORBIT] Angular velocity in radians/sec (world space). Original GemSpawner set
        /// Rigidbody.angularVelocity to Random(±1.5) per axis so gems tumbled while exploding.
        /// </summary>
        [GhostField] public float3 AngularVelocity;
    }
}
