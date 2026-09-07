using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Static stat summing from a chassis prefab hierarchy plus a <see cref="ShipFamilyDefinition"/>.
    /// Walks child transforms named <c>{familyId}_{part}</c> (catalog id is the full name), scales stats by transform size, applies
    /// propulsion aggregation, weapon projectile-speed max (not sum), then stat fallbacks. Weapon fire
    /// power / rate stay summed for power scores; live shots use per-mount combat from
    /// <c>ShipWeaponMountCombatLogic</c>. Shared by editor previews, power-score baking, and runtime UI.
    /// </summary>
    public static class ShipFamilyStatsCalculator
    {
        /// <summary>Per-component match list returned alongside the summed total.</summary>
        public struct SumResult
        {
            public ShipComponentAbilityStats TotalStats;
            public List<string> MatchedComponentIds;
            /// <summary>Scale-adjusted stats (catalog × prefab <c>localScale</c>) parallel to ids.</summary>
            public List<ShipComponentAbilityStats> PerComponentStats;
            /// <summary>
            /// Authored prefab child <c>localScale</c> parallel to ids.
            /// Moon-store extras use <c>(1,1,1)</c> — they have no chassis-prefab transform.
            /// Ability details cards read this so Base / PerExtra can show × starting scale.
            /// </summary>
            public List<Vector3> PerComponentLocalScales;
        }

        /// <summary>
        /// Sums prefab stats at level 1, then Extra Level at <paramref name="shipLevel"/> with
        /// zero ability purchases. Returns false when prefab or family is missing or the sum is all zero.
        /// </summary>
        public static bool TrySumFromPrefab(
            GameObject prefab,
            ShipFamilyDefinition family,
            int shipLevel,
            out ShipComponentAbilityStats effectiveAtLevel)
        {
            var zeroAbilities = default(ShipAbilityLevelCounts);
            return TrySumFromPrefab(prefab, family, shipLevel, in zeroAbilities, out effectiveAtLevel);
        }

        /// <summary>
        /// Same prefab scan as <see cref="TrySumFromPrefab(GameObject, ShipFamilyDefinition, int, out ShipComponentAbilityStats)"/>,
        /// then Extra Level with explicit ability purchases (use <see cref="ShipAbilityLevelCounts.Maxed"/>
        /// for a fully upgraded preview).
        /// </summary>
        public static bool TrySumFromPrefab(
            GameObject prefab,
            ShipFamilyDefinition family,
            int shipLevel,
            in ShipAbilityLevelCounts abilities,
            out ShipComponentAbilityStats effectiveAtLevel)
        {
            return TrySumFromPrefab(
                prefab, family, shipLevel, in abilities, out effectiveAtLevel, out _);
        }

        /// <summary>
        /// Same as <see cref="TrySumFromPrefab(GameObject, ShipFamilyDefinition, int, in ShipAbilityLevelCounts, out ShipComponentAbilityStats)"/>
        /// and also returns the scale-adjusted per-part list so callers can sum
        /// all-gun DPS (every mount Extra-Leveled, then <c>FP × RoF</c>).
        /// </summary>
        public static bool TrySumFromPrefab(
            GameObject prefab,
            ShipFamilyDefinition family,
            int shipLevel,
            in ShipAbilityLevelCounts abilities,
            out ShipComponentAbilityStats effectiveAtLevel,
            out SumResult rawParts)
        {
            effectiveAtLevel = default;
            rawParts = default;
            if (prefab == null || family == null)
                return false;

            // Raw parts at authored bases — Extra Level applies shipLevel + abilities below.
            rawParts = SumFromPrefabHierarchy(
                prefab, family, shipLevel: 1, applyPropulsionAndWeaponRules: false);
            if (rawParts.MatchedComponentIds == null || rawParts.MatchedComponentIds.Count == 0)
                return false;

            effectiveAtLevel = ShipComponentExtraLevelMath.AggregateAndEvaluate(
                rawParts.MatchedComponentIds,
                rawParts.PerComponentStats,
                shipLevel,
                in abilities);
            effectiveAtLevel = ShipComponentExtraLevelMath.ApplyMobilityPenalties(effectiveAtLevel, shipLevel);
            if (family != null)
            {
                effectiveAtLevel = family.ApplyStatFallbacks(effectiveAtLevel);
                effectiveAtLevel = family.ApplySpecialBonuses(effectiveAtLevel);
            }

            return !ShipComponentAbilityStatsMath.IsAllZero(effectiveAtLevel);
        }

        /// <summary>
        /// Core scan: walk prefab-asset children, match names, sum scaled stats.
        /// When <paramref name="applyPropulsionAndWeaponRules"/> is true (default), applies shared
        /// propulsion aggregation, weapon projectile-speed max, and family fallbacks.
        /// Pass false when the caller will append extra components (e.g. moon-store engines)
        /// and re-run aggregation on the combined list.
        /// </summary>
        public static SumResult SumFromPrefabHierarchy(
            GameObject prefab,
            ShipFamilyDefinition family,
            int shipLevel = 1,
            bool applyPropulsionAndWeaponRules = true)
        {
            // --- SumFromPrefabHierarchy ---
            var result = new SumResult
            {
                TotalStats = default,
                MatchedComponentIds = new List<string>(),
                PerComponentStats = new List<ShipComponentAbilityStats>(),
                PerComponentLocalScales = new List<Vector3>(),
            };

            if (prefab == null || family == null)
                return result;

            string familyId = !string.IsNullOrWhiteSpace(family.familyId)
                ? family.familyId.Trim()
                : string.Empty;
            if (string.IsNullOrEmpty(familyId))
                return result;

            // Walk the prefab asset. Do not Instantiate — dedicated Docker clones are stripped
            // (Dedicated Server Optimizations) so child names vanish and stats fall back to
            // family defaults. GetComponentsInChildren works on the asset; weapon bake already
            // uses this path.
            Transform root = prefab.transform;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null || t == root)
                    continue;

                string name = t.name;
                if (string.IsNullOrEmpty(name))
                    continue;
                // [TITAN-ORBIT] Child names must start with familyId_ to count as a stat-bearing part.
                if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                    continue;

                string componentId = ShipFamilyDefinition.NormalizeComponentId(name);
                if (string.IsNullOrWhiteSpace(componentId))
                    continue;

                if (!family.TryGetStatsForComponent(componentId, out ShipComponentAbilityStats stats))
                    continue;

                ShipComponentAbilityStats scaled = ShipComponentAbilityStatsMath.ScaleStatsByTransform(stats, t, componentId);
                result.TotalStats.AddInPlace(scaled);
                result.MatchedComponentIds.Add(componentId);
                result.PerComponentStats.Add(scaled);
                // [TITAN-ORBIT] Keep the authored start scale so HUD formula cards can show
                // catalog × scale (a Cockpit at 3× multiplies Health / Gems / Troops by 3).
                result.PerComponentLocalScales.Add(t.localScale);
            }

            if (applyPropulsionAndWeaponRules)
                ApplySharedAggregationRules(ref result, family, shipLevel);

            return result;
        }

        /// <summary>
        /// Appends moon-store-purchased components onto a prefab sum, then re-runs shared aggregation.
        /// Stack pools keep only the primary part; extras scale via Extra Level component count.
        /// </summary>
        public static SumResult AppendExtraComponentsAndAggregate(
            SumResult prefabSum,
            ShipFamilyDefinition family,
            IReadOnlyList<string> extraComponentIds,
            int shipLevel = 1)
        {
            // --- Append store extras then re-aggregate ---
            if (family == null)
                return prefabSum;

            // Start from a raw (pre-aggregation) copy when possible — caller should pass
            // SumFromPrefabHierarchy(..., applyPropulsionAndWeaponRules: false).
            var result = prefabSum;
            if (result.MatchedComponentIds == null)
                result.MatchedComponentIds = new List<string>();
            if (result.PerComponentStats == null)
                result.PerComponentStats = new List<ShipComponentAbilityStats>();
            if (result.PerComponentLocalScales == null)
                result.PerComponentLocalScales = new List<Vector3>();

            if (extraComponentIds != null)
            {
                for (int i = 0; i < extraComponentIds.Count; i++)
                {
                    string componentId = extraComponentIds[i];
                    if (string.IsNullOrWhiteSpace(componentId))
                        continue;
                    if (!family.TryGetStatsForComponent(componentId, out ShipComponentAbilityStats stats))
                        continue;

                    // [TITAN-ORBIT] Store buys have no prefab transform scale — catalog stats at ×1.
                    result.TotalStats.AddInPlace(stats);
                    result.MatchedComponentIds.Add(componentId);
                    result.PerComponentStats.Add(stats);
                    result.PerComponentLocalScales.Add(Vector3.one);
                }
            }

            ApplySharedAggregationRules(ref result, family, shipLevel);
            return result;
        }

        /// <summary>
        /// Shared post-sum rules: primary-per-pool aggregate, then family fallbacks / special bonuses.
        /// Extra Level ship/ability scaling is applied later by
        /// <see cref="ShipComponentExtraLevelMath.AggregateAndEvaluate"/>.
        /// </summary>
        public static void ApplySharedAggregationRules(ref SumResult result, ShipFamilyDefinition family, int shipLevel)
        {
            // --- Primary-only pools (extras counted later by Extra Level formula) ---
            _ = shipLevel;
            result.TotalStats = ShipComponentStackAggregation.AggregateAllPools(
                result.MatchedComponentIds,
                result.PerComponentStats);

            // [TITAN-ORBIT] Primary weapon already owns bullet speed/range — max helpers are no-ops
            // when only one weapon contributes, but keep them for mixed non-weapon speed sources.
            result.TotalStats = ShipComponentAbilityStatsMath.ApplyWeaponProjectileSpeedToSummedStats(
                result.TotalStats,
                result.MatchedComponentIds,
                result.PerComponentStats);
            result.TotalStats = ShipComponentAbilityStatsMath.ApplyWeaponBulletRangeToSummedStats(
                result.TotalStats,
                result.MatchedComponentIds,
                result.PerComponentStats);
            result.TotalStats = ShipComponentAbilityStatsMath.ApplyWeaponFirePowerToSummedStats(
                result.TotalStats,
                result.MatchedComponentIds,
                result.PerComponentStats);
            result.TotalStats = ShipComponentAbilityStatsMath.ApplyWeaponFireRateToSummedStats(
                result.TotalStats,
                result.MatchedComponentIds,
                result.PerComponentStats);
            if (family != null)
            {
                result.TotalStats = family.ApplyStatFallbacks(result.TotalStats);
                result.TotalStats = family.ApplySpecialBonuses(result.TotalStats);
            }
        }

        /// <summary>Maps a baked <see cref="ShipFamilyPowerScoreBreakdown"/> back into ability-stat fields.</summary>
        public static ShipComponentAbilityStats BreakdownToBaseStats(ShipFamilyPowerScoreBreakdown breakdown)
        {
            // --- BreakdownToBaseStats ---
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
