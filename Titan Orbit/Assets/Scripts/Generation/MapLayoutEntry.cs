using System;
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
    /// Implements <see cref="IEquatable{MapLayoutEntry}"/> so it can be stored in <see cref="NetworkList{T}"/>.
    /// </summary>
    public struct MapLayoutEntry : INetworkSerializable, IEquatable<MapLayoutEntry>
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

        public bool Equals(MapLayoutEntry other)
        {
            return Kind == other.Kind
                && HomeTeamIndex == other.HomeTeamIndex
                && Position == other.Position
                && Rotation == other.Rotation
                && Scale == other.Scale
                && ExtraFloat.Equals(other.ExtraFloat);
        }

        public override bool Equals(object obj) => obj is MapLayoutEntry other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Kind;
                h = (h * 397) ^ HomeTeamIndex;
                h = (h * 397) ^ Position.GetHashCode();
                h = (h * 397) ^ Rotation.GetHashCode();
                h = (h * 397) ^ Scale.GetHashCode();
                h = (h * 397) ^ ExtraFloat.GetHashCode();
                return h;
            }
        }
    }
}
