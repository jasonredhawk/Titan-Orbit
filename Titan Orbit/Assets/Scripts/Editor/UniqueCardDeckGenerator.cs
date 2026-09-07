#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Writes unique per-family CardData + CardDeckDefinition assets from
    /// <see cref="ShipFamilyUniqueCardDeckTable"/>. Replaces the stub prefab-scan generator.
    /// Menu: used by Titan Orbit → Card Decks and the family inspector Redo button.
    /// </summary>
    public static class UniqueCardDeckGenerator
    {
        [MenuItem("Titan Orbit/Card Decks/Redo All Families")]
        public static void RedoAllFromMenu()
        {
            if (!EditorUtility.DisplayDialog(
                "Redo all card decks",
                "Replace every family's CardDeck assets from the unique-archetype table?",
                "Redo all",
                "Cancel"))
                return;
            RebuildAllFamilyDecks(interactiveDialogs: true);
        }

        /// <summary>Rebuilds one family's authored deck. Returns the number of cards written.</summary>
        public static int RebuildFamilyDeck(ShipFamilyDefinition family, bool interactiveDialogs)
        {
            if (family == null)
            {
                if (interactiveDialogs)
                    EditorUtility.DisplayDialog("Card Deck", "No ship family selected.", "OK");
                return 0;
            }

            string familyPath = AssetDatabase.GetAssetPath(family);
            if (string.IsNullOrEmpty(familyPath))
            {
                if (interactiveDialogs)
                    EditorUtility.DisplayDialog("Card Deck", "Save the family asset first.", "OK");
                return 0;
            }

            string familyDir = Path.GetDirectoryName(familyPath)?.Replace('\\', '/') ?? "Assets";
            string cardsDir = familyDir + "/CardDeck";
            EnsureFolder(cardsDir);

            string deckPath = familyDir + "/" + Sanitize(family.familyId) + "_CardDeck.asset";
            CardDeckDefinition deck = AssetDatabase.LoadAssetAtPath<CardDeckDefinition>(deckPath);
            if (deck == null)
            {
                deck = ScriptableObject.CreateInstance<CardDeckDefinition>();
                AssetDatabase.CreateAsset(deck, deckPath);
            }

            deck.deckId = Sanitize(family.familyId) + "Deck";
            if (deck.cards == null)
                deck.cards = new List<CardData>();
            deck.cards.Clear();

            var archetypes = ShipFamilyUniqueCardDeckTable.GetArchetypes(family.familyId, family.specialBonuses);
            int written = 0;
            for (int a = 0; a < archetypes.Count; a++)
            {
                var arch = archetypes[a];
                for (int level = 1; level <= 7; level++)
                {
                    string cardId = ShipFamilyUniqueCardDeckTable.FormatCardId(family.familyId, arch.idSuffix, level);
                    float mag = ShipFamilyUniqueCardDeckTable.MagnitudeAtLevel(arch, level);
                    string desc = arch.usesFamilyBonusOverlay
                        ? arch.description + "  " + FamilyStatHudCopy.FormatNonIdentityBonuses(family.specialBonuses.ScaleNonIdentity(mag))
                        : arch.description + "  " + FormatMagnitude(arch.kind, mag);

                    CardData card = CardDeckAssetWriteHelper.WriteCard(
                        cardsDir,
                        cardId,
                        arch.displayName + " " + level,
                        desc,
                        level,
                        arch.rarity,
                        SlotType.Ship,
                        null,
                        0f,
                        c =>
                        {
                            if (arch.usesFamilyBonusOverlay)
                            {
                                c.familyBonusOverlay = family.specialBonuses.ScaleNonIdentity(mag);
                            }
                            else if (arch.kind != CardEffectKind.None)
                            {
                                c.effects.Add(new CardEffect { kind = arch.kind, magnitude = mag });
                            }
                        });
                    deck.cards.Add(card);
                    written++;
                }
            }

            family.upgradeCardDeck = deck;
            EditorUtility.SetDirty(deck);
            EditorUtility.SetDirty(family);
            AssetDatabase.SaveAssets();
            return written;
        }

        /// <summary>Rebuilds every <see cref="ShipFamilyDefinition"/> under Assets/Prefabs/Ships.</summary>
        public static int RebuildAllFamilyDecks(bool interactiveDialogs)
        {
            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition");
            int total = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var family = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (family == null)
                    continue;
                EditorUtility.DisplayProgressBar("Card Decks", family.familyId, (i + 0.5f) / guids.Length);
                total += RebuildFamilyDeck(family, interactiveDialogs: false);
            }

            EditorUtility.ClearProgressBar();
            if (interactiveDialogs)
                EditorUtility.DisplayDialog("Card Decks", $"Wrote {total} cards across {guids.Length} families.", "OK");
            return total;
        }

        static string FormatMagnitude(CardEffectKind kind, float mag)
        {
            if (CardEffect.IsAddKind(kind))
                return $"+{mag:0.##}";
            float pct = (mag - 1f) * 100f;
            return pct >= 0f ? $"+{pct:0.#}%" : $"{pct:0.#}%";
        }

        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;
            string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/') ?? "Assets";
            string name = Path.GetFileName(assetFolder);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        static string Sanitize(string familyId)
        {
            if (string.IsNullOrWhiteSpace(familyId))
                return "Family";
            return familyId.Trim().Replace(" ", "");
        }
    }
}
#endif
