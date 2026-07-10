using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative bullet simulation state stored in a singleton buffer on the entity tagged
    /// with <see cref="ActiveBulletsTag"/>. Each element is one live projectile advanced by
    /// <see cref="BulletSimulationSystem"/>. Not ghost-replicated — clients see tracers from spawn events.
    /// </summary>
    public struct BulletElement : IBufferElementData
    {
        /// <summary>Logical toroidal position this tick (ECS sim space).</summary>
        public float3 Position;
        /// <summary>World-units-per-second velocity vector.</summary>
        public float3 Velocity;
        /// <summary>Maximum travel distance before despawn.</summary>
        public float MaxDistance;
        /// <summary>Seconds until automatic despawn.</summary>
        public float Lifetime;
        /// <summary>Damage applied on hit (ships, moons, asteroids).</summary>
        public float Damage;
        /// <summary>Shooter's NetCode network id for attribution.</summary>
        public int OwnerNetworkId;
        /// <summary>Shooter team as byte (cast to <c>TeamId</c>).</summary>
        public byte OwnerTeam;
        /// <summary>Monotonic shot id for client VFX deduplication.</summary>
        public uint Sequence;
        /// <summary>Distance traveled since spawn (toroidal segment sum).</summary>
        public float Traveled;
        /// <summary>Seconds since spawn.</summary>
        public float Age;
    }

    /// <summary>
    /// One-shot spawn notification written by <see cref="BulletSimulationSystem"/> and consumed by
    /// <see cref="BulletPresentationSystem"/> to create cosmetic tracer entities.
    /// </summary>
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

    /// <summary>
    /// Hit notification for client VFX (impact sparks). Written by <see cref="BulletTracerUpdateSystem"/>
    /// when a cosmetic tracer intersects geometry.
    /// </summary>
    public struct BulletHitEventElement : IBufferElementData
    {
        public float3 HitPosition;
        public float Damage;
        public byte OwnerTeam;
        /// <summary>Index into weapon VFX bank for muzzle/tracer style.</summary>
        public int BankIndex;
        public float ScaleMultiplier;
    }

    /// <summary>
    /// Tag on the singleton entity that owns all bullet buffers. Required by bullet sim and presentation systems.
    /// </summary>
    public struct ActiveBulletsTag : IComponentData { }

    /// <summary>
    /// Marks a tracer entity whose positions are already in client display/world space
    /// (not logical ECS toroidal space). Used by hybrid VFX bridges.
    /// </summary>
    public struct BulletTracerDisplaySpace : IComponentData { }

    /// <summary>
    /// Cosmetic bullet tracer — presentation-only entity advanced by <see cref="BulletTracerUpdateSystem"/>.
    /// Does not deal authoritative damage; server sim uses <see cref="BulletElement"/>.
    /// </summary>
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
