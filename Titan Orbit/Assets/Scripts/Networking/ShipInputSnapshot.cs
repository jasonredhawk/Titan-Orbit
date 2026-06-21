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
        /// <summary>Owner client predicted pose — server uses for wing tractor targets (not motor authority).</summary>
        public Vector3 PredictedPosition;
        public Quaternion PredictedRotation;

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
    /// Server-authoritative motor pose published after each physics tick. Owner client reconciles
    /// against this instead of NetworkTransform (which would fight local prediction).
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

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
            serializer.SerializeValue(ref Velocity);
            serializer.SerializeValue(ref LastAppliedInputSequence);
            serializer.SerializeValue(ref MotorPublishTick);
            serializer.SerializeValue(ref SimMass);
            serializer.SerializeValue(ref ServerTime);
        }
    }
}
