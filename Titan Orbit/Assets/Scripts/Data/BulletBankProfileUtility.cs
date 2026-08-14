using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Helpers for <see cref="BulletVfxBank"/> category selection and family-default bank preview.
    /// Combat applies the live B-key index at fire/hit time in <c>BulletBankCombatLogic</c>.
    /// </summary>
    public static class BulletBankProfileUtility
    {
        /// <summary>
        /// Overlays the family's default bank stat multipliers on store/HUD preview stats.
        /// Live fire uses the current <c>RuntimeBulletIndex</c>, not this preview.
        /// </summary>
        public static ShipComponentAbilityStats ApplyProfileToComponentStats(
            ShipComponentAbilityStats stats,
            ShipFamilyComponentEntry entry,
            ShipFamilyDefinition family = null)
        {
            int bankIndex = ResolveBankIndexForFamily(family);
            var bank = BulletVfxBank.LoadDefault();
            if (bank == null || !bank.TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return stats;

            BulletBankStatModifiers m = profile.statModifiers;
            stats.firePower *= Mul(m.firePowerMultiplier);
            stats.bulletSpeed *= Mul(m.bulletSpeedMultiplier);
            stats.fireRate *= Mul(m.fireRateMultiplier);
            stats.rammingPower *= Mul(m.rammingPowerMultiplier);
            stats.bulletRange *= Mul(m.bulletRangeMultiplier);
            return stats;
        }

        /// <summary>Applies the given bank index's fire-power / rate / speed / ram / range to a power-bar breakdown.</summary>
        public static ShipFamilyPowerScoreBreakdown ApplyProfileToBreakdown(
            ShipFamilyPowerScoreBreakdown breakdown,
            int bankIndex)
        {
            var bank = BulletVfxBank.LoadDefault();
            if (bank == null || !bank.TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return breakdown;

            BulletBankStatModifiers m = profile.statModifiers;
            breakdown.firePower *= Mul(m.firePowerMultiplier);
            breakdown.bulletSpeed *= Mul(m.bulletSpeedMultiplier);
            breakdown.fireRate *= Mul(m.fireRateMultiplier);
            breakdown.rammingPower *= Mul(m.rammingPowerMultiplier);
            return breakdown;
        }

        static float Mul(float authored) => authored > 0f ? authored : 1f;

        /// <summary>
        /// Family default bank category from <see cref="ShipFamilyDefinition.bulletPrefabIndex"/>.
        /// Negative authored values clamp to 0 (Laserbolt / first bank category).
        /// </summary>
        public static int ResolveBankIndexForFamily(ShipFamilyDefinition family)
        {
            // --- Family → bank ---
            // [TITAN-ORBIT] Wired into ShipLoadoutState.RuntimeBulletIndex by ShipStatApplyLogic.
            if (family == null)
                return 0;
            return family.bulletPrefabIndex < 0 ? 0 : family.bulletPrefabIndex;
        }
    }
}
