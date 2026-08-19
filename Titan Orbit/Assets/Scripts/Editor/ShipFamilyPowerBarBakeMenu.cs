using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Bakes <see cref="ShipFamilyChassisTierEntry.powerScoreBreakdownAtShipLevel"/>
    /// on every family under Prefabs/Ships. Extra Level at each chassis's tree level,
    /// every HUD ability maxed. Does not resort the tree or rewrite level-1
    /// <c>powerScoreBreakdown</c>. Menu: TitanOrbit → Ship Families → Bake Upgrade-Tree Power Bars At Ship Level.
    /// </summary>
    public static class ShipFamilyPowerBarBakeMenu
    {
        const string MenuPath = "TitanOrbit/Ship Families/Bake Upgrade-Tree Power Bars At Ship Level";

        /// <summary>Walks all ship family assets and bakes at-ship-level power-bar breakdowns.</summary>
        [MenuItem(MenuPath)]
        public static void BakeAllFamilies()
        {
            BakeAllFamiliesCore(out int updated, out int tiers, out string error);
            if (!string.IsNullOrEmpty(error))
            {
                EditorUtility.DisplayDialog("Bake Power Bars", error, "OK");
                return;
            }

            EditorUtility.DisplayDialog(
                "Bake Power Bars",
                $"Baked at-ship-level breakdowns on {tiers} chassis tier(s) across {updated} family asset(s).",
                "OK");
        }

        /// <summary>
        /// Same bake as the menu item, without a blocking dialog. Returns false when no family assets exist.
        /// </summary>
        public static bool BakeAllFamiliesCore(out int updated, out int tiers, out string error)
        {
            updated = 0;
            tiers = 0;
            error = null;

            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition", new[] { "Assets/Prefabs/Ships" });
            if (guids == null || guids.Length == 0)
            {
                error = "No ShipFamilyDefinition assets found under Assets/Prefabs/Ships.";
                return false;
            }

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def?.upgradeTree == null)
                    continue;

                Undo.RecordObject(def, "Bake Upgrade-Tree Power Bars At Ship Level");
                for (int t = 0; t < def.upgradeTree.Count; t++)
                {
                    ShipFamilyChassisTierEntry tier = def.upgradeTree[t];
                    if (tier?.prefab == null)
                        continue;
                    ShipFamilyPowerBarNorm.BakeAtShipLevel(tier, def);
                    tiers++;
                }

                EditorUtility.SetDirty(def);
                updated++;
            }

            ShipFamilyDefinition.InvalidateGlobalMaxUpgradeTreeTurnSpeedCache();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ShipFamilyPowerBarBake] Baked {tiers} tier(s) on {updated} family asset(s).");
            return true;
        }

        const string ResortAllPath =
            "TitanOrbit/Ship Families/Resort All Upgrade Trees (All-Gun DPS + Energy Sustain)";

        /// <summary>
        /// Re-sums every family prefab with all-gun DPS + energy-sustain sort,
        /// reorders unlocked tree slots, and bakes at-ship-level power bars.
        /// Does not rewrite component catalog numbers from Part Profiles.
        /// </summary>
        [MenuItem(ResortAllPath)]
        public static void ResortAllUpgradeTrees()
        {
            if (!ResortAllUpgradeTreesCore(out int updated, out int unlocked, out string error))
            {
                EditorUtility.DisplayDialog("Resort Upgrade Trees", error ?? "Resort failed.", "OK");
                return;
            }

            EditorUtility.DisplayDialog(
                "Resort Upgrade Trees",
                $"Resorted {unlocked} unlocked chassis across {updated} family asset(s).\n" +
                "Power scores now use all-gun DPS and energy sustain.",
                "OK");
        }

        /// <summary>Batch resort without a blocking dialog. Returns false when no families exist.</summary>
        public static bool ResortAllUpgradeTreesCore(out int updated, out int unlocked, out string error)
        {
            updated = 0;
            unlocked = 0;
            error = null;

            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition", new[] { "Assets/Prefabs/Ships" });
            if (guids == null || guids.Length == 0)
            {
                error = "No ShipFamilyDefinition assets found under Assets/Prefabs/Ships.";
                return false;
            }

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def?.upgradeTree == null)
                    continue;

                ShipFamilyDefinitionEditor.ResortUpgradeTreeResult result =
                    ShipFamilyDefinitionEditor.ResortUpgradeTreeAndRecalculateStats(
                        def, showDialog: false, saveAssets: false);
                if (!result.success)
                    continue;

                unlocked += result.resortedUnlocked;
                updated++;
            }

            ShipFamilyPowerBarNorm.InvalidateCache();
            ShipFamilyDefinition.InvalidateGlobalMaxUpgradeTreeTurnSpeedCache();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ShipFamilyResort] Resorted {unlocked} unlocked chassis on {updated} family asset(s).");
            return updated > 0;
        }
    }
}
