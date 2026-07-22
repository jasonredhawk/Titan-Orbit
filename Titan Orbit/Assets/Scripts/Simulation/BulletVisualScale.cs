using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Bullet VFX sizing from fire power + designer scale knobs on <c>BulletVfxBank</c>.
    /// Authored cannon scale is the baseline; size then grows with this shot’s damage vs a
    /// per-barrel level-1 reference. Bullet speed does <b>not</b> affect size.
    /// <para>
    /// Global shrink/grow: bank <c>GlobalVisualScaleMultiplier</c> × per-category global
    /// (default 1), applied in <see cref="Entities.BulletVisualFactory"/>.
    /// Upgrade growth: bank <see cref="ActiveUpgradeVisualScaleMultiplier"/> × per-category
    /// upgrade (default 1) — e.g. bank 0.5 × category 1 → fire power 3→8 grows size by ~1.83×.
    /// </para>
    /// Burst-safe static — server writes <c>ScaleMultiplier</c> on spawn; clients render it.
    /// </summary>
    [BurstCompile]
    public static class BulletVisualScale
    {
        /// <summary>Reference damage for "no upgrade" visual baseline (matches default cannon).</summary>
        public const float DefaultReferenceBulletDamage = 8f;

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
        /// Scale multiplier from fire power vs reference. Bullet speed is ignored (API kept so
        /// call sites need not change).
        /// </summary>
        /// <param name="bulletDamage">This shot’s fire power (per-mount when available).</param>
        /// <param name="bulletSpeed">Unused — retained for signature stability.</param>
        /// <param name="referenceBulletDamage">
        /// Level-1 / chassis baseline damage for this barrel (upgradeMul ≈ 1 at that baseline).
        /// </param>
        /// <param name="referenceBulletSpeed">Unused — retained for signature stability.</param>
        /// <param name="categoryUpgradeVisualScaleMultiplier">
        /// Per-category override from <c>BulletVfxBank</c> (1 = 100% of bank upgrade growth).
        /// </param>
        /// <returns>Scale factor ≥ 1 when damage meets or beats the reference.</returns>
        [BurstCompile]
        public static float ComputeUpgradeScaleMultiplier(
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed,
            float categoryUpgradeVisualScaleMultiplier = 1f)
        {
            // --- Fire power only (speed does not grow the mesh) ---
            _ = bulletSpeed;
            _ = referenceBulletSpeed;

            float damageMul = bulletDamage / math.max(0.01f, referenceBulletDamage);

            // [TITAN-ORBIT] Bank Upgrade × category Upgrade (category default 1 = unchanged).
            // Example: bank 0.5 × category 1, damage 8 vs ref 3 → 1 + (8/3 − 1)×0.5 ≈ 1.83×.
            float categoryMul = categoryUpgradeVisualScaleMultiplier > 0.001f
                ? categoryUpgradeVisualScaleMultiplier
                : 1f;

            // [BURST] Read SharedStatic — not a plain C# static float (BC1040).
            float bankUpgradeMul = s_ActiveUpgradeVisualScaleMultiplier.Data;
            float growthFactor = math.max(0f, bankUpgradeMul * categoryMul);
            float damageGrowth = (damageMul - 1f) * growthFactor;
            return 1f + math.max(0f, damageGrowth);
        }

        /// <summary>
        /// Final per-shot visual scale = cannon authored scale × fire-power upgrade multiplier (floor 0.1).
        /// Bank×category global scale is applied later in <see cref="Entities.BulletVisualFactory"/>.
        /// </summary>
        /// <param name="categoryUpgradeVisualScaleMultiplier">
        /// Per-category upgrade knob (1 = 100%). See <see cref="ComputeUpgradeScaleMultiplier"/>.
        /// </param>
        [BurstCompile]
        public static float ComputePerShotScale(
            float cannonBulletScale,
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed,
            float categoryUpgradeVisualScaleMultiplier = 1f)
        {
            float upgradeMul = ComputeUpgradeScaleMultiplier(
                bulletDamage,
                bulletSpeed,
                referenceBulletDamage,
                referenceBulletSpeed,
                categoryUpgradeVisualScaleMultiplier);
            return math.max(0.1f, math.max(0.1f, cannonBulletScale) * upgradeMul);
        }
    }
}
