using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Bakes <see cref="ShipFamilyChassisTierEntry.powerScoreBreakdownAtShipLevel"/>
    /// on every family under Prefabs/Ships. Extra Level at each chassis's tree level,
    /// ability purchases = 0. Does not resort the tree or rewrite level-1
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
    }
}
