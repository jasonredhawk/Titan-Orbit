using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Ten-stat power breakdown for ship upgrade tree UI bars and gem-cost derivation.
    /// Holds both legacy five-category totals (offense/defense/energy/mobility/capacity) and per-stat
    /// display fields used by <see cref="UI.ShipUpgradeTreePowerBarUI"/>. Baked on chassis tiers at edit time.
    /// <para>
    /// [TITAN-ORBIT] Slot 0 on the colourful power bar is <b>sustained DPS</b>
    /// (<c>firePower × fireRate</c>), not raw damage per shot. A slow cannon with
    /// huge Fire Power and a rapid gun with modest Fire Power can now compare fairly.
    /// Authored <see cref="firePower"/> / <see cref="fireRate"/> stay per-hit and
    /// shots-per-second so combat and Extra Level math are unchanged.
    /// </para>
    /// </summary>
    [Serializable]
    public struct ShipFamilyPowerScoreBreakdown
    {
        public const int DisplayStatCount = 10;

        public float offense;
        public float defense;
        public float energy;
        public float mobility;
        public float capacity;
        public float firePower;
        public float bulletSpeed;
        public float fireRate;
        /// <summary>
        /// All-gun sustained DPS (<c>Σ firePower_i × fireRate_i</c> after Extra Level).
        /// Power-bar slot 0 and the Fire Power chip read this. 0 means "unset" — fall
        /// back to <c>firePower × fireRate</c> (primary-gun product).
        /// </summary>
        public float sustainedDps;
        public float rammingPower;
        public float healthCap;
        public float healthRegen;
        public float energyCap;
        public float energyRegen;
        public float moveSpeed;
        public float turnSpeed;
        public float gemCap;
        public float peopleCap;

        public float Total => offense + defense + energy + mobility + capacity;

        public float DisplayTotal =>
            firePower + bulletSpeed + healthCap + healthRegen + energyCap + energyRegen +
            moveSpeed + turnSpeed + gemCap + peopleCap;

        public bool HasDisplayStats => DisplayTotal > 0.01f;

        public float GetDisplayTotalForUi() => HasDisplayStats ? DisplayTotal : Total;

        /// <summary>
        /// Canonical total for upgrade-tree ordering.
        /// Replaces raw Fire Power with <see cref="ShipWeaponDpsMath.ComputeSustainAdjustedDps"/>
        /// so a 4-gun hull ranks above a 1-gun hull, and a fat energy battery / fast
        /// regen ranks above a glass cannon that dumps its clip in one second.
        /// Other display stats (health, energy, mobility, cargo) stay in the sum.
        /// </summary>
        public float GetUpgradeTreeSortPowerScore()
        {
            if (!HasDisplayStats)
                return Total;

            float rest = DisplayTotal - firePower;
            float combat = ShipWeaponDpsMath.ComputeSustainAdjustedDps(
                GetDisplayDps(), energyCap, energyRegen);
            return combat + rest;
        }

        /// <summary>Compares two breakdowns for ascending total power score.</summary>
        public static int CompareForUpgradeTreeSort(
            ShipFamilyPowerScoreBreakdown a,
            ShipFamilyPowerScoreBreakdown b)
        {
            int cmp = a.GetUpgradeTreeSortPowerScore().CompareTo(b.GetUpgradeTreeSortPowerScore());
            if (cmp != 0)
                return cmp;
            return a.GetDisplayTotalForUi().CompareTo(b.GetDisplayTotalForUi());
        }

        /// <summary>Max attribute upgrades for a tier = minHomePlanetLevel.</summary>
        public static int GetMaxUpgradeCountForTier(int minHomePlanetLevel) =>
            Mathf.Max(0, minHomePlanetLevel);

        /// <summary>Inflates summed stats by per-level × upgrade count (max-level preview).</summary>
        public static ShipComponentAbilityStats ApplyMaxEffectiveLevels(ShipComponentAbilityStats stats, int upgradeCount)
        {
            stats.firePower += stats.firePowerPerExtraLevel * upgradeCount;
            stats.bulletRange += stats.bulletRangePerExtraLevel * upgradeCount;
            stats.fireRate += stats.fireRatePerExtraLevel * upgradeCount;
            stats.rammingPower += stats.rammingPowerPerExtraLevel * upgradeCount;
            stats.healthCap += stats.healthCapPerExtraLevel * upgradeCount;
            stats.healthRegen += stats.healthRegenPerExtraLevel * upgradeCount;
            stats.energyCap += stats.energyCapPerExtraLevel * upgradeCount;
            stats.energyRegen += stats.energyRegenPerExtraLevel * upgradeCount;
            stats.moveSpeed += stats.moveSpeedPerExtraLevel * upgradeCount;
            stats.accelerationCap += stats.accelerationCapPerExtraLevel * upgradeCount;
            stats.extraSpeedPercent += stats.extraSpeedPercentPerExtraLevel * upgradeCount;
            stats.extraSpeedEnergyDrain += stats.extraSpeedEnergyDrainPerExtraLevel * upgradeCount;
            stats.turnSpeed += stats.turnSpeedPerExtraLevel * upgradeCount;
            stats.maxGems += stats.maxGemsPerExtraLevel * upgradeCount;
            stats.maxPeople += stats.maxPeoplePerExtraLevel * upgradeCount;
            return stats;
        }

        /// <summary>
        /// Sustained damage per second for power bars and ability chips:
        /// <c>max(0, firePower) × max(0, fireRate)</c>.
        /// Fire Power is damage per shot; Fire Rate is shots per second. Their
        /// product is the hull's average DPS. Zero rate (unarmed) stays 0.
        /// </summary>
        /// <param name="shotDamage">Damage per shot (Fire Power).</param>
        /// <param name="shotsPerSecond">Shots per second (Fire Rate).</param>
        /// <returns>Sustained DPS. Never negative.</returns>
        public static float ComputeSustainedDps(float shotDamage, float shotsPerSecond) =>
            Mathf.Max(0f, shotDamage) * Mathf.Max(0f, shotsPerSecond);

        /// <summary>
        /// Power-bar / chip Fire Power lane: all-gun DPS when
        /// <see cref="sustainedDps"/> was baked, else <c>firePower × fireRate</c>.
        /// </summary>
        public float GetDisplayDps() =>
            sustainedDps > 0.0001f ? sustainedDps : ComputeSustainedDps(firePower, fireRate);

        /// <summary>
        /// Stat value for one bar segment (0–9). Slot 0 is sustained DPS, not raw
        /// Fire Power. Falls back to a half-category split when display stats are unset.
        /// </summary>
        /// <param name="statIndex">0 = DPS … 9 = Troop Cap.</param>
        public float GetDisplayStatValue(int statIndex)
        {
            // --- Per-stat readout (Orbit Menu equal-slot bars) ---
            if (HasDisplayStats)
            {
                switch (statIndex)
                {
                    case 0: return GetDisplayDps();
                    case 1: return bulletSpeed;
                    case 2: return healthCap;
                    case 3: return healthRegen;
                    case 4: return energyCap;
                    case 5: return energyRegen;
                    case 6: return moveSpeed;
                    case 7: return turnSpeed;
                    case 8: return gemCap;
                    case 9: return peopleCap;
                }

                return 0f;
            }

            // --- Legacy five-category bake (no per-stat fields) ---
            const float halfCategory = 0.5f;
            switch (statIndex)
            {
                case 0:
                case 1: return offense * halfCategory;
                case 2:
                case 3: return defense * halfCategory;
                case 4:
                case 5: return energy * halfCategory;
                case 6:
                case 7: return mobility * halfCategory;
                case 8:
                case 9: return capacity * halfCategory;
                default: return 0f;
            }
        }

        /// <summary>
        /// Gem purchase cost for a chassis tier: 2× gem cap from breakdown, with level-based fallback.
        /// [TITAN-ORBIT] Hull swaps and upgrades use this formula across orbit station and CardShop.
        /// </summary>
        public static int GetPurchaseGemCost(ShipFamilyChassisTierEntry tier, int shipLevel)
        {
            // --- Compute value ---
            if (tier == null)
                return 0;
            float baseCap = tier.powerScoreBreakdown.gemCap > 0.01f
                ? tier.powerScoreBreakdown.gemCap
                : 50f + shipLevel * 25f;
            return Mathf.RoundToInt(2f * Mathf.Max(0f, baseCap));
        }

        /// <summary>Builds a breakdown struct from summed <see cref="ShipComponentAbilityStats"/>.</summary>
        public static ShipFamilyPowerScoreBreakdown FromSummedShipStats(ShipComponentAbilityStats s)
        {
            // --- FromSummedShipStats ---
            return new ShipFamilyPowerScoreBreakdown
            {
                firePower = s.firePower,
                bulletSpeed = s.bulletSpeed,
                fireRate = s.fireRate,
                sustainedDps = ComputeSustainedDps(s.firePower, s.fireRate),
                rammingPower = s.rammingPower,
                healthCap = s.healthCap,
                healthRegen = s.healthRegen,
                energyCap = s.energyCap,
                energyRegen = s.energyRegen,
                moveSpeed = s.moveSpeed,
                turnSpeed = s.turnSpeed,
                gemCap = s.maxGems,
                peopleCap = s.maxPeople,
                offense = s.firePower + s.bulletSpeed + s.bulletRange + s.fireRate + s.rammingPower,
                defense = s.healthCap + s.healthRegen,
                energy = s.energyCap + s.energyRegen,
                mobility = s.moveSpeed + s.turnSpeed + s.accelerationCap,
                capacity = s.maxGems + s.maxPeople
            };
        }

        /// <summary>
        /// Same as <see cref="FromSummedShipStats"/> but slot 0 / sort use the
        /// all-gun DPS the caller already summed (every mount, Extra-Leveled).
        /// </summary>
        public static ShipFamilyPowerScoreBreakdown FromEvaluatedHull(
            in ShipComponentAbilityStats evaluated,
            float allGunDps)
        {
            ShipFamilyPowerScoreBreakdown breakdown = FromSummedShipStats(evaluated);
            breakdown.sustainedDps = Mathf.Max(0f, allGunDps);
            return breakdown;
        }
    }
}
