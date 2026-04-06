using Unity.Netcode;
using UnityEngine;

namespace TitanOrbit.Generation
{
    public enum MapLayoutKind : byte
    {
        Home = 0,
        Neutral = 1,
        Asteroid = 2
    }

    /// <summary>
    /// Serializable snapshot of one map entity so joining clients can replay a progressive "build"
    /// with the same order and transforms as the host, without waiting for network spawns.
    /// </summary>
    public struct MapLayoutEntry : INetworkSerializable
    {
        public MapLayoutKind Kind;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        /// <summary>Home planet index (0–4) for <see cref="MapLayoutKind.Home"/>; otherwise 0.</summary>
        public byte HomeTeamIndex;
        /// <summary>Neutral: rolled planet size. Asteroid: gem size roll (before radius mapping).</summary>
        public float ExtraFloat;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            byte k = (byte)Kind;
            serializer.SerializeValue(ref k);
            Kind = (MapLayoutKind)k;
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
            serializer.SerializeValue(ref Scale);
            serializer.SerializeValue(ref HomeTeamIndex);
            serializer.SerializeValue(ref ExtraFloat);
        }
    }
}
