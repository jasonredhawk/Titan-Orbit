using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] 4-byte death-explosion instruction on the ship ghost. 0 = alive / no show.
    /// Non-zero packs a 16-bit seed, 8-bit XZ impulse angle, and 8-bit impulse power so every
    /// client plays the same cosmetic breakup without an RPC.
    /// <para>
    /// Must be baked on StarshipGhost — runtime <c>AddComponent</c> does not register GhostFields.
    /// Written by <see cref="ShipDeathRecordingSystem"/>; cleared by <see cref="ShipRespawnSystem"/>.
    /// </para>
    /// </summary>
    public struct ShipDeathVfxState : IComponentData
    {
        /// <summary>Damage that maps to a full (255) power byte. Larger hits clamp.</summary>
        public const float PowerReference = 64f;

        /// <summary>
        /// 0 = alive. Bits 0–15 seed, 16–23 angle (0–255 = 360°), 24–31 power (0–255).
        /// </summary>
        [GhostField] public uint Packed;

        /// <summary>True when clients should play / keep the breakup.</summary>
        public bool HasExplosion => Packed != 0;

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
}
