using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Bullet VFX sizing from fire power only. Authored cannon scale is the baseline; size then grows
    /// with this shot’s damage vs a per-barrel reference (level-1 firePower). Bullet speed does
    /// <b>not</b> affect size — the player already sees speed from how fast the tracer moves.
    /// Global bank scale (0.5) is applied in <see cref="Entities.BulletVisualFactory"/>.
    /// Burst-compiled — safe for sim hot paths and client VFX bridges. Server hit detection uses
    /// logical radius; this is presentation only.
    /// <para>
    /// [TITAN-ORBIT] Fire-power size growth is half of the damage ratio step.
    /// Example: damage 8 vs reference 3 → ratio 2.67, visual ≈ 1 + (2.67−1)×0.5 ≈ 1.83×.
    /// Tunable via <see cref="DamageVisualGrowthFactor"/>.
    /// </para>
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
        /// How much of the (damage / reference − 1) ratio becomes visual size growth.
        /// 1.0 = linear with fire power; 0.5 = half-step (8 vs 3 ≈ 1.83×, not 2.67×).
        /// </summary>
        public const float DamageVisualGrowthFactor = 0.5f;

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
        /// <returns>Scale factor ≥ 1 when damage meets or beats the reference.</returns>
        [BurstCompile]
        public static float ComputeUpgradeScaleMultiplier(
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed)
        {
            // --- Fire power only (speed does not grow the mesh) ---
            _ = bulletSpeed;
            _ = referenceBulletSpeed;

            // damageMul 2.67 means “this shot hits 2.67× harder than the reference.”
            float damageMul = bulletDamage / math.max(0.01f, referenceBulletDamage);

            // [TITAN-ORBIT] Half-step: (ratio − 1) × 0.5 so firePower 3→8 grows size by ~0.83×.
            float damageGrowth = (damageMul - 1f) * DamageVisualGrowthFactor;
            return 1f + math.max(0f, damageGrowth);
        }

        /// <summary>
        /// Final per-shot visual scale = cannon authored scale × fire-power upgrade multiplier (floor 0.1).
        /// </summary>
        [BurstCompile]
        public static float ComputePerShotScale(
            float cannonBulletScale,
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed)
        {
            // --- Combine authored cannon scale with fire-power growth only ---
            float upgradeMul = ComputeUpgradeScaleMultiplier(
                bulletDamage,
                bulletSpeed,
                referenceBulletDamage,
                referenceBulletSpeed);
            return math.max(0.1f, math.max(0.1f, cannonBulletScale) * upgradeMul);
        }
    }
}
