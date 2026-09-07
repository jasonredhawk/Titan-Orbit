using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Deterministic per-piece launch for the cosmetic ship-death breakup.
    /// Seed + part index → same velocity / spin on every client. No physics.
    /// </summary>
    public static class ShipDeathDebrisMath
    {
        /// <summary>
        /// Builds outward + kill-impulse velocity and Euler spin (deg/s) for one component.
        /// </summary>
        /// <param name="seed">16-bit seed from <c>ShipDeathVfxState</c>.</param>
        /// <param name="partIndex">Stable index of this piece (0..n-1).</param>
        /// <param name="partOffsetFromCenter">Piece world XZ minus ship center XZ (display space).</param>
        /// <param name="impulseDir">Unit XZ kill direction (bullet / asteroid). Zero = omnidirectional.</param>
        /// <param name="power01">Packed power 0–1.</param>
        /// <param name="hullRadius">Approx hull radius for blast falloff.</param>
        /// <param name="shipVelocity">Ghosted ship velocity added to every piece.</param>
        /// <param name="settings">Designer knobs.</param>
        public static void ComputeLaunch(
            uint seed,
            int partIndex,
            float3 partOffsetFromCenter,
            float2 impulseDir,
            float power01,
            float hullRadius,
            float3 shipVelocity,
            ShipDeathDebrisSettings settings,
            out float3 velocity,
            out float3 spinDegPerSec)
        {
            var rng = new Random(MixSeed(seed, partIndex));
            float3 offset = partOffsetFromCenter;
            offset.y = 0f;
            float3 radial = math.lengthsq(offset) > 1e-6f
                ? math.normalize(offset)
                : RandomUnitXz(ref rng);

            float radialMul = rng.NextFloat(
                settings.RadialSpeedRandomMin,
                settings.RadialSpeedRandomMax);
            float radialSpeed = settings.RadialSpeed * radialMul;

            float3 impulse = float3.zero;
            if (math.lengthsq(impulseDir) > 1e-6f && power01 > 0.001f)
            {
                float3 dir = new float3(impulseDir.x, 0f, impulseDir.y);
                float radius = math.max(0.25f, hullRadius);
                float3 impact = -dir * radius;
                float blastR = radius * math.max(0.25f, settings.BlastRadiusHullMul);
                float dist = math.distance(offset, impact);
                float falloff = 1f - math.saturate(dist / math.max(0.05f, blastR));
                impulse = dir * (settings.ImpulseSpeed * power01 * falloff);
            }

            velocity = radial * radialSpeed + impulse + shipVelocity;
            velocity.y = 0f;

            float spinMax = settings.MaxSpinDegreesPerSecond;
            spinDegPerSec = new float3(
                rng.NextFloat(-spinMax, spinMax),
                rng.NextFloat(-spinMax, spinMax),
                rng.NextFloat(-spinMax, spinMax));
        }

        /// <summary>Deterministic seconds after breakup before this piece ignites.</summary>
        public static float ComputeBurnDelay(
            uint seed,
            int partIndex,
            float delayMin,
            float delayMax)
        {
            var rng = new Random(MixSeed(seed, partIndex + 97));
            float min = math.max(0f, delayMin);
            float max = math.max(min, delayMax);
            return rng.NextFloat(min, max);
        }

        /// <summary>Applies linear / angular drag for one frame.</summary>
        public static void IntegrateDrag(
            ref float3 velocity,
            ref float3 spinDegPerSec,
            float dt,
            float linearDrag,
            float angularDrag)
        {
            float lin = math.max(0f, 1f - linearDrag * dt);
            float ang = math.max(0f, 1f - angularDrag * dt);
            velocity *= lin;
            spinDegPerSec *= ang;
        }

        static uint MixSeed(uint seed, int partIndex)
        {
            uint s = seed == 0 ? 1u : seed;
            s ^= (uint)(partIndex + 1) * 747796405u;
            if (s == 0)
                s = 1;
            return s;
        }

        static float3 RandomUnitXz(ref Random rng)
        {
            float angle = rng.NextFloat(0f, 2f * math.PI);
            return new float3(math.sin(angle), 0f, math.cos(angle));
        }
    }
}
