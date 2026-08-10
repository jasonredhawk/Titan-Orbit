using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Batch repair for any remaining <see cref="CardData"/> assets in the project.
    /// Re-runs <see cref="CardDataRuntimeRestore.TryRestoreFromAssetName"/> so empty
    /// cardId/displayName fields are repopulated from generator file names.
    /// <para>
    /// [TITAN-ORBIT] Authored decks under Assets/Data/Cards were removed — moon shop / spin
    /// cards are built at runtime by <see cref="CardDeckRuntimeDefaults"/>. This menu is kept
    /// only if someone re-imports orphaned CardData assets.
    /// </para>
    /// </summary>
    public static class CardDataEditorRepair
    {
        /// <summary>
        /// Scans all CardData under Assets/, restores missing ids/stats, marks dirty, saves.
        /// </summary>
        [MenuItem("Titan Orbit/Repair Card Data Assets (Ids + Stats From File Names)", false, 2100)]
        public static void RepairAllCardDataAssets()
        {
            // --- RepairAllCardDataAssets ---
            string[] guids = AssetDatabase.FindAssets("t:CardData", new[] { "Assets" });
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
            Debug.Log(
                $"CardDataEditorRepair: updated {repaired} of {guids.Length} CardData asset(s). " +
                "Runtime decks use CardDeckRuntimeDefaults (authored PrefabDeck folders were removed).");
        }
    }
}
