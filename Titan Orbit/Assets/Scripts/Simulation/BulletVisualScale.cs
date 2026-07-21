using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Bullet VFX sizing from fire power + designer scale knobs on <c>BulletVfxBank</c>.
    /// Authored cannon scale is the baseline; size then grows with this shot’s damage vs a
    /// per-barrel level-1 reference. Bullet speed does <b>not</b> affect size.
    /// <para>
    /// Global shrink/grow: <c>BulletVfxBank.GlobalVisualScaleMultiplier</c> (applied in
    /// <see cref="Entities.BulletVisualFactory"/>).
    /// Upgrade growth: <see cref="ActiveUpgradeVisualScaleMultiplier"/> (pushed from the bank on
    /// load) — e.g. 0.5 → fire power 3→8 grows size by ~1.83×, not 2.67×.
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
        /// Default upgrade growth when the VFX bank has not refreshed the cache yet (0.5 = half-step).
        /// </summary>
        public const float DefaultUpgradeVisualScaleMultiplier = 0.5f;

        /// <summary>
        /// [LEGACY] Same meaning as <see cref="DefaultUpgradeVisualScaleMultiplier"/>.
        /// Prefer the bank field / <see cref="ActiveUpgradeVisualScaleMultiplier"/>.
        /// </summary>
        public const float DamageVisualGrowthFactor = DefaultUpgradeVisualScaleMultiplier;

        /// <summary>
        /// Live upgrade growth factor from <c>BulletVfxBank.UpgradeVisualScaleMultiplier</c>.
        /// Written by <c>BulletVfxBank.ApplyScaleCache</c> / <c>LoadDefault</c>. Burst-readable.
        /// </summary>
        public static float ActiveUpgradeVisualScaleMultiplier = DefaultUpgradeVisualScaleMultiplier;

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

            float damageMul = bulletDamage / math.max(0.01f, referenceBulletDamage);

            // [TITAN-ORBIT] Upgrade Visual Scale Multiplier from BulletVfxBank (cached).
            // Example at 0.5: damage 8 vs reference 3 → 1 + (8/3 − 1)×0.5 ≈ 1.83×.
            float growthFactor = math.clamp(ActiveUpgradeVisualScaleMultiplier, 0f, 1f);
            float damageGrowth = (damageMul - 1f) * growthFactor;
            return 1f + math.max(0f, damageGrowth);
        }

        /// <summary>
        /// Final per-shot visual scale = cannon authored scale × fire-power upgrade multiplier (floor 0.1).
        /// Global bank scale is applied later in <see cref="Entities.BulletVisualFactory"/>.
        /// </summary>
        [BurstCompile]
        public static float ComputePerShotScale(
            float cannonBulletScale,
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed)
        {
            float upgradeMul = ComputeUpgradeScaleMultiplier(
                bulletDamage,
                bulletSpeed,
                referenceBulletDamage,
                referenceBulletSpeed);
            return math.max(0.1f, math.max(0.1f, cannonBulletScale) * upgradeMul);
        }
    }
}
