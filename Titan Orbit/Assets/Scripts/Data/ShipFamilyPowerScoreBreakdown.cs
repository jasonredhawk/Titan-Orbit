using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Ten-stat power breakdown for ship upgrade tree UI bars and gem-cost derivation.
    /// Holds both legacy five-category totals (offense/defense/energy/mobility/capacity) and per-stat
    /// display fields used by <see cref="UI.ShipUpgradeTreePowerBarUI"/>. Baked on chassis tiers at edit time.
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
            stats.firePower += stats.firePowerPerLevel * upgradeCount;
            stats.bulletRange += stats.bulletRangePerLevel * upgradeCount;
            stats.fireRate += stats.fireRatePerLevel * upgradeCount;
            stats.rammingPower += stats.rammingPowerPerLevel * upgradeCount;
            stats.healthCap += stats.healthCapPerLevel * upgradeCount;
            stats.healthRegen += stats.healthRegenPerLevel * upgradeCount;
            stats.energyCap += stats.energyCapPerLevel * upgradeCount;
            stats.energyRegen += stats.energyRegenPerLevel * upgradeCount;
            stats.moveSpeed += stats.moveSpeedPerLevel * upgradeCount;
            stats.accelerationCap += stats.accelerationCapPerLevel * upgradeCount;
            stats.turnSpeed += stats.turnSpeedPerLevel * upgradeCount;
            stats.maxGems += stats.maxGemsPerLevel * upgradeCount;
            stats.maxPeople += stats.maxPeoplePerLevel * upgradeCount;
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
                    case 8: return gemCap;
                    case 9: return peopleCap;
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
    }
}
