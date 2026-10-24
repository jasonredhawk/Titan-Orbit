using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared fire-time numbers for one ship / MEGA shot. Server
    /// <c>BulletSimulationSystem</c> and client anticipation / barrel reproject
    /// use this so range, speed, lifetime, and visual scale cannot drift.
    /// </summary>
    public struct BulletShotPlan
    {
        /// <summary>Muzzle origin in logical / unbounded XZ (Y kept from the barrel).</summary>
        public float3 Origin;

        /// <summary>Planar velocity: <c>aim * speed + shipVel</c>.</summary>
        public float3 Velocity;

        /// <summary>Euclidean travel budget (mount range when set, else hull).</summary>
        public float MaxDistance;

        /// <summary>Age budget in seconds (rebuilt from range/speed when modifiers apply).</summary>
        public float Lifetime;

        /// <summary>Per-shot damage after bank modifiers.</summary>
        public float Damage;

        /// <summary>Per-shot visual scale written onto the tracer / spawn RPC.</summary>
        public float VisualScale;

        /// <summary>Cooldown scale: modified fireRate / hull fireRate.</summary>
        public float FireRateMul;
    }

    /// <summary>
    /// Builds a <see cref="BulletShotPlan"/> from hull + mount combat numbers.
    /// Burst-safe primitives only — no EntityManager.
    /// </summary>
    public static class BulletShotMath
    {
        /// <summary>
        /// Per-mount travel range when authored, otherwise the hull max distance.
        /// </summary>
        public static float ResolveMaxDistance(float mountBulletRange, float hullMaxDistance)
        {
            return mountBulletRange > 0.5f ? mountBulletRange : hullMaxDistance;
        }

        /// <summary>
        /// Per-mount muzzle speed when authored (MEGA unique component), otherwise hull speed.
        /// </summary>
        public static float ResolveMuzzleSpeed(float mountBulletSpeed, float hullBulletSpeed)
        {
            return mountBulletSpeed > 0.01f ? mountBulletSpeed : hullBulletSpeed;
        }

        /// <summary>
        /// One ship/MEGA shot: bank modifiers, visual scale, Starblast velocity.
        /// Guns scale as this shot's fire power ÷ the Bullet VFX Bank
        /// reference (<see cref="BulletVisualScale.ResolveFixedBulletReferenceDamage"/>, default 4).
        /// <paramref name="sniperVisual"/> divides fire power by 50.
        /// <paramref name="missileVisual"/> lerps the live store-rocket size curve from
        /// this shot's fire power, then multiplies by <paramref name="authoredBulletScale"/>
        /// (MEGA Weapon Missile bank scale, 1 = the curve unchanged).
        /// </summary>
        public static BulletShotPlan Build(
            float3 fireOrigin,
            float3 fireForward,
            float3 shipVel,
            float damage,
            float hullBulletSpeed,
            float hullMaxDistance,
            float hullLifetime,
            float hullFireRate,
            float mountBulletRange,
            float authoredBulletScale,
            float referenceDamage,
            float referenceSpeed,
            int bankIndex,
            int firePowerExtras,
            float categoryUpgradeScale,
            bool sniperVisual = false,
            bool missileVisual = false)
        {
            // Fire power before the bullet-type multiplier. Type changes hit damage, not size.
            float bulletDamage = damage;
            float bulletSpeed = hullBulletSpeed;
            float maxDistance = ResolveMaxDistance(mountBulletRange, hullMaxDistance);
            float lifetime = hullLifetime;
            float fireRate = hullFireRate;
            BulletBankCombatLogic.ApplyFireModifiers(
                bankIndex, ref damage, ref bulletSpeed, ref maxDistance, ref lifetime, ref fireRate,
                firePowerExtras);
            float fireRateMul = fireRate / math.max(0.1f, hullFireRate);

            // Guns: fire power ÷ the bullet bank asset reference
            // (Bullet Reference Fire Power, live value 4).
            // A sniper divides by 50. A missile lerps the store-rocket size curve
            // from this shot's fire power (50 → 1.1). The gun divisor is not used.
            // Lasers never reach this builder.
            _ = categoryUpgradeScale;
            _ = referenceDamage;
            _ = referenceSpeed;
            float visualScale = missileVisual
                ? BulletVisualScale.ComputeMissileShotScale(
                    RocketShotMath.InterpolateVisualScale(bulletDamage),
                    authoredBulletScale)
                : sniperVisual
                    ? BulletVisualScale.ComputeSniperShotScale(bulletDamage)
                    : bulletDamage / math.max(0.01f, BulletVisualScale.ResolveFixedBulletReferenceDamage());

            fireForward.y = 0f;
            if (math.lengthsq(fireForward) < 0.0001f)
                fireForward = new float3(0f, 0f, 1f);
            else
                fireForward = math.normalize(fireForward);

            float3 vel = shipVel;
            vel.y = 0f;
            vel = fireForward * math.max(1f, bulletSpeed) + vel;

            return new BulletShotPlan
            {
                Origin = fireOrigin,
                Velocity = vel,
                MaxDistance = math.max(10f, maxDistance),
                Lifetime = math.max(0.1f, lifetime),
                Damage = damage,
                VisualScale = visualScale,
                FireRateMul = fireRateMul,
            };
        }
    }
}
