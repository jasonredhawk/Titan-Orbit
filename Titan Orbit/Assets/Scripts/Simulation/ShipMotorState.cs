using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>Deterministic planar ship motor state stepped identically on server and all clients.</summary>
    public struct ShipMotorState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public float Mass;
        public uint LastSimTick;

        public void ResetAt(Vector3 position, Quaternion rotation, float mass)
        {
            Position = position;
            Position.y = 0f;
            Rotation = rotation;
            Velocity = Vector3.zero;
            Mass = Mathf.Max(0.5f, mass);
            LastSimTick = 0;
        }
    }
}
