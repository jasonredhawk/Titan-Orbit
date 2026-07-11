using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Legacy Starship bullet VFX sizing: authored cannon scale at baseline, then exaggerates growth from
    /// fire-power / bullet-speed upgrades. Global bank scale (0.5) is applied in
    /// <see cref="Entities.BulletVisualFactory"/>. Burst-compiled — safe for sim hot paths and
    /// client VFX bridges. Server hit detection uses logical radius; this is presentation only.
    /// </summary>
    [BurstCompile]
    public static class BulletVisualScale
    {
        /// <summary>Reference damage for "no upgrade" visual baseline (matches default cannon).</summary>
        public const float DefaultReferenceBulletDamage = 8f;

        /// <summary>Reference speed for "no upgrade" visual baseline.</summary>
        public const float DefaultReferenceBulletSpeed = 20f;

        /// <summary>How much upgrade stats inflate visual size beyond linear scaling.</summary>
        public const float UpgradeExaggeration = 1.5f;

        /// <summary>
        /// Multiplier from combat stat upgrades — damage weighted 65%, speed 35%, then exaggerated.
        /// </summary>
        [BurstCompile]
        public static float ComputeUpgradeScaleMultiplier(
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed,
            float exaggeration = UpgradeExaggeration)
        {
            // --- Normalize stats against baseline cannon ---
            float damageMul = bulletDamage / math.max(0.01f, referenceBulletDamage);
            float speedMul = bulletSpeed / math.max(0.01f, referenceBulletSpeed);

            // --- Weighted combat boost + exaggeration curve ---
            // [TITAN-ORBIT] Damage drives 65% of visual growth; speed 35%.
            float combatBoost = (damageMul - 1f) * 0.65f + (speedMul - 1f) * 0.35f;
            float upgradeProduct = 1f + math.max(0f, combatBoost);
            exaggeration = math.max(0.5f, exaggeration);
            return 1f + (upgradeProduct - 1f) * exaggeration;
        }

        /// <summary>
        /// Final per-shot visual scale = cannon authored scale × upgrade multiplier (floor 0.1).
        /// </summary>
        [BurstCompile]
        public static float ComputePerShotScale(
            float cannonBulletScale,
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed)
        {
            // --- Combine authored cannon scale with upgrade multiplier ---
            float upgradeMul = ComputeUpgradeScaleMultiplier(
                bulletDamage,
                bulletSpeed,
                referenceBulletDamage,
                referenceBulletSpeed);
            return math.max(0.1f, math.max(0.1f, cannonBulletScale) * upgradeMul);
        }
    }
}
