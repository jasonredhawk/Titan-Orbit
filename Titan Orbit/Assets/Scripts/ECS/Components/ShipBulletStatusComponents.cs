using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Timed electric-shock disable. Ghosted so owner prediction freezes with the server.
    /// <see cref="ExpiresAt"/> is server sim elapsed seconds; 0 = not shocked.
    /// The server writes 0 again when the window ends so a late-join client does not
    /// keep the impact loop against its own world clock.
    /// </summary>
    public struct ShipElectricShockState : IComponentData
    {
        /// <summary>Server sim elapsed when move / turn / fire unlock. 0 = inactive.</summary>
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
    /// Burn DoT. Tick schedule stays server-only; expiry, VFX bank, tick sequence, and the
    /// newest collider contact are ghosted so clients can loop impact VFX on the hit and
    /// spawn floating damage.
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

        /// <summary>
        /// Newest burn's collider contact in ship-local XZ, in world units along the hull axes.
        /// Clients divide by the proxy scale when placing the impact loop.
        /// </summary>
        [GhostField(Quantization = 100, Smoothing = SmoothingAction.Clamp)]
        public float HitLocalX;

        /// <summary>Z component of <see cref="HitLocalX"/>.</summary>
        [GhostField(Quantization = 100, Smoothing = SmoothingAction.Clamp)]
        public float HitLocalZ;

        public double NextTickAt;
        public float Dps;
        public float TickInterval;
        public int SourceNetworkId;
        public byte SourceTeam;

        public bool IsActive(double elapsed) => ExpiresAt > 0.01f && elapsed < ExpiresAt;
    }

    /// <summary>
    /// One burn from a single bullet hit. Server-only buffer on ships and asteroids.
    /// <see cref="HitOffset"/> is the impact in the body's local XZ (world units along local
    /// axes). Ticks rotate it with the body so the burn stays on the collider contact.
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
