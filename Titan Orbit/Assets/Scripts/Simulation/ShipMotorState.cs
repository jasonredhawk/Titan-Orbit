using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>Deterministic planar ship motor state stepped identically on server and all clients.</summary>
    public struct ShipMotorState
    {
        public float3 Position;
        public quaternion Rotation;
        public float3 Velocity;
        public float Mass;
        public uint LastSimTick;

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
