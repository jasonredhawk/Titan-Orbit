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
            if (IsHealBankIndex(index) || IsStoreReservedBankIndex(index))
                return 0;
            return index;
        }

        /// <summary>
        /// Rockets (and any category whose name contains "rocket") stay off family guns,
        /// B-key ownership, and drones — reserved for store rocket packs.
        /// </summary>
        public static bool IsStoreReservedBankIndex(int bankIndex)
        {
            var bank = BulletVfxBank.LoadDefault();
            if (bank == null || !bank.TryGetCategoryName(bankIndex, out string name) ||
                string.IsNullOrEmpty(name))
                return false;
            return name.IndexOf("rocket", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Next bank for debug cycle-all, skipping store-reserved categories (Rockets).</summary>
        public static int NextDebugCycleBankIndex(int current, int categoryCount)
        {
            if (categoryCount < 1)
                return 0;
            int start = current < 0 ? 0 : current;
            int next = (start + 1) % categoryCount;
            for (int i = 0; i < categoryCount; i++)
            {
                if (!IsStoreReservedBankIndex(next))
                    return next;
                next = (next + 1) % categoryCount;
            }

            return start;
        }

        /// <summary>
        /// Planetary defense always uses the owning family's default damage bank (never heal).
        /// </summary>
        public static int ResolveBankIndexForPlanetaryDefense(ShipFamilyDefinition family) =>
            Mathf.Max(0, ResolveBankIndexForFamily(family));

        /// <summary>Prefix stamped on drone <c>EquippedEquipmentElement.ComponentId</c> at purchase.</summary>
        public const string DroneSourceFamilyIdPrefix = "DroneFam:";

        /// <summary>Encodes the store planet's family config index onto a purchased drone.</summary>
        public static string FormatDroneSourceFamilyId(int familyConfigIndex) =>
            DroneSourceFamilyIdPrefix + Mathf.Max(0, familyConfigIndex);

        /// <summary>True when <paramref name="componentId"/> is a drone source-family stamp.</summary>
        public static bool TryParseDroneSourceFamilyId(string componentId, out int familyConfigIndex)
        {
            familyConfigIndex = 0;
            if (string.IsNullOrEmpty(componentId) ||
                !componentId.StartsWith(DroneSourceFamilyIdPrefix, System.StringComparison.Ordinal))
                return false;
            return int.TryParse(componentId.Substring(DroneSourceFamilyIdPrefix.Length), out familyConfigIndex);
        }

        /// <summary>
        /// Bank for a combat drone: stamped purchase-planet family, else the hull family default.
        /// Never returns the heal bank.
        /// </summary>
        public static int ResolveBankIndexForDrone(string componentId, ShipFamilyDefinition hullFallback = null)
        {
            if (TryParseDroneSourceFamilyId(componentId, out int familyIndex))
            {
                var config = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
                var entry = config != null ? config.GetFamilyByConfigIndex(familyIndex) : null;
                if (entry?.shipFamilyDefinition != null)
                    return ResolveBankIndexForFamily(entry.shipFamilyDefinition);
            }

            return ResolveBankIndexForFamily(hullFallback);
        }

        /// <summary>
        /// Reserved Rockets bank index for store packs, or -1 when the category is missing.
        /// Name match is case-insensitive ("Rockets", "Rocket").
        /// </summary>
        public static int FindRocketBankIndex()
        {
            var bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return -1;
            if (bank.TryGetCategoryIndexByName("Rockets", out int index))
                return index;
            int count = bank.CategoryCount;
            for (int i = 0; i < count; i++)
            {
                if (IsStoreReservedBankIndex(i))
                    return i;
            }

            return -1;
        }

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
