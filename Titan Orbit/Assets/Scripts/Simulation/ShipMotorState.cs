using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Transient motor state stepped identically on server and all clients each fixed tick.
    /// Lives only for the duration of <see cref="ShipMotorSimulator.Step"/> — not stored on entities.
    /// Position here is read from LocalTransform; when Unity Physics is active, the motor does not
    /// write Position back (physics solver owns hull position).
    /// </summary>
    public struct ShipMotorState
    {
        public float3 Position;
        public quaternion Rotation;
        public float3 Velocity;
        public float Mass;
        public uint LastSimTick;

        /// <summary>Hard-reset at spawn, respawn, or dock snap.</summary>
        public void ResetAt(float3 position, quaternion rotation, float mass)
        {
            Position = position;
            Position.y = 0f;
            Rotation = rotation;
            Velocity = float3.zero;
            Mass = math.max(0.5f, mass);
            LastSimTick = 0;
        }
    }
}
