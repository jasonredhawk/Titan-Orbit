using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Blittable copy of <see cref="ShipImpactSpinSettings"/> for Burst drive / contact jobs.
    /// </summary>
    public struct ShipImpactSpinTuning
    {
        public float MinGlancingSpeed;
        public float MinClosingSpeed;
        public float CollisionYawScale;
        public float BulletYawScale;
        public float CollisionClosingRef;
        public float DamageRef;
        public float HeadOnSpinMul;
        public float FullHealthSpinMul;
        public float ZeroHealthSpinMul;
        public float RandomSpread;
        public float MaxYawRateDegPerSec;
        public float DecayHalfLifeSeconds;
        public float WreckDecayHalfLifeSeconds;
        public float LivingRecoverRefDegPerSec;
        public float WreckMaxSeconds;
        public float LeftoverFirepowerGemRate;
    }

    /// <summary>
    /// Shared planar yaw-spin math. Magnitude is impact × angle × leftover hull × jitter —
    /// not raw lever×mass (that always landed in the same huge band).
    /// </summary>
    public static class ShipImpactSpinLogic
    {
        public const float DefaultMinGemSpawnValue = 0.25f;

        const float ReferenceYawDegPerSec = 90f;

        public static ShipImpactSpinTuning FromSettings(ShipImpactSpinSettings settings)
        {
            if (settings == null)
                settings = ShipImpactSpinSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            return new ShipImpactSpinTuning
            {
                MinGlancingSpeed = settings.MinGlancingSpeed,
                MinClosingSpeed = settings.MinClosingSpeed,
                CollisionYawScale = settings.CollisionYawScale,
                BulletYawScale = settings.BulletYawScale,
                CollisionClosingRef = settings.CollisionClosingRef,
                DamageRef = settings.DamageRef,
                HeadOnSpinMul = settings.HeadOnSpinMul,
                FullHealthSpinMul = settings.FullHealthSpinMul,
                ZeroHealthSpinMul = settings.ZeroHealthSpinMul,
                RandomSpread = settings.RandomSpread,
                MaxYawRateDegPerSec = settings.MaxYawRateDegPerSec,
                DecayHalfLifeSeconds = settings.DecayHalfLifeSeconds,
                WreckDecayHalfLifeSeconds = settings.WreckDecayHalfLifeSeconds,
                LivingRecoverRefDegPerSec = settings.LivingRecoverRefDegPerSec,
                WreckMaxSeconds = settings.WreckMaxSeconds,
                LeftoverFirepowerGemRate = settings.LeftoverFirepowerGemRate,
            };
        }

        public static bool IsWreckActive(float wreckExpiresAt, double elapsed) =>
            wreckExpiresAt > 0.01f && elapsed < wreckExpiresAt;

        public static float ComputeYawInertia(float mass, float hullRadius)
        {
            float r = math.max(0.25f, hullRadius);
            float m = math.max(ShipMassLogic.MinMass, mass);
            return math.max(0.05f, m * r * r);
        }

        public static float Cross2(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        public static bool PassesGlancingThreshold(
            float closingSpeed,
            float glancingSpeed,
            in ShipImpactSpinTuning tuning)
        {
            float close = math.max(0f, closingSpeed);
            float glance = math.max(0f, glancingSpeed);
            return close >= tuning.MinClosingSpeed || glance >= tuning.MinGlancingSpeed;
        }

        public static float GlancingSpeed(float2 relVelXZ, float2 normalXZ)
        {
            float2 n = math.normalizesafe(normalXZ);
            return math.abs(Cross2(relVelXZ, n));
        }

        /// <summary>0 = head-on, 1 = full scrape (|sin| of approach vs contact normal).</summary>
        public static float CollisionAngleFactor(float closingSpeed, float glancingSpeed)
        {
            float close = math.max(0f, closingSpeed);
            float glance = math.max(0f, glancingSpeed);
            float speed = math.sqrt(close * close + glance * glance);
            if (speed < 0.01f)
                return 0f;
            return math.saturate(glance / speed);
        }

        /// <summary>Full hull → <paramref name="fullHealthMul"/>; 0 HP → <paramref name="zeroHealthMul"/>.</summary>
        public static float HealthInstability(
            float health,
            float maxHealth,
            float fullHealthMul,
            float zeroHealthMul)
        {
            float frac = math.saturate(health / math.max(1f, maxHealth));
            return math.lerp(math.max(0f, zeroHealthMul), math.max(0f, fullHealthMul), frac);
        }

        public static float DeterministicJitter(uint seed, float spread)
        {
            var rng = new Random(math.max(1u, seed));
            float s = math.saturate(spread);
            return math.lerp(1f - s, 1f + s, rng.NextFloat());
        }

        public static uint MixSeed(uint a, uint b, uint c)
        {
            return math.hash(new uint3(a, b, c));
        }

        public static float2 FallbackLeverXZ(float2 normalXZ, float2 relVelXZ, float hullRadius)
        {
            float2 n = math.normalizesafe(normalXZ);
            float2 tangent = new float2(-n.y, n.x);
            float sign = math.sign(math.dot(relVelXZ, tangent));
            if (sign == 0f)
                sign = 1f;
            return tangent * (math.max(0.25f, hullRadius) * sign);
        }

        public static void AddYawRate(ref float yawRateDegPerSec, float deltaDegPerSec, float maxAbs)
        {
            yawRateDegPerSec = math.clamp(
                yawRateDegPerSec + deltaDegPerSec,
                -math.max(1f, maxAbs),
                math.max(1f, maxAbs));
        }

        /// <summary>
        /// Collision yaw: closing strength × glance angle × leftover hull × jitter.
        /// Sign follows lever × normal so a left scrape yaws the other way from a right scrape.
        /// </summary>
        public static float ComputeCollisionYawDelta(
            float closingSpeed,
            float glancingSpeed,
            float2 leverXZ,
            float2 normalXZ,
            float health,
            float maxHealth,
            uint seed,
            in ShipImpactSpinTuning tuning)
        {
            float angle = CollisionAngleFactor(closingSpeed, glancingSpeed);
            float angleMul = math.lerp(tuning.HeadOnSpinMul, 1f, angle);
            float impact = math.saturate(closingSpeed / math.max(0.1f, tuning.CollisionClosingRef));
            float instability = HealthInstability(
                health, maxHealth, tuning.FullHealthSpinMul, tuning.ZeroHealthSpinMul);
            float jitter = DeterministicJitter(seed, tuning.RandomSpread);
            float mag = ReferenceYawDegPerSec
                        * math.max(0f, tuning.CollisionYawScale)
                        * impact
                        * angleMul
                        * instability
                        * jitter;
            if (mag < 0.05f)
                return 0f;

            float sign = math.sign(Cross2(leverXZ, math.normalizesafe(normalXZ)));
            if (sign == 0f)
                sign = (seed & 1u) == 0u ? -1f : 1f;
            return mag * sign;
        }

        /// <summary>
        /// Combat yaw from damage / leftover firepower. 0 HP uses the wreck multiplier.
        /// </summary>
        public static float ComputeDamageYawDelta(
            float impactFirepower,
            float2 leverXZ,
            float2 impulseXZ,
            float health,
            float maxHealth,
            uint seed,
            in ShipImpactSpinTuning tuning)
        {
            float impact = math.saturate(impactFirepower / math.max(0.1f, tuning.DamageRef));
            float instability = HealthInstability(
                health, maxHealth, tuning.FullHealthSpinMul, tuning.ZeroHealthSpinMul);
            float jitter = DeterministicJitter(seed, tuning.RandomSpread);
            float mag = ReferenceYawDegPerSec
                        * math.max(0f, tuning.BulletYawScale)
                        * impact
                        * instability
                        * jitter;
            if (mag < 0.05f)
                return 0f;

            float2 dir = math.lengthsq(impulseXZ) > 1e-8f ? impulseXZ : leverXZ;
            float sign = math.sign(Cross2(leverXZ, dir));
            if (sign == 0f)
                sign = (seed & 1u) == 0u ? -1f : 1f;
            return mag * sign;
        }

        public static float2 FirepowerImpulseXZ(float2 directionXZ, float leftoverFirepower)
        {
            float2 dir = math.normalizesafe(directionXZ);
            return dir * math.max(0f, leftoverFirepower);
        }

        public static void IntegrateYaw(ref quaternion rotation, float yawRateDegPerSec, float dt)
        {
            if (dt <= 0f || math.abs(yawRateDegPerSec) < 0.01f)
                return;
            quaternion delta = quaternion.AxisAngle(math.up(), math.radians(yawRateDegPerSec * dt));
            rotation = math.normalize(math.mul(delta, rotation));
        }

        public static void DecayYawRate(ref float yawRateDegPerSec, float halfLifeSeconds, float dt)
        {
            if (dt <= 0f || math.abs(yawRateDegPerSec) < 0.01f)
            {
                if (math.abs(yawRateDegPerSec) < 0.01f)
                    yawRateDegPerSec = 0f;
                return;
            }

            float hl = math.max(0.05f, halfLifeSeconds);
            float keep = math.pow(0.5f, dt / hl);
            yawRateDegPerSec *= keep;
            if (math.abs(yawRateDegPerSec) < 0.05f)
                yawRateDegPerSec = 0f;
        }

        public static float LivingRecoverScale(float yawRateDegPerSec, float recoverRefDegPerSec)
        {
            float reference = math.max(1f, recoverRefDegPerSec);
            return 1f / (1f + math.abs(yawRateDegPerSec) / reference);
        }

        public static float ComputeGemsToExpel(
            float leftoverFirepower,
            float currentGems,
            float gemRate,
            float minGemSpawn)
        {
            if (leftoverFirepower <= 0.0001f || currentGems < minGemSpawn)
                return 0f;
            float raw = leftoverFirepower * math.max(0f, gemRate);
            if (raw < minGemSpawn)
                return 0f;
            return math.min(currentGems, raw);
        }
    }
}
