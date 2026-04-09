using System;
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Builds <see cref="CardData"/> assets and a <see cref="CardDeckDefinition"/> using <see cref="CardDeckBalance"/> and CleanFlatIcon sprites.
    /// Paths and deck id are derived from <see cref="ShipFamilyDefinition.familyId"/>; the deck is assigned to <see cref="ShipFamilyDefinition.upgradeCardDeck"/>.
    /// </summary>
    public static class CardDeckScaledAssetGenerator
    {
        private static readonly string[] RarityNames = { "", "Common", "Uncommon", "Rare", "Epic", "Legendary" };

        /// <summary>
        /// Builds scaled card assets next to <c>Assets/Data/Cards/&lt;familyId&gt;Scaled/</c>, updates the deck, and assigns it to the given definition.
        /// </summary>
        public static void BuildScaledDeckForFamily(ShipFamilyDefinition def, bool interactiveDialogs)
        {
            if (def == null)
            {
                if (interactiveDialogs)
                    EditorUtility.DisplayDialog("Ship Family", "No ShipFamilyDefinition selected.", "OK");
                else
                    Debug.LogError("CardDeckScaledAssetGenerator: ShipFamilyDefinition is null.");
                return;
            }

            if (string.IsNullOrWhiteSpace(def.familyId))
            {
                if (interactiveDialogs)
                    EditorUtility.DisplayDialog("Ship Family", "Set a non-empty Family Id on this Ship Family Definition before building the deck.", "OK");
                else
                    Debug.LogError("CardDeckScaledAssetGenerator: familyId is empty.");
                return;
            }

            string familyId = def.familyId.Trim();
            string outputDir = $"Assets/Data/Cards/{familyId}Scaled";
            string deckPath = $"{outputDir}/{familyId}ScaledDeck.asset";
            string deckId = $"{familyId}Scaled";
            string cardPrefix = CardDeckAssetWriteHelper.GetCardIdFilePrefix(familyId);

            if (interactiveDialogs && !Application.isBatchMode)
            {
                if (!EditorUtility.DisplayDialog(
                        "Build scaled card deck",
                        $"Creates or updates CardData assets in {outputDir} and writes {familyId}ScaledDeck.asset, then assigns Upgrade Card Deck on this Ship Family Definition. Continue?",
                        "Yes",
                        "Cancel"))
                    return;
            }

            Directory.CreateDirectory(outputDir);

            CardDeckAssetWriteHelper.DefaultCardIcons icons = CardDeckAssetWriteHelper.LoadDefaultCardIcons();
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

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_r{r}_kinetic",
                        $"Kinetic Focus {L} ({RarityNames[r]})",
                        $"+{(dmgMul - 1f) * 100f:F1}% weapon damage multiplier.",
                        L, (CardRarity)r, SlotType.Weapon, icons.Game, cost,
                        c => c.damageMultiplier = dmgMul));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_r{r}_aegis",
                        $"Aegis Plating {L} ({RarityNames[r]})",
                        $"+{hull:F0} max hull.",
                        L, (CardRarity)r, SlotType.Ship, icons.Shield, cost,
                        c => c.maxHealthAdd = hull));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_r{r}_cargo",
                        $"Cargo Bay {L} ({RarityNames[r]})",
                        $"+{gems:F0} gem capacity.",
                        L, (CardRarity)r, SlotType.Cargo, icons.Store, cost,
                        c => c.gemCapacityAdd = gems));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_r{r}_shard",
                        $"Shard Projector {L} ({RarityNames[r]})",
                        $"+{(bulletMul - 1f) * 100f:F1}% projectile speed.",
                        L, (CardRarity)r, SlotType.Weapon, icons.Game, cost,
                        c => c.bulletSpeedMultiplier = bulletMul));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_r{r}_arc",
                        $"Arc Reactor {L} ({RarityNames[r]})",
                        $"+{eReg:F2} energy/sec.",
                        L, (CardRarity)r, SlotType.Ship, icons.Device, cost,
                        c => c.energyRegenAdd = eReg));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_r{r}_cap",
                        $"Capacitor Bank {L} ({RarityNames[r]})",
                        $"+{eCap:F0} energy capacity.",
                        L, (CardRarity)r, SlotType.Ship, icons.Device, cost,
                        c => c.energyCapacityAdd = eCap));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_r{r}_refinery",
                        $"Refinery Drones {L} ({RarityNames[r]})",
                        $"+{(qol - 1f) * 100f:F1}% gem deposit speed.",
                        L, (CardRarity)r, SlotType.Cargo, icons.Tool, cost,
                        c => c.gemDepositSpeedMultiplier = qol));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_r{r}_transit",
                        $"Transit Uplink {L} ({RarityNames[r]})",
                        $"+{(qol - 1f) * 100f:F1}% people transfer speed.",
                        L, (CardRarity)r, SlotType.Cargo, icons.Feeling, cost,
                        c => c.peopleTransferSpeedMultiplier = qol));
                }

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_afterburn",
                    $"Afterburner {L}",
                    $"+{CardDeckBalance.AfterburnerMoveAdd(L):F2} thrust.",
                    L, CardRarity.Rare, SlotType.Ship, icons.Traffic, CardDeckBalance.SuggestedGemCost(L, 3),
                    c => c.movementSpeedAdd = CardDeckBalance.AfterburnerMoveAdd(L)));

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_gyro",
                    $"Gyro Stabilizer {L}",
                    $"+{CardDeckBalance.GyroRotationAdd(L):F0} turn rate.",
                    L, CardRarity.Uncommon, SlotType.Ship, icons.Traffic, CardDeckBalance.SuggestedGemCost(L, 2),
                    c => c.rotationSpeedAdd = CardDeckBalance.GyroRotationAdd(L)));

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_regen",
                    $"Regen Gel {L}",
                    $"+{CardDeckBalance.RegenGelHealthRegenAdd(L):F3} hull/sec.",
                    L, CardRarity.Uncommon, SlotType.Ship, icons.Shield, CardDeckBalance.SuggestedGemCost(L, 2),
                    c => c.healthRegenAdd = CardDeckBalance.RegenGelHealthRegenAdd(L)));

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_mine",
                    $"Mining Laser {L}",
                    $"+{CardDeckBalance.MiningRateAdd(L):F2} mining rate.",
                    L, CardRarity.Uncommon, SlotType.Cargo, icons.Tool, CardDeckBalance.SuggestedGemCost(L, 2),
                    c => c.miningRateAdd = CardDeckBalance.MiningRateAdd(L)));

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_colony",
                    $"Colony Pod {L}",
                    $"+{CardDeckBalance.ColonyPeopleAdd(L):F0} people capacity.",
                    L, CardRarity.Common, SlotType.Cargo, icons.Feeling, CardDeckBalance.SuggestedGemCost(L, 1),
                    c => c.peopleCapacityAdd = CardDeckBalance.ColonyPeopleAdd(L)));

                float tfDmg = CardDeckBalance.TitanforgeDamageMul(L);
                float tfHull = CardDeckBalance.TitanforgeHullAdd(L);
                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_L{L}_titanforge",
                    $"Titanforge {L} ({RarityNames[5]})",
                    $"+{(tfDmg - 1f) * 100f:F0}% damage and +{tfHull:F0} hull.",
                    L, CardRarity.Legendary, SlotType.Weapon, icons.Game, CardDeckBalance.SuggestedGemCost(L, 5),
                    c =>
                    {
                        c.damageMultiplier = tfDmg;
                        c.maxHealthAdd = tfHull;
                    }));
            }

            var deck = AssetDatabase.LoadAssetAtPath<CardDeckDefinition>(deckPath);
            if (deck == null)
            {
                deck = ScriptableObject.CreateInstance<CardDeckDefinition>();
                AssetDatabase.CreateAsset(deck, deckPath);
            }

            deck.deckId = deckId;
            deck.cards = deckCards;
            Undo.RecordObject(deck, "Build scaled card deck");
            EditorUtility.SetDirty(deck);

            Undo.RecordObject(def, "Build scaled card deck");
            def.upgradeCardDeck = deck;
            EditorUtility.SetDirty(def);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = def;
            EditorGUIUtility.PingObject(def);
            if (interactiveDialogs && !Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(
                    "Card deck built",
                    $"Wrote {deckCards.Count} CardData assets, {deckPath}, and assigned Upgrade Card Deck on {def.name}. CardShopSystem reads the deck from each ship's ShipFamilyDefinition.",
                    "OK");
            }
            else
            {
                Debug.Log($"CardDeckScaledAssetGenerator: wrote {deckCards.Count} cards to {outputDir} and linked deck on '{def.name}'.");
            }
        }

        [MenuItem("Assets/Titan Orbit/Create New Card Deck (Ship Family, scaled)", false, 499)]
        private static void MenuBuildScaledFromSelection()
        {
            if (Selection.activeObject is ShipFamilyDefinition def)
                BuildScaledDeckForFamily(def, interactiveDialogs: true);
        }

        [MenuItem("Assets/Titan Orbit/Create New Card Deck (Ship Family, scaled)", true)]
        private static bool MenuBuildScaledFromSelectionValidate()
        {
            return Selection.activeObject is ShipFamilyDefinition;
        }

        [MenuItem("CONTEXT/ShipFamilyDefinition/Create New Card Deck (scaled, like Cards menu)", false, 1400)]
        private static void ContextCreateNewCardDeck(MenuCommand command)
        {
            if (command.context is ShipFamilyDefinition def)
                BuildScaledDeckForFamily(def, interactiveDialogs: true);
        }

        [MenuItem("Titan Orbit/Cards/Build Scaled Astro Eagle Deck (Assets + Icons)")]
        public static void BuildMenuAstroEagle()
        {
            var def = FindShipFamilyByFamilyId("AstroEagle");
            if (def == null)
            {
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog(
                        "Ship Family",
                        "Could not find a ShipFamilyDefinition with Family Id 'AstroEagle'. Open AstroEagleShipFamily (or set family Id) and use 'Build Scaled Card Deck' on the inspector instead.",
                        "OK");
                else
                    Debug.LogError("CardDeckScaledAssetGenerator: no ShipFamilyDefinition with familyId AstroEagle.");
                return;
            }

            BuildScaledDeckForFamily(def, interactiveDialogs: !Application.isBatchMode);
        }

        private static ShipFamilyDefinition FindShipFamilyByFamilyId(string familyId)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:ShipFamilyDefinition"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var d = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (d != null && string.Equals(d.familyId?.Trim(), familyId, StringComparison.OrdinalIgnoreCase))
                    return d;
            }

            return null;
        }
    }
}
