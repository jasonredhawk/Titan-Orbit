using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Legacy Starship bullet VFX sizing: authored cannon scale at baseline, then exaggerates growth from
    /// fire-power / bullet-speed upgrades. Global bank scale (0.5) is applied in BulletVisualFactory.
    /// </summary>
    [BurstCompile]
    public static class BulletVisualScale
    {
        public const float DefaultReferenceBulletDamage = 8f;
        public const float DefaultReferenceBulletSpeed = 20f;
        public const float UpgradeExaggeration = 1.5f;

        [BurstCompile]
        public static float ComputeUpgradeScaleMultiplier(
            float bulletDamage,
            float bulletSpeed,
            float referenceBulletDamage = DefaultReferenceBulletDamage,
            float referenceBulletSpeed = DefaultReferenceBulletSpeed,
            float exaggeration = UpgradeExaggeration)
        {
            float damageMul = bulletDamage / math.max(0.01f, referenceBulletDamage);
            float speedMul = bulletSpeed / math.max(0.01f, referenceBulletSpeed);
            float combatBoost = (damageMul - 1f) * 0.65f + (speedMul - 1f) * 0.35f;
            float upgradeProduct = 1f + math.max(0f, combatBoost);
            exaggeration = math.max(0.5f, exaggeration);
            return 1f + (upgradeProduct - 1f) * exaggeration;
        }

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
