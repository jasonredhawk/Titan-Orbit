using System.Collections.Generic;
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Builds <see cref="CardData"/> assets and an <see cref="CardDeckDefinition"/> using <see cref="CardDeckBalance"/> and CleanFlatIcon sprites.
    /// </summary>
    public static class CardDeckScaledAssetGenerator
    {
        private const string OutputDir = "Assets/Data/Cards/AstroEagleScaled";
        private const string DeckPath = "Assets/Data/Cards/AstroEagleScaled/AstroEagleScaledDeck.asset";

        private static readonly string[] RarityNames = { "", "Common", "Uncommon", "Rare", "Epic", "Legendary" };

        [MenuItem("Titan Orbit/Cards/Build Scaled Astro Eagle Deck (Assets + Icons)")]
        public static void Build()
        {
            if (!Application.isBatchMode)
            {
                if (!EditorUtility.DisplayDialog(
                        "Build scaled card deck",
                        "Creates or updates CardData assets in Assets/Data/Cards/AstroEagleScaled and writes AstroEagleScaledDeck.asset. Continue?",
                        "Yes",
                        "Cancel"))
                    return;
            }

            Directory.CreateDirectory(OutputDir);

            Sprite iconGame = LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_game/icon_line_game_1.png");
            Sprite iconShield = LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_shield/icon_line_shield_1.png");
            Sprite iconStore = LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_store/icon_line_store_1.png");
            Sprite iconDevice = LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_device/icon_line_device_1.png");
            Sprite iconTraffic = LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_traffic/icon_line_traffic_10.png");
            Sprite iconTool = LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_tool/icon_line_tool_20.png");
            Sprite iconFeeling = LoadSprite("Assets/CleanFlatIcon/png_128/icon_line/icon_line_feeling/icon_line_feeling_1.png");

            var deckCards = new List<CardData>();

            for (int L = 1; L <= 7; L++)
            {
                for (int r = 1; r <= 4; r++)
                {
                    float dmgMul = CardDeckBalance.KineticDamageMultiplier(L, r);
                    float hull = CardDeckBalance.AegisHullAdd(L, r);
                    float gems = CardDeckBalance.CargoGemAdd(L, r);
                    float bulletMul = CardDeckBalance.ShardBulletSpeedMultiplier(L, r);
                    float eReg = CardDeckBalance.ArcEnergyRegenAdd(L, r);
                    float eCap = CardDeckBalance.CapacitorEnergyCapAdd(L, r);
                    float qol = CardDeckBalance.QualityOfLifeMultiplier(L, r);
                    float cost = CardDeckBalance.SuggestedGemCost(L, r);

                    deckCards.Add(WriteCard($"ae_L{L}_r{r}_kinetic",
                        $"Kinetic Focus {L} ({RarityNames[r]})",
                        $"+{(dmgMul - 1f) * 100f:F1}% weapon damage multiplier.",
                        L, (CardRarity)r, SlotType.Weapon, iconGame, cost,
                        c => c.damageMultiplier = dmgMul));

                    deckCards.Add(WriteCard($"ae_L{L}_r{r}_aegis",
                        $"Aegis Plating {L} ({RarityNames[r]})",
                        $"+{hull:F0} max hull.",
                        L, (CardRarity)r, SlotType.Ship, iconShield, cost,
                        c => c.maxHealthAdd = hull));

                    deckCards.Add(WriteCard($"ae_L{L}_r{r}_cargo",
                        $"Cargo Bay {L} ({RarityNames[r]})",
                        $"+{gems:F0} gem capacity.",
                        L, (CardRarity)r, SlotType.Cargo, iconStore, cost,
                        c => c.gemCapacityAdd = gems));

                    deckCards.Add(WriteCard($"ae_L{L}_r{r}_shard",
                        $"Shard Projector {L} ({RarityNames[r]})",
                        $"+{(bulletMul - 1f) * 100f:F1}% projectile speed.",
                        L, (CardRarity)r, SlotType.Weapon, iconGame, cost,
                        c => c.bulletSpeedMultiplier = bulletMul));

                    deckCards.Add(WriteCard($"ae_L{L}_r{r}_arc",
                        $"Arc Reactor {L} ({RarityNames[r]})",
                        $"+{eReg:F2} energy/sec.",
                        L, (CardRarity)r, SlotType.Ship, iconDevice, cost,
                        c => c.energyRegenAdd = eReg));

                    deckCards.Add(WriteCard($"ae_L{L}_r{r}_cap",
                        $"Capacitor Bank {L} ({RarityNames[r]})",
                        $"+{eCap:F0} energy capacity.",
                        L, (CardRarity)r, SlotType.Ship, iconDevice, cost,
                        c => c.energyCapacityAdd = eCap));

                    deckCards.Add(WriteCard($"ae_L{L}_r{r}_refinery",
                        $"Refinery Drones {L} ({RarityNames[r]})",
                        $"+{(qol - 1f) * 100f:F1}% gem deposit speed.",
                        L, (CardRarity)r, SlotType.Cargo, iconTool, cost,
                        c => c.gemDepositSpeedMultiplier = qol));

                    deckCards.Add(WriteCard($"ae_L{L}_r{r}_transit",
                        $"Transit Uplink {L} ({RarityNames[r]})",
                        $"+{(qol - 1f) * 100f:F1}% people transfer speed.",
                        L, (CardRarity)r, SlotType.Cargo, iconFeeling, cost,
                        c => c.peopleTransferSpeedMultiplier = qol));
                }

                deckCards.Add(WriteCard($"ae_L{L}_afterburn",
                    $"Afterburner {L}",
                    $"+{CardDeckBalance.AfterburnerMoveAdd(L):F2} thrust.",
                    L, CardRarity.Rare, SlotType.Ship, iconTraffic, CardDeckBalance.SuggestedGemCost(L, 3),
                    c => c.movementSpeedAdd = CardDeckBalance.AfterburnerMoveAdd(L)));

                deckCards.Add(WriteCard($"ae_L{L}_gyro",
                    $"Gyro Stabilizer {L}",
                    $"+{CardDeckBalance.GyroRotationAdd(L):F0} turn rate.",
                    L, CardRarity.Uncommon, SlotType.Ship, iconTraffic, CardDeckBalance.SuggestedGemCost(L, 2),
                    c => c.rotationSpeedAdd = CardDeckBalance.GyroRotationAdd(L)));

                deckCards.Add(WriteCard($"ae_L{L}_regen",
                    $"Regen Gel {L}",
                    $"+{CardDeckBalance.RegenGelHealthRegenAdd(L):F3} hull/sec.",
                    L, CardRarity.Uncommon, SlotType.Ship, iconShield, CardDeckBalance.SuggestedGemCost(L, 2),
                    c => c.healthRegenAdd = CardDeckBalance.RegenGelHealthRegenAdd(L)));

                deckCards.Add(WriteCard($"ae_L{L}_mine",
                    $"Mining Laser {L}",
                    $"+{CardDeckBalance.MiningRateAdd(L):F2} mining rate.",
                    L, CardRarity.Uncommon, SlotType.Cargo, iconTool, CardDeckBalance.SuggestedGemCost(L, 2),
                    c => c.miningRateAdd = CardDeckBalance.MiningRateAdd(L)));

                deckCards.Add(WriteCard($"ae_L{L}_colony",
                    $"Colony Pod {L}",
                    $"+{CardDeckBalance.ColonyPeopleAdd(L):F1} people capacity.",
                    L, CardRarity.Common, SlotType.Cargo, iconFeeling, CardDeckBalance.SuggestedGemCost(L, 1),
                    c => c.peopleCapacityAdd = CardDeckBalance.ColonyPeopleAdd(L)));

                float tfDmg = CardDeckBalance.TitanforgeDamageMul(L);
                float tfHull = CardDeckBalance.TitanforgeHullAdd(L);
                deckCards.Add(WriteCard($"ae_L{L}_titanforge",
                    $"Titanforge {L} ({RarityNames[5]})",
                    $"+{(tfDmg - 1f) * 100f:F0}% damage and +{tfHull:F0} hull.",
                    L, CardRarity.Legendary, SlotType.Weapon, iconGame, CardDeckBalance.SuggestedGemCost(L, 5),
                    c =>
                    {
                        c.damageMultiplier = tfDmg;
                        c.maxHealthAdd = tfHull;
                    }));
            }

            var deck = AssetDatabase.LoadAssetAtPath<CardDeckDefinition>(DeckPath);
            if (deck == null)
            {
                deck = ScriptableObject.CreateInstance<CardDeckDefinition>();
                AssetDatabase.CreateAsset(deck, DeckPath);
            }

            deck.deckId = "AstroEagleScaled";
            deck.cards = deckCards;
            EditorUtility.SetDirty(deck);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = deck;
            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(
                    "Card deck built",
                    $"Wrote {deckCards.Count} CardData assets and {DeckPath}.\n\n" +
                    "On your CardShopSystem: assign AstroEagleScaledDeck to Card Deck and leave All Cards empty (or assign the same list) so the shop uses these assets.",
                    "OK");
            }
            else
            {
                Debug.Log($"CardDeckScaledAssetGenerator: wrote {deckCards.Count} cards to {OutputDir}.");
            }
        }

        private delegate void ApplyCardStats(CardData c);

        private static CardData WriteCard(
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
            string path = $"{OutputDir}/{fileBaseId}.asset";
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

        private static void ResetNeutralStats(CardData c)
        {
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
        }

        private static Sprite LoadSprite(string assetPath)
        {
            var s = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (s == null)
                Debug.LogWarning($"CardDeckScaledAssetGenerator: could not load sprite at '{assetPath}'. Assign icons manually if needed.");
            return s;
        }
    }
}
