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

        /// <summary>
        /// MEGA shot bank: Titan Bullet (Gun) mounts follow the hull cycle / heal
        /// index. Laser cannons, missiles, and snipers keep the catalog bank
        /// stamped on the mount.
        /// </summary>
        public static int ResolveMegaMountFireBank(
            in ShipWeaponMountElement mount,
            int hullRuntimeBank)
        {
            int hull = hullRuntimeBank < 0 ? 0 : hullRuntimeBank;
            if (ShipWeaponKind.KeepsAuthoredBulletBank(mount.WeaponKind))
                return mount.BulletBankIndex >= 0 ? mount.BulletBankIndex : hull;
            return hull;
        }
    }
}
