using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Transient motor state stepped identically on server and client each fixed tick inside
    /// <see cref="ShipMotorSimulator.Step"/>. Not stored on entities — rebuilt from
    /// LocalTransform and PhysicsVelocity at the start of each motor call. When Unity Physics
    /// is active, Position is read-only input; the solver integrates hull position after the motor.
    /// </summary>
    public struct ShipMotorState
    {
        /// <summary>Current hull position (Y forced to 0 for top-down play).</summary>
        public float3 Position;

        /// <summary>Facing quaternion — motor writes this; physics does not spin the ship.</summary>
        public quaternion Rotation;

        /// <summary>Linear velocity mirrored to PhysicsVelocity.Linear after the step.</summary>
        public float3 Velocity;

        /// <summary>Effective mass for thrust/brake curves (from ShipMassLogic).</summary>
        public float Mass;

        /// <summary>Last simulation tick index — detects stale reads across rollback.</summary>
        public uint LastSimTick;

        /// <summary>
        /// Hard-reset at spawn, respawn, or moon dock snap. Zeros velocity and sets mass floor.
        /// </summary>
        public void ResetAt(float3 position, quaternion rotation, float mass)
        {
            // --- Spawn / respawn / dock snap baseline ---
            Position = position;
            Position.y = 0f; // [TITAN-ORBIT] Top-down — lock Y to play plane.
            Rotation = rotation;
            Velocity = float3.zero;
            Mass = math.max(0.5f, mass);
            LastSimTick = 0;
        }
    }
}
