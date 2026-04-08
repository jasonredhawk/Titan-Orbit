using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Procedural <see cref="CardData"/> used when a <see cref="ShipFamilyDefinition"/> has no <see cref="CardDeckDefinition"/> assigned.
    /// Card IDs are prefixed by family so different families do not collide.
    /// </summary>
    public static class CardDeckRuntimeDefaults
    {
        public static List<CardData> CreateProceduralDeck(string familyIdForCardPrefix)
        {
            string prefix = SanitizeCardIdPrefix(familyIdForCardPrefix);
            var list = new List<CardData>();
            int id = 0;

            CardData Add(string name, string desc, int level, int rar, SlotType slotType, float dmgMul = 1f, float gemAdd = 0f, float energyRegenAdd = 0f, float energyCapAdd = 0f, float healthAdd = 0f, float healthRegenAdd = 0f, float moveAdd = 0f, float rotAdd = 0f, float bulletSpeedMul = 1f, float miningAdd = 0f, float peopleAdd = 0f, float gemDepositSpeedMul = 1f, float peopleTransferSpeedMul = 1f)
            {
                var c = ScriptableObject.CreateInstance<CardData>();
                c.cardId = prefix + "_" + (id++);
                c.displayName = name;
                c.description = desc;
                c.cardLevel = level;
                c.rarity = (CardRarity)Mathf.Clamp(rar, 1, 5);
                c.slotType = slotType;
                c.minHomePlanetLevel = 1;
                c.originPlanetId = 0;
                c.gemCost = 0f;
                c.damageMultiplier = dmgMul;
                c.gemCapacityAdd = gemAdd;
                c.energyRegenAdd = energyRegenAdd;
                c.energyCapacityAdd = energyCapAdd;
                c.maxHealthAdd = healthAdd;
                c.healthRegenAdd = healthRegenAdd;
                c.movementSpeedAdd = moveAdd;
                c.rotationSpeedAdd = rotAdd;
                c.bulletSpeedMultiplier = bulletSpeedMul;
                c.miningRateAdd = miningAdd;
                c.peopleCapacityAdd = peopleAdd;
                c.gemDepositSpeedMultiplier = gemDepositSpeedMul;
                c.peopleTransferSpeedMultiplier = peopleTransferSpeedMul;
                list.Add(c);
                return c;
            }

            string[] rn = { "", "Common", "Uncommon", "Rare", "Epic", "Legendary" };

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
                    float qolMul = CardDeckBalance.QualityOfLifeMultiplier(L, r);
                    Add($"Kinetic Focus {L} ({rn[r]})", $"+{(dmgMul - 1f) * 100f:F1}% weapon damage multiplier.", L, r, SlotType.Weapon, dmgMul: dmgMul);
                    Add($"Aegis Plating {L} ({rn[r]})", $"+{hull:F0} max hull.", L, r, SlotType.Ship, healthAdd: hull);
                    Add($"Cargo Bay {L} ({rn[r]})", $"+{gems:F0} gem capacity.", L, r, SlotType.Cargo, gemAdd: gems);
                    Add($"Shard Projector {L} ({rn[r]})", $"+{(bulletMul - 1f) * 100f:F1}% projectile speed.", L, r, SlotType.Weapon, bulletSpeedMul: bulletMul);
                    Add($"Arc Reactor {L} ({rn[r]})", $"+{eReg:F2} energy/sec.", L, r, SlotType.Ship, energyRegenAdd: eReg);
                    Add($"Capacitor Bank {L} ({rn[r]})", $"+{eCap:F0} energy capacity.", L, r, SlotType.Ship, energyCapAdd: eCap);
                    Add($"Refinery Drones {L} ({rn[r]})", $"+{(qolMul - 1f) * 100f:F1}% gem deposit speed.", L, r, SlotType.Cargo, gemDepositSpeedMul: qolMul);
                    Add($"Transit Uplink {L} ({rn[r]})", $"+{(qolMul - 1f) * 100f:F1}% people transfer speed.", L, r, SlotType.Cargo, peopleTransferSpeedMul: qolMul);
                }

                Add($"Afterburner {L}", $"+{CardDeckBalance.AfterburnerMoveAdd(L):F2} thrust.", L, 3, SlotType.Ship, moveAdd: CardDeckBalance.AfterburnerMoveAdd(L));
                Add($"Gyro Stabilizer {L}", $"+{CardDeckBalance.GyroRotationAdd(L):F0} turn rate.", L, 2, SlotType.Ship, rotAdd: CardDeckBalance.GyroRotationAdd(L));
                Add($"Regen Gel {L}", $"+{CardDeckBalance.RegenGelHealthRegenAdd(L):F3} hull/sec.", L, 2, SlotType.Ship, healthRegenAdd: CardDeckBalance.RegenGelHealthRegenAdd(L));
                Add($"Mining Laser {L}", $"+{CardDeckBalance.MiningRateAdd(L):F2} mining rate.", L, 2, SlotType.Cargo, miningAdd: CardDeckBalance.MiningRateAdd(L));
                Add($"Colony Pod {L}", $"+{CardDeckBalance.ColonyPeopleAdd(L):F0} people capacity.", L, 1, SlotType.Cargo, peopleAdd: CardDeckBalance.ColonyPeopleAdd(L));

                float tfDmg = CardDeckBalance.TitanforgeDamageMul(L);
                float tfHull = CardDeckBalance.TitanforgeHullAdd(L);
                Add($"Titanforge {L} ({rn[5]})", $"+{(tfDmg - 1f) * 100f:F0}% damage and +{tfHull:F0} hull.", L, 5, SlotType.Weapon, dmgMul: tfDmg, healthAdd: tfHull);
            }

            return list;
        }

        private static string SanitizeCardIdPrefix(string familyId)
        {
            if (string.IsNullOrWhiteSpace(familyId)) return "Card";
            var sb = new StringBuilder();
            foreach (char c in familyId.Trim())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else sb.Append('_');
            }
            string s = sb.ToString();
            return string.IsNullOrEmpty(s) ? "Card" : s;
        }
    }
}
