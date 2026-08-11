using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Static stat summing from a chassis prefab hierarchy plus a <see cref="ShipFamilyDefinition"/>.
    /// Walks child transforms named <c>{familyId}_{componentId}</c>, skips nested duplicate part meshes
    /// (same idea as chassis weapon-mount bake), scales stats by clamped transform size, then applies
    /// stack pools, weapon projectile-speed max (not sum), and family fallbacks.
    /// Weapon fire power / rate stay pool-summed for power scores; live shots use per-mount combat from
    /// <c>ShipWeaponMountCombatLogic</c>. Shared by editor previews, power-score baking, and runtime UI.
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

            effectiveAtLevel = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(
                sum.TotalStats,
                shipLevel,
                family.ResolveShipLevelStatGrowthFraction());
            return true;
        }

        /// <summary>
        /// Core scan: instantiate prefab if needed, match children, sum scaled stats.
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
                // --- Phase 1: collect scaled part matches (skip nested duplicate meshes) ---
                var ids = new List<string>(32);
                var scaledStats = new List<ShipComponentAbilityStats>(32);
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

                    // [TITAN-ORBIT] Nested mesh children often reuse the same Family_Weapon name.
                    // Live mount bake collapses those into one body — power-score sum must too.
                    if (IsNestedDuplicatePartTransform(t, familyId, componentId))
                        continue;

                    if (!family.TryGetStatsForComponent(componentId, out ShipComponentAbilityStats stats))
                        continue;

                    ShipComponentAbilityStats scaled = ShipComponentAbilityStatsMath.ScaleStatsByTransform(stats, t, componentId);
                    ids.Add(componentId);
                    scaledStats.Add(scaled);
                }

                // --- Phase 2: one weapon row per component id (keep strongest barrel) ---
                // Prefabs often place several sibling Missile/Weapon meshes with the same id.
                // Stacking them all created overgunned outliers; CountParts already uses a HashSet.
                CollapseDuplicateWeaponComponentIds(ids, scaledStats);

                for (int i = 0; i < ids.Count; i++)
                {
                    result.TotalStats.AddInPlace(scaledStats[i]);
                    result.MatchedComponentIds.Add(ids[i]);
                    result.PerComponentStats.Add(scaledStats[i]);
                }

                if (applyPropulsionAndWeaponRules)
                    ApplySharedAggregationRules(ref result, family, shipLevel);
            }
            finally
            {
                if (destroyInstance && instance != null)
                    UnityEngine.Object.Destroy(instance);
            }

            return result;
        }

        /// <summary>
        /// Appends moon-store-purchased components onto a prefab sum, then re-runs shared aggregation.
        /// Stack pools use primary ×1 + extras × extraStackWeight of their own stats.
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

            if (extraComponentIds != null)
            {
                for (int i = 0; i < extraComponentIds.Count; i++)
                {
                    string componentId = extraComponentIds[i];
                    if (string.IsNullOrWhiteSpace(componentId))
                        continue;
                    if (!family.TryGetStatsForComponent(componentId, out ShipComponentAbilityStats stats))
                        continue;

                    // [TITAN-ORBIT] Store buys have no prefab transform scale — use catalog base stats.
                    result.TotalStats.AddInPlace(stats);
                    result.MatchedComponentIds.Add(componentId);
                    result.PerComponentStats.Add(stats);
                }
            }

            ApplySharedAggregationRules(ref result, family, shipLevel);
            return result;
        }

        /// <summary>
        /// Keeps at most one weapon entry per canonical component id (highest firePower×fireRate).
        /// Non-weapon parts are left unchanged so mirrored engines / stacked wings still stack.
        /// </summary>
        /// <param name="ids">Parallel component ids (mutated in place).</param>
        /// <param name="stats">Parallel scaled stats (mutated in place).</param>
        static void CollapseDuplicateWeaponComponentIds(
            List<string> ids,
            List<ShipComponentAbilityStats> stats)
        {
            if (ids == null || stats == null || ids.Count <= 1)
                return;

            int count = Mathf.Min(ids.Count, stats.Count);
            var bestIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string id = ids[i];
                if (string.IsNullOrWhiteSpace(id) || !ShipComponentAbilityStats.IsWeaponComponent(id))
                    continue;

                string key = ShipFamilyDefinition.NormalizeComponentId(id);
                if (string.IsNullOrEmpty(key))
                    key = id.Trim();

                float dps = Mathf.Max(0f, stats[i].firePower) * Mathf.Max(0f, stats[i].fireRate);
                if (!bestIndexById.TryGetValue(key, out int prev) || dps > Mathf.Max(0f, stats[prev].firePower) * Mathf.Max(0f, stats[prev].fireRate))
                    bestIndexById[key] = i;
            }

            if (bestIndexById.Count == 0)
                return;

            var keepWeapon = new HashSet<int>();
            foreach (var pair in bestIndexById)
                keepWeapon.Add(pair.Value);

            for (int i = count - 1; i >= 0; i--)
            {
                if (!ShipComponentAbilityStats.IsWeaponComponent(ids[i]))
                    continue;
                if (keepWeapon.Contains(i))
                    continue;
                ids.RemoveAt(i);
                stats.RemoveAt(i);
            }
        }

        /// <summary>
        /// True when <paramref name="t"/> sits under another transform that already owns the same
        /// part id (or a parent weapon body for a weapon child). Skips tip/LOD meshes that would
        /// otherwise multi-count the same barrel in power-score sums.
        /// </summary>
        /// <param name="t">Candidate part transform under the chassis prefab.</param>
        /// <param name="familyId">Family id prefix used in child names.</param>
        /// <param name="componentId">Component id parsed from <paramref name="t"/>'s name.</param>
        static bool IsNestedDuplicatePartTransform(Transform t, string familyId, string componentId)
        {
            if (t == null || string.IsNullOrEmpty(familyId) || string.IsNullOrWhiteSpace(componentId))
                return false;

            string canonical = ShipFamilyDefinition.NormalizeComponentId(componentId);
            bool childIsWeapon = ShipComponentAbilityStats.IsWeaponComponent(componentId);
            Transform parent = t.parent;
            while (parent != null)
            {
                string parentName = parent.name;
                if (!string.IsNullOrEmpty(parentName)
                    && parentName.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                {
                    string parentId = parentName.Substring(familyId.Length + 1);
                    string parentCanonical = ShipFamilyDefinition.NormalizeComponentId(parentId);

                    // Same part id nested under itself (Weapon / Weapon tip mesh).
                    if (!string.IsNullOrEmpty(canonical)
                        && string.Equals(canonical, parentCanonical, StringComparison.OrdinalIgnoreCase))
                        return true;

                    // Weapon tip under a differently named weapon body (Ammunition under Machinegun).
                    if (childIsWeapon && ShipComponentAbilityStats.IsWeaponComponent(parentId))
                        return true;
                }

                parent = parent.parent;
            }

            return false;
        }

        /// <summary>
        /// Shared post-sum rules: extra-stack pools, weapon projectile speed max, fire power/rate notes, fallbacks.
        /// </summary>
        public static void ApplySharedAggregationRules(ref SumResult result, ShipFamilyDefinition family, int shipLevel)
        {
            // --- Extra stack weight pools (primary ×1 + extras × weight) ---
            // [TITAN-ORBIT] Rebuilds hull totals from per-part lists. Engines+Thrusters share
            // Propulsion; other types pool separately. Replaces naive field-wise Add for stackables.
            _ = shipLevel;
            result.TotalStats = ShipComponentStackAggregation.AggregateAllPools(
                result.MatchedComponentIds,
                result.PerComponentStats);

            // [TITAN-ORBIT] Bullet speed is per-projectile — max across weapons, never N× sum.
            result.TotalStats = ShipComponentAbilityStatsMath.ApplyWeaponProjectileSpeedToSummedStats(
                result.TotalStats,
                result.MatchedComponentIds,
                result.PerComponentStats);
            // [TITAN-ORBIT] Bullet range is per-projectile — max across weapons (grows with ship level).
            result.TotalStats = ShipComponentAbilityStatsMath.ApplyWeaponBulletRangeToSummedStats(
                result.TotalStats,
                result.MatchedComponentIds,
                result.PerComponentStats);
            // [TITAN-ORBIT] Per-bullet damage lives on each mount — hull sum stays for power score.
            result.TotalStats = ShipComponentAbilityStatsMath.ApplyWeaponFirePowerToSummedStats(
                result.TotalStats,
                result.MatchedComponentIds,
                result.PerComponentStats);
            // [TITAN-ORBIT] Per-barrel cadence lives on each mount — hull sum stays for power score.
            result.TotalStats = ShipComponentAbilityStatsMath.ApplyWeaponFireRateToSummedStats(
                result.TotalStats,
                result.MatchedComponentIds,
                result.PerComponentStats);
            if (family != null)
            {
                result.TotalStats = family.ApplyStatFallbacks(result.TotalStats);
                // [TITAN-ORBIT] Per-family special bonuses after aggregation + fallbacks.
                result.TotalStats = family.ApplySpecialBonuses(result.TotalStats);
            }

            // --- Engine ↔ weapon complementarity (live drain after mesh scale) ---
            // Family-table engine Cap/Regen cannot see prefab scale. Re-fit hull energy so
            // Cap ≈ 3s of this chassis's fire and Regen ≈ 30% of drain (designer request).
            result.TotalStats = ApplyEnergyComplementarityToSummedStats(result.TotalStats);
        }

        /// <summary>
        /// Rewrites hull <c>energyCap</c> / <c>energyRegen</c> from summed firePower × fireRate
        /// so every armed chassis matches <see cref="GameBalanceTargets"/> battery seconds and
        /// regen fraction. Unarmed hulls keep authored engine fallback energy.
        /// </summary>
        /// <param name="total">Aggregated hull stats (weapons + engines already pooled).</param>
        /// <returns>Stats with energy fields fitted to the combat-loop targets.</returns>
        public static ShipComponentAbilityStats ApplyEnergyComplementarityToSummedStats(
            ShipComponentAbilityStats total)
        {
            float firePower = Mathf.Max(0f, total.firePower);
            float fireRate = Mathf.Max(0f, total.fireRate);
            if (firePower <= 0.0001f || fireRate <= 0.0001f)
                return total;

            float drain = ShipComponentWeaponSuggestions.ComputeSustainedEnergyDrain(firePower, fireRate);
            if (drain <= 0.0001f)
                return total;

            total.energyCap = drain * GameBalanceTargets.EnergyBatterySecondsOfSustainedFire;
            total.energyRegen = drain * GameBalanceTargets.EnergyRegenFractionOfSustainedDrain;
            total.energyCapPerAbilityLevel = total.energyCap * ShipPropulsionAggregation.PerLevelFractionOfBase;
            total.energyRegenPerAbilityLevel = total.energyRegen * ShipPropulsionAggregation.PerLevelFractionOfBase;
            return total;
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
