using Unity.Netcode;
using UnityEngine;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Compact player intent sent from owner to server each physics step.
    /// </summary>
    public struct ShipInputCommand : INetworkSerializable
    {
        public Vector2 AimWorldXZ;
        public bool Thrust;
        public bool Fire;
        public bool SpaceBrakes;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref AimWorldXZ);
            serializer.SerializeValue(ref Thrust);
            serializer.SerializeValue(ref Fire);
            serializer.SerializeValue(ref SpaceBrakes);
        }

        public static ShipInputCommand Default => new ShipInputCommand
        {
            AimWorldXZ = Vector2.zero,
            Thrust = false,
            Fire = false,
            SpaceBrakes = true,
        };
    }
}
