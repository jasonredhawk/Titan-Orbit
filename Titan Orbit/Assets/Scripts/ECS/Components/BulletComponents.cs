using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    public struct BulletElement : IBufferElementData
    {
        public float3 Position;
        public float3 Velocity;
        public float MaxDistance;
        public float Lifetime;
        public float Damage;
        public int OwnerNetworkId;
        public byte OwnerTeam;
        public uint Sequence;
        public float Traveled;
        public float Age;
    }

    public struct BulletSpawnEventElement : IBufferElementData
    {
        public float3 SpawnPosition;
        public float3 Velocity;
        public float Lifetime;
        public float MaxDistance;
        public float Damage;
        public byte OwnerTeam;
        public uint Sequence;
        public int BankIndex;
        public float ScaleMultiplier;
    }

    public struct BulletHitEventElement : IBufferElementData
    {
        public float3 HitPosition;
        public float Damage;
        public byte OwnerTeam;
        public int BankIndex;
        public float ScaleMultiplier;
    }

    public struct ActiveBulletsTag : IComponentData { }

    public struct BulletTracerState : IComponentData
    {
        public float3 Position;
        public float3 SpawnPosition;
        public float3 Velocity;
        public float RemainingLifetime;
        public float MaxDistance;
        public float Scale;
        public float ScaleMultiplier;
        public float Damage;
        public byte OwnerTeam;
        public int BankIndex;
    }
}
