using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Tracer size is fire power ÷ reference fire power. A shot at 7.5 against a
    /// base of 3 is 2.5. The bank global scale is applied when the tracer is drawn.
    /// Bullet type, bullet speed, and the old upgrade blend do not change it.
    /// </summary>
    [BurstCompile]
    public static class BulletVisualScale
    {
        /// <summary>Reference damage for "no upgrade" visual baseline (matches default cannon).</summary>
        public const float DefaultReferenceBulletDamage = 8f;

        /// <summary>
        /// Ship-bullet tracer reference. Every gun and bullet type uses this, not the
        /// barrel's own catalog base. Drawn size is fire power ÷ this, then the bank global.
        /// Missiles, lasers, turrets, and ramming do not use it.
        /// </summary>
        public const float FixedBulletReferenceDamage = 3f;

        /// <summary>
        /// Kept for call-site compatibility only — speed no longer drives visual size.
        /// </summary>
        public const float DefaultReferenceBulletSpeed = 20f;

        /// <summary>
        /// Default bank upgrade growth when the VFX bank has not refreshed the cache yet (0.5 = half-step).
        /// </summary>
        public const float DefaultUpgradeVisualScaleMultiplier = 0.5f;

        /// <summary>
        /// [LEGACY] Same meaning as <see cref="DefaultUpgradeVisualScaleMultiplier"/>.
        /// Prefer the bank field / <see cref="ActiveUpgradeVisualScaleMultiplier"/>.
        /// </summary>
        public const float DamageVisualGrowthFactor = DefaultUpgradeVisualScaleMultiplier;

        // --- Burst-safe bank upgrade cache ---
        // [BURST] Ordinary mutable C# statics (e.g. `public static float X = …`) trigger BC1040
        // when read from [BurstCompile] methods — Linux / IL2CPP player builds fail AOT compile.
        // SharedStatic is the Burst-supported way to share a float written from managed bank load
        // (BulletSimulationSystem, BulletVfxDriver, EcsWorldVisualizer) and read here.
        private struct ActiveUpgradeVisualScaleKey { }

        private static readonly SharedStatic<float> s_ActiveUpgradeVisualScaleMultiplier =
            SharedStatic<float>.GetOrCreate<ActiveUpgradeVisualScaleKey>();

        /// <summary>
        /// True after static ctor or a managed setter has written the SharedStatic default/bank value.
        /// Managed-only — Burst paths read SharedStatic.Data directly.
        /// </summary>
        private static bool s_ActiveUpgradeInitialized;

        /// <summary>
        /// Live <b>bank-wide</b> upgrade growth from <c>BulletVfxBank.UpgradeVisualScaleMultiplier</c>.
        /// Written on bank load. Per-category upgrade is passed into
        /// <see cref="ComputeUpgradeScaleMultiplier"/> each shot (default 1 = 100%).
        /// </summary>
        public static float ActiveUpgradeVisualScaleMultiplier
        {
            get
            {
                // [STANDARD] Lazy default so Editor / first call before bank load still gets 0.5.
                EnsureActiveUpgradeInitialized();
                return s_ActiveUpgradeVisualScaleMultiplier.Data;
            }
            set
            {
                s_ActiveUpgradeVisualScaleMultiplier.Data = value;
                s_ActiveUpgradeInitialized = true;
            }
        }

        /// <summary>
        /// Type initializer — seeds SharedStatic to the designer default before any shot math runs.
        /// </summary>
        static BulletVisualScale()
        {
            s_ActiveUpgradeVisualScaleMultiplier.Data = DefaultUpgradeVisualScaleMultiplier;
            s_ActiveUpgradeInitialized = true;
        }

        /// <summary>
        /// Ensures the Burst SharedStatic holds the default when managed code reads the property
        /// before the static constructor has been observed (defensive; static ctor normally runs first).
        /// </summary>
        private static void EnsureActiveUpgradeInitialized()
        {
            if (s_ActiveUpgradeInitialized)
                return;
            s_ActiveUpgradeVisualScaleMultiplier.Data = DefaultUpgradeVisualScaleMultiplier;
            s_ActiveUpgradeInitialized = true;
        }

        /// <summary>
        /// Fire power ÷ reference fire power. 7.5 against a base of 3 is 2.5.
        /// Speed, the bank upgrade knob, and category upgrade are not inputs.
        /// Bank global scale is applied later in <see cref="Entities.BulletVisualFactory"/>.
        /// </summary>
        /// <param name="bulletDamage">This shot’s fire power.</param>
        /// <param name="bulletSpeed">Unused — retained for signature stability.</param>
        /// <param name="referenceBulletDamage">Unleveled catalog fire power for this shot.</param>
        /// <param name="referenceBulletSpeed">Unused — retained for signature stability.</param>
        /// <param name="categoryUpgradeVisualScaleMultiplier">Unused — retained for signature stability.</param>
        [BurstCompile]
        public static float ComputeUpgradeScaleMultiplier(
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed,
            float categoryUpgradeVisualScaleMultiplier = 1f)
        {
            _ = bulletSpeed;
            _ = referenceBulletSpeed;
            _ = categoryUpgradeVisualScaleMultiplier;
            return bulletDamage / referenceBulletDamage;
        }

        /// <summary>
        /// Per-shot tracer scale before the bank global: fire power ÷ reference fire power.
        /// </summary>
        /// <param name="categoryUpgradeVisualScaleMultiplier">Unused. See <see cref="ComputeUpgradeScaleMultiplier"/>.</param>
        [BurstCompile]
        public static float ComputePerShotScale(
            float cannonBulletScale,
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed,
            float categoryUpgradeVisualScaleMultiplier = 1f)
        {
            _ = cannonBulletScale;
            return ComputeUpgradeScaleMultiplier(
                bulletDamage,
                bulletSpeed,
                referenceBulletDamage,
                referenceBulletSpeed,
                categoryUpgradeVisualScaleMultiplier);
        }
    }
}
