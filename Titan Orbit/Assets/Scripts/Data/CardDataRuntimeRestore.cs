using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Re-applies identity and baseline stats when <see cref="CardData"/> assets lost serialized fields
    /// (e.g. empty cardId) but still use generator file names like <c>ae_pf_L1_r1_kinetic</c>.
    /// Called from <see cref="CardData.OnEnable"/> and editor <c>OnValidate</c>. Formulas mirror
    /// <see cref="CardDeckBalance"/> and <see cref="CardDeckRuntimeDefaults"/>.
    /// </summary>
    public static class CardDataRuntimeRestore
    {
        private static readonly string[] RarityNames = { "", "Common", "Uncommon", "Rare", "Epic", "Legendary" };

        /// <summary>Matches ae_pf_L3_r2_kinetic style names — level, rarity, kind suffix.</summary>
        private static readonly Regex TieredRarity = new Regex(@"_L(\d+)_r(\d+)_(\w+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        /// <summary>Matches ae_pf_L3_afterburn style names — level and kind only.</summary>
        private static readonly Regex TieredSimple = new Regex(@"_L(\d+)_(\w+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Parses <paramref name="card"/>.name when cardId or displayName is missing. No-op when both
        /// fields are already populated.
        /// </summary>
        public static void TryRestoreFromAssetName(CardData card)
        {
            if (card == null || string.IsNullOrEmpty(card.name)) return;
            if (!string.IsNullOrEmpty(card.cardId) && !string.IsNullOrEmpty(card.displayName)) return;

            string assetName = card.name;
            if (string.IsNullOrEmpty(card.cardId))
                card.cardId = assetName;

            // --- Try tiered+rarity pattern first (most generated shop cards) ---
            var tiered = TieredRarity.Match(assetName);
            if (tiered.Success)
            {
                int L = ParseLevel(tiered.Groups[1].Value, card.cardLevel);
                int r = ParseRarity(tiered.Groups[2].Value, (int)card.rarity);
                ApplyTiered(card, L, r, tiered.Groups[3].Value);
                return;
            }

            // --- Fallback: level + kind without rarity column ---
            var simple = TieredSimple.Match(assetName);
            if (simple.Success)
            {
                int L = ParseLevel(simple.Groups[1].Value, card.cardLevel);
                ApplySimple(card, L, simple.Groups[2].Value);
            }
        }

        private static int ParseLevel(string s, int fallback) =>
            int.TryParse(s, out int L) && L >= 1 && L <= 7 ? L : Mathf.Max(1, fallback);

        private static int ParseRarity(string s, int fallback) =>
            int.TryParse(s, out int r) && r >= 1 && r <= 5 ? r : Mathf.Clamp(fallback, 1, 5);

        /// <summary>Applies one of the eight tiered+rarity card kinds (kinetic, aegis, cargo, …).</summary>
        private static void ApplyTiered(CardData c, int L, int r, string kind)
        {
            c.cardLevel = L;
            c.rarity = (CardRarity)Mathf.Clamp(r, 1, 5);
            c.shapeMask = c.shapeMask == 0 ? 1ul : c.shapeMask;

            switch (kind.ToLowerInvariant())
            {
                case "kinetic":
                    c.slotType = SlotType.Weapon;
                    c.displayName = $"Kinetic Focus {L} ({RarityNames[r]})";
                    c.description = $"+{(CardDeckBalance.KineticDamageMultiplier(L, r) - 1f) * 100f:F1}% weapon damage multiplier.";
                    c.damageMultiplier = CardDeckBalance.KineticDamageMultiplier(L, r);
                    break;
                case "aegis":
                    c.slotType = SlotType.Ship;
                    c.displayName = $"Aegis Plating {L} ({RarityNames[r]})";
                    c.description = $"+{CardDeckBalance.AegisHullAdd(L, r):F0} max hull.";
                    c.maxHealthAdd = CardDeckBalance.AegisHullAdd(L, r);
                    break;
                case "cargo":
                    c.slotType = SlotType.Cargo;
                    c.displayName = $"Cargo Bay {L} ({RarityNames[r]})";
                    c.description = $"+{CardDeckBalance.CargoGemAdd(L, r):F0} gem capacity.";
                    c.gemCapacityAdd = CardDeckBalance.CargoGemAdd(L, r);
                    break;
                case "shard":
                    c.slotType = SlotType.Weapon;
                    c.displayName = $"Shard Projector {L} ({RarityNames[r]})";
                    c.description = $"+{(CardDeckBalance.ShardBulletSpeedMultiplier(L, r) - 1f) * 100f:F1}% projectile speed.";
                    c.bulletSpeedMultiplier = CardDeckBalance.ShardBulletSpeedMultiplier(L, r);
                    break;
                case "arc":
                    c.slotType = SlotType.Ship;
                    c.displayName = $"Arc Reactor {L} ({RarityNames[r]})";
                    c.description = $"+{CardDeckBalance.ArcEnergyRegenAdd(L, r):F2} energy/sec.";
                    c.energyRegenAdd = CardDeckBalance.ArcEnergyRegenAdd(L, r);
                    break;
                case "cap":
                    c.slotType = SlotType.Ship;
                    c.displayName = $"Capacitor Bank {L} ({RarityNames[r]})";
                    c.description = $"+{CardDeckBalance.CapacitorEnergyCapAdd(L, r):F0} energy capacity.";
                    c.energyCapacityAdd = CardDeckBalance.CapacitorEnergyCapAdd(L, r);
                    break;
                case "refinery":
                    c.slotType = SlotType.Cargo;
                    c.displayName = $"Refinery Drones {L} ({RarityNames[r]})";
                    {
                        float qol = CardDeckBalance.QualityOfLifeMultiplier(L, r);
                        c.description = $"+{(qol - 1f) * 100f:F1}% gem deposit speed.";
                        c.gemDepositSpeedMultiplier = qol;
                    }
                    break;
                case "transit":
                    c.slotType = SlotType.Cargo;
                    c.displayName = $"Transit Uplink {L} ({RarityNames[r]})";
                    {
                        float qol = CardDeckBalance.QualityOfLifeMultiplier(L, r);
                        c.description = $"+{(qol - 1f) * 100f:F1}% people transfer speed.";
                        c.peopleTransferSpeedMultiplier = qol;
                    }
                    break;
                default:
                    return;
            }

            if (c.gemCost <= 0f)
                c.gemCost = CardDeckBalance.SuggestedGemCost(L, r);
        }

        /// <summary>Applies single-rarity cards (afterburn, gyro, regen, mine, colony, titanforge).</summary>
        private static void ApplySimple(CardData c, int L, string kind)
        {
            c.cardLevel = L;
            c.shapeMask = c.shapeMask == 0 ? 1ul : c.shapeMask;

            switch (kind.ToLowerInvariant())
            {
                case "afterburn":
                    c.rarity = CardRarity.Rare;
                    c.slotType = SlotType.Ship;
                    c.displayName = $"Afterburner {L}";
                    c.description = $"+{CardDeckBalance.AfterburnerMoveAdd(L):F2} thrust.";
                    c.movementSpeedAdd = CardDeckBalance.AfterburnerMoveAdd(L);
                    break;
                case "gyro":
                    c.rarity = CardRarity.Uncommon;
                    c.slotType = SlotType.Ship;
                    c.displayName = $"Gyro Stabilizer {L}";
                    c.description = $"+{CardDeckBalance.GyroRotationAdd(L):F0} turn rate.";
                    c.rotationSpeedAdd = CardDeckBalance.GyroRotationAdd(L);
                    break;
                case "regen":
                    c.rarity = CardRarity.Uncommon;
                    c.slotType = SlotType.Ship;
                    c.displayName = $"Regen Gel {L}";
                    c.description = $"+{CardDeckBalance.RegenGelHealthRegenAdd(L):F3} hull/sec.";
                    c.healthRegenAdd = CardDeckBalance.RegenGelHealthRegenAdd(L);
                    break;
                case "mine":
                    c.rarity = CardRarity.Uncommon;
                    c.slotType = SlotType.Cargo;
                    c.displayName = $"Mining Laser {L}";
                    c.description = $"+{CardDeckBalance.MiningRateAdd(L):F2} mining rate.";
                    c.miningRateAdd = CardDeckBalance.MiningRateAdd(L);
                    break;
                case "colony":
                    c.rarity = CardRarity.Common;
                    c.slotType = SlotType.Cargo;
                    c.displayName = $"Colony Pod {L}";
                    c.description = $"+{CardDeckBalance.ColonyPeopleAdd(L):F0} people capacity.";
                    c.peopleCapacityAdd = CardDeckBalance.ColonyPeopleAdd(L);
                    break;
                case "titanforge":
                    c.rarity = CardRarity.Legendary;
                    c.slotType = SlotType.Weapon;
                    c.displayName = $"Titanforge {L} ({RarityNames[5]})";
                    {
                        float tfDmg = CardDeckBalance.TitanforgeDamageMul(L);
                        float tfHull = CardDeckBalance.TitanforgeHullAdd(L);
                        c.description = $"+{(tfDmg - 1f) * 100f:F0}% damage and +{tfHull:F0} hull.";
                        c.damageMultiplier = tfDmg;
                        c.maxHealthAdd = tfHull;
                    }
                    break;
                default:
                    return;
            }

            if (c.gemCost <= 0f)
                c.gemCost = CardDeckBalance.SuggestedGemCost(L, (int)c.rarity);
        }
    }

}
