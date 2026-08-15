using System.Collections.Generic;
using TitanOrbit.Data;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Owned damage banks: hull family default plus each equipped weapon's source-family bank.
    /// Heal / EnergySpheres is never in this set.
    /// </summary>
    public static class BulletBankOwnership
    {
        static readonly List<int> s_Scratch = new List<int>(8);

        /// <summary>
        /// Fills <paramref name="dest"/> with unique owned damage bank indices (sorted).
        /// Returns how many were written.
        /// </summary>
        public static int CollectOwnedDamageBanks(
            EntityManager em,
            Entity shipEntity,
            int[] dest)
        {
            if (dest == null || dest.Length == 0)
                return 0;

            s_Scratch.Clear();
            var config = UnityEngine.Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            ShipFamilyDefinition hullFamily = ResolveHullFamily(em, shipEntity, config);
            int hullBank = BulletBankProfileUtility.ResolveBankIndexForFamily(hullFamily);
            AddUniqueDamageBank(s_Scratch, hullBank);

            if (em.HasBuffer<EquippedEquipmentElement>(shipEntity))
            {
                var equipment = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
                for (int i = 0; i < equipment.Length; i++)
                {
                    string id = equipment[i].ComponentId.ToString();
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    if (!ShipComponentAbilityStats.IsWeaponComponent(id))
                        continue;
                    AddUniqueDamageBank(s_Scratch, BulletBankProfileUtility.ResolveBankIndexForComponent(id, config));
                }
            }

            s_Scratch.Sort();
            int count = 0;
            for (int i = 0; i < s_Scratch.Count && count < dest.Length; i++)
                dest[count++] = s_Scratch[i];
            return count;
        }

        /// <summary>Next owned damage bank after <paramref name="current"/>, or current when only one.</summary>
        public static int NextOwnedDamageBank(EntityManager em, Entity shipEntity, int current)
        {
            int[] dest = s_NextScratch;
            int count = CollectOwnedDamageBanks(em, shipEntity, dest);
            if (count <= 0)
                return current < 0 ? 0 : current;
            if (count == 1)
                return dest[0];

            int idx = 0;
            for (int i = 0; i < count; i++)
            {
                if (dest[i] == current)
                {
                    idx = i;
                    break;
                }
            }

            return dest[(idx + 1) % count];
        }

        static readonly int[] s_NextScratch = new int[16];

        static void AddUniqueDamageBank(List<int> list, int bankIndex)
        {
            if (bankIndex < 0 ||
                BulletBankProfileUtility.IsHealBankIndex(bankIndex) ||
                BulletBankProfileUtility.IsStoreReservedBankIndex(bankIndex))
                return;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == bankIndex)
                    return;
            }

            list.Add(bankIndex);
        }

        static ShipFamilyDefinition ResolveHullFamily(
            EntityManager em,
            Entity shipEntity,
            PlanetShipFamilyConfig config)
        {
            if (config == null)
                return null;
            int familyIndex = 0;
            if (em.HasComponent<ShipState>(shipEntity))
                familyIndex = em.GetComponentData<ShipState>(shipEntity).ShipFamilyConfigIndex;
            var entry = config.GetFamilyByConfigIndex(familyIndex);
            return entry != null ? entry.shipFamilyDefinition : null;
        }
    }
}
