using UnityEngine;

namespace TitanOrbit.Data
{
    public static class BulletBankProfileUtility
    {
        public static ShipComponentAbilityStats ApplyProfileToComponentStats(
            ShipComponentAbilityStats stats,
            ShipFamilyComponentEntry entry,
            ShipFamilyDefinition family = null) => stats;

        public static ShipFamilyPowerScoreBreakdown ApplyProfileToBreakdown(
            ShipFamilyPowerScoreBreakdown breakdown,
            int bankIndex) => breakdown;

        public static int ResolveBankIndexForFamily(ShipFamilyDefinition family) => -1;
    }
}
