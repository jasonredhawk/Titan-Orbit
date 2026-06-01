using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>Target class for bullet-bank damage multiplier abilities.</summary>
    public enum BulletBankDamageTarget
    {
        Asteroid,
        ShipOrDrone,
        GemMoon
    }

    /// <summary>Special behaviors attached to a bullet bank category (Needle, Rocket, etc.).</summary>
    public enum BulletBankAbilityType
    {
        /// <summary>Disables target ship movement, rotation, and firing for <see cref="BulletBankAbility.duration"/> seconds (server).</summary>
        ElectricShockDisable = 0,
        [System.Obsolete("Renamed to ElectricShockDisable")]
        ElectricShockRotationLock = ElectricShockDisable,
        /// <summary>Damage over time: <see cref="BulletBankAbility.magnitude"/> DPS for <see cref="BulletBankAbility.duration"/> s, every <see cref="BulletBankAbility.tickInterval"/> s.</summary>
        BurnOverTime = 1,
        /// <summary>Heals friendly ships instead of damaging them; <see cref="BulletBankAbility.magnitude"/> = heal per hit.</summary>
        HealFriendly = 2,
        /// <summary>Pushes target away from impact; <see cref="BulletBankAbility.magnitude"/> = impulse strength.</summary>
        ConcussivePush = 3,
        /// <summary>Pulls target toward impact point; <see cref="BulletBankAbility.magnitude"/> = impulse strength.</summary>
        GravityPull = 4,
        /// <summary>Multiplies damage vs asteroids; magnitude 2 = +100%.</summary>
        DamageMultiplierVsAsteroid = 5,
        /// <summary>Multiplies damage vs enemy ships/drones; magnitude 0.5 = -50%.</summary>
        DamageMultiplierVsShip = 6,
        /// <summary>Multiplies damage vs gem moons.</summary>
        DamageMultiplierVsGemMoon = 7,
    }

    /// <summary>
    /// Percent-style multipliers for the four combat stats that bullet banks can tune.
    /// 1 = unchanged, 1.5 = +50%, 0.7 = -30%. Stacks multiplicatively with ship family stats at fire time.
    /// </summary>
    [Serializable]
    public struct BulletBankStatModifiers
    {
        [Tooltip("Damage per bullet (fire power). 1 = no change.")]
        public float firePowerMultiplier;
        [Tooltip("Projectile speed. 1 = no change.")]
        public float bulletSpeedMultiplier;
        [Tooltip("Shots per second. 1 = no change.")]
        public float fireRateMultiplier;
        [Tooltip("Ramming offense rating. 1 = no change.")]
        public float rammingPowerMultiplier;

        public static BulletBankStatModifiers Identity => new BulletBankStatModifiers
        {
            firePowerMultiplier = 1f,
            bulletSpeedMultiplier = 1f,
            fireRateMultiplier = 1f,
            rammingPowerMultiplier = 1f,
        };

        public static BulletBankStatModifiers Combine(BulletBankStatModifiers a, BulletBankStatModifiers b)
        {
            return new BulletBankStatModifiers
            {
                firePowerMultiplier = SafeMul(a.firePowerMultiplier, b.firePowerMultiplier),
                bulletSpeedMultiplier = SafeMul(a.bulletSpeedMultiplier, b.bulletSpeedMultiplier),
                fireRateMultiplier = SafeMul(a.fireRateMultiplier, b.fireRateMultiplier),
                rammingPowerMultiplier = SafeMul(a.rammingPowerMultiplier, b.rammingPowerMultiplier),
            };
        }

        private static float SafeMul(float x, float y)
        {
            if (x <= 0f) x = 1f;
            if (y <= 0f) y = 1f;
            return x * y;
        }

        public bool IsIdentity =>
            Mathf.Approximately(firePowerMultiplier, 1f) &&
            Mathf.Approximately(bulletSpeedMultiplier, 1f) &&
            Mathf.Approximately(fireRateMultiplier, 1f) &&
            Mathf.Approximately(rammingPowerMultiplier, 1f);
    }

    [Serializable]
    public class BulletBankAbility
    {
        public BulletBankAbilityType type = BulletBankAbilityType.BurnOverTime;
        [Tooltip("Meaning depends on type: DPS (burn), heal amount, push/pull force, or damage multiplier (2 = double).")]
        public float magnitude = 1f;
        [Tooltip("Duration in seconds (shock, burn).")]
        public float duration = 1f;
        [Tooltip("Seconds between burn ticks.")]
        public float tickInterval = 0.25f;
        [Tooltip("For DamageMultiplier* abilities: which target class this entry applies to.")]
        public BulletBankDamageTarget damageTarget = BulletBankDamageTarget.Asteroid;
    }

    /// <summary>Authoring profile for one bullet bank category. Referenced from <see cref="TitanOrbit.Systems.BulletBankCategory"/>.</summary>
    [Serializable]
    public class BulletBankProfile
    {
        public BulletBankStatModifiers statModifiers = BulletBankStatModifiers.Identity;
        [Tooltip("Unique special behaviors for this bullet type. Duplicate types stack where noted.")]
        public List<BulletBankAbility> abilities = new List<BulletBankAbility>();

        public bool HasAbility(BulletBankAbilityType type)
        {
            if (abilities == null || abilities.Count == 0) return false;
            for (int i = 0; i < abilities.Count; i++)
            {
                if (abilities[i] != null && abilities[i].type == type)
                    return true;
            }
            return false;
        }

        public bool TryGetAbility(BulletBankAbilityType type, out BulletBankAbility ability)
        {
            ability = null;
            if (abilities == null) return false;
            for (int i = 0; i < abilities.Count; i++)
            {
                if (abilities[i] != null && abilities[i].type == type)
                {
                    ability = abilities[i];
                    return true;
                }
            }
            return false;
        }

        public float GetDamageMultiplier(BulletBankDamageTarget target)
        {
            float mul = 1f;
            if (abilities == null) return mul;
            for (int i = 0; i < abilities.Count; i++)
            {
                BulletBankAbility a = abilities[i];
                if (a == null) continue;
                BulletBankAbilityType t = a.type;
                bool matches = t switch
                {
                    BulletBankAbilityType.DamageMultiplierVsAsteroid => target == BulletBankDamageTarget.Asteroid,
                    BulletBankAbilityType.DamageMultiplierVsShip => target == BulletBankDamageTarget.ShipOrDrone,
                    BulletBankAbilityType.DamageMultiplierVsGemMoon => target == BulletBankDamageTarget.GemMoon,
                    _ => false,
                };
                if (!matches) continue;
                float m = a.magnitude > 0f ? a.magnitude : 1f;
                mul *= m;
            }
            return mul;
        }
    }
}
