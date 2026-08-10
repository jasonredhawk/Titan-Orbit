using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Batch rebalance for engine Energy Cap/Regen, thruster turn, and weapon energy clear.
    /// Runs the same post-Scan helpers as Recalculate on every <see cref="ShipFamilyDefinition"/> under Prefabs/Ships,
    /// then refreshes chassis <c>powerScoreBreakdown</c> energy so the upgrade-tree UI matches.
    /// Engine Cap/Regen mirrors the old weapon pools (max Fire Power attribute shots + 35% regen).
    /// Menu: TitanOrbit → Ship Families → Rebalance Engine Energy + Thruster Turn.
    /// </summary>
    public static class ShipFamilyEnergyPropulsionRebalanceMenu
    {
        const string MenuPath = "TitanOrbit/Ship Families/Rebalance Engine Energy + Thruster Turn";

        /// <summary>
        /// Finds all ship family assets, refreshes categories from part roles, recalculates profile
        /// stats where possible, then applies thruster turn + engine energy + weapon energy clear,
        /// and rewrites chassis power-score energy fields without resorting the upgrade tree.
        /// </summary>
        [MenuItem(MenuPath)]
        public static void RebalanceAllShipFamilies()
        {
            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition", new[] { "Assets/Prefabs/Ships" });
            if (guids == null || guids.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "Rebalance Ship Families",
                    "No ShipFamilyDefinition assets found under Assets/Prefabs/Ships.",
                    "OK");
                return;
            }

            var profileSet = ShipFamilyPartCalcProfileSetEditorUtility.FindOrLoadShared();
            int updated = 0;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def?.components == null || def.components.Count == 0)
                    continue;

                Undo.RecordObject(def, "Rebalance Engine Energy + Thruster Turn");

                // --- Refresh categories to new roles (engines+Energy, weapons Offense-only) ---
                for (int c = 0; c < def.components.Count; c++)
                {
                    ShipFamilyComponentEntry entry = def.components[c];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                        continue;

                    // Cosmetics: keep zero stats / empty categories when profile says so.
                    if (profileSet != null && !profileSet.ContributesAbilityStats(entry.componentId))
                    {
                        entry.statCategories.Clear();
                        entry.stats = default;
                        continue;
                    }

                    entry.statCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(entry.componentId);

                    if (profileSet != null)
                    {
                        entry.stats = profileSet.SuggestStatsForComponent(entry.componentId, entry.statCategories);
                        entry.enablePropulsionVfx = profileSet.ShouldEnablePropulsionVfx(
                            entry.componentId, out float scale);
                        entry.propulsionVfxScale = scale;
                    }
                }

                // [TITAN-ORBIT] Weapons Cap-only (battery); engines Cap+Regen (plant sized from fire drain).
                ShipPropulsionAggregation.BalanceWeaponEnergyForComponents(def.components);
                ShipPropulsionAggregation.ApplyThrusterTurnSuggestionsForComponents(def.components);
                ShipPropulsionAggregation.ApplyEngineOverdriveSuggestionsForComponents(
                    def.components,
                    overwriteExisting: true);
                ShipPropulsionAggregation.BalanceEngineEnergyForComponents(def.components);
                def.EnforceComponentStatCategories();
                def.InvalidateComponentStatsLookup();

                // --- Refresh chassis power-score energy so upgrade-tree UI matches component Cap/Regen ---
                // [EDITOR] Does not resort the tree — only rewrites powerScoreBreakdown from summed parts.
                string familyId = def.familyId != null ? def.familyId.Trim() : string.Empty;
                if (!string.IsNullOrEmpty(familyId) && def.upgradeTree != null)
                {
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

                EditorUtility.SetDirty(def);
                updated++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Rebalance Ship Families",
                $"Updated {updated} family asset(s).\n" +
                "Engines: Energy Cap/Regen (fleet pool from fire drain).\n" +
                "Weapons: Energy Cap only (extra storage; no Regen).\n" +
                "Engines: ExtraSpeed OVERDRIVE knobs (drain/sec = ExtraSpeedEnergyDrain).",
                "OK");

            Debug.Log($"[ShipFamilyEnergyPropulsionRebalance] Updated {updated} ShipFamilyDefinition asset(s).");
        }
    }
}
