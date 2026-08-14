using System;
using System.Collections.Generic;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Name-token defaults for <see cref="BulletVfxBank"/> category profiles.
    /// Editor populate fills empty ability lists / unset stat mods; designers can override after.
    /// Primaries are weak vs starting ~3 bullet damage; Per Extra scales with Fire Power Extra Levels.
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
                stats.fireRateMultiplier = 0.55f;
                abilities.Add(Shock(0.75f, 0.12f));
                return;
            }

            if (Contains(n, "fireballsv2") || Contains(n, "fireballs"))
            {
                stats.bulletSpeedMultiplier = 0.8f;
                stats.firePowerMultiplier = 1.15f;
                stats.fireRateMultiplier = 0.6f;
                stats.bulletRangeMultiplier = 1.2f;
                abilities.Add(Burn(1.5f, 0.4f, 2f, 0.25f, 0.25f, 1.2f, 0.03f));
                return;
            }

            if (Contains(n, "liquid"))
            {
                stats.bulletSpeedMultiplier = 0.85f;
                stats.fireRateMultiplier = 0.7f;
                abilities.Add(Burn(1.2f, 0.3f, 1.8f, 0.2f, 0.3f, 1.1f, 0.02f));
                return;
            }

            if (Contains(n, "shockwave"))
            {
                stats.firePowerMultiplier = 1.1f;
                stats.bulletSpeedMultiplier = 0.9f;
                stats.fireRateMultiplier = 0.65f;
                stats.rammingPowerMultiplier = 1.15f;
                abilities.Add(Push(8f, 1.5f, 5f, 0.4f));
                return;
            }

            if (Contains(n, "rift"))
            {
                stats.fireRateMultiplier = 0.55f;
                abilities.Add(Gravity(6f, 0.5f, 9f, 1.5f, 1.2f, 0.15f));
                return;
            }

            if (Contains(n, "ring2") || Contains(n, "ring"))
            {
                stats.fireRateMultiplier = 0.7f;
                abilities.Add(Gravity(4f, 0.4f, 6f, 1f, 1f, 0.12f));
                return;
            }

            if (Contains(n, "rocket"))
            {
                stats.firePowerMultiplier = 1.4f;
                stats.bulletSpeedMultiplier = 0.7f;
                stats.fireRateMultiplier = 0.55f;
                stats.bulletRangeMultiplier = 1.3f;
                stats.rammingPowerMultiplier = 1.15f;
                abilities.Add(Push(9f, 1.8f, 5f, 0.4f));
                abilities.Add(MulVs(BulletBankAbilityType.DamageMultiplierVsAsteroid, 1.2f, 0.04f));
                return;
            }

            if (Contains(n, "energysphere") || Contains(n, "energy"))
            {
                stats.firePowerMultiplier = 0.75f;
                stats.fireRateMultiplier = 0.5f;
                abilities.Add(Heal(4f, 1.2f));
                return;
            }

            if (Contains(n, "sharp"))
            {
                stats.firePowerMultiplier = 1.2f;
                stats.bulletSpeedMultiplier = 1.15f;
                stats.fireRateMultiplier = 0.9f;
                abilities.Add(MulVs(BulletBankAbilityType.DamageMultiplierVsShip, 1.12f, 0.04f));
                return;
            }

            if (Contains(n, "sparkler"))
            {
                stats.fireRateMultiplier = 0.85f;
                stats.firePowerMultiplier = 0.85f;
                abilities.Add(Burn(0.8f, 0.2f, 1f, 0.15f, 0.2f, 1f, 0.02f));
                return;
            }

            if (Contains(n, "plasma"))
            {
                stats.fireRateMultiplier = 0.65f;
                abilities.Add(MulVs(BulletBankAbilityType.DamageMultiplierVsShip, 1.1f, 0.03f));
                abilities.Add(Burn(1f, 0.25f, 1.2f, 0.15f, 0.25f, 1f, 0.02f));
                return;
            }

            if (Contains(n, "laserbolt") || Contains(n, "lasersmall") || Contains(n, "laser"))
            {
                stats.bulletSpeedMultiplier = 1.25f;
                stats.bulletRangeMultiplier = 1.15f;
                stats.firePowerMultiplier = 0.9f;
                stats.fireRateMultiplier = 0.95f;
                abilities.Add(Stretch(0.5f, 0.02f, 2f, 0.08f));
                return;
            }

            if (Contains(n, "bullet"))
            {
                stats.fireRateMultiplier = 1.05f;
                abilities.Add(MulVs(BulletBankAbilityType.DamageMultiplierVsAsteroid, 1.12f, 0.03f));
            }
        }

        static bool Contains(string name, string token) =>
            name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        static BulletBankAbility Shock(float duration, float durationPerExtra) => new BulletBankAbility
        {
            type = BulletBankAbilityType.ElectricShockDisable,
            duration = duration,
            durationPerExtra = durationPerExtra,
            magnitude = 1f,
            energyDrain = 3.5f,
            energyDrainPerExtra = 0.5f,
        };

        static BulletBankAbility Burn(
            float dps, float dpsPerExtra,
            float duration, float durationPerExtra,
            float tick, float extraRange, float extraRangePerExtra) =>
            new BulletBankAbility
            {
                type = BulletBankAbilityType.BurnOverTime,
                magnitude = dps,
                magnitudePerExtra = dpsPerExtra,
                duration = duration,
                durationPerExtra = durationPerExtra,
                tickInterval = tick,
                radius = extraRange,
                radiusPerExtra = extraRangePerExtra,
                energyDrain = 2.5f,
                energyDrainPerExtra = 0.55f,
            };

        static BulletBankAbility Heal(float amount, float amountPerExtra) => new BulletBankAbility
        {
            type = BulletBankAbilityType.HealFriendly,
            magnitude = amount,
            magnitudePerExtra = amountPerExtra,
            energyDrain = 5f,
            energyDrainPerExtra = 0.9f,
        };

        static BulletBankAbility Push(float force, float forcePerExtra, float radius, float radiusPerExtra) =>
            new BulletBankAbility
            {
                type = BulletBankAbilityType.ConcussivePush,
                magnitude = force,
                magnitudePerExtra = forcePerExtra,
                radius = radius,
                radiusPerExtra = radiusPerExtra,
                energyDrain = 3f,
                energyDrainPerExtra = 0.45f,
            };

        static BulletBankAbility Gravity(
            float radius, float radiusPerExtra,
            float force, float forcePerExtra,
            float duration, float durationPerExtra) =>
            new BulletBankAbility
            {
                type = BulletBankAbilityType.GravityPull,
                radius = radius,
                radiusPerExtra = radiusPerExtra,
                magnitude = force,
                magnitudePerExtra = forcePerExtra,
                duration = duration,
                durationPerExtra = durationPerExtra,
                energyDrain = 3.5f,
                energyDrainPerExtra = 0.55f,
            };

        static BulletBankAbility MulVs(BulletBankAbilityType type, float magnitude, float magnitudePerExtra) =>
            new BulletBankAbility
            {
                type = type,
                magnitude = magnitude,
                magnitudePerExtra = magnitudePerExtra,
                energyDrain = 0.75f,
                energyDrainPerExtra = 0.12f,
            };

        static BulletBankAbility Stretch(float start, float startPerExtra, float end, float endPerExtra) =>
            new BulletBankAbility
            {
                type = BulletBankAbilityType.StretchLengthInFlight,
                radius = start,
                radiusPerExtra = startPerExtra,
                magnitude = end,
                magnitudePerExtra = endPerExtra,
                energyDrain = 0.5f,
                energyDrainPerExtra = 0.08f,
            };
    }
}
