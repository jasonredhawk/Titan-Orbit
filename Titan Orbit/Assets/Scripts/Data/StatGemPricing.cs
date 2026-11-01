using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Turns a part's stats into a gem price using <see cref="StatGemRates"/>.
    /// Each stat is multiplied by that stat's rate (1.25 gems per damage, 0.375 gems per health)
    /// and the products are added. Regular parts grow with Extra Level first, so a higher
    /// buy level costs more because the numbers got bigger, not because of a separate percent.
    /// Titan parts use the same rates on their catalog stats.
    /// <para>
    /// [TITAN-ORBIT] A ship's price is the sum of those part prices, one charge per bolted copy.
    /// The orbit-tree pill and the server debit both use the hull sum. A hull child is scaled
    /// by its prefab <c>localScale</c> first (same rules as the power bar), then Extra Level
    /// runs, then that family's bonus multipliers. A gear card bought in the store has no hull
    /// mesh and no family roll yet, so that sticker stays the catalog part.
    /// </para>
    /// </summary>
    public static class StatGemPricing
    {
        /// <summary>
        /// Gems for one part whose stats are already final.
        /// Family callers pass Extra Level output. Titan callers pass catalog bases.
        /// A zero-stat part costs <see cref="StatGemRates.minimumPartGems"/>.
        /// </summary>
        /// <param name="stats">The numbers on the part right now. Per-extra fields are not read.</param>
        public static int Price(in ShipComponentAbilityStats stats)
        {
            StatGemRates rates = StatGemRates.Load();
            rates.ClampRates();

            // --- Each stat × its own rate ---
            // [TITAN-ORBIT] Turn is degrees, damage is a small number. They do not share a rate.
            // A stat at 0 adds nothing. Negative stats are treated as 0.
            float gems = 0f;
            gems += NonNegative(stats.firePower) * rates.gemsPerDamage;
            gems += NonNegative(stats.bulletSpeed) * rates.gemsPerBulletSpeed;
            gems += NonNegative(stats.bulletRange) * rates.gemsPerBulletRange;
            gems += NonNegative(stats.fireRate) * rates.gemsPerFireRate;
            gems += NonNegative(stats.rammingPower) * rates.gemsPerRamming;
            gems += NonNegative(stats.healthCap) * rates.gemsPerHealth;
            gems += NonNegative(stats.healthRegen) * rates.gemsPerHealthRegen;
            gems += NonNegative(stats.energyCap) * rates.gemsPerEnergy;
            gems += NonNegative(stats.energyRegen) * rates.gemsPerEnergyRegen;
            gems += NonNegative(stats.moveSpeed) * rates.gemsPerMoveSpeed;
            gems += NonNegative(stats.accelerationCap) * rates.gemsPerAcceleration;
            gems += NonNegative(stats.turnSpeed) * rates.gemsPerTurnSpeed;
            gems += NonNegative(stats.maxGems) * rates.gemsPerGemCap;
            gems += NonNegative(stats.maxPeople) * rates.gemsPerTroopCap;
            gems += NonNegative(stats.tractorBeamDistance) * rates.gemsPerTractorRange;
            gems += NonNegative(stats.tractorBeamPower) * rates.gemsPerTractorPower;
            gems += NonNegative(stats.extraSpeedPercent) * rates.gemsPerOverdrive;

            int rounded = Mathf.RoundToInt(gems);
            int floor = Mathf.RoundToInt(rates.minimumPartGems);
            return Mathf.Max(floor, rounded);
        }

        /// <summary>
        /// Gems to buy one family gear part at <paramref name="shipLevel"/>.
        /// Extra Level runs first (ability purchases stay at 0 — the sticker is the fresh part).
        /// Fleet bonuses and bullet-bank overlays stay off the price, matching the old gear card.
        /// </summary>
        public static int PriceFamilyPart(ShipFamilyComponentEntry entry, int shipLevel)
        {
            if (entry == null)
                return Mathf.RoundToInt(StatGemRates.Load().minimumPartGems);

            // --- Grow, then price ---
            // [TITAN-ORBIT] Level 1 already includes one step of Per Extra. Weapons keep
            // bullet speed at the catalog base unless a bullet-speed ability is purchased,
            // and that purchase is not on this sticker.
            shipLevel = Mathf.Max(1, shipLevel);
            ShipComponentAbilityStats grown = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(
                entry.stats, shipLevel, entry.componentId);
            return Price(grown);
        }

        /// <summary>
        /// Gems to buy this family hull: each prefab child that matches a catalog part
        /// adds that part's gear price at <paramref name="shipLevel"/>. Two copies of the
        /// same wing add the wing price twice.
        /// </summary>
        /// <param name="config">Family list. Null loads the default Resources asset.</param>
        /// <param name="chassisId">Upgrade-tree id such as <c>AstroEagle_07</c>.</param>
        /// <param name="shipLevel">Level of the card being bought.</param>
        /// <returns>Sum of part prices. A hull with no matched parts costs the single-part floor so the buy is not free.</returns>
        public static int PriceFamilyHull(PlanetShipFamilyConfig config, string chassisId, int shipLevel)
        {
            if (config == null)
                config = PlanetShipFamilyConfig.LoadDefault();

            ShipFamilyChassisTierEntry tier = config != null
                ? config.GetTierEntryForChassisId(chassisId)
                : null;

            // Chassis ids are Family_Slot (AstroEagle_07). The family id is the prefix.
            string familyId = null;
            if (!string.IsNullOrEmpty(chassisId))
            {
                int split = chassisId.IndexOf('_');
                if (split > 0)
                    familyId = chassisId.Substring(0, split);
            }

            ShipFamilyDefinition family = config != null
                ? config.GetFamilyDefinitionByFamilyId(familyId)
                : null;

            return PriceFamilyPrefab(tier?.prefab, family, shipLevel);
        }

        /// <summary>
        /// Gems for one hull prefab at <paramref name="shipLevel"/>, using <paramref name="family"/>'s catalog.
        /// Each child is scaled, grown with Extra Level, then multiplied by that family's bonuses.
        /// The upgrade-tree sort on the family inspector and on ShipFamilyDefinitionCatalog both call this.
        /// </summary>
        public static int PriceFamilyPrefab(GameObject prefab, ShipFamilyDefinition family, int shipLevel)
        {
            if (prefab == null || family == null)
                return Mathf.RoundToInt(StatGemRates.Load().minimumPartGems);

            shipLevel = Mathf.Max(1, shipLevel);
            int total = SumPrefabParts(prefab, family, shipLevel);
            if (total <= 0)
                return Mathf.RoundToInt(StatGemRates.Load().minimumPartGems);
            return total;
        }

        /// <summary>
        /// Adds the gear price of every stat-bearing child on <paramref name="prefab"/>.
        /// Scale is applied to that child's catalog row before Extra Level, matching the
        /// power bar. Children whose names do not match a catalog row add nothing.
        /// </summary>
        static int SumPrefabParts(GameObject prefab, ShipFamilyDefinition family, int shipLevel)
        {
            string familyId = string.IsNullOrWhiteSpace(family.familyId) ? string.Empty : family.familyId.Trim();
            if (familyId.Length == 0)
                return 0;

            // [UNITY] Walk the prefab asset. Do not Instantiate — a dedicated server
            // build strips cloned names, and GetComponentsInChildren still sees the asset.
            Transform[] transforms = prefab.GetComponentsInChildren<Transform>(true);
            int total = 0;
            string prefix = familyId + "_";
            shipLevel = Mathf.Max(1, shipLevel);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child == null || child == prefab.transform)
                    continue;

                string componentId = ShipFamilyDefinition.NormalizeComponentId(child.name);
                if (string.IsNullOrWhiteSpace(componentId))
                    continue;
                if (!componentId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!family.TryGetComponentEntry(componentId, out ShipFamilyComponentEntry entry) || entry == null)
                    continue;

                // --- Scale, Extra Level, family bonus, then the rate table ---
                // [TITAN-ORBIT] A cockpit at scale 3 is 3× health before Per Extra grows it.
                // A uniform scale-2 gun is 4× damage and ¼ fire rate. The family bonus
                // (authored multipliers, or this match's roll) is the same multiply the
                // power bar and the live hull apply. Home planets stay 1× during a match.
                ShipComponentAbilityStats scaled = ShipComponentAbilityStatsMath.ScaleStatsByTransform(
                    entry.stats, child, componentId);
                ShipComponentAbilityStats grown = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(
                    scaled, shipLevel, entry.componentId);
                grown = family.ApplySpecialBonuses(grown);
                total += Price(grown);
            }

            return total;
        }

        static float NonNegative(float value)
        {
            return value > 0f ? value : 0f;
        }
    }
}
