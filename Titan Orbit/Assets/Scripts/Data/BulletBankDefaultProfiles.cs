using System;
using System.Collections.Generic;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Name-token defaults for <see cref="BulletVfxBank"/> category profiles.
    /// Editor populate fills empty ability lists / unset stat mods; designers can override after.
    /// </summary>
    public static class BulletBankDefaultProfiles
    {
        /// <summary>True when every multiplier is 0 (unauthored) or identity.</summary>
        public static bool ShouldFillStatModifiers(in BulletBankStatModifiers s)
        {
            bool allZero =
                s.firePowerMultiplier <= 0f &&
                s.bulletSpeedMultiplier <= 0f &&
                s.fireRateMultiplier <= 0f &&
                s.rammingPowerMultiplier <= 0f &&
                s.bulletRangeMultiplier <= 0f;
            return allZero || s.IsIdentity;
        }

        /// <summary>True when the ability list is missing or empty.</summary>
        public static bool ShouldFillAbilities(List<BulletBankAbility> abilities) =>
            abilities == null || abilities.Count == 0;

        /// <summary>
        /// Builds first-pass stats + abilities from a category / folder name.
        /// More specific tokens (FireballsV2, Laserbolt) win over generic ones (Fireballs, Laser).
        /// </summary>
        public static void BuildDefaults(
            string categoryName,
            out BulletBankStatModifiers stats,
            out List<BulletBankAbility> abilities)
        {
            stats = BulletBankStatModifiers.Identity;
            abilities = new List<BulletBankAbility>();
            if (string.IsNullOrWhiteSpace(categoryName))
                return;

            string n = categoryName.Trim();

            if (Contains(n, "lightning"))
            {
                stats.firePowerMultiplier = 0.9f;
                stats.fireRateMultiplier = 0.85f;
                abilities.Add(Shock(1.5f));
                return;
            }

            if (Contains(n, "fireballsv2") || Contains(n, "fireballs"))
            {
                stats.bulletSpeedMultiplier = 0.8f;
                stats.firePowerMultiplier = 1.15f;
                stats.bulletRangeMultiplier = 1.2f;
                abilities.Add(Burn(8f, 2.5f, 0.25f, 1.2f));
                return;
            }

            if (Contains(n, "liquid"))
            {
                stats.bulletSpeedMultiplier = 0.85f;
                abilities.Add(Burn(4f, 2f, 0.3f, 1.1f));
                return;
            }

            if (Contains(n, "shockwave"))
            {
                stats.firePowerMultiplier = 1.1f;
                stats.bulletSpeedMultiplier = 0.9f;
                stats.rammingPowerMultiplier = 1.15f;
                abilities.Add(Push(16f));
                return;
            }

            if (Contains(n, "rift"))
            {
                abilities.Add(Gravity(12f, 18f, 2f));
                return;
            }

            if (Contains(n, "ring2") || Contains(n, "ring"))
            {
                abilities.Add(Gravity(8f, 12f, 1.5f));
                return;
            }

            if (Contains(n, "rocket"))
            {
                stats.firePowerMultiplier = 1.4f;
                stats.bulletSpeedMultiplier = 0.7f;
                stats.fireRateMultiplier = 0.6f;
                stats.bulletRangeMultiplier = 1.3f;
                stats.rammingPowerMultiplier = 1.15f;
                abilities.Add(Push(18f));
                abilities.Add(MulVs(BulletBankAbilityType.DamageMultiplierVsAsteroid, 1.35f));
                return;
            }

            if (Contains(n, "energysphere") || Contains(n, "energy"))
            {
                stats.firePowerMultiplier = 0.75f;
                abilities.Add(Heal(12f));
                return;
            }

            if (Contains(n, "sharp"))
            {
                stats.firePowerMultiplier = 1.2f;
                stats.bulletSpeedMultiplier = 1.15f;
                abilities.Add(MulVs(BulletBankAbilityType.DamageMultiplierVsShip, 1.25f));
                return;
            }

            if (Contains(n, "sparkler"))
            {
                stats.fireRateMultiplier = 1.25f;
                stats.firePowerMultiplier = 0.85f;
                abilities.Add(Burn(2f, 1.2f, 0.2f, 1f));
                return;
            }

            if (Contains(n, "plasma"))
            {
                abilities.Add(MulVs(BulletBankAbilityType.DamageMultiplierVsShip, 1.15f));
                abilities.Add(Burn(3f, 1.5f, 0.25f, 1f));
                return;
            }

            if (Contains(n, "laserbolt") || Contains(n, "lasersmall") || Contains(n, "laser"))
            {
                stats.bulletSpeedMultiplier = 1.25f;
                stats.bulletRangeMultiplier = 1.15f;
                stats.firePowerMultiplier = 0.9f;
                abilities.Add(Stretch(0.5f, 2f));
                return;
            }

            if (Contains(n, "bullet"))
            {
                stats.fireRateMultiplier = 1.2f;
                abilities.Add(MulVs(BulletBankAbilityType.DamageMultiplierVsAsteroid, 1.2f));
            }
        }

        static bool Contains(string name, string token) =>
            name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        static BulletBankAbility Shock(float duration) => new BulletBankAbility
        {
            type = BulletBankAbilityType.ElectricShockDisable,
            duration = duration,
            magnitude = 1f,
        };

        static BulletBankAbility Burn(float dps, float duration, float tick, float extraRange) =>
            new BulletBankAbility
            {
                type = BulletBankAbilityType.BurnOverTime,
                magnitude = dps,
                duration = duration,
                tickInterval = tick,
                radius = extraRange,
            };

        static BulletBankAbility Heal(float amount) => new BulletBankAbility
        {
            type = BulletBankAbilityType.HealFriendly,
            magnitude = amount,
        };

        static BulletBankAbility Push(float force, float radius = 6f) => new BulletBankAbility
        {
            type = BulletBankAbilityType.ConcussivePush,
            magnitude = force,
            radius = radius,
        };

        static BulletBankAbility Gravity(float radius, float force, float duration) =>
            new BulletBankAbility
            {
                type = BulletBankAbilityType.GravityPull,
                radius = radius,
                magnitude = force,
                duration = duration,
            };

        static BulletBankAbility MulVs(BulletBankAbilityType type, float magnitude) =>
            new BulletBankAbility
            {
                type = type,
                magnitude = magnitude,
            };

        static BulletBankAbility Stretch(float start, float end) => new BulletBankAbility
        {
            type = BulletBankAbilityType.StretchLengthInFlight,
            radius = start,
            magnitude = end,
        };
    }
}
