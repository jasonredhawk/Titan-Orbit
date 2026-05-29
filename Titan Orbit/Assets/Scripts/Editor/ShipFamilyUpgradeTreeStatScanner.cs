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

            meanStats = sum * (1f / prefabCount);
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
            var total = new ShipComponentAbilityStats();
            if (root == null || def == null || string.IsNullOrEmpty(familyId))
                return total;

            var matchedIds = new List<string>();
            var perComponentStats = new List<ShipComponentAbilityStats>();

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

                if (def.TryGetStatsForComponent(componentId, out var stats))
                {
                    ShipComponentAbilityStats scaled = ShipComponentAbilityStats.ScaleStatsByTransform(stats, t, componentId);
                    matchedIds.Add(componentId);
                    perComponentStats.Add(scaled);
                    total.AddInPlace(scaled);
                }
            }

            return ShipPropulsionAggregation.ApplyPropulsionToSummedStats(total, matchedIds, perComponentStats, shipLevel: 1);
        }
    }
}
