using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Sums <see cref="ShipComponentAbilityStats"/> from upgrade-tree prefabs using the same rules as
    /// <see cref="Entities.ShipFamilyStatsPreview"/> (per-component <c>localScale</c> via
    /// <see cref="ShipComponentAbilityStats.ScaleStatsByTransform"/>).
    /// </summary>
    public static class ShipFamilyUpgradeTreeStatScanner
    {
        public static bool TryMeanStatsFromUpgradeTreePrefabs(ShipFamilyDefinition def, out ShipComponentAbilityStats meanStats, out int prefabCount, out string errorMessage)
        {
            meanStats = default;
            prefabCount = 0;
            errorMessage = null;

            if (def == null)
            {
                errorMessage = "No ShipFamilyDefinition.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(def.familyId))
            {
                errorMessage = "Family Id is empty.";
                return false;
            }

            if (def.upgradeTree == null || def.upgradeTree.Count == 0)
            {
                errorMessage = "Upgrade tree is empty. Build the upgrade tree first.";
                return false;
            }

            string familyId = def.familyId.Trim();
            var sum = new ShipComponentAbilityStats();

            foreach (var entry in def.upgradeTree)
            {
                if (entry?.prefab == null)
                    continue;

                sum.AddInPlace(SumStatsForPrefabAsset(entry.prefab, def, familyId));
                prefabCount++;
            }

            if (prefabCount == 0)
            {
                errorMessage = "No valid prefabs in the upgrade tree (assign prefabs to each tier).";
                return false;
            }

            meanStats = ShipComponentAbilityStatsMath.Multiply(sum, 1f / prefabCount);
            return true;
        }

        /// <summary>
        /// Loads prefab contents (nested prefab scales included) and returns scale-adjusted summed stats for power scoring.
        /// </summary>
        public static ShipComponentAbilityStats SumStatsForPrefabAsset(GameObject prefabAsset, ShipFamilyDefinition def, string familyId)
        {
            if (prefabAsset == null || def == null || string.IsNullOrEmpty(familyId))
                return default;

            string path = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(path))
                return SumStatsUnderRoot(prefabAsset, def, familyId);

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
                return default;

            try
            {
#if UNITY_EDITOR
                def.InvalidateComponentStatsLookup();
#endif
                return SumStatsUnderRoot(root, def, familyId);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Sums scale-adjusted stats for all transforms named <c>FamilyId_ComponentId</c> with a matching component entry.
        /// Non-weapons scale by average localScale (x+y+z)/3; weapons use average(x,y) for fire power and 1/z for fire rate.
        /// Engine/thruster move speed and acceleration use authored values (not scaled); other stats follow the same rules as runtime.
        /// </summary>
        public static ShipComponentAbilityStats SumStatsUnderRoot(GameObject root, ShipFamilyDefinition def, string familyId)
        {
            CollectStatsUnderRoot(root, def, familyId, out ShipComponentAbilityStats total, out var matchedIds, out var perComponentStats);
            return SumStatsAtShipLevelWithFallbacks(total, matchedIds, perComponentStats, shipLevel: 1, def);
        }

        /// <summary>Applies propulsion aggregation at the given ship level to an already-summed component total.</summary>
        public static ShipComponentAbilityStats SumStatsAtShipLevel(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> matchedIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel)
        {
            return ShipPropulsionAggregation.ApplyPropulsionToSummedStats(total, matchedIds, perComponentStats, shipLevel);
        }

        private static ShipComponentAbilityStats SumStatsAtShipLevelWithFallbacks(
            ShipComponentAbilityStats total,
            IReadOnlyList<string> matchedIds,
            IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel,
            ShipFamilyDefinition def)
        {
            ShipComponentAbilityStats summed = SumStatsAtShipLevel(total, matchedIds, perComponentStats, shipLevel);
            if (def == null)
                return summed;
            summed = def.ApplyStatFallbacks(summed);
            // [TITAN-ORBIT] Match runtime ShipFamilyStatsCalculator (fallbacks then family bonuses).
            return def.ApplySpecialBonuses(summed);
        }

        /// <inheritdoc cref="ShipFamilyPowerScoreBreakdown.GetMaxUpgradeCountForTier"/>
        public static int GetMaxUpgradeCountForTier(int minHomePlanetLevel) =>
            ShipFamilyPowerScoreBreakdown.GetMaxUpgradeCountForTier(minHomePlanetLevel);

        public struct StatMinMax
        {
            public float min;
            public float max;

            public StatMinMax(float min, float max)
            {
                this.min = min;
                this.max = max;
            }
        }

        /// <summary>Min/max effective stats for upgrade-tree inspector (matches ship upgrade menu categories).</summary>
        public struct UpgradeTreeStatPreview
        {
            public StatMinMax firePower;
            public StatMinMax bulletSpeed;
            public StatMinMax fireRate;
            public StatMinMax ramPower;
            public StatMinMax healthCap;
            public StatMinMax healthRegen;
            public StatMinMax energyCap;
            public StatMinMax energyRegen;
            public StatMinMax moveSpeed;
            public StatMinMax turnSpeed;
            public StatMinMax gemCap;
            public StatMinMax peopleCap;
            public StatMinMax powerScoreTotal;
        }

        public static bool TryGetUpgradeTreeStatPreview(
            GameObject prefabAsset,
            ShipFamilyDefinition def,
            string familyId,
            int minHomePlanetLevel,
            out UpgradeTreeStatPreview preview)
        {
            preview = default;
            if (prefabAsset == null || def == null || string.IsNullOrWhiteSpace(familyId))
                return false;

            if (!TryCollectSummedStats(prefabAsset, def, familyId.Trim(), out ShipComponentAbilityStats total, out var matchedIds, out var perComponentStats))
                return false;

            int maxUpgrades = GetMaxUpgradeCountForTier(minHomePlanetLevel);
            ShipComponentAbilityStats atMinLevel = SumStatsAtShipLevelWithFallbacks(total, matchedIds, perComponentStats, shipLevel: 1, def);

            preview.firePower = RangeFromPerLevel(atMinLevel.firePower, atMinLevel.firePowerPerLevel, maxUpgrades);
            preview.bulletSpeed = new StatMinMax(atMinLevel.bulletSpeed, atMinLevel.bulletSpeed);
            preview.fireRate = RangeFromPerLevel(atMinLevel.fireRate, atMinLevel.fireRatePerLevel, maxUpgrades);
            preview.ramPower = RangeFromPerLevel(atMinLevel.rammingPower, atMinLevel.rammingPowerPerLevel, maxUpgrades);
            preview.healthCap = RangeFromPerLevel(atMinLevel.healthCap, atMinLevel.healthCapPerLevel, maxUpgrades);
            preview.healthRegen = RangeFromPerLevel(atMinLevel.healthRegen, atMinLevel.healthRegenPerLevel, maxUpgrades);
            preview.energyCap = RangeFromPerLevel(atMinLevel.energyCap, atMinLevel.energyCapPerLevel, maxUpgrades);
            preview.energyRegen = RangeFromPerLevel(atMinLevel.energyRegen, atMinLevel.energyRegenPerLevel, maxUpgrades);
            preview.gemCap = RangeFromPerLevel(atMinLevel.maxGems, atMinLevel.maxGemsPerLevel, maxUpgrades);
            preview.peopleCap = RangeFromPerLevel(atMinLevel.maxPeople, atMinLevel.maxPeoplePerLevel, maxUpgrades);
            // Propulsion at high ship levels applies a mobility penalty at runtime; use base + per-level ├ù tier upgrades.
            preview.moveSpeed = RangeFromPerLevel(atMinLevel.moveSpeed, atMinLevel.moveSpeedPerLevel, maxUpgrades);
            preview.turnSpeed = RangeFromPerLevel(atMinLevel.turnSpeed, atMinLevel.turnSpeedPerLevel, maxUpgrades);
            preview.powerScoreTotal = new StatMinMax(
                ShipFamilyPowerScoreBreakdown.FromSummedShipStats(atMinLevel).Total,
                ShipFamilyPowerScoreBreakdown.FromSummedShipStats(
                    ShipFamilyPowerScoreBreakdown.ApplyMaxEffectiveLevels(atMinLevel, maxUpgrades)).Total);
            return true;
        }


        private static StatMinMax RangeFromPerLevel(float baseValue, float perLevel, int upgradeCount)
        {
            return new StatMinMax(baseValue, baseValue + perLevel * upgradeCount);
        }

        private static bool TryCollectSummedStats(
            GameObject prefabAsset,
            ShipFamilyDefinition def,
            string familyId,
            out ShipComponentAbilityStats total,
            out List<string> matchedIds,
            out List<ShipComponentAbilityStats> perComponentStats)
        {
            total = default;
            matchedIds = null;
            perComponentStats = null;

            string path = AssetDatabase.GetAssetPath(prefabAsset);
            GameObject root = !string.IsNullOrEmpty(path)
                ? PrefabUtility.LoadPrefabContents(path)
                : prefabAsset;

            if (root == null)
                return false;

            bool loadedContents = !string.IsNullOrEmpty(path);
            try
            {
#if UNITY_EDITOR
                def.InvalidateComponentStatsLookup();
#endif
                CollectStatsUnderRoot(root, def, familyId, out total, out matchedIds, out perComponentStats);
                return true;
            }
            finally
            {
                if (loadedContents)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void CollectStatsUnderRoot(
            GameObject root,
            ShipFamilyDefinition def,
            string familyId,
            out ShipComponentAbilityStats total,
            out List<string> matchedIds,
            out List<ShipComponentAbilityStats> perComponentStats)
        {
            total = new ShipComponentAbilityStats();
            matchedIds = new List<string>();
            perComponentStats = new List<ShipComponentAbilityStats>();

            if (root == null || def == null || string.IsNullOrEmpty(familyId))
                return;

            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                if (t == null)
                    continue;
                string name = t.name;
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!name.StartsWith(familyId + "_", StringComparison.OrdinalIgnoreCase))
                    continue;

                string componentId = name.Substring(familyId.Length + 1);
                if (string.IsNullOrWhiteSpace(componentId))
                    continue;

                if (!def.TryGetStatsForComponent(componentId, out var stats))
                    continue;

                ShipComponentAbilityStats scaled = ShipComponentAbilityStatsMath.ScaleStatsByTransform(stats, t, componentId);
                matchedIds.Add(componentId);
                perComponentStats.Add(scaled);
                total.AddInPlace(scaled);
            }
        }
    }
}
