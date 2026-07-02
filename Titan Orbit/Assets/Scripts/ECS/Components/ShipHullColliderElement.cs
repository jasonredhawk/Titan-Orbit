using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>Oriented box collider in ship-root local space (matches scaled visual hull).</summary>
    public struct ShipHullColliderElement : IBufferElementData
    {
        public float3 LocalCenter;
        public quaternion LocalRotation;
        public float3 HalfExtents;
    }
}
