using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    public struct GemKinematics : IComponentData
    {
        public float3 Velocity;
    }
}
