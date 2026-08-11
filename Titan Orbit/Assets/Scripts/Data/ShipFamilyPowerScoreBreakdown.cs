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
    /// [TITAN-ORBIT] <see cref="gemCap"/> / <see cref="peopleCap"/> store <b>raw</b> cargo numbers
    /// (purchase cost uses raw gemCap × 2). Power-score totals and bar segments use
    /// <see cref="WeightedGemCapForPowerScore"/> / <see cref="WeightedPeopleCapForPowerScore"/>
    /// so a gem hold of 142 contributes ~14.2 — comparable to firePower ~13 — instead of drowning
    /// combat stats.
    /// </para>
    /// </summary>
    [Serializable]
    public struct ShipFamilyPowerScoreBreakdown
    {
        public const int DisplayStatCount = 10;

        /// <summary>
        /// Raw gem capacity is divided by this for power-score / bar contribution.
        /// Example: gemCap 142 → power contribution 14.2.
        /// </summary>
        public const float GemCapPowerScoreDivisor = 10f;

        /// <summary>
        /// Raw people capacity divisor for power-score (milder than gems — cargo people matter
        /// for capture but should not dominate firepower bars).
        /// Example: peopleCap 40 → power contribution 10.
        /// </summary>
        public const float PeopleCapPowerScoreDivisor = 4f;

        public float offense;
        public float defense;
        public float energy;
        public float mobility;
        public float capacity;
        public float firePower;
        public float bulletSpeed;
        public float fireRate;
        public float rammingPower;
        public float healthCap;
        public float healthRegen;
        public float energyCap;
        public float energyRegen;
        public float moveSpeed;
        public float turnSpeed;
        /// <summary>Raw max gems (purchase cost). Power bars use <see cref="WeightedGemCapForPowerScore"/>.</summary>
        public float gemCap;
        /// <summary>Raw max people. Power bars use <see cref="WeightedPeopleCapForPowerScore"/>.</summary>
        public float peopleCap;

        /// <summary>Gem contribution to power score / UI bars (raw ÷ <see cref="GemCapPowerScoreDivisor"/>).</summary>
        public float WeightedGemCapForPowerScore =>
            gemCap / Mathf.Max(0.01f, GemCapPowerScoreDivisor);

        /// <summary>People contribution to power score / UI bars (raw ÷ <see cref="PeopleCapPowerScoreDivisor"/>).</summary>
        public float WeightedPeopleCapForPowerScore =>
            peopleCap / Mathf.Max(0.01f, PeopleCapPowerScoreDivisor);

        public float Total => offense + defense + energy + mobility + capacity;

        public float DisplayTotal =>
            firePower + bulletSpeed + healthCap + healthRegen + energyCap + energyRegen +
            moveSpeed + turnSpeed + WeightedGemCapForPowerScore + WeightedPeopleCapForPowerScore;

        public bool HasDisplayStats => DisplayTotal > 0.01f;

        public float GetDisplayTotalForUi() => HasDisplayStats ? DisplayTotal : Total;

        /// <summary>
        /// Canonical total power score for upgrade-tree ordering.
        /// Prefer display total when set; else legacy category total.
        /// </summary>
        public float GetUpgradeTreeSortPowerScore() =>
            HasDisplayStats ? DisplayTotal : Total;

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
            stats.firePower += stats.firePowerPerAbilityLevel * upgradeCount;
            stats.bulletRange += stats.bulletRangePerAbilityLevel * upgradeCount;
            stats.fireRate += stats.fireRatePerAbilityLevel * upgradeCount;
            stats.rammingPower += stats.rammingPowerPerAbilityLevel * upgradeCount;
            stats.healthCap += stats.healthCapPerAbilityLevel * upgradeCount;
            stats.healthRegen += stats.healthRegenPerAbilityLevel * upgradeCount;
            stats.energyCap += stats.energyCapPerAbilityLevel * upgradeCount;
            stats.energyRegen += stats.energyRegenPerAbilityLevel * upgradeCount;
            stats.moveSpeed += stats.moveSpeedPerAbilityLevel * upgradeCount;
            stats.accelerationCap += stats.accelerationCapPerAbilityLevel * upgradeCount;
            stats.extraSpeedPercent += stats.extraSpeedPercentPerAbilityLevel * upgradeCount;
            stats.extraSpeedEnergyDrain += stats.extraSpeedEnergyDrainPerAbilityLevel * upgradeCount;
            stats.turnSpeed += stats.turnSpeedPerAbilityLevel * upgradeCount;
            stats.maxGems += stats.maxGemsPerAbilityLevel * upgradeCount;
            stats.maxPeople += stats.maxPeoplePerAbilityLevel * upgradeCount;
            return stats;
        }

        /// <summary>Stat value for one bar segment (0–9); falls back to half-category split when display stats are unset.</summary>
        public float GetDisplayStatValue(int statIndex)
        {
            // --- Compute value ---
            if (HasDisplayStats)
            {
                switch (statIndex)
                {
                    case 0: return firePower;
                    case 1: return bulletSpeed;
                    case 2: return healthCap;
                    case 3: return healthRegen;
                    case 4: return energyCap;
                    case 5: return energyRegen;
                    case 6: return moveSpeed;
                    case 7: return turnSpeed;
                    // Power bars show weighted cargo so gem/people hold does not dwarf firePower.
                    case 8: return WeightedGemCapForPowerScore;
                    case 9: return WeightedPeopleCapForPowerScore;
                }

                return 0f;
            }

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
        /// Gem purchase cost for a chassis tier: 2× <b>raw</b> gem cap from breakdown, with level-based fallback.
        /// [TITAN-ORBIT] Uses unweighted <see cref="gemCap"/> — power-score ÷10 must not shrink shop prices.
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
            // Raw cargo stored; capacity category + DisplayTotal use weighted helpers via properties
            // after construction (capacity field stores the weighted sum for legacy Total).
            float rawGems = s.maxGems;
            float rawPeople = s.maxPeople;
            float weightedGems = rawGems / Mathf.Max(0.01f, GemCapPowerScoreDivisor);
            float weightedPeople = rawPeople / Mathf.Max(0.01f, PeopleCapPowerScoreDivisor);

            return new ShipFamilyPowerScoreBreakdown
            {
                firePower = s.firePower,
                bulletSpeed = s.bulletSpeed,
                fireRate = s.fireRate,
                rammingPower = s.rammingPower,
                healthCap = s.healthCap,
                healthRegen = s.healthRegen,
                energyCap = s.energyCap,
                energyRegen = s.energyRegen,
                moveSpeed = s.moveSpeed,
                turnSpeed = s.turnSpeed,
                gemCap = rawGems,
                peopleCap = rawPeople,
                offense = s.firePower + s.bulletSpeed + s.bulletRange + s.fireRate + s.rammingPower,
                defense = s.healthCap + s.healthRegen,
                energy = s.energyCap + s.energyRegen,
                mobility = s.moveSpeed + s.turnSpeed + s.accelerationCap,
                capacity = weightedGems + weightedPeople
            };
        }
    }
}
