using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] 4-byte death-explosion instruction on the ship ghost. 0 = alive / no seed yet.
    /// Non-zero packs a 16-bit seed, 8-bit XZ impulse angle, and 8-bit impulse power so every
    /// client plays the same cosmetic breakup without an RPC.
    /// <para>
    /// Must be baked on StarshipGhost — runtime <c>AddComponent</c> does not register GhostFields.
    /// Written by <see cref="ShipDeathRecordingSystem"/>; cleared by <see cref="ShipRespawnSystem"/>.
    /// Packed uses <see cref="SmoothingAction.Clamp"/> so the 0 → seed → 0 → seed cycle does not
    /// lerp. Clients still explode when this is 0 if <c>ShipState.IsDead</c> is already true
    /// (ShipDeathDebrisDriver).
    /// </para>
    /// </summary>
    public struct ShipDeathVfxState : IComponentData
    {
        /// <summary>Damage that maps to a full (255) power byte. Larger hits clamp.</summary>
        public const float PowerReference = 64f;

        /// <summary>
        /// 0 = alive. Bits 0–15 seed, 16–23 angle (0–255 = 360°), 24–31 power (0–255).
        /// <para>
        /// [NETCODE] Clamp, not Interpolate. This is an event word (0 → seed → 0 → new seed),
        /// not a quantity to lerp. Interpolating a uint from 0 to a packed seed left the
        /// second death at 0 for a few ticks while <c>ShipState.IsDead</c> was already true,
        /// so the hull hid with no breakup.
        /// </para>
        /// </summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public uint Packed;

        /// <summary>
        /// Last damaging ship's <c>GhostOwner.NetworkId</c> (bullet / ram / mine owner).
        /// 0 = environment or unknown. Clamp — this is an event id, not a quantity.
        /// </summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public int SourceNetworkId;

        /// <summary>
        /// <c>GhostInstance.ghostId</c> of the last damaging body (asteroid or ship).
        /// 0 when the source was not a live ghost.
        /// </summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public int SourceGhostId;

        /// <summary><see cref="DeathVfxSourceKind"/> of that body.</summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public byte SourceKind;

        /// <summary>
        /// Logical X of the last-hit body (mine site, turret pad, fallback).
        /// Quantized — camera only, not sim.
        /// </summary>
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Clamp)]
        public float SourcePosX;

        /// <summary>Logical Z of the last-hit body. Paired with <see cref="SourcePosX"/>.</summary>
        [GhostField(Quantization = 10, Smoothing = SmoothingAction.Clamp)]
        public float SourcePosZ;

        /// <summary>1 when <see cref="SourcePosX"/> / <see cref="SourcePosZ"/> were stamped.</summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public byte SourceHasPos;

        /// <summary>True when clients should play / keep the breakup.</summary>
        public bool HasExplosion => Packed != 0;

        /// <summary>True when a last-hit body was packed for the death camera.</summary>
        public bool HasSource => SourceKind != 0 || SourceNetworkId > 0 || SourceGhostId != 0 || SourceHasPos != 0;

        /// <summary>Packs a non-zero instruction. Seed 0 is forced to 1 so Packed stays live.</summary>
        public static uint Pack(uint seed, float2 impulseXZ, float power)
        {
            seed &= 0xFFFFu;
            if (seed == 0)
                seed = 1;

            float2 dir = impulseXZ;
            float angle01 = 0f;
            if (math.lengthsq(dir) > 1e-8f)
            {
                dir = math.normalize(dir);
                float angle = math.atan2(dir.x, dir.y);
                angle01 = (angle + math.PI) / (2f * math.PI);
            }

            uint angleByte = (uint)math.clamp((int)math.round(angle01 * 255f), 0, 255);
            float p = math.max(0f, power) / math.max(0.01f, PowerReference);
            uint powerByte = (uint)math.clamp((int)math.round(p * 255f), 0, 255);
            return seed | (angleByte << 16) | (powerByte << 24);
        }

        /// <summary>Unpacks seed, unit XZ impulse, and 0–1 power.</summary>
        public static void Unpack(uint packed, out uint seed, out float2 impulseDir, out float power01)
        {
            seed = packed & 0xFFFFu;
            uint angleByte = (packed >> 16) & 0xFFu;
            uint powerByte = (packed >> 24) & 0xFFu;
            float angle = (angleByte / 255f) * 2f * math.PI - math.PI;
            impulseDir = powerByte == 0
                ? float2.zero
                : new float2(math.sin(angle), math.cos(angle));
            power01 = powerByte / 255f;
        }
    }

    /// <summary>What the last killing hit came from — packed on <see cref="ShipDeathVfxState"/>.</summary>
    public enum DeathVfxSourceKind : byte
    {
        None = 0,
        Ship = 1,
        Asteroid = 2,
        Turret = 3,
        Missile = 4,
        Mine = 5,
    }
}
