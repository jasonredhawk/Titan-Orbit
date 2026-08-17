using System;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>Shared asset creation for editor card deck generators.</summary>
    public static class CardDeckAssetWriteHelper
    {
        public delegate void ApplyCardStats(CardData c);

        public static CardData WriteCard(
            string outputDir,
            string fileBaseId,
            string displayName,
            string description,
            int cardLevel,
            CardRarity rarity,
            SlotType slotType,
            Sprite icon,
            float gemCost,
            ApplyCardStats apply)
        {
            string path = $"{outputDir}/{fileBaseId}.asset";
            CardData card = AssetDatabase.LoadAssetAtPath<CardData>(path);
            if (card == null)
            {
                card = ScriptableObject.CreateInstance<CardData>();
                AssetDatabase.CreateAsset(card, path);
            }

            ResetNeutralStats(card);
            card.cardId = fileBaseId;
            card.displayName = displayName;
            card.description = description;
            card.cardLevel = cardLevel;
            card.rarity = rarity;
            card.slotType = slotType;
            card.icon = icon;
            card.gemCost = gemCost;
            card.minHomePlanetLevel = 1;
            card.originPlanetId = 0;
            card.gridWidth = 1;
            card.gridHeight = 1;
            card.shapeMask = 1;
            apply(card);
            EditorUtility.SetDirty(card);
            return card;
        }

        public static void ResetNeutralStats(CardData c)
        {
            // --- ResetNeutralStats ---
            c.movementSpeedAdd = 0f;
            c.rotationSpeedAdd = 0f;
            c.maxHealthAdd = 0f;
            c.healthRegenAdd = 0f;
            c.energyCapacityAdd = 0f;
            c.energyRegenAdd = 0f;
            c.gemCapacityAdd = 0f;
            c.peopleCapacityAdd = 0f;
            c.miningRateAdd = 0f;
            c.damageMultiplier = 1f;
            c.fireRateMultiplier = 1f;
            c.bulletSpeedMultiplier = 1f;
            c.gemDepositSpeedMultiplier = 1f;
            c.peopleTransferSpeedMultiplier = 1f;
            c.familyBonusOverlay = ShipFamilySpecialBonuses.Identity;
            if (c.effects == null)
                c.effects = new System.Collections.Generic.List<CardEffect>();
            else
                c.effects.Clear();
        }

        public static Sprite LoadSprite(string assetPath)
        {
            // --- LoadSprite ---
            var s = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (s == null)
                Debug.LogWarning($"CardDeckAssetWriteHelper: could not load sprite at '{assetPath}'.");
            return s;
        }

        public readonly struct DefaultCardIcons
        {
            public readonly Sprite Game;
            public readonly Sprite Shield;
            public readonly Sprite Store;
            public readonly Sprite Device;
            public readonly Sprite Traffic;
            public readonly Sprite Tool;
            public readonly Sprite Feeling;

            public DefaultCardIcons(Sprite game, Sprite shield, Sprite store, Sprite device, Sprite traffic, Sprite tool, Sprite feeling)
            {
                // --- DefaultCardIcons ---
                Game = game;
                Shield = shield;
                Store = store;
                Device = device;
                Traffic = traffic;
                Tool = tool;
                Feeling = feeling;
            }
        }

        public static DefaultCardIcons LoadDefaultCardIcons()
        {
            // --- LoadDefaultCardIcons ---
            return new DefaultCardIcons(
                LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_game/icon_line_game_1.png"),
                LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_shield/icon_line_shield_1.png"),
                LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_store/icon_line_store_1.png"),
                LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_device/icon_line_device_1.png"),
                LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_traffic/icon_line_traffic_10.png"),
                LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_tool/icon_line_tool_20.png"),
                LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_feeling/icon_line_feeling_1.png"));
        }

        /// <summary>Stable short prefix for card filenames. AstroEagle keeps <c>ae</c>.</summary>
        public static string GetCardIdFilePrefix(string familyId)
        {
            // --- Compute value ---
            if (string.IsNullOrWhiteSpace(familyId))
                return "card";
            familyId = familyId.Trim();
            if (string.Equals(familyId, "AstroEagle", StringComparison.OrdinalIgnoreCase))
                return "ae";

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < familyId.Length; i++)
            {
                char c = familyId[i];
                if (char.IsLetter(c) && (i == 0 || char.IsUpper(c)))
                    sb.Append(char.ToLowerInvariant(c));
            }

            if (sb.Length >= 2)
                return sb.ToString();

            foreach (char c in familyId)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    if (sb.Length >= 12)
                        break;
                }
            }

            return sb.Length > 0 ? sb.ToString() : "card";
        }

        public static int StableStringHash(string s)
        {
            // --- StableStringHash ---
            unchecked
            {
                int h = 5381;
                if (s == null) return h;
                foreach (char c in s)
                    h = (h * 33) + c;
                return h;
            }
        }
    }
}
