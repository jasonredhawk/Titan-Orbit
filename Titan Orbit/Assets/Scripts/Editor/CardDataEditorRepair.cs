using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    public static class CardDataEditorRepair
    {
        [MenuItem("Titan Orbit/Repair Card Data Assets (Ids + Stats From File Names)", false, 2100)]
        public static void RepairAllCardDataAssets()
        {
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
