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
            int index = family.bulletPrefabIndex < 0 ? 0 : family.bulletPrefabIndex;
            return IsHealBankIndex(index) ? 0 : index;
        }

        /// <summary>
        /// Planetary defense always uses the owning family's default damage bank (never heal).
        /// </summary>
        public static int ResolveBankIndexForPlanetaryDefense(ShipFamilyDefinition family) =>
            Mathf.Max(0, ResolveBankIndexForFamily(family));

        /// <summary>EnergySpheres / HealFriendly bank index, or -1 when missing.</summary>
        public static int FindHealBankIndex()
        {
            var bank = BulletVfxBank.LoadDefault();
            if (bank != null && bank.TryGetCategoryIndexByName("EnergySpheres", out int index))
                return index;
            return -1;
        }

        public static bool IsHealBankIndex(int bankIndex)
        {
            int heal = FindHealBankIndex();
            if (heal >= 0 && bankIndex == heal)
                return true;
            var bank = BulletVfxBank.LoadDefault();
            return bank != null &&
                   bank.TryGetProfile(bankIndex, out BulletBankProfile profile) &&
                   profile != null &&
                   profile.HasAbility(BulletBankAbilityType.HealFriendly);
        }

        /// <summary>
        /// Bank for a purchased component: authored override, else the part's source family default.
        /// </summary>
        public static int ResolveBankIndexForComponent(string componentId, PlanetShipFamilyConfig config = null)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return 0;
            if (config == null)
                config = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            if (config?.families == null)
                return 0;

            for (int i = 0; i < config.families.Count; i++)
            {
                var family = config.families[i]?.shipFamilyDefinition;
                if (family == null || !family.TryGetComponentEntry(componentId, out ShipFamilyComponentEntry entry))
                    continue;
                if (entry.bulletPrefabIndex >= 0)
                    return IsHealBankIndex(entry.bulletPrefabIndex) ? ResolveBankIndexForFamily(family) : entry.bulletPrefabIndex;
                return ResolveBankIndexForFamily(family);
            }

            return 0;
        }

        /// <summary>Looks up a component id on any family in the planet config.</summary>
        public static bool TryFindComponentInAnyFamily(
            string componentId,
            out ShipFamilyComponentEntry entry,
            PlanetShipFamilyConfig config = null)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(componentId))
                return false;
            if (config == null)
                config = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            if (config?.families == null)
                return false;

            for (int i = 0; i < config.families.Count; i++)
            {
                var family = config.families[i]?.shipFamilyDefinition;
                if (family != null && family.TryGetComponentEntry(componentId, out entry) && entry != null)
                    return true;
            }

            return false;
        }
    }
}
