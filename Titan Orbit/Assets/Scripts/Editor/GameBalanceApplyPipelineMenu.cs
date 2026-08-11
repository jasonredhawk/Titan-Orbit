using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] End-to-end balance apply: reset ProfileSet from <see cref="GameBalanceTargets"/> seeds,
    /// push stats + energy/thruster rebalance onto every ShipFamilyDefinition, refresh power scores,
    /// and optionally resort upgrade trees. Menu: TitanOrbit → Balance → Apply Seeds And Rebalance All Families.
    /// </summary>
    public static class GameBalanceApplyPipelineMenu
    {
        const string ApplyMenu = "TitanOrbit/Balance/Apply Seeds And Rebalance All Families";
        const string ApplyAndResortMenu = "TitanOrbit/Balance/Apply Seeds, Rebalance, And Resort Upgrade Trees";
        const string ApplySilentMenu = "TitanOrbit/Balance/Apply Seeds And Rebalance All Families (Silent)";
        const string ApplyResortSilentMenu = "TitanOrbit/Balance/Apply Seeds Rebalance Resort (Silent)";
        const string TuneAsteroidMenu = "TitanOrbit/Balance/Tune AsteroidSettings From Fleet Median DPS";
        const string TuneAsteroidSilentMenu = "TitanOrbit/Balance/Tune AsteroidSettings From Fleet Median DPS (Silent)";

        /// <summary>
        /// Resets shared ProfileSet from code seeds, recalculates every family component table,
        /// runs engine/weapon energy balancers, refreshes chassis power-score energy. Does not resort trees.
        /// </summary>
        [MenuItem(ApplyMenu)]
        public static void ApplySeedsAndRebalanceAllFamilies()
        {
            if (!EditorUtility.DisplayDialog(
                    "Apply Balance Seeds",
                    "Reset ShipFamilyPartCalcProfileSet from GameBalanceTargets / suggestion seeds, " +
                    "then recalculate + energy-rebalance all ship families under Prefabs/Ships?\n\n" +
                    "This overwrites component ability stats on family assets.",
                    "Apply",
                    "Cancel"))
                return;

            int families = RunPipeline(resortTrees: false, tuneAsteroids: false);
            EditorUtility.DisplayDialog(
                "Apply Balance Seeds",
                $"Updated ProfileSet + {families} family asset(s). Re-run Balance reports to verify.",
                "OK");
        }

        /// <summary>[EDITOR] No confirmation — for MCP / batch automation.</summary>
        [MenuItem(ApplySilentMenu)]
        public static void ApplySeedsAndRebalanceAllFamiliesSilent()
        {
            int families = RunPipeline(resortTrees: false, tuneAsteroids: false);
            Debug.Log($"[GameBalance] Silent apply finished: {families} families.");
        }

        /// <summary>Same as Apply, then resorts each family’s upgrade tree by ascending power score.</summary>
        [MenuItem(ApplyAndResortMenu)]
        public static void ApplySeedsRebalanceAndResort()
        {
            if (!EditorUtility.DisplayDialog(
                    "Apply + Resort",
                    "Reset ProfileSet, rebalance all families, AND resort upgrade trees by power score?\n\n" +
                    "Locked tree entries keep their index; unlocked tiers are re-leveled.",
                    "Apply + Resort",
                    "Cancel"))
                return;

            int families = RunPipeline(resortTrees: true, tuneAsteroids: false);
            EditorUtility.DisplayDialog(
                "Apply + Resort",
                $"Updated ProfileSet + {families} family asset(s) (trees resorted). Re-run Balance reports.",
                "OK");
        }

        /// <summary>[EDITOR] No confirmation — apply + resort for MCP / batch.</summary>
        [MenuItem(ApplyResortSilentMenu)]
        public static void ApplySeedsRebalanceAndResortSilent()
        {
            int families = RunPipeline(resortTrees: true, tuneAsteroids: false);
            Debug.Log($"[GameBalance] Silent apply+resort finished: {families} families.");
        }

        /// <summary>
        /// Sets AsteroidSettings.HealthPerSize so mid-rock TTK ≈ ideal seconds at current fleet median L1 DPS.
        /// GemsPerSize left unchanged unless Health change would make gem/HP ratio extreme.
        /// </summary>
        [MenuItem(TuneAsteroidMenu)]
        public static void TuneAsteroidSettingsFromFleetDps()
        {
            if (!TryComputeSuggestedHealthPerSize(out float medDps, out float suggested, out string error))
            {
                EditorUtility.DisplayDialog("Tune Asteroids", error ?? "Failed.", "OK");
                return;
            }

            var asteroid = Resources.Load<AsteroidSettings>("AsteroidSettings");
            if (asteroid == null)
            {
                EditorUtility.DisplayDialog(
                    "Tune Asteroids",
                    "Resources/AsteroidSettings.asset not found.",
                    "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Tune Asteroids",
                    $"Median L1 DPS={medDps:0.##}\n" +
                    $"Current HealthPerSize={asteroid.HealthPerSize:0.###}\n" +
                    $"Suggested HealthPerSize={suggested:0.###}\n\n" +
                    "Apply to AsteroidSettings.asset?",
                    "Apply",
                    "Cancel"))
                return;

            ApplyHealthPerSize(asteroid, suggested, medDps);
            EditorUtility.DisplayDialog("Tune Asteroids", $"HealthPerSize set to {suggested:0.###}.", "OK");
        }

        /// <summary>[EDITOR] No confirmation — write suggested HealthPerSize from fleet median L1 DPS.</summary>
        [MenuItem(TuneAsteroidSilentMenu)]
        public static void TuneAsteroidSettingsFromFleetDpsSilent()
        {
            if (!TryComputeSuggestedHealthPerSize(out float medDps, out float suggested, out string error))
            {
                Debug.LogError("[GameBalance] Tune asteroids failed: " + error);
                return;
            }

            var asteroid = Resources.Load<AsteroidSettings>("AsteroidSettings");
            if (asteroid == null)
            {
                Debug.LogError("[GameBalance] AsteroidSettings missing.");
                return;
            }

            ApplyHealthPerSize(asteroid, suggested, medDps);
        }

        static bool TryComputeSuggestedHealthPerSize(out float medDps, out float suggested, out string error)
        {
            medDps = 0f;
            suggested = 0f;
            var rows = GameBalanceFleetAnalyzer.ScanAllChassis(out error);
            if (rows.Count == 0)
                return false;

            var l1 = rows.FindAll(r => r.ShipLevel == 1);
            if (l1.Count == 0)
                l1 = rows;
            medDps = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Dps);
            suggested = GameBalanceTargets.SuggestHealthPerSizeForMidRock(medDps);
            return true;
        }

        static void ApplyHealthPerSize(AsteroidSettings asteroid, float suggested, float medDps)
        {
            Undo.RecordObject(asteroid, "Tune Asteroid HealthPerSize");
            asteroid.HealthPerSize = suggested;
            EditorUtility.SetDirty(asteroid);
            AssetDatabase.SaveAssets();
            Debug.Log($"[GameBalance] AsteroidSettings.HealthPerSize → {suggested:0.###} (median L1 DPS {medDps:0.##})");
        }

        /// <summary>Core pipeline used by menu items.</summary>
        public static int RunPipeline(bool resortTrees, bool tuneAsteroids)
        {
            var profileSet = ShipFamilyPartCalcProfileSetEditorUtility.FindOrLoadShared();
            if (profileSet == null)
            {
                Debug.LogError("[GameBalance] Missing ShipFamilyPartCalcProfileSet.");
                return 0;
            }

            // --- Reset part profiles from GameBalanceTargets-backed seeds ---
            Undo.RecordObject(profileSet, "Reset Part Profiles From Balance Seeds");
            profileSet.ResetPartProfilesToCodeDefaults();
            EditorUtility.SetDirty(profileSet);

            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition", new[] { "Assets/Prefabs/Ships" });
            int updated = 0;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def?.components == null || def.components.Count == 0)
                    continue;

                Undo.RecordObject(def, "Apply Balance Seeds To Ship Family");

                // --- Push ProfileSet stats onto every component ---
                for (int c = 0; c < def.components.Count; c++)
                {
                    ShipFamilyComponentEntry entry = def.components[c];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                        continue;

                    if (!profileSet.ContributesAbilityStats(entry.componentId))
                    {
                        entry.statCategories.Clear();
                        entry.stats = default;
                        continue;
                    }

                    entry.statCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(entry.componentId);
                    entry.stats = profileSet.SuggestStatsForComponent(entry.componentId, entry.statCategories);
                    entry.enablePropulsionVfx = profileSet.ShouldEnablePropulsionVfx(
                        entry.componentId, out float scale);
                    entry.propulsionVfxScale = scale;
                }

                // [TITAN-ORBIT] Same post-Scan balancers as energy rebalance menu.
                ShipPropulsionAggregation.BalanceWeaponEnergyForComponents(def.components);
                ShipPropulsionAggregation.ApplyThrusterTurnSuggestionsForComponents(def.components);
                ShipPropulsionAggregation.ApplyEngineOverdriveSuggestionsForComponents(
                    def.components,
                    overwriteExisting: true);
                ShipPropulsionAggregation.BalanceEngineEnergyForComponents(def.components);
                def.EnforceComponentStatCategories();
                def.InvalidateComponentStatsLookup();

                RefreshPowerScores(def);

                if (resortTrees)
                    ResortUpgradeTreeSilent(def);

                EditorUtility.SetDirty(def);
                updated++;
            }

            if (tuneAsteroids)
            {
                var rows = GameBalanceFleetAnalyzer.ScanAllChassis(out _);
                var l1 = rows.FindAll(r => r.ShipLevel == 1);
                if (l1.Count == 0)
                    l1 = rows;
                if (l1.Count > 0)
                {
                    float medDps = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Dps);
                    float suggested = GameBalanceTargets.SuggestHealthPerSizeForMidRock(medDps);
                    var asteroid = Resources.Load<AsteroidSettings>("AsteroidSettings");
                    if (asteroid != null)
                    {
                        Undo.RecordObject(asteroid, "Tune Asteroid HealthPerSize");
                        asteroid.HealthPerSize = suggested;
                        EditorUtility.SetDirty(asteroid);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(
                $"[GameBalance] Pipeline complete: ProfileSet reset, {updated} families updated" +
                (resortTrees ? ", trees resorted" : string.Empty) + ".");
            return updated;
        }

        /// <summary>Rewrites chassis powerScoreBreakdown from summed prefab stats (no resort).</summary>
        static void RefreshPowerScores(ShipFamilyDefinition def)
        {
            string familyId = def.familyId != null ? def.familyId.Trim() : string.Empty;
            if (string.IsNullOrEmpty(familyId) || def.upgradeTree == null)
                return;

            for (int t = 0; t < def.upgradeTree.Count; t++)
            {
                ShipFamilyChassisTierEntry tier = def.upgradeTree[t];
                if (tier?.prefab == null)
                    continue;

                ShipComponentAbilityStats summed =
                    ShipFamilyUpgradeTreeStatScanner.SumStatsForPrefabAsset(tier.prefab, def, familyId);
                ShipFamilyPowerScoreBreakdown breakdown =
                    ShipFamilyPowerScoreBreakdown.FromSummedShipStats(summed);
                tier.powerScoreBreakdown = breakdown;
                tier.powerScore = breakdown.GetUpgradeTreeSortPowerScore();
                int maxUpgrades = ShipFamilyPowerScoreBreakdown.GetMaxUpgradeCountForTier(
                    tier.minHomePlanetLevel);
                tier.powerScoreAtMaxLevel = ShipFamilyPowerScoreBreakdown.FromSummedShipStats(
                    ShipFamilyPowerScoreBreakdown.ApplyMaxEffectiveLevels(summed, maxUpgrades)).Total;
            }
        }

        /// <summary>
        /// Silent copy of upgrade-tree resort (no dialogs). Locked tiers keep index; unlocked
        /// prefab tiers are ordered by ascending power into triangular level rows.
        /// </summary>
        static void ResortUpgradeTreeSilent(ShipFamilyDefinition def)
        {
            string familyId = def.familyId != null ? def.familyId.Trim() : string.Empty;
            if (string.IsNullOrEmpty(familyId) || def.upgradeTree == null || def.upgradeTree.Count == 0)
                return;

            int treeCount = def.upgradeTree.Count;
            var unlockedWithPrefab =
                new List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
            var trailingNoPrefab = new List<ShipFamilyChassisTierEntry>();

            for (int i = 0; i < treeCount; i++)
            {
                ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                if (tier == null)
                    continue;

                if (tier.prefab == null)
                {
                    if (!tier.lockedInUpgradeTree)
                        trailingNoPrefab.Add(tier);
                    continue;
                }

                ShipComponentAbilityStats stats =
                    ShipFamilyUpgradeTreeStatScanner.SumStatsForPrefabAsset(tier.prefab, def, familyId);
                ShipFamilyPowerScoreBreakdown breakdown =
                    ShipFamilyPowerScoreBreakdown.FromSummedShipStats(stats);
                float power = breakdown.GetUpgradeTreeSortPowerScore();
                tier.powerScoreBreakdown = breakdown;
                tier.componentMass = def.ComputeComponentMassFromPrefab(tier.prefab);

                if (!tier.lockedInUpgradeTree)
                    unlockedWithPrefab.Add((tier, power, breakdown));
            }

            if (unlockedWithPrefab.Count == 0)
                return;

            unlockedWithPrefab.Sort((a, b) => a.power.CompareTo(b.power));

            // Triangular levels with gem-cost descending within each row (matches DefinitionEditor).
            var ordered = new List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
            int listIdx = 0;
            int chunkSize = 1;
            int level = 1;
            while (listIdx < unlockedWithPrefab.Count)
            {
                var chunk =
                    new List<(ShipFamilyChassisTierEntry entry, float power, ShipFamilyPowerScoreBreakdown breakdown)>();
                for (int c = 0; c < chunkSize && listIdx < unlockedWithPrefab.Count; c++)
                    chunk.Add(unlockedWithPrefab[listIdx++]);

                int levelForCost = level;
                chunk.Sort((a, b) =>
                {
                    int costA = ShipFamilyPowerScoreBreakdown.GetPurchaseGemCost(a.entry, levelForCost);
                    int costB = ShipFamilyPowerScoreBreakdown.GetPurchaseGemCost(b.entry, levelForCost);
                    int cmp = costB.CompareTo(costA);
                    if (cmp != 0) return cmp;
                    cmp = b.power.CompareTo(a.power);
                    if (cmp != 0) return cmp;
                    string idA = a.entry?.chassisId ?? a.entry?.prefab?.name ?? string.Empty;
                    string idB = b.entry?.chassisId ?? b.entry?.prefab?.name ?? string.Empty;
                    return string.Compare(idA, idB, System.StringComparison.OrdinalIgnoreCase);
                });
                ordered.AddRange(chunk);
                chunkSize++;
                level++;
            }

            var newTree = new List<ShipFamilyChassisTierEntry>(treeCount + trailingNoPrefab.Count);
            int unlockedIdx = 0;
            int currentLevel = 1;
            int shipsAtCurrentLevel = 1;
            int assignedAtThisLevel = 0;

            for (int i = 0; i < treeCount; i++)
            {
                ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                if (tier == null)
                {
                    newTree.Add(null);
                    continue;
                }

                ShipFamilyChassisTierEntry entry;
                if (tier.lockedInUpgradeTree)
                {
                    entry = tier;
                }
                else if (tier.prefab != null)
                {
                    if (unlockedIdx >= ordered.Count)
                        continue;
                    var (sortedEntry, power, breakdown) = ordered[unlockedIdx++];
                    entry = sortedEntry;
                    entry.powerScore = power;
                    entry.powerScoreBreakdown = breakdown;
                }
                else
                {
                    continue;
                }

                if (assignedAtThisLevel >= shipsAtCurrentLevel)
                {
                    currentLevel++;
                    shipsAtCurrentLevel++;
                    assignedAtThisLevel = 0;
                }

                entry.minHomePlanetLevel = currentLevel;
                newTree.Add(entry);
                assignedAtThisLevel++;
            }

            for (int i = 0; i < trailingNoPrefab.Count; i++)
                newTree.Add(trailingNoPrefab[i]);

            def.upgradeTree = newTree;
            def.RecalculateTotalComponentMass();
        }
    }
}
