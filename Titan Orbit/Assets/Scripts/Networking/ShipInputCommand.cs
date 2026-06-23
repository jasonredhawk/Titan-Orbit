using Unity.Netcode;
using UnityEngine;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Compact player intent. Server assigns <see cref="ServerTick"/>; all peers apply at that tick.
    /// </summary>
    public struct ShipInputCommand : INetworkSerializable
    {
        public uint Sequence;
        public uint ClientTick;
        public uint ServerTick;
        public Vector2 AimWorldXZ;
        public bool Thrust;
        public bool Fire;
        public bool SpaceBrakes;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref ClientTick);
            serializer.SerializeValue(ref ServerTick);
            serializer.SerializeValue(ref AimWorldXZ);
            serializer.SerializeValue(ref Thrust);
            serializer.SerializeValue(ref Fire);
            serializer.SerializeValue(ref SpaceBrakes);
        }

        public static ShipInputCommand Default => new ShipInputCommand
        {
            Sequence = 0,
            ClientTick = 0,
            ServerTick = 0,
            AimWorldXZ = Vector2.zero,
            Thrust = false,
            Fire = false,
            SpaceBrakes = true,
        };
    }

    /// <summary>Sparse server correction when sim drift exceeds threshold.</summary>
    public struct ShipMotorCorrectionKeyframe : INetworkSerializable
    {
        public uint ServerTick;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public float Mass;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ServerTick);
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
            serializer.SerializeValue(ref Velocity);
            serializer.SerializeValue(ref Mass);
        }
    }
}
