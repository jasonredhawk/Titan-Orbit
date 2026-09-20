using TitanOrbit.Data;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// MEGA unique-weapon class stamped on <see cref="ShipWeaponMountElement.WeaponKind"/>.
    /// Regular family barrels stay <see cref="Gun"/> (projectile). Cannons are hitscan lasers.
    /// </summary>
    public static class ShipWeaponKind
    {
        /// <summary>Rapid projectile (default).</summary>
        public const byte Gun = 0;

        /// <summary>Cone-locked continuous laser — no bullet spawn.</summary>
        public const byte Cannon = 1;

        /// <summary>Homing / rocket bank projectile.</summary>
        public const byte Missile = 2;

        /// <summary>High-speed projectile.</summary>
        public const byte Sniper = 3;

        /// <summary>
        /// Cannon / missile / sniper keep catalog banks when the hull B-key cycles.
        /// Only <see cref="Gun"/> (Titan Bullet) mounts adopt
        /// <c>ShipLoadoutState.RuntimeBulletIndex</c>.
        /// </summary>
        public static bool KeepsAuthoredBulletBank(byte weaponKind)
        {
            return weaponKind == Cannon || weaponKind == Missile || weaponKind == Sniper;
        }

        /// <summary>True when this barrel should fire the hull's cycled damage bank.</summary>
        public static bool UsesCycledBulletBank(in ShipWeaponMountElement mount)
        {
            return !KeepsAuthoredBulletBank(mount.WeaponKind);
        }

        /// <summary>True when this barrel burns with the cannon laser (not a bullet).</summary>
        public static bool IsCannonLaser(in ShipWeaponMountElement mount)
        {
            return mount.WeaponKind == Cannon;
        }

        /// <summary>
        /// Mount kind, or the ghosted gunner slot when predicted rollback cleared the mount.
        /// </summary>
        public static bool IsCannonLaser(
            in ShipWeaponMountElement mount,
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            int mountIndex)
        {
            if (mount.WeaponKind == Cannon)
                return true;
            return gunners.IsCreated
                   && mountIndex >= 0
                   && mountIndex < gunners.Length
                   && gunners[mountIndex].WeaponKind == Cannon;
        }

        /// <summary>
        /// Copies ghosted <see cref="MegaShipGunnerSlotElement.WeaponKind"/> onto mounts
        /// after prediction rollback (mount buffer is not a GhostField).
        /// </summary>
        public static void RestoreMountKindsFromGhostedSlots(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            DynamicBuffer<MegaShipGunnerSlotElement> gunners)
        {
            if (!mounts.IsCreated || !gunners.IsCreated)
                return;

            int n = mounts.Length < gunners.Length ? mounts.Length : gunners.Length;
            for (int i = 0; i < n; i++)
            {
                byte kind = gunners[i].WeaponKind;
                if (kind == 0)
                    continue;
                var mount = mounts[i];
                if (mount.WeaponKind == kind)
                    continue;
                mount.WeaponKind = kind;
                mounts[i] = mount;
            }
        }

        /// <summary>Maps a catalog / family part type onto a mount kind byte.</summary>
        public static byte FromPartType(string partType)
        {
            if (ShipFamilyPartTypes.IsWeaponCannonProfile(partType))
                return Cannon;
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponMissile, System.StringComparison.OrdinalIgnoreCase))
                return Missile;
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponSniper, System.StringComparison.OrdinalIgnoreCase))
                return Sniper;
            return Gun;
        }

        /// <summary>
        /// Unique-row <c>isLaser</c> wins. Otherwise part type / names
        /// (<c>Cannon</c>, <c>Turret_Dual</c>) when the catalog row is missing.
        /// </summary>
        public static byte Resolve(
            MegaShipComponentEntry row,
            string partType,
            string displayName,
            string instanceName)
        {
            if (row != null)
                return row.isLaser ? Cannon : FromPartTypeAsProjectile(row.partType ?? partType);

            byte fromType = FromPartType(partType);
            if (fromType == Cannon)
                return Cannon;
            if (NameImpliesCannon(displayName) || NameImpliesCannon(instanceName))
                return Cannon;
            return fromType;
        }

        /// <summary>Part type without inferring a laser from Weapon Cannon (the <c>isLaser</c> toggle owns that).</summary>
        static byte FromPartTypeAsProjectile(string partType)
        {
            byte kind = FromPartType(partType);
            return kind == Cannon ? Gun : kind;
        }

        static bool NameImpliesCannon(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.IndexOf("cannon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return name.IndexOf("dual", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
