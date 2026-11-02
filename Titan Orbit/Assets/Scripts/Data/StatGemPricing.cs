using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Turns a part's stats into a gem price using <see cref="StatGemRates"/>.
    /// Each stat is multiplied by that stat's rate (1.25 gems per damage, 0.375 gems per health)
    /// and the products are added. A hull still prices those grown numbers directly.
    /// A gear card prices the same numbers, then multiplies by the buy level, so a higher
    /// level costs more even when Per Extra is only a small step. Titan parts use the
    /// same rates on their catalog stats (no buy-level multiply — Titans do not Extra-Level).
    /// <para>
    /// [TITAN-ORBIT] A ship's price is the sum of those part prices, one charge per bolted copy.
    /// The orbit-tree pill and the server debit both use the hull sum. A hull child is scaled
    /// by its prefab <c>localScale</c> first (same rules as the power bar), then Extra Level
    /// runs, then that family's bonus multipliers. A gear card bought in the store has no hull
    /// mesh and no family roll yet, so that sticker is the catalog part at the buy level.
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
            return RoundGems(GemSum(stats, rates), rates);
        }

        /// <summary>
        /// Gems to buy one family gear part at <paramref name="shipLevel"/>.
        /// The orbit menu card and the server debit both call this, so the number on
        /// the button is the number taken from the gem bank.
        /// </summary>
        /// <param name="entry">Catalog row for the part being bought. Null costs the part floor.</param>
        /// <param name="shipLevel">
        /// Buy level, already <c>min(ship, docked planet)</c>. Level 1 keeps the gem value.
        /// Each higher level multiplies the value of the stats at that level.
        /// </param>
        public static int PriceFamilyPart(ShipFamilyComponentEntry entry, int shipLevel)
        {
            if (entry == null)
                return Mathf.RoundToInt(StatGemRates.Load().minimumPartGems);

            // --- Value at this buy level ---
            // [TITAN-ORBIT] Extra Level grows the catalog numbers. Ability purchases stay
            // at 0 — the sticker is the fresh part, not the ship's bottom-bar chips.
            // Level 1 already includes one step of Per Extra. Weapon bullet speed stays
            // at the catalog base unless a bullet-speed ability is purchased, and that
            // purchase is not on this sticker.
            // Ship-wide mobility drag is left off. That drag shrinks move and turn as the
            // ship levels, which made a higher-level fin cheaper than a level-1 fin.
            shipLevel = Mathf.Max(1, shipLevel);
            var attrs = default(ShipAbilityLevelCounts);
            bool weapon = ShipComponentAbilityStats.IsWeaponComponent(entry.componentId);
            ShipComponentAbilityStats atLevel = ShipComponentExtraLevelMath.EvaluatePool(
                entry.stats,
                componentCount: 1,
                shipLevel,
                in attrs,
                isWeaponPool: weapon);

            // --- Gem value × buy level ---
            // [TITAN-ORBIT] Per Extra is often a small step (a fin gains 10 degrees, an
            // engine gains 0.1 move). The rate table turns that into a fraction of a gem,
            // so the rounded sticker stayed flat from level 1 to level 6. Multiplying the
            // gem value by the buy level makes the price follow both inputs: the stats at
            // this level, and the level itself. Level 1 is unchanged (×1).
            StatGemRates rates = StatGemRates.Load();
            rates.ClampRates();
            float gems = GemSum(atLevel, rates) * shipLevel;
            return RoundGems(gems, rates);
        }

        /// <summary>
        /// Unrounded gem sum for stats that are already final. Per-extra fields are not read.
        /// Callers that need an integer sticker use <see cref="RoundGems"/>.
        /// </summary>
        static float GemSum(in ShipComponentAbilityStats stats, StatGemRates rates)
        {
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
            return gems;
        }

        /// <summary>
        /// Nearest gem, never under <see cref="StatGemRates.minimumPartGems"/>.
        /// </summary>
        static int RoundGems(float gems, StatGemRates rates)
        {
            int rounded = Mathf.RoundToInt(gems);
            int floor = Mathf.RoundToInt(rates.minimumPartGems);
            return Mathf.Max(floor, rounded);
        }

        /// <summary>
        /// Gems to buy this family hull: each prefab child that matches a catalog part
        /// adds the gem value of that child's stats at <paramref name="shipLevel"/>.
        /// This is not the gear-card sticker — hulls do not also multiply by the level.
        /// Two copies of the same wing add that value twice.
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
