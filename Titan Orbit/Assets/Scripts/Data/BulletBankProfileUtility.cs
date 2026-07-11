using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// [LEGACY] Stub for applying active <see cref="BulletBankProfile"/> modifiers to ship component
    /// stats and power-score breakdowns. Full bullet-bank switching was deferred — every method returns
    /// inputs unchanged. Reserved for when runtime bullet index drives per-bank power bars in the
    /// upgrade tree and moon-dock UI. Paired with <see cref="BulletVfxBank"/> category profiles.
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
        /// Always -1 until family→bank index mapping is implemented on <see cref="ShipFamilyDefinition"/>.
        /// Callers should treat -1 as "use default bank 0".
        /// </summary>
        public static int ResolveBankIndexForFamily(ShipFamilyDefinition family) => -1;
    }
}
