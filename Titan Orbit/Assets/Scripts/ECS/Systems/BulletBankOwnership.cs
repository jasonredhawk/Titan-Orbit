using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Data;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One HUD / cycle row: a <c>BulletVfxBank</c> category the ship may show, plus whether
    /// production mode would allow it (hull default or a purchased weapon).
    /// </summary>
    public struct VisibleBankRow
    {
        /// <summary>Category index into <see cref="BulletVfxBank"/>.</summary>
        public int BankIndex;

        /// <summary>True when this bank is on the hull or a purchased weapon component.</summary>
        public bool IsOwned;
    }

    /// <summary>
    /// Owned damage banks: hull family default plus each equipped weapon's source-family bank.
    /// Heal / EnergySpheres is never in the production set. Cycle-all (GameManager Test) adds
    /// every non-reserved catalog category so B and the HUD walk the full bank list.
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

        /// <summary>
        /// Banks the HUD and B-key should show right now. Production = owned damage only.
        /// Cycle-all = every non-reserved catalog category (including EnergySpheres), with
        /// <see cref="VisibleBankRow.IsOwned"/> marked so testers see what Production allows.
        /// </summary>
        /// <param name="em">World that owns <paramref name="shipEntity"/> (client ghost or server).</param>
        /// <param name="shipEntity">Local ship whose loadout we read.</param>
        /// <param name="dest">Caller-owned buffer. Writes stop at dest.Length.</param>
        /// <returns>How many rows were written.</returns>
        public static int CollectVisibleBankRows(
            EntityManager em,
            Entity shipEntity,
            VisibleBankRow[] dest)
        {
            if (dest == null || dest.Length == 0)
                return 0;

            int[] owned = s_OwnedScratch;
            int ownedCount = CollectOwnedDamageBanks(em, shipEntity, owned);

            if (!TitanOrbitDebugFlags.CycleAllBulletBanks)
            {
                int count = 0;
                for (int i = 0; i < ownedCount && count < dest.Length; i++)
                {
                    dest[count++] = new VisibleBankRow
                    {
                        BankIndex = owned[i],
                        IsOwned = true
                    };
                }

                return count;
            }

            // --- Test / cycle-all ---
            // Walk the catalog so B and tiles share one list. Skip store Rockets.
            var bank = BulletVfxBank.LoadDefault();
            int categoryCount = bank != null ? bank.CategoryCount : 0;
            int written = 0;
            for (int i = 0; i < categoryCount && written < dest.Length; i++)
            {
                if (BulletBankProfileUtility.IsStoreReservedBankIndex(i))
                    continue;

                bool isOwned = false;
                for (int o = 0; o < ownedCount; o++)
                {
                    if (owned[o] == i)
                    {
                        isOwned = true;
                        break;
                    }
                }

                dest[written++] = new VisibleBankRow
                {
                    BankIndex = i,
                    IsOwned = isOwned
                };
            }

            return written;
        }

        /// <summary>
        /// True when a HUD click / SetBulletBank may write this index onto the ship.
        /// Cycle-all accepts any non-reserved catalog bank. Production requires ownership.
        /// </summary>
        public static bool IsSelectableBank(EntityManager em, Entity shipEntity, int bankIndex)
        {
            if (bankIndex < 0)
                return false;
            if (BulletBankProfileUtility.IsStoreReservedBankIndex(bankIndex))
                return false;

            if (TitanOrbitDebugFlags.CycleAllBulletBanks)
            {
                var bank = BulletVfxBank.LoadDefault();
                int categoryCount = bank != null ? bank.CategoryCount : 0;
                return bankIndex < categoryCount;
            }

            int[] owned = s_OwnedScratch;
            int ownedCount = CollectOwnedDamageBanks(em, shipEntity, owned);
            for (int i = 0; i < ownedCount; i++)
            {
                if (owned[i] == bankIndex)
                    return true;
            }

            return false;
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

        /// <summary>Scratch for ownership checks shared by visible-row and selectable helpers.</summary>
        static readonly int[] s_OwnedScratch = new int[16];

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
