using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Helpers for <see cref="BulletVfxBank"/> category selection and (future) bank combat modifiers.
    /// <see cref="ResolveBankIndexForFamily"/> is live — used by <c>ShipStatApplyLogic</c> to set
    /// <c>ShipLoadoutState.RuntimeBulletIndex</c>. Profile ability multipliers on damage remain deferred.
    /// </summary>
    public static class BulletBankProfileUtility
    {
        /// <summary>
        /// No-op — returns <paramref name="stats"/> unchanged until bank profiles are wired into
        /// <see cref="ShipComponentStoreData.GetEffectiveStatsForDisplay"/>.
        /// </summary>
        public static ShipComponentAbilityStats ApplyProfileToComponentStats(
            ShipComponentAbilityStats stats,
            ShipFamilyComponentEntry entry,
            ShipFamilyDefinition family = null) => stats;

        /// <summary>No-op — returns <paramref name="breakdown"/> unchanged for UI power bars.</summary>
        public static ShipFamilyPowerScoreBreakdown ApplyProfileToBreakdown(
            ShipFamilyPowerScoreBreakdown breakdown,
            int bankIndex) => breakdown;

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
