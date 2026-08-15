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
        GemMoon,
        /// <summary>Physical gem pickups in the world (asteroid drops, ship expulsion).</summary>
        Gem,
        /// <summary>Applies to asteroids, enemy ships/drones, gem moons, and gem pickups.</summary>
        Everything,
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
        /// <summary>Pull field at impact: <see cref="BulletBankAbility.radius"/> = range, <see cref="BulletBankAbility.magnitude"/> = pull force, <see cref="BulletBankAbility.duration"/> = field lifetime.</summary>
        GravityPull = 4,
        /// <summary>Multiplies damage vs asteroids; magnitude 2 = +100%.</summary>
        DamageMultiplierVsAsteroid = 5,
        /// <summary>Multiplies damage vs enemy ships/drones; magnitude 0.5 = -50%.</summary>
        DamageMultiplierVsShip = 6,
        /// <summary>Multiplies damage vs gem moons.</summary>
        DamageMultiplierVsGemMoon = 7,
        /// <summary>Multiplies damage vs physical gem pickups.</summary>
        DamageMultiplierVsGem = 8,
        /// <summary>Multiplies damage vs targets selected in <see cref="BulletBankAbility.damageTarget"/> (incl. Everything).</summary>
        DamageMultiplier = 9,
        /// <summary>
        /// Client-only: bullet visual length scales from <see cref="BulletBankAbility.radius"/> at spawn
        /// to <see cref="BulletBankAbility.magnitude"/> at max travel distance.
        /// </summary>
        StretchLengthInFlight = 10,
    }

    /// <summary>
    /// Percent-style multipliers for combat stats that bullet banks can tune at fire time.
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
        [Tooltip("Max travel distance before the bullet expires. 1 = default (~30 units), 1.5 = 50% farther.")]
        public float bulletRangeMultiplier;

        public static BulletBankStatModifiers Identity => new BulletBankStatModifiers
        {
            firePowerMultiplier = 1f,
            bulletSpeedMultiplier = 1f,
            fireRateMultiplier = 1f,
            rammingPowerMultiplier = 1f,
            bulletRangeMultiplier = 1f,
        };

        public static BulletBankStatModifiers Combine(BulletBankStatModifiers a, BulletBankStatModifiers b)
        {
            // --- Combine ---
            return new BulletBankStatModifiers
            {
                firePowerMultiplier = SafeMul(a.firePowerMultiplier, b.firePowerMultiplier),
                bulletSpeedMultiplier = SafeMul(a.bulletSpeedMultiplier, b.bulletSpeedMultiplier),
                fireRateMultiplier = SafeMul(a.fireRateMultiplier, b.fireRateMultiplier),
                rammingPowerMultiplier = SafeMul(a.rammingPowerMultiplier, b.rammingPowerMultiplier),
                bulletRangeMultiplier = SafeMul(a.bulletRangeMultiplier, b.bulletRangeMultiplier),
            };
        }

        private static float SafeMul(float x, float y)
        {
            // --- SafeMul ---
            if (x <= 0f) x = 1f;
            if (y <= 0f) y = 1f;
            return x * y;
        }

        public bool IsIdentity =>
            Mathf.Approximately(firePowerMultiplier, 1f) &&
            Mathf.Approximately(bulletSpeedMultiplier, 1f) &&
            Mathf.Approximately(fireRateMultiplier, 1f) &&
            Mathf.Approximately(rammingPowerMultiplier, 1f) &&
            Mathf.Approximately(bulletRangeMultiplier, 1f);
    }

    [Serializable]
    /// <summary>
    /// One special behavior row on a <see cref="BulletBankProfile"/>. Magnitude/duration/radius meaning
    /// depends on <see cref="type"/> — see <see cref="BulletBankAbilityType"/> tooltips. Server sim
    /// reads these in <c>BulletSimulationSystem</c>; client VFX reads stretch/gravity profiles only.
    /// </summary>
    public class BulletBankAbility
    {
        public BulletBankAbilityType type = BulletBankAbilityType.BurnOverTime;
        [Tooltip("Primary value. Meaning depends on type: DPS (burn), heal, push/pull force, or damage multiplier.")]
        public float magnitude = 1f;
        [Tooltip("Added per Fire Power Extra Level: (shipLevel−1) + Fire Power purchases.")]
        public float magnitudePerExtra;
        [Tooltip("Primary duration in seconds (shock, burn DoT, gravity well).")]
        public float duration = 1f;
        [Tooltip("Duration added per Fire Power Extra Level.")]
        public float durationPerExtra;
        [Tooltip("Primary seconds between burn damage ticks.")]
        public float tickInterval = 0.25f;
        [Tooltip("Tick interval added per Fire Power Extra Level (use negative to tick faster).")]
        public float tickIntervalPerExtra;
        [Tooltip("Primary radius / extra-range / stretch-start, depending on type.")]
        public float radius = 0f;
        [Tooltip("Radius added per Fire Power Extra Level.")]
        public float radiusPerExtra;
        [Tooltip("Extra energy spent per shot for this ability (added on top of fire power).")]
        public float energyDrain;
        [Tooltip("Energy drain added per Fire Power Extra Level.")]
        public float energyDrainPerExtra;
        [Tooltip("For DamageMultiplier* abilities: which target class this entry applies to.")]
        public BulletBankDamageTarget damageTarget = BulletBankDamageTarget.Asteroid;

        /// <summary>
        /// Weapon Extra Level steps: <c>(shipLevel−1) + Fire Power purchases</c>.
        /// </summary>
        public static int FirePowerExtraLevels(int shipLevel, int firePowerAbilityLevel) =>
            ShipComponentExtraLevelMath.CountWeaponExtraLevels(shipLevel, firePowerAbilityLevel);

        public float ScaledMagnitude(int extras) => ScaleField(magnitude, magnitudePerExtra, extras);
        public float ScaledDuration(int extras) => ScaleField(duration, durationPerExtra, extras);
        public float ScaledTickInterval(int extras) => ScaleField(tickInterval, tickIntervalPerExtra, extras);
        public float ScaledRadius(int extras) => ScaleField(radius, radiusPerExtra, extras);
        public float ScaledEnergyDrain(int extras) => Mathf.Max(0f, ScaleField(energyDrain, energyDrainPerExtra, extras));

        /// <summary>
        /// Copy with fields already scaled. Never mutate the authored ScriptableObject row.
        /// </summary>
        public BulletBankAbility Resolved(int firePowerExtraLevels)
        {
            int extras = Mathf.Max(0, firePowerExtraLevels);
            return new BulletBankAbility
            {
                type = type,
                magnitude = ScaledMagnitude(extras),
                duration = ScaledDuration(extras),
                tickInterval = ScaledTickInterval(extras),
                radius = ScaledRadius(extras),
                energyDrain = ScaledEnergyDrain(extras),
                damageTarget = damageTarget,
            };
        }

        static float ScaleField(float primary, float perExtra, int extras) =>
            primary + perExtra * Mathf.Max(0, extras);
    }

    /// <summary>
    /// Authoring profile for one bullet bank category (Needle, Rocket, Laserbolt, …). Referenced from
    /// <see cref="TitanOrbit.Systems.BulletBankCategory"/> index and mirrored on
    /// <see cref="BulletVfxBank.Category.profile"/> for client tracers. Stat modifiers stack
    /// multiplicatively at fire time with ship family stats.
    /// </summary>
    [Serializable]
    public class BulletBankProfile
    {
        public BulletBankStatModifiers statModifiers = BulletBankStatModifiers.Identity;
        [Tooltip("Unique special behaviors for this bullet type. Duplicate types stack where noted.")]
        public List<BulletBankAbility> abilities = new List<BulletBankAbility>();

        /// <summary>Returns true when any ability row matches <paramref name="type"/>.</summary>
        public bool HasAbility(BulletBankAbilityType type)
        {
            // --- HasAbility ---
            if (abilities == null || abilities.Count == 0) return false;
            for (int i = 0; i < abilities.Count; i++)
            {
                if (abilities[i] != null && abilities[i].type == type)
                    return true;
            }
            return false;
        }

        /// <summary>First matching ability row, or false when none.</summary>
        public bool TryGetAbility(BulletBankAbilityType type, out BulletBankAbility ability)
        {
            // --- Attempt resolution ---
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

        /// <summary>Authored row scaled by Fire Power Extra Levels (copy — does not mutate the asset).</summary>
        public bool TryGetResolvedAbility(
            BulletBankAbilityType type,
            int firePowerExtraLevels,
            out BulletBankAbility ability)
        {
            if (!TryGetAbility(type, out BulletBankAbility authored) || authored == null)
            {
                ability = null;
                return false;
            }

            ability = authored.Resolved(firePowerExtraLevels);
            return true;
        }

        /// <summary>Multiplies all damage-multiplier abilities that match <paramref name="target"/>.</summary>
        public float GetDamageMultiplier(BulletBankDamageTarget target, int firePowerExtraLevels = 0)
        {
            // --- Compute value ---
            float mul = 1f;
            if (abilities == null) return mul;
            for (int i = 0; i < abilities.Count; i++)
            {
                BulletBankAbility a = abilities[i];
                if (a == null || !BulletBankAbilityTargeting.IsDamageMultiplierType(a.type)) continue;
                if (!BulletBankAbilityTargeting.MatchesDamageTarget(a, target)) continue;
                float m = a.ScaledMagnitude(firePowerExtraLevels);
                if (m <= 0f) m = 1f;
                mul *= m;
            }
            return mul;
        }

        /// <summary>Longest burn DoT duration on this profile (0 if none).</summary>
        public float GetBurnDuration(int firePowerExtraLevels = 0)
        {
            // --- Compute value ---
            float best = 0f;
            if (abilities == null) return 0f;
            for (int i = 0; i < abilities.Count; i++)
            {
                BulletBankAbility a = abilities[i];
                if (a == null || a.type != BulletBankAbilityType.BurnOverTime) continue;
                float d = a.ScaledDuration(firePowerExtraLevels);
                best = Mathf.Max(best, d > 0f ? d : 2f);
            }
            return best;
        }

        /// <summary>Max bullet travel range multiplier from burn abilities (1 = unchanged).</summary>
        public float GetBurnBulletRangeMultiplier(int firePowerExtraLevels = 0)
        {
            // --- Compute value ---
            float best = 1f;
            if (abilities == null) return 1f;
            for (int i = 0; i < abilities.Count; i++)
            {
                BulletBankAbility a = abilities[i];
                if (a == null || a.type != BulletBankAbilityType.BurnOverTime) continue;
                float m = a.ScaledRadius(firePowerExtraLevels);
                if (m <= 0f) m = 1.35f;
                best = Mathf.Max(best, m);
            }
            return best;
        }

        /// <summary>Sum of every ability row's scaled energy drain (0 when none).</summary>
        public float GetTotalAbilityEnergyDrain(int firePowerExtraLevels = 0)
        {
            float sum = 0f;
            if (abilities == null) return 0f;
            for (int i = 0; i < abilities.Count; i++)
            {
                BulletBankAbility a = abilities[i];
                if (a == null) continue;
                sum += a.ScaledEnergyDrain(firePowerExtraLevels);
            }
            return sum;
        }

        public bool HasBurn => HasAbility(BulletBankAbilityType.BurnOverTime);

        public bool HasStretchLengthInFlight => HasAbility(BulletBankAbilityType.StretchLengthInFlight);

        /// <summary>Start/end length multipliers for <see cref="BulletBankAbilityType.StretchLengthInFlight"/> (defaults 0.5 → 2).</summary>
        public bool TryGetStretchLengthFactors(out float startFactor, out float endFactor, int firePowerExtraLevels = 0)
        {
            // --- Attempt resolution ---
            startFactor = 0.5f;
            endFactor = 2f;
            if (!TryGetResolvedAbility(
                    BulletBankAbilityType.StretchLengthInFlight, firePowerExtraLevels, out BulletBankAbility ability) ||
                ability == null)
                return false;

            startFactor = ability.radius > 0f ? ability.radius : 0.5f;
            endFactor = ability.magnitude > 0f ? ability.magnitude : 2f;
            return true;
        }
    }
}
