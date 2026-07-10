using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Static stat summing from a chassis prefab hierarchy plus a <see cref="ShipFamilyDefinition"/>.
    /// Walks child transforms named <c>{familyId}_{componentId}</c>, scales stats by transform size, applies
    /// propulsion aggregation, then stat fallbacks. Shared by editor previews, power-score baking, and runtime UI.
    /// </summary>
    public static class ShipFamilyStatsCalculator
    {
        /// <summary>Per-component match list returned alongside the summed total.</summary>
        public struct SumResult
        {
            public ShipComponentAbilityStats TotalStats;
            public List<string> MatchedComponentIds;
            public List<ShipComponentAbilityStats> PerComponentStats;
        }

        /// <summary>
        /// Sums prefab stats at level 1, then applies per-level scaling for <paramref name="shipLevel"/>.
        /// Returns false when prefab or family is missing or the sum is all zero.
        /// </summary>
        public static bool TrySumFromPrefab(
            GameObject prefab,
            ShipFamilyDefinition family,
            int shipLevel,
            out ShipComponentAbilityStats effectiveAtLevel)
        {
            effectiveAtLevel = default;
            if (prefab == null || family == null)
                return false;

            SumResult sum = SumFromPrefabHierarchy(prefab, family, shipLevel: 1);
            if (ShipComponentAbilityStatsMath.IsAllZero(sum.TotalStats))
                return false;

            effectiveAtLevel = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(sum.TotalStats, shipLevel);
            return true;
        }

        /// <summary>
        /// Core scan: instantiate prefab if needed, match children, sum scaled stats, apply propulsion rules.
        /// </summary>
        public static SumResult SumFromPrefabHierarchy(GameObject prefab, ShipFamilyDefinition family, int shipLevel = 1)
        {
            var result = new SumResult
            {
                TotalStats = default,
                MatchedComponentIds = new List<string>(),
                PerComponentStats = new List<ShipComponentAbilityStats>(),
            };

            if (prefab == null || family == null)
                return result;

            string familyId = !string.IsNullOrWhiteSpace(family.familyId)
                ? family.familyId.Trim()
                : string.Empty;
            if (string.IsNullOrEmpty(familyId))
                return result;

            // [UNITY] Prefab assets are not in a scene — instantiate temporarily so GetComponentsInChildren works.
            GameObject instance = prefab;
            bool destroyInstance = false;
            if (!prefab.scene.IsValid())
            {
                instance = UnityEngine.Object.Instantiate(prefab);
                destroyInstance = true;
            }

            try
            {
                var transforms = instance.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform t = transforms[i];
                    if (t == null || t == instance.transform)
                        continue;

                    string name = t.name;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    // [TITAN-ORBIT] Child names must start with familyId_ to count as a stat-bearing part.
                    if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string componentId = name.Substring(familyId.Length + 1);
                    if (string.IsNullOrWhiteSpace(componentId))
                        continue;

                    if (!family.TryGetStatsForComponent(componentId, out ShipComponentAbilityStats stats))
                        continue;

                    ShipComponentAbilityStats scaled = ShipComponentAbilityStatsMath.ScaleStatsByTransform(stats, t, componentId);
                    result.TotalStats.AddInPlace(scaled);
                    result.MatchedComponentIds.Add(componentId);
                    result.PerComponentStats.Add(scaled);
                }

                // [TITAN-ORBIT] Engines/thrusters share one move-speed pool — replace naive sum here.
                result.TotalStats = ShipPropulsionAggregation.ApplyPropulsionToSummedStats(
                    result.TotalStats,
                    result.MatchedComponentIds,
                    result.PerComponentStats,
                    shipLevel);
                result.TotalStats = family.ApplyStatFallbacks(result.TotalStats);
            }
            finally
            {
                if (destroyInstance && instance != null)
                    UnityEngine.Object.Destroy(instance);
            }

            return result;
        }

        /// <summary>Maps a baked <see cref="ShipFamilyPowerScoreBreakdown"/> back into ability-stat fields.</summary>
        public static ShipComponentAbilityStats BreakdownToBaseStats(ShipFamilyPowerScoreBreakdown breakdown)
        {
            return new ShipComponentAbilityStats
            {
                firePower = breakdown.firePower,
                bulletSpeed = breakdown.bulletSpeed,
                fireRate = breakdown.fireRate,
                rammingPower = breakdown.rammingPower,
                healthCap = breakdown.healthCap,
                healthRegen = breakdown.healthRegen,
                energyCap = breakdown.energyCap,
                energyRegen = breakdown.energyRegen,
                moveSpeed = breakdown.moveSpeed,
                turnSpeed = breakdown.turnSpeed,
                maxGems = breakdown.gemCap,
                maxPeople = breakdown.peopleCap,
            };
        }
    }
}
