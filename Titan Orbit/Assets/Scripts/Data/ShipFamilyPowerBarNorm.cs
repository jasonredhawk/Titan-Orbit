using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Per-stat ceilings for the Orbit Menu upgrade-tree power bar. Each of the ten
    /// ability slots fills as <c>thisShip / globalMax</c>, so Health Regen is readable
    /// next to Health Cap. Values come from every family's chassis evaluated at that
    /// chassis's tree level with every HUD ability maxed. Paired with
    /// <see cref="UI.ShipUpgradeTreePowerBarUI"/>.
    /// </summary>
    public struct ShipPowerBarStatMaxes
    {
        public const int StatCount = ShipFamilyPowerScoreBreakdown.DisplayStatCount;

        public float firePower;
        public float bulletSpeed;
        public float healthCap;
        public float healthRegen;
        public float energyCap;
        public float energyRegen;
        public float moveSpeed;
        public float turnSpeed;
        public float gemCap;
        public float peopleCap;

        /// <summary>Floor so a missing catalog never divides by zero (empty bar stays empty).</summary>
        public const float MinDenominator = 0.001f;

        /// <summary>All zeros — caller must <see cref="Absorb"/> real ships or <see cref="EnsureMinimum"/>.</summary>
        public static ShipPowerBarStatMaxes CreateEmpty() => default;

        /// <summary>Raises each slot to at least <see cref="MinDenominator"/> so fill ratios stay defined.</summary>
        public void EnsureMinimum()
        {
            firePower = Mathf.Max(firePower, MinDenominator);
            bulletSpeed = Mathf.Max(bulletSpeed, MinDenominator);
            healthCap = Mathf.Max(healthCap, MinDenominator);
            healthRegen = Mathf.Max(healthRegen, MinDenominator);
            energyCap = Mathf.Max(energyCap, MinDenominator);
            energyRegen = Mathf.Max(energyRegen, MinDenominator);
            moveSpeed = Mathf.Max(moveSpeed, MinDenominator);
            turnSpeed = Mathf.Max(turnSpeed, MinDenominator);
            gemCap = Mathf.Max(gemCap, MinDenominator);
            peopleCap = Mathf.Max(peopleCap, MinDenominator);
        }

        /// <summary>Keeps the higher value per display stat from <paramref name="breakdown"/>.</summary>
        public void Absorb(in ShipFamilyPowerScoreBreakdown breakdown)
        {
            firePower = Mathf.Max(firePower, breakdown.GetDisplayStatValue(0));
            bulletSpeed = Mathf.Max(bulletSpeed, breakdown.GetDisplayStatValue(1));
            healthCap = Mathf.Max(healthCap, breakdown.GetDisplayStatValue(2));
            healthRegen = Mathf.Max(healthRegen, breakdown.GetDisplayStatValue(3));
            energyCap = Mathf.Max(energyCap, breakdown.GetDisplayStatValue(4));
            energyRegen = Mathf.Max(energyRegen, breakdown.GetDisplayStatValue(5));
            moveSpeed = Mathf.Max(moveSpeed, breakdown.GetDisplayStatValue(6));
            turnSpeed = Mathf.Max(turnSpeed, breakdown.GetDisplayStatValue(7));
            gemCap = Mathf.Max(gemCap, breakdown.GetDisplayStatValue(8));
            peopleCap = Mathf.Max(peopleCap, breakdown.GetDisplayStatValue(9));
        }

        /// <summary>Global max for display stat index 0–9 (Fire Power … Troop Cap).</summary>
        public float Get(int statIndex)
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
                default: return MinDenominator;
            }
        }
    }

    /// <summary>
    /// Resolves upgrade-tree power-bar stats: Extra Level at ship level with every HUD
    /// ability maxed (<see cref="ShipAbilityLevelCounts.Maxed"/>). Same formulas as live
    /// ships — non-weapons use <c>(ship−1) + ability + (N−1)</c>; weapons omit N;
    /// weapon bullet speed is ability-only. Live prefab sums are cached per session.
    /// Also walks the catalog for the ten global maxes.
    /// </summary>
    public static class ShipFamilyPowerBarNorm
    {
        static readonly Dictionary<string, ShipFamilyPowerScoreBreakdown> s_liveCache =
            new Dictionary<string, ShipFamilyPowerScoreBreakdown>();

        static ShipPowerBarStatMaxes s_cachedMaxes;
        static bool s_hasCachedMaxes;

        /// <summary>
        /// Extra Level at the tier's tree level with every HUD ability maxed.
        /// Writes <see cref="ShipFamilyChassisTierEntry.powerScoreBreakdownAtShipLevel"/>.
        /// Editor bake path — call after the tier's ship level is assigned.
        /// </summary>
        public static void BakeAtShipLevel(ShipFamilyChassisTierEntry entry, ShipFamilyDefinition family)
        {
            if (entry == null)
                return;

            if (entry.prefab == null || family == null)
            {
                entry.powerScoreBreakdownAtShipLevel = default;
                return;
            }

            int shipLevel = Mathf.Max(1, entry.minHomePlanetLevel);
            ShipAbilityLevelCounts maxed = ShipAbilityLevelCounts.Maxed(shipLevel);
            if (ShipFamilyStatsCalculator.TrySumFromPrefab(
                    entry.prefab, family, shipLevel, in maxed, out ShipComponentAbilityStats stats))
            {
                entry.powerScoreBreakdownAtShipLevel =
                    ShipFamilyPowerScoreBreakdown.FromSummedShipStats(stats);
                return;
            }

            entry.powerScoreBreakdownAtShipLevel = default;
        }

        /// <summary>
        /// Display breakdown for a chassis at <paramref name="shipLevel"/> with every HUD
        /// ability maxed. Live-sums the prefab (session-cached) so bars stay correct even
        /// when the editor bake still has the older ability-0 values.
        /// </summary>
        public static ShipFamilyPowerScoreBreakdown GetBreakdownAtShipLevel(
            ShipFamilyDefinition family,
            ShipFamilyChassisTierEntry tier,
            int shipLevel)
        {
            if (tier == null)
                return default;

            shipLevel = Mathf.Max(1, shipLevel);
            string cacheKey = (tier.chassisId ?? string.Empty) + "@" + shipLevel + "@max";
            if (s_liveCache.TryGetValue(cacheKey, out ShipFamilyPowerScoreBreakdown cached))
                return cached;

            ShipAbilityLevelCounts maxed = ShipAbilityLevelCounts.Maxed(shipLevel);
            if (tier.prefab != null &&
                family != null &&
                ShipFamilyStatsCalculator.TrySumFromPrefab(
                    tier.prefab, family, shipLevel, in maxed, out ShipComponentAbilityStats stats))
            {
                ShipFamilyPowerScoreBreakdown live =
                    ShipFamilyPowerScoreBreakdown.FromSummedShipStats(stats);
                s_liveCache[cacheKey] = live;
                return live;
            }

            // Last resort: level-1 bake so the bar is not blank if the prefab cannot be summed.
            return tier.powerScoreBreakdown;
        }

        /// <summary>
        /// Highest value of each of the ten display stats across every upgrade-tree chassis,
        /// each evaluated at that chassis's tree level with every HUD ability maxed.
        /// Cached until <see cref="InvalidateCache"/>.
        /// </summary>
        public static ShipPowerBarStatMaxes GetGlobalMaxPerStat(PlanetShipFamilyConfig config = null)
        {
            if (s_hasCachedMaxes)
                return s_cachedMaxes;

            var maxes = ShipPowerBarStatMaxes.CreateEmpty();
            IEnumerable<ShipFamilyDefinition> families = EnumerateFamilies(config);
            if (families != null)
            {
                foreach (ShipFamilyDefinition def in families)
                {
                    if (def?.upgradeTree == null)
                        continue;

                    for (int i = 0; i < def.upgradeTree.Count; i++)
                    {
                        ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                        if (tier == null)
                            continue;

                        int level = Mathf.Max(1, tier.minHomePlanetLevel);
                        ShipFamilyPowerScoreBreakdown breakdown = GetBreakdownAtShipLevel(def, tier, level);
                        maxes.Absorb(breakdown);
                    }
                }
            }

            maxes.EnsureMinimum();
            s_cachedMaxes = maxes;
            s_hasCachedMaxes = true;
            return s_cachedMaxes;
        }

        /// <summary>Drops live-sum and global-max caches after an editor rebake.</summary>
        public static void InvalidateCache()
        {
            s_hasCachedMaxes = false;
            s_cachedMaxes = default;
            s_liveCache.Clear();
        }

        static IEnumerable<ShipFamilyDefinition> EnumerateFamilies(PlanetShipFamilyConfig config)
        {
            if (config?.families != null && config.families.Count > 0)
            {
                var list = new List<ShipFamilyDefinition>(config.families.Count);
                for (int i = 0; i < config.families.Count; i++)
                {
                    ShipFamilyDefinition def = config.families[i]?.shipFamilyDefinition;
                    if (def != null)
                        list.Add(def);
                }

                if (list.Count > 0)
                    return list;
            }

            return Resources.FindObjectsOfTypeAll<ShipFamilyDefinition>();
        }
    }
}
