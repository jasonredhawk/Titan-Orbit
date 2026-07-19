using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Server-authoritative bullet simulation state stored in a singleton buffer on the
    /// entity tagged with <see cref="ActiveBulletsTag"/>. Each element is one live projectile advanced
    /// by <see cref="BulletSimulationSystem"/>. Not ghost-replicated — clients see tracers from
    /// <see cref="BulletSpawnRpc"/> / <see cref="BulletVfxBridge"/> (see <c>BulletVfxDriver</c>).
    /// </summary>
    public struct BulletElement : IBufferElementData
    {
        // --- Type members ---
        /// <summary>[ECS/DOTS] Logical toroidal position this tick (ECS sim space).</summary>
        public float3 Position;

        /// <summary>[ECS/DOTS] World-units-per-second velocity vector on the XZ plane.</summary>
        public float3 Velocity;

        /// <summary>[TITAN-ORBIT] Maximum travel distance before despawn (Euclidean step sum).</summary>
        public float MaxDistance;

        /// <summary>[UNITY] Seconds until automatic despawn.</summary>
        public float Lifetime;

        /// <summary>[TITAN-ORBIT] Damage applied on hit (ships, moons, asteroids).</summary>
        public float Damage;

        /// <summary>[NETCODE] Shooter's network id for kill attribution and friendly-fire rules.</summary>
        public int OwnerNetworkId;

        /// <summary>[TITAN-ORBIT] Shooter team as byte (cast to <see cref="Core.TeamId"/>).</summary>
        public byte OwnerTeam;

        /// <summary>[TITAN-ORBIT] Monotonic shot id for client VFX deduplication.</summary>
        public uint Sequence;

        /// <summary>[TITAN-ORBIT] Distance traveled since spawn (Euclidean step sum on unbounded flight).</summary>
        public float Traveled;

        /// <summary>[UNITY] Seconds since spawn.</summary>
        public float Age;

        /// <summary>[TITAN-ORBIT] <see cref="BulletVfxBank"/> category for spawn/hit VFX (from loadout).</summary>
        public int BankIndex;

        /// <summary>[TITAN-ORBIT] Per-shot visual scale carried to impact VFX.</summary>
        public float ScaleMultiplier;
    }

    /// <summary>
    /// [ECS/DOTS] One-shot spawn notification written by <see cref="BulletSimulationSystem"/> and
    /// consumed by <see cref="BulletPresentationSystem"/> to create cosmetic tracer entities.
    /// </summary>
    public struct BulletSpawnEventElement : IBufferElementData
    {
        /// <summary>[ECS/DOTS] Muzzle world position at fire time.</summary>
        public float3 SpawnPosition;

        /// <summary>[ECS/DOTS] Initial bullet velocity.</summary>
        public float3 Velocity;

        /// <summary>[UNITY] Tracer lifetime in seconds.</summary>
        public float Lifetime;

        /// <summary>[TITAN-ORBIT] Max travel distance for cosmetic tracer.</summary>
        public float MaxDistance;

        /// <summary>[TITAN-ORBIT] Display damage value (cosmetic; server sim owns real damage).</summary>
        public float Damage;

        /// <summary>[TITAN-ORBIT] Shooter team for tracer color.</summary>
        public byte OwnerTeam;

        /// <summary>[TITAN-ORBIT] Shot sequence for VFX deduplication.</summary>
        public uint Sequence;

        /// <summary>[TITAN-ORBIT] Index into weapon VFX bank for muzzle/tracer style.</summary>
        public int BankIndex;

        /// <summary>[TITAN-ORBIT] Authored scale multiplier for tracer mesh.</summary>
        public float ScaleMultiplier;
    }

    /// <summary>
    /// [ECS/DOTS] Hit notification for client VFX (impact sparks). Written when a cosmetic tracer
    /// intersects geometry in presentation systems.
    /// </summary>
    public struct BulletHitEventElement : IBufferElementData
    {
        /// <summary>[ECS/DOTS] World position of impact.</summary>
        public float3 HitPosition;

        /// <summary>[TITAN-ORBIT] Damage value for impact VFX intensity.</summary>
        public float Damage;

        /// <summary>[TITAN-ORBIT] Shooter team for impact color.</summary>
        public byte OwnerTeam;

        /// <summary>[TITAN-ORBIT] Index into weapon VFX bank for impact style.</summary>
        public int BankIndex;

        /// <summary>[TITAN-ORBIT] Authored scale multiplier for impact VFX.</summary>
        public float ScaleMultiplier;
    }

    /// <summary>
    /// [ECS/DOTS] Tag on the singleton entity that owns all bullet buffers. Required by bullet sim
    /// and presentation systems to find the active bullet collection.
    /// </summary>
    public struct ActiveBulletsTag : IComponentData { }

    /// <summary>
    /// [HYBRID] Marks a tracer entity whose positions are already in client display/world space
    /// (not logical ECS toroidal space). Used by hybrid VFX bridges.
    /// </summary>
    public struct BulletTracerDisplaySpace : IComponentData { }

    /// <summary>
    /// [ECS/DOTS] Cosmetic bullet tracer — presentation-only entity advanced by tracer update systems.
    /// Does not deal authoritative damage; server sim uses <see cref="BulletElement"/>.
    /// </summary>
    public struct BulletTracerState : IComponentData
    {
        /// <summary>[ECS/DOTS] Current tracer position.</summary>
        public float3 Position;

        /// <summary>[ECS/DOTS] Spawn position for trail rendering.</summary>
        public float3 SpawnPosition;

        /// <summary>[ECS/DOTS] Tracer velocity.</summary>
        public float3 Velocity;

        /// <summary>[UNITY] Seconds until tracer despawns.</summary>
        public float RemainingLifetime;

        /// <summary>[TITAN-ORBIT] Max distance before tracer despawn.</summary>
        public float MaxDistance;

        /// <summary>[UNITY] Base tracer mesh scale.</summary>
        public float Scale;

        /// <summary>[TITAN-ORBIT] Authored scale multiplier from weapon config.</summary>
        public float ScaleMultiplier;

        /// <summary>[TITAN-ORBIT] Display damage for VFX sizing.</summary>
        public float Damage;

        /// <summary>[TITAN-ORBIT] Shooter team for color.</summary>
        public byte OwnerTeam;

        /// <summary>[TITAN-ORBIT] VFX bank index for tracer style.</summary>
        public int BankIndex;
    }
}
