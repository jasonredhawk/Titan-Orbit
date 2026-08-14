using TitanOrbit.Data;
using TitanOrbit;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Resolves which <see cref="BulletVfxBank"/> category a ship actually fires.
    /// Heal mode and the debug cycle-all flag override the B-key damage index.
    /// </summary>
    public static class BulletBankFireResolve
    {
        /// <summary>
        /// Bank used for this shot. Debug cycle-all uses <see cref="ShipLoadoutState.RuntimeBulletIndex"/>
        /// as-is (may be EnergySpheres). Otherwise heal mode forces the heal bank.
        /// </summary>
        public static int ResolveFireBankIndex(in ShipLoadoutState loadout)
        {
            int runtime = loadout.RuntimeBulletIndex < 0 ? 0 : loadout.RuntimeBulletIndex;
            if (TitanOrbitDebugFlags.CycleAllBulletBanks)
                return runtime;
            if (loadout.HealingBulletsActive)
            {
                int heal = BulletBankProfileUtility.FindHealBankIndex();
                return heal >= 0 ? heal : runtime;
            }

            return runtime;
        }
    }
}
