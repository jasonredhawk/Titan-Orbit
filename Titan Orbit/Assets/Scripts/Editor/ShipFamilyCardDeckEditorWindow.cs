#if UNITY_EDITOR
using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Browse unique card decks per ship family and regenerate them from the archetype table.
    /// Menu: Titan Orbit → Card Decks.
    /// </summary>
    public class ShipFamilyCardDeckEditorWindow : EditorWindow
    {
        Vector2 _familyScroll;
        Vector2 _cardScroll;
        List<ShipFamilyDefinition> _families = new List<ShipFamilyDefinition>();
        int _selected;
        int _filterLevel;
        bool _filterFamilyBonus;

        [MenuItem("Titan Orbit/Card Decks")]
        public static void Open()
        {
            var win = GetWindow<ShipFamilyCardDeckEditorWindow>("Card Decks");
            win.minSize = new Vector2(720f, 420f);
            win.RefreshFamilies();
        }

        /// <summary>Opens the window focused on this family (inspector Redo shortcut).</summary>
        public static void OpenFocused(ShipFamilyDefinition family)
        {
            Open();
            var win = GetWindow<ShipFamilyCardDeckEditorWindow>();
            win.RefreshFamilies();
            if (family == null)
                return;
            for (int i = 0; i < win._families.Count; i++)
            {
                if (win._families[i] == family)
                {
                    win._selected = i;
                    break;
                }
            }
        }

        void OnEnable() => RefreshFamilies();

        void RefreshFamilies()
        {
            _families.Clear();
            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition");
            for (int i = 0; i < guids.Length; i++)
            {
                var family = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (family != null)
                    _families.Add(family);
            }

            _families.Sort((a, b) => string.CompareOrdinal(a.familyId, b.familyId));
            _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _families.Count - 1));
        }

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh list", GUILayout.Width(110)))
                RefreshFamilies();
            if (GUILayout.Button("Redo all families", GUILayout.Width(150)))
            {
                if (EditorUtility.DisplayDialog(
                    "Redo all card decks",
                    "Replace every family's CardDeck assets from the unique-archetype table?",
                    "Redo all",
                    "Cancel"))
                {
                    UniqueCardDeckGenerator.RebuildAllFamilyDecks(interactiveDialogs: true);
                    RefreshFamilies();
                }
            }

            _filterLevel = EditorGUILayout.IntField("Level", _filterLevel, GUILayout.Width(160));
            _filterFamilyBonus = EditorGUILayout.ToggleLeft("Family bonus only", _filterFamilyBonus, GUILayout.Width(140));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawFamilyList();
            DrawCardTable();
            EditorGUILayout.EndHorizontal();
        }

        void DrawFamilyList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(240));
            EditorGUILayout.LabelField("Families", EditorStyles.boldLabel);
            _familyScroll = EditorGUILayout.BeginScrollView(_familyScroll);
            for (int i = 0; i < _families.Count; i++)
            {
                ShipFamilyDefinition family = _families[i];
                int count = family.upgradeCardDeck != null && family.upgradeCardDeck.cards != null
                    ? family.upgradeCardDeck.cards.Count
                    : 0;
                string status = count > 0 ? $"{count} cards" : "procedural fallback";
                string label = (string.IsNullOrWhiteSpace(family.familyId) ? family.name : family.familyId) + "  —  " + status;
                if (GUILayout.Toggle(_selected == i, label, "Button"))
                    _selected = i;
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        void DrawCardTable()
        {
            EditorGUILayout.BeginVertical();
            if (_families.Count == 0 || _selected < 0 || _selected >= _families.Count)
            {
                EditorGUILayout.HelpBox("No ship families found.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            ShipFamilyDefinition family = _families[_selected];
            EditorGUILayout.LabelField(FamilyStatHudCopy.FormatFamilyCaption(family), EditorStyles.boldLabel);
            if (FamilyStatHudCopy.HasVisibleFamilyStats(family))
                EditorGUILayout.LabelField("FAMILY STATS  " + FamilyStatHudCopy.FormatNonIdentityBonuses(family.specialBonuses));
            else
                EditorGUILayout.LabelField("No family special bonuses (all 1×).");

            if (GUILayout.Button("Redo this family", GUILayout.Height(26)))
            {
                if (EditorUtility.DisplayDialog(
                    "Redo card deck",
                    $"Replace {family.familyId} cards from the unique-archetype table?",
                    "Redo",
                    "Cancel"))
                {
                    UniqueCardDeckGenerator.RebuildFamilyDeck(family, interactiveDialogs: true);
                    RefreshFamilies();
                }
            }

            _cardScroll = EditorGUILayout.BeginScrollView(_cardScroll);
            IReadOnlyList<CardData> cards = family.GetUpgradeCards();
            if (cards == null || cards.Count == 0)
            {
                EditorGUILayout.HelpBox("No cards on this family.", MessageType.Warning);
            }
            else
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    CardData card = cards[i];
                    if (card == null)
                        continue;
                    if (_filterLevel > 0 && card.cardLevel != _filterLevel)
                        continue;
                    if (_filterFamilyBonus && card.familyBonusOverlay.IsIdentity)
                        continue;

                    EditorGUILayout.BeginHorizontal("box");
                    if (GUILayout.Button(card.GetDisplayNameOrDefault(), GUILayout.Width(180)))
                    {
                        Selection.activeObject = card;
                        EditorGUIUtility.PingObject(card);
                    }

                    EditorGUILayout.LabelField($"Lv {card.cardLevel}", GUILayout.Width(40));
                    EditorGUILayout.LabelField(card.rarity.ToString(), GUILayout.Width(80));
                    EditorGUILayout.LabelField(SummarizeCard(card));
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        static string SummarizeCard(CardData card)
        {
            if (card.effects != null)
            {
                for (int i = 0; i < card.effects.Count; i++)
                {
                    if (!card.effects[i].IsActive)
                        continue;
                    return card.effects[i].kind + " " + card.effects[i].magnitude.ToString("0.##");
                }
            }

            if (!card.familyBonusOverlay.IsIdentity)
                return "Family crest  " + FamilyStatHudCopy.FormatNonIdentityBonuses(card.familyBonusOverlay);
            if (!string.IsNullOrEmpty(card.description))
                return card.description;
            return card.GetStableCardId();
        }
    }
}
#endif
