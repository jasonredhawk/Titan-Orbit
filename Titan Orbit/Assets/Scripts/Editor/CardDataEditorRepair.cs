using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Batch repair for <see cref="CardData"/> assets under Assets/Data/Cards. Re-runs
    /// <see cref="CardDataRuntimeRestore.TryRestoreFromAssetName"/> so empty cardId/displayName fields
    /// are repopulated from generator file names (ae_pf_L1_r1_kinetic, etc.). Safe to run after
    /// asset migration or YAML merge conflicts.
    /// </summary>
    public static class CardDataEditorRepair
    {
        /// <summary>
        /// Scans all CardData in Assets/Data/Cards, restores missing ids/stats, marks dirty, saves.
        /// </summary>
        [MenuItem("Titan Orbit/Repair Card Data Assets (Ids + Stats From File Names)", false, 2100)]
        public static void RepairAllCardDataAssets()
        {
            // --- RepairAllCardDataAssets ---
            string[] guids = AssetDatabase.FindAssets("t:CardData", new[] { "Assets/Data/Cards" });
            int repaired = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var card = AssetDatabase.LoadAssetAtPath<CardData>(path);
                if (card == null) continue;

                string beforeId = card.cardId;
                CardDataRuntimeRestore.TryRestoreFromAssetName(card);
                if (card.cardId != beforeId || !string.IsNullOrEmpty(card.displayName))
                {
                    EditorUtility.SetDirty(card);
                    repaired++;
                }
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"CardDataEditorRepair: updated {repaired} of {guids.Length} card asset(s) under Assets/Data/Cards.");
        }
    }
}
