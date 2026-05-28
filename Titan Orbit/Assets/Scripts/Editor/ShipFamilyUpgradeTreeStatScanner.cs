using System;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Sums <see cref="ShipComponentAbilityStats"/> from upgrade-tree prefabs using the same rules as the ship family editor.
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
                string path = AssetDatabase.GetAssetPath(entry.prefab);
                if (string.IsNullOrEmpty(path))
                    continue;

                GameObject root = PrefabUtility.LoadPrefabContents(path);
                if (root == null)
                    continue;

                try
                {
                    sum.AddInPlace(SumStatsUnderRoot(root, def, familyId));
                    prefabCount++;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            if (prefabCount == 0)
            {
                errorMessage = "No valid prefabs in the upgrade tree (assign prefabs to each tier).";
                return false;
            }

            meanStats = sum * (1f / prefabCount);
            return true;
        }

        /// <summary>Sums scaled stats for all children named <c>FamilyId_ComponentId</c> with a matching component entry.</summary>
        public static ShipComponentAbilityStats SumStatsUnderRoot(GameObject root, ShipFamilyDefinition def, string familyId)
        {
            var total = new ShipComponentAbilityStats();
            if (root == null || def == null || string.IsNullOrEmpty(familyId))
                return total;

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
                    total.AddInPlace(scaled);
                }
            }

            return total;
        }
    }
}
