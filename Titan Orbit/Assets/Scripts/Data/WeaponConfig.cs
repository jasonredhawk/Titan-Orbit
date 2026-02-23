using UnityEngine;
using System;
using System.Collections.Generic;

namespace TitanOrbit.Data
{
    /// <summary>Spread behavior for a cannon.</summary>
    public enum CannonSpreadType
    {
        Straight,       // Shoots straight (directionAngle only)
        FixedSpread,    // Multiple fixed angles (e.g. left, center, right)
        RandomSpread    // Random angle within [spreadAngleMin, spreadAngleMax] each shot
    }

    /// <summary>Configuration for a single cannon (rate, energy, damage, direction, spread).</summary>
    [Serializable]
    public class CannonConfig
    {
        [Tooltip("Shots per second for this cannon.")]
        public float fireRate = 2f;
        [Tooltip("Energy consumed per shot.")]
        public float energyCostPerShot = 1f;
        [Tooltip("Damage per bullet.")]
        public float damagePerBullet = 8f;
        [Tooltip("Base direction angle in degrees from ship forward (0 = straight). Positive = right.")]
        public float directionAngle = 0f;
        public CannonSpreadType spreadType = CannonSpreadType.Straight;
        [Tooltip("For RandomSpread: min angle offset from direction (degrees). For FixedSpread: leftmost angle.")]
        public float spreadAngleMin = -5f;
        [Tooltip("For RandomSpread: max angle offset from direction (degrees). For FixedSpread: rightmost angle.")]
        public float spreadAngleMax = 5f;
        [Tooltip("For FixedSpread: number of bullets in spread (e.g. 3 = left, center, right).")]
        public int spreadProjectileCount = 3;
        [Tooltip("Bullet size multiplier (same visual, different scale).")]
        public float bulletScale = 1f;
        [Tooltip("Local X offset from ship fire point (left/right).")]
        public float localOffsetX = 0f;
        [Tooltip("Local Z offset from ship fire point (forward/back).")]
        public float localOffsetZ = 0f;
        [Tooltip("Bullet speed for this cannon.")]
        public float bulletSpeed = 20f;

        public CannonConfig Clone()
        {
            return new CannonConfig
            {
                fireRate = fireRate,
                energyCostPerShot = energyCostPerShot,
                damagePerBullet = damagePerBullet,
                directionAngle = directionAngle,
                spreadType = spreadType,
                spreadAngleMin = spreadAngleMin,
                spreadAngleMax = spreadAngleMax,
                spreadProjectileCount = spreadProjectileCount,
                bulletScale = bulletScale,
                localOffsetX = localOffsetX,
                localOffsetZ = localOffsetZ,
                bulletSpeed = bulletSpeed
            };
        }
    }

    /// <summary>Weapon configuration: multiple cannons, same bullet skin. Used by ShipData.</summary>
    [CreateAssetMenu(fileName = "WeaponConfig", menuName = "Titan Orbit/Weapon Config")]
    public class WeaponConfig : ScriptableObject
    {
        public string displayName = "Weapon";
        [Tooltip("Cannons. Each has its own rate, energy, damage, direction, spread.")]
        public List<CannonConfig> cannons = new List<CannonConfig>();

        public int CannonCount => cannons != null ? cannons.Count : 0;
    }
}
