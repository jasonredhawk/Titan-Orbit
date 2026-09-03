using TitanOrbit.Data;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Reads equipped upgrade cards and folds their <see cref="CardEffect"/> rows plus
    /// legacy CardData multipliers into one overlay value for a sim system.
    /// Client and server both call this — cards are ghosted as <see cref="EquippedCardElement"/> ids.
    /// </summary>
    public static class CardEffectQuery
    {
        /// <summary>
        /// Combined multiplier (or flat add) for <paramref name="kind"/> on this ship.
        /// Multipliers start at 1 and multiply across cards. Adds start at 0 and sum.
        /// Missing cards or empty buffers return identity (1 or 0).
        /// </summary>
        public static float GetValue(EntityManager em, Entity shipEntity, CardEffectKind kind)
        {
            bool add = CardEffect.IsAddKind(kind);
            float value = add ? 0f : 1f;
            if (kind == CardEffectKind.None || !em.Exists(shipEntity))
                return value;

            if (!TryResolveFamily(em, shipEntity, out ShipFamilyDefinition family) || family == null)
                return value;

            if (!em.HasBuffer<EquippedCardElement>(shipEntity))
                return value;

            var cards = em.GetBuffer<EquippedCardElement>(shipEntity);
            for (int i = 0; i < cards.Length; i++)
            {
                string cardId = cards[i].CardId.ToString();
                if (string.IsNullOrWhiteSpace(cardId))
                    continue;

                CardData card = ShipStatApplyLogic.FindCardInFamily(family, cardId);
                if (card == null)
                    card = ShipStatApplyLogic.FindCardAnywhere(cardId);
                if (card == null)
                    continue;

                AccumulateCard(ref value, add, kind, card);
            }

            return value;
        }

        /// <summary>Convenience: treat missing / identity as 1 for rate multipliers.</summary>
        public static float GetMul(EntityManager em, Entity shipEntity, CardEffectKind kind)
        {
            float v = GetValue(em, shipEntity, kind);
            return v > 0.0001f ? v : 1f;
        }

        /// <summary>
        /// Incoming hull damage after card resist. Below-1 <see cref="CardEffectKind.IncomingDamageTakenMul"/>
        /// reduces the hit; missing cards leave damage unchanged.
        /// </summary>
        public static float ScaleIncomingDamage(EntityManager em, Entity shipEntity, float damage)
        {
            if (damage <= 0f)
                return damage;
            return damage * GetMul(em, shipEntity, CardEffectKind.IncomingDamageTakenMul);
        }

        static void AccumulateCard(ref float value, bool add, CardEffectKind kind, CardData card)
        {
            // --- Authored effect list ---
            if (card.effects != null)
            {
                for (int e = 0; e < card.effects.Count; e++)
                {
                    CardEffect row = card.effects[e];
                    if (row.kind != kind || !row.IsActive)
                        continue;
                    if (add)
                        value += row.magnitude;
                    else
                        value *= row.magnitude > 0.0001f ? row.magnitude : 1f;
                }
            }

            // --- Legacy CardData fields (same kinds the procedural deck already authored) ---
            if (kind == CardEffectKind.GemDepositSpeedMul && card.gemDepositSpeedMultiplier > 0.0001f
                && !Mathf.Approximately(card.gemDepositSpeedMultiplier, 1f))
                value *= card.gemDepositSpeedMultiplier;
            if (kind == CardEffectKind.PeopleTransferSpeedMul && card.peopleTransferSpeedMultiplier > 0.0001f
                && !Mathf.Approximately(card.peopleTransferSpeedMultiplier, 1f))
                value *= card.peopleTransferSpeedMultiplier;
            if (kind == CardEffectKind.MiningRateMul && Mathf.Abs(card.miningRateAdd) > 0.0001f)
            {
                // [TITAN-ORBIT] Legacy field is a flat add on mining rate. Convert to a mul
                // against the constant MiningRate so old cards still do something.
                float baseRate = Mathf.Max(0.01f, GemEconomyConstants.MiningRate);
                value *= 1f + (card.miningRateAdd / baseRate);
            }
            if (kind == CardEffectKind.FireRateMul && card.fireRateMultiplier > 0.0001f
                && !Mathf.Approximately(card.fireRateMultiplier, 1f))
                value *= card.fireRateMultiplier;
            if (kind == CardEffectKind.BulletRangeMul)
            {
                // No dedicated legacy range field — skip.
            }
        }

        static bool TryResolveFamily(EntityManager em, Entity shipEntity, out ShipFamilyDefinition family)
        {
            family = null;
            if (!em.HasComponent<ShipState>(shipEntity))
                return false;
            var ship = em.GetComponentData<ShipState>(shipEntity);
            return TryResolveFamilyFromShipIndex(ship, out family);
        }

        static bool TryResolveFamilyFromShipIndex(in ShipState ship, out ShipFamilyDefinition family)
        {
            family = null;
            var config = PlanetShipFamilyConfig.LoadDefault();
            if (config == null || config.families == null)
                return false;
            int idx = ship.ShipFamilyConfigIndex;
            if (idx < 0 || idx >= config.families.Count)
                idx = 0;
            family = config.families[idx] != null ? config.families[idx].shipFamilyDefinition : null;
            return family != null;
        }
    }
}
