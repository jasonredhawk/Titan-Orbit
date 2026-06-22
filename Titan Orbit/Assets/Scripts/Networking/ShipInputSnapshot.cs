using UnityEngine;
using Unity.Netcode;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Compact player intent sent from owner to server each tick. World-space aim (XZ) avoids
    /// screen/camera differences between peers.
    /// </summary>
    public struct ShipInputSnapshot : INetworkSerializable
    {
        public uint Sequence;
        public float ClientSendTime;
        public Vector2 AimWorldXZ;
        public bool Thrust;
        public bool Fire;
        public bool SpaceBrakes;
        /// <summary>Owner client predicted pose — server adopts this for human ships (collisions + broadcast).</summary>
        public Vector3 PredictedPosition;
        public Quaternion PredictedRotation;
        public Vector3 PredictedVelocity;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref ClientSendTime);
            serializer.SerializeValue(ref AimWorldXZ);
            serializer.SerializeValue(ref Thrust);
            serializer.SerializeValue(ref Fire);
            serializer.SerializeValue(ref SpaceBrakes);
            serializer.SerializeValue(ref PredictedPosition);
            serializer.SerializeValue(ref PredictedRotation);
            serializer.SerializeValue(ref PredictedVelocity);
        }

        public static ShipInputSnapshot Default => new ShipInputSnapshot
        {
            Sequence = 0,
            ClientSendTime = 0f,
            AimWorldXZ = Vector2.zero,
            Thrust = false,
            Fire = false,
            SpaceBrakes = true,
        };
    }

    /// <summary>
    /// Motor pose published after each physics tick. For human ships on a dedicated server the pose comes from
    /// the owner's client prediction; remote clients interpolate it. AI ships are fully server-simulated.
    /// </summary>
    public struct ShipMotorStateSnapshot : INetworkSerializable
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public uint LastAppliedInputSequence;
        public uint MotorPublishTick;
        public float SimMass;
        public double ServerTime;
        /// <summary>Owner's forward-thrust intent at this pose. Lets observers light engine/thruster VFX in lockstep with the interpolated motion.</summary>
        public bool Thrust;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
            serializer.SerializeValue(ref Velocity);
            serializer.SerializeValue(ref LastAppliedInputSequence);
            serializer.SerializeValue(ref MotorPublishTick);
            serializer.SerializeValue(ref SimMass);
            serializer.SerializeValue(ref ServerTime);
            serializer.SerializeValue(ref Thrust);
        }
    }
}
