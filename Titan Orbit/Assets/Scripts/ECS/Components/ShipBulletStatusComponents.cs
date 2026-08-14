using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Timed electric-shock disable. Ghosted so owner prediction freezes with the server.
    /// <see cref="ExpiresAt"/> is ECS elapsed seconds; 0 = not shocked.
    /// </summary>
    public struct ShipElectricShockState : IComponentData
    {
        /// <summary>World elapsed time when move / turn / fire unlock. 0 = inactive.</summary>
        [GhostField(Quantization = 100)]
        public float ExpiresAt;

        /// <summary>BulletVfxBank category whose impact VFX loops for the stun.</summary>
        [GhostField]
        public int VfxBankIndex;

        /// <summary>Shooter team for impact tint / prefab color.</summary>
        [GhostField]
        public byte VfxTeam;

        /// <summary>True while <paramref name="elapsed"/> is still inside the stun window.</summary>
        public bool IsActive(double elapsed) => ExpiresAt > 0.01f && elapsed < ExpiresAt;
    }

    /// <summary>
    /// Burn DoT. Tick schedule stays server-only; expiry, VFX bank, and tick sequence
    /// are ghosted so clients can loop impact VFX and spawn floating damage.
    /// </summary>
    public struct ShipBurnOverTimeState : IComponentData
    {
        [GhostField(Quantization = 100)]
        public float ExpiresAt;

        /// <summary>BulletVfxBank category whose impact VFX loops while burning.</summary>
        [GhostField]
        public int VfxBankIndex;

        [GhostField]
        public byte VfxTeam;

        /// <summary>Increments on each applied burn tick so clients can spawn floating damage.</summary>
        [GhostField]
        public uint TickSequence;

        /// <summary>Hull damage applied on the latest tick (for floating-count UI).</summary>
        [GhostField(Quantization = 100)]
        public float LastTickDamage;

        public double NextTickAt;
        public float Dps;
        public float TickInterval;
        public int SourceNetworkId;
        public byte SourceTeam;

        public bool IsActive(double elapsed) => ExpiresAt > 0.01f && elapsed < ExpiresAt;
    }

    /// <summary>
    /// One burn from a single bullet hit. Server-only buffer on ships and asteroids.
    /// <see cref="HitOffset"/> is the toroidal XZ offset from the body center at impact
    /// so each tick replays at that hull location as the body moves.
    /// </summary>
    public struct BurnOverTimeElement : IBufferElementData
    {
        public const int MaxInstances = 8;

        public float3 HitOffset;
        public float ExpiresAt;
        public double NextTickAt;
        public float Dps;
        public float TickInterval;
        public int VfxBankIndex;
        public byte VfxTeam;
        public int SourceNetworkId;
        public byte SourceTeam;

        public bool IsActive(double elapsed) => ExpiresAt > 0.01f && elapsed < ExpiresAt;
    }

    /// <summary>
    /// Server-only asteroid burn DoT. Asteroids are seed-hydrated (not ghost-relevant),
    /// so clients see ticks via Sequence-0 <see cref="BulletHitRpc"/>, not GhostFields.
    /// </summary>
    public struct AsteroidBurnOverTimeState : IComponentData
    {
        public float ExpiresAt;
        public double NextTickAt;
        public float Dps;
        public float TickInterval;
        public int VfxBankIndex;
        public byte VfxTeam;
        public int SourceNetworkId;
        public byte SourceTeam;

        public bool IsActive(double elapsed) => ExpiresAt > 0.01f && elapsed < ExpiresAt;
    }

    /// <summary>
    /// Short-lived pull field spawned at bullet impact. Lives on the
    /// <see cref="ActiveBulletsTag"/> singleton buffer.
    /// </summary>
    public struct GravityWellElement : IBufferElementData
    {
        public float3 Center;
        public float Radius;
        public float PullAccel;
        public double ExpiresAt;
        /// <summary>Shooter NetworkId — that ship is never pulled.</summary>
        public int OwnerNetworkId;
        /// <summary>Shooter team — same-team ships are never pulled.</summary>
        public byte OwnerTeam;
    }
}
