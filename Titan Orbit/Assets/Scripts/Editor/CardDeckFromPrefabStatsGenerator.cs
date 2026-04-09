using System.Collections.Generic;
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Builds a card deck from <see cref="CardDeckBalance"/> baselines, scaled by mean stats scanned from the upgrade-tree prefabs,
    /// with small deterministic random jitter per card.
    /// </summary>
    public static class CardDeckFromPrefabStatsGenerator
    {
        private const float NeutralCategoryShare = 0.2f;
        private static readonly string[] RarityNames = { "", "Common", "Uncommon", "Rare", "Epic", "Legendary" };

        public static void BuildPrefabDerivedDeckForFamily(ShipFamilyDefinition def, bool interactiveDialogs)
        {
            if (def == null)
            {
                if (interactiveDialogs)
                    EditorUtility.DisplayDialog("Ship Family", "No ShipFamilyDefinition selected.", "OK");
                else
                    Debug.LogError("CardDeckFromPrefabStatsGenerator: definition is null.");
                return;
            }

            if (!ShipFamilyUpgradeTreeStatScanner.TryMeanStatsFromUpgradeTreePrefabs(def, out var meanStats, out int prefabCount, out string scanError))
            {
                if (interactiveDialogs)
                    EditorUtility.DisplayDialog("Prefab scan", scanError, "OK");
                else
                    Debug.LogError("CardDeckFromPrefabStatsGenerator: " + scanError);
                return;
            }

            string familyId = def.familyId.Trim();
            string outputDir = $"Assets/Data/Cards/{familyId}PrefabDeck";
            string deckPath = $"{outputDir}/{familyId}PrefabDeck.asset";
            string deckId = $"{familyId}PrefabDeck";
            string cardPrefix = CardDeckAssetWriteHelper.GetCardIdFilePrefix(familyId);

            if (interactiveDialogs && !Application.isBatchMode)
            {
                var breakdown = ShipFamilyPowerScoreBreakdown.FromSummedShipStats(meanStats);
                if (!EditorUtility.DisplayDialog(
                        "Build prefab-derived card deck",
                        $"Scanned {prefabCount} upgrade-tree prefab(s). Mean hull (authored cap) ≈ {meanStats.healthCap:F0}, gems ≈ {meanStats.maxGems:F0}, thrust ≈ {meanStats.moveSpeed:F1}.\n" +
                        $"Category weights — O:{breakdown.offense:F1} D:{breakdown.defense:F1} E:{breakdown.energy:F1} M:{breakdown.mobility:F1} C:{breakdown.capacity:F1}\n\n" +
                        $"Writes CardData to {outputDir} and assigns Upgrade Card Deck. Continue?",
                        "Yes",
                        "Cancel"))
                    return;
            }

            Directory.CreateDirectory(outputDir);

            var bd = ShipFamilyPowerScoreBreakdown.FromSummedShipStats(meanStats);
            float t = bd.Total;
            float invT = t > 0.0001f ? 1f / t : 1f;
            float wO = t > 0.0001f ? bd.offense * invT : NeutralCategoryShare;
            float wD = t > 0.0001f ? bd.defense * invT : NeutralCategoryShare;
            float wE = t > 0.0001f ? bd.energy * invT : NeutralCategoryShare;
            float wM = t > 0.0001f ? bd.mobility * invT : NeutralCategoryShare;
            float wC = t > 0.0001f ? bd.capacity * invT : NeutralCategoryShare;

            float mOff = AffinityMultiplier(wO);
            float mDef = AffinityMultiplier(wD);
            float mEn = AffinityMultiplier(wE);
            float mMob = AffinityMultiplier(wM);
            float mCap = AffinityMultiplier(wC);

            int rngSeed = CardDeckAssetWriteHelper.StableStringHash(familyId) ^ unchecked((int)0xC4EDDEAD);
            Random.InitState(rngSeed);

            CardDeckAssetWriteHelper.DefaultCardIcons icons = CardDeckAssetWriteHelper.LoadDefaultCardIcons();
            var deckCards = new List<CardData>();

            for (int L = 1; L <= 7; L++)
            {
                for (int r = 1; r <= 4; r++)
                {
                    float dmgMul = ScaleMul(CardDeckBalance.KineticDamageMultiplier(L, r), mOff);
                    float hull = ScaleFlat(CardDeckBalance.AegisHullAdd(L, r), mDef);
                    float gems = ScaleFlatInt(CardDeckBalance.CargoGemAdd(L, r), mCap);
                    float bulletMul = ScaleMul(CardDeckBalance.ShardBulletSpeedMultiplier(L, r), mOff);
                    float eReg = ScaleFlat(CardDeckBalance.ArcEnergyRegenAdd(L, r), mEn);
                    float eCap = ScaleFlat(CardDeckBalance.CapacitorEnergyCapAdd(L, r), mEn);
                    float qol = ScaleMul(CardDeckBalance.QualityOfLifeMultiplier(L, r), Mathf.Lerp(mCap, 1f, 0.35f));
                    float cost = CardDeckBalance.SuggestedGemCost(L, r) * Jitter(0.97f, 1.03f);

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_r{r}_kinetic",
                        $"Kinetic Focus {L} ({RarityNames[r]})",
                        $"+{(dmgMul - 1f) * 100f:F1}% weapon damage multiplier.",
                        L, (CardRarity)r, SlotType.Weapon, icons.Game, cost,
                        c => c.damageMultiplier = dmgMul));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_r{r}_aegis",
                        $"Aegis Plating {L} ({RarityNames[r]})",
                        $"+{hull:F0} max hull.",
                        L, (CardRarity)r, SlotType.Ship, icons.Shield, cost,
                        c => c.maxHealthAdd = hull));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_r{r}_cargo",
                        $"Cargo Bay {L} ({RarityNames[r]})",
                        $"+{gems:F0} gem capacity.",
                        L, (CardRarity)r, SlotType.Cargo, icons.Store, cost,
                        c => c.gemCapacityAdd = gems));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_r{r}_shard",
                        $"Shard Projector {L} ({RarityNames[r]})",
                        $"+{(bulletMul - 1f) * 100f:F1}% projectile speed.",
                        L, (CardRarity)r, SlotType.Weapon, icons.Game, cost,
                        c => c.bulletSpeedMultiplier = bulletMul));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_r{r}_arc",
                        $"Arc Reactor {L} ({RarityNames[r]})",
                        $"+{eReg:F2} energy/sec.",
                        L, (CardRarity)r, SlotType.Ship, icons.Device, cost,
                        c => c.energyRegenAdd = eReg));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_r{r}_cap",
                        $"Capacitor Bank {L} ({RarityNames[r]})",
                        $"+{eCap:F0} energy capacity.",
                        L, (CardRarity)r, SlotType.Ship, icons.Device, cost,
                        c => c.energyCapacityAdd = eCap));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_r{r}_refinery",
                        $"Refinery Drones {L} ({RarityNames[r]})",
                        $"+{(qol - 1f) * 100f:F1}% gem deposit speed.",
                        L, (CardRarity)r, SlotType.Cargo, icons.Tool, cost,
                        c => c.gemDepositSpeedMultiplier = qol));

                    deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_r{r}_transit",
                        $"Transit Uplink {L} ({RarityNames[r]})",
                        $"+{(qol - 1f) * 100f:F1}% people transfer speed.",
                        L, (CardRarity)r, SlotType.Cargo, icons.Feeling, cost,
                        c => c.peopleTransferSpeedMultiplier = qol));
                }

                float after = ScaleFlat(CardDeckBalance.AfterburnerMoveAdd(L), mMob);
                float gyro = ScaleFlat(CardDeckBalance.GyroRotationAdd(L), mMob);
                float regen = ScaleFlat(CardDeckBalance.RegenGelHealthRegenAdd(L), mDef);
                float mine = ScaleFlat(CardDeckBalance.MiningRateAdd(L), mCap);
                float colony = ScaleFlatInt(CardDeckBalance.ColonyPeopleAdd(L), mCap);

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_afterburn",
                    $"Afterburner {L}",
                    $"+{after:F2} thrust.",
                    L, CardRarity.Rare, SlotType.Ship, icons.Traffic, CardDeckBalance.SuggestedGemCost(L, 3) * Jitter(0.97f, 1.03f),
                    c => c.movementSpeedAdd = after));

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_gyro",
                    $"Gyro Stabilizer {L}",
                    $"+{gyro:F0} turn rate.",
                    L, CardRarity.Uncommon, SlotType.Ship, icons.Traffic, CardDeckBalance.SuggestedGemCost(L, 2) * Jitter(0.97f, 1.03f),
                    c => c.rotationSpeedAdd = gyro));

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_regen",
                    $"Regen Gel {L}",
                    $"+{regen:F3} hull/sec.",
                    L, CardRarity.Uncommon, SlotType.Ship, icons.Shield, CardDeckBalance.SuggestedGemCost(L, 2) * Jitter(0.97f, 1.03f),
                    c => c.healthRegenAdd = regen));

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_mine",
                    $"Mining Laser {L}",
                    $"+{mine:F2} mining rate.",
                    L, CardRarity.Uncommon, SlotType.Cargo, icons.Tool, CardDeckBalance.SuggestedGemCost(L, 2) * Jitter(0.97f, 1.03f),
                    c => c.miningRateAdd = mine));

                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_colony",
                    $"Colony Pod {L}",
                    $"+{colony:F0} people capacity.",
                    L, CardRarity.Common, SlotType.Cargo, icons.Feeling, CardDeckBalance.SuggestedGemCost(L, 1) * Jitter(0.97f, 1.03f),
                    c => c.peopleCapacityAdd = colony));

                float tfDmg = ScaleMul(CardDeckBalance.TitanforgeDamageMul(L), mOff);
                float tfHull = ScaleFlat(CardDeckBalance.TitanforgeHullAdd(L), mDef);
                deckCards.Add(CardDeckAssetWriteHelper.WriteCard(outputDir, $"{cardPrefix}_pf_L{L}_titanforge",
                    $"Titanforge {L} ({RarityNames[5]})",
                    $"+{(tfDmg - 1f) * 100f:F0}% damage and +{tfHull:F0} hull.",
                    L, CardRarity.Legendary, SlotType.Weapon, icons.Game, CardDeckBalance.SuggestedGemCost(L, 5) * Jitter(0.97f, 1.03f),
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
            Undo.RecordObject(deck, "Build prefab-derived card deck");
            EditorUtility.SetDirty(deck);

            Undo.RecordObject(def, "Build prefab-derived card deck");
            def.upgradeCardDeck = deck;
            EditorUtility.SetDirty(def);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = def;
            EditorGUIUtility.PingObject(def);

            if (interactiveDialogs && !Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(
                    "Prefab-derived deck",
                    $"Wrote {deckCards.Count} cards to {outputDir} and assigned Upgrade Card Deck on {def.name}.",
                    "OK");
            }
            else
            {
                Debug.Log($"CardDeckFromPrefabStatsGenerator: {deckCards.Count} cards for '{def.name}' (mean over {prefabCount} prefab(s)).");
            }
        }

        private static float AffinityMultiplier(float categoryWeight)
        {
            return Mathf.Clamp(categoryWeight / NeutralCategoryShare, 0.5f, 2f);
        }

        private static float ScaleMul(float baseMultiplier, float affinity)
        {
            float delta = baseMultiplier - 1f;
            return 1f + delta * affinity * Jitter(0.94f, 1.06f);
        }

        private static float ScaleFlat(float baseValue, float affinity)
        {
            return Mathf.Max(0f, baseValue * affinity * Jitter(0.94f, 1.06f));
        }

        private static float ScaleFlatInt(float baseValue, float affinity)
        {
            return Mathf.Max(0f, Mathf.Round(baseValue * affinity * Jitter(0.94f, 1.06f)));
        }

        private static float Jitter(float lo, float hi)
        {
            return Random.Range(lo, hi);
        }

        [MenuItem("Assets/Titan Orbit/Build Prefab-Derived Card Deck (Ship Family)", false, 500)]
        private static void MenuBuildFromSelection()
        {
            if (Selection.activeObject is ShipFamilyDefinition def)
                BuildPrefabDerivedDeckForFamily(def, interactiveDialogs: true);
        }

        [MenuItem("Assets/Titan Orbit/Build Prefab-Derived Card Deck (Ship Family)", true)]
        private static bool MenuBuildFromSelectionValidate()
        {
            return Selection.activeObject is ShipFamilyDefinition;
        }

        [MenuItem("CONTEXT/ShipFamilyDefinition/Build Prefab-Derived Card Deck…", false, 1500)]
        private static void ContextBuildPrefabDeck(MenuCommand command)
        {
            if (command.context is ShipFamilyDefinition def)
                BuildPrefabDerivedDeckForFamily(def, interactiveDialogs: true);
        }
    }
}
