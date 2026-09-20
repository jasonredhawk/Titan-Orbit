using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

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

        /// <summary>
        /// True for the hull family's default gun (always the first owned row).
        /// The HUD labels this tile with the local ship family, not "whoever authored the bank first".
        /// </summary>
        public bool IsHullDefault;
    }

    /// <summary>
    /// Owned damage banks: hull family default first (Titan: first catalog Gun bank),
    /// then each purchased weapon's bank. Heal / EnergySpheres is never in the
    /// production set. Cycle-all (GameManager Test) walks every non-reserved catalog
    /// category so B and the HUD stay on the same list.
    /// </summary>
    public static class BulletBankOwnership
    {
        static readonly List<int> s_Scratch = new List<int>(8);

        /// <summary>
        /// Fills <paramref name="dest"/> with unique owned damage bank indices
        /// (hull default first, then purchases). Returns how many were written.
        /// </summary>
        public static int CollectOwnedDamageBanks(
            EntityManager em,
            Entity shipEntity,
            int[] dest)
        {
            if (dest == null || dest.Length == 0)
                return 0;

            s_Scratch.Clear();
            var config = PlanetShipFamilyConfig.LoadDefault();
            ResolveHullDefault(em, shipEntity, config, out _, out int hullBank);

            // --- Hull default first ---
            // [TITAN-ORBIT] Do not sort. The HUD and B-key walk hull, then purchases.
            // Planet-stamped HullBulletBankIndex wins; family Laserbolt is the fallback.
            // Sanitize remaps heal / rocket authors to 0 so this always yields a damage bank.
            AddUniqueDamageBank(s_Scratch, hullBank);
            if (s_Scratch.Count == 0)
                s_Scratch.Add(0);

            if (em.HasBuffer<EquippedEquipmentElement>(shipEntity))
            {
                var equipment = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
                for (int i = 0; i < equipment.Length; i++)
                {
                    EquippedEquipmentElement item = equipment[i];
                    if ((StoreItemType)item.ItemType != StoreItemType.ShipComponent)
                        continue;

                    string id = item.ComponentId.ToString();
                    if (!IsPurchasedWeaponComponent(id))
                        continue;
                    AddUniqueDamageBank(
                        s_Scratch,
                        ResolvePurchasedWeaponBank(em, id, config, hullBank));
                }
            }

            int count = 0;
            for (int i = 0; i < s_Scratch.Count && count < dest.Length; i++)
                dest[count++] = s_Scratch[i];
            return count;
        }

        /// <summary>
        /// Banks the HUD lists right now. Production = hull default + purchased weapons.
        /// Cycle-all = every non-reserved catalog category (same walk as B).
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
                        IsOwned = true,
                        IsHullDefault = i == 0
                    };
                }

                return count;
            }

            // --- Test / cycle-all ---
            // B and the HUD must walk the same catalog. Owned-only tiles parked the
            // caret on the hull gun while fire used EnergySpheres / empty banks —
            // looked like every owned weapon had jammed.
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
                    IsOwned = isOwned,
                    IsHullDefault = ownedCount > 0 && owned[0] == i
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

        /// <summary>
        /// Next bank on the same list the Weapons HUD paints. Production walks hull
        /// default then purchases. Cycle-all walks every non-reserved catalog row.
        /// When <paramref name="current"/> is missing from that list, treat the caret
        /// as parked on the first row (same as the HUD) and step to the second.
        /// </summary>
        /// <param name="em">World that owns <paramref name="shipEntity"/>.</param>
        /// <param name="shipEntity">Ship whose visible banks we walk.</param>
        /// <param name="current">Bank the player is on (runtime index or optimistic HUD caret).</param>
        /// <returns>The next visible bank, or <paramref name="current"/> when the list is empty.</returns>
        public static int NextVisibleBank(EntityManager em, Entity shipEntity, int current)
        {
            // --- Same rows as BulletTypeHUD ---
            // [TITAN-ORBIT] B and the left-side WEAPONS strip must share this walk.
            // NextOwnedDamageBank is production-only; cycle-all used a catalog increment
            // that could land on a row the 16-tile HUD never painted.
            int count = CollectVisibleBankRows(em, shipEntity, s_VisibleScratch);
            if (count <= 0)
                return current < 0 ? 0 : current;
            if (count == 1)
                return s_VisibleScratch[0].BankIndex;

            // --- Find the live row ---
            // Missing current → idx 0. HUD already parks the caret there, so B
            // advances to row 1 instead of snapping to a type the player thinks
            // they already have selected.
            int idx = 0;
            for (int i = 0; i < count; i++)
            {
                if (s_VisibleScratch[i].BankIndex == current)
                {
                    idx = i;
                    break;
                }
            }

            return s_VisibleScratch[(idx + 1) % count].BankIndex;
        }

        static readonly int[] s_NextScratch = new int[16];

        /// <summary>
        /// Scratch for <see cref="NextVisibleBank"/>. Length matches
        /// <c>BulletTypeHUD</c> MaxRows so B cannot pick a bank the strip never shows.
        /// </summary>
        static readonly VisibleBankRow[] s_VisibleScratch = new VisibleBankRow[16];

        /// <summary>Scratch for ownership checks shared by visible-row and selectable helpers.</summary>
        static readonly int[] s_OwnedScratch = new int[16];

        /// <summary>
        /// True for a Moon Orbit extra part that should add a fire-type row.
        /// Name match plus Part Profile (Weapon Bullet / Cannon / …) so family-prefixed
        /// store ids like CosmicShark_Gun_2 still count.
        /// </summary>
        static bool IsPurchasedWeaponComponent(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return false;
            if (ShipComponentAbilityStats.IsWeaponComponent(componentId))
                return true;

            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);
            return ShipFamilyPartTypes.IsWeapon(partType);
        }

        /// <summary>
        /// Purchased inherit guns use the planet that rolled their source family,
        /// not the current hull stamp. Buying a Cosmic Shark gun at a Fireballs world
        /// while flying a Laserbolt home hull must add Fireballs to B-key.
        /// </summary>
        static int ResolvePurchasedWeaponBank(
            EntityManager em,
            string componentId,
            PlanetShipFamilyConfig config,
            int hullFallback)
        {
            if (!BulletBankProfileUtility.TryFindComponentInAnyFamily(
                    componentId, out ShipFamilyComponentEntry entry, out ShipFamilyDefinition family,
                    out int familyIndex, config))
            {
                return BulletBankProfileUtility.ResolveBankIndexForComponent(
                    componentId, config, hullFallback);
            }

            int sourceBank = ResolvePlanetBankForFamilyIndex(em, familyIndex, hullFallback);
            return BulletBankProfileUtility.ResolveBankIndexForComponentEntry(entry, family, sourceBank);
        }

        static int s_FamilyPlanetBankFrame = -1;
        static int[] s_FamilyPlanetBanks;

        /// <summary>
        /// One planet walk per frame: family config index → that world's rolled gun.
        /// Home family (0) is always Laserbolt. Missing neutrals fall back to
        /// <paramref name="hullFallback"/>.
        /// </summary>
        static int ResolvePlanetBankForFamilyIndex(EntityManager em, int familyIndex, int hullFallback)
        {
            if (familyIndex <= PlanetShipFamilyAssignment.HomeFamilyConfigIndex)
                return PlanetShipFamilyAssignment.DefaultBulletBankIndex;

            EnsureFamilyPlanetBanks(em);
            if (s_FamilyPlanetBanks != null
                && familyIndex >= 0
                && familyIndex < s_FamilyPlanetBanks.Length
                && s_FamilyPlanetBanks[familyIndex] >= 0)
            {
                return PlanetShipFamilyAssignment.SanitizeSelectableDamageBank(
                    s_FamilyPlanetBanks[familyIndex]);
            }

            return hullFallback >= 0
                ? PlanetShipFamilyAssignment.SanitizeSelectableDamageBank(hullFallback)
                : PlanetShipFamilyAssignment.DefaultBulletBankIndex;
        }

        static void EnsureFamilyPlanetBanks(EntityManager em)
        {
            int frame = Time.frameCount;
            if (s_FamilyPlanetBankFrame == frame && s_FamilyPlanetBanks != null)
                return;

            s_FamilyPlanetBankFrame = frame;
            if (s_FamilyPlanetBanks == null)
                s_FamilyPlanetBanks = new int[16];
            for (int i = 0; i < s_FamilyPlanetBanks.Length; i++)
                s_FamilyPlanetBanks[i] = -1;
            s_FamilyPlanetBanks[PlanetShipFamilyAssignment.HomeFamilyConfigIndex] =
                PlanetShipFamilyAssignment.DefaultBulletBankIndex;

            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<PlanetState>());
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                var planet = states[i];
                int idx = planet.IsHomePlanet
                    ? PlanetShipFamilyAssignment.HomeFamilyConfigIndex
                    : planet.ShipFamilyConfigIndex;
                if (idx < 0 || idx >= s_FamilyPlanetBanks.Length)
                    continue;
                if (s_FamilyPlanetBanks[idx] >= 0)
                    continue;
                s_FamilyPlanetBanks[idx] = planet.IsHomePlanet
                    ? PlanetShipFamilyAssignment.DefaultBulletBankIndex
                    : PlanetShipFamilyAssignment.SanitizeSelectableDamageBank(planet.BulletBankIndex);
            }
        }

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

        /// <summary>
        /// Family row plus the gun this hull actually starts with.
        /// Planet roll on <c>ShipState.HullBulletBankIndex</c> wins; missing stamp uses
        /// the family's Laserbolt fallback.
        /// </summary>
        static void ResolveHullDefault(
            EntityManager em,
            Entity shipEntity,
            PlanetShipFamilyConfig config,
            out ShipFamilyDefinition family,
            out int hullBank)
        {
            family = null;
            hullBank = PlanetShipFamilyAssignment.DefaultBulletBankIndex;
            if (em.HasComponent<MegaShipState>(shipEntity)
                && em.GetComponentData<MegaShipState>(shipEntity).IsMega)
            {
                var catalog = MegaShipCatalog.Load();
                var mega = em.GetComponentData<MegaShipState>(shipEntity);
                if (catalog != null
                    && catalog.TryGetEntry(mega.CatalogIndex, out MegaShipCatalogEntry entry)
                    && catalog.TryGetFirstGunBankIndex(entry, out int gunBank))
                {
                    hullBank = gunBank;
                    return;
                }

                if (catalog != null)
                    hullBank = catalog.GetTypeTableBankIndex(ShipFamilyPartTypes.WeaponBullet);
                return;
            }

            int familyIndex = 0;
            byte stampedBank = PlanetShipFamilyAssignment.DefaultBulletBankIndex;
            bool hasStamp = false;
            if (em.HasComponent<ShipState>(shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(shipEntity);
                familyIndex = ship.ShipFamilyConfigIndex;
                stampedBank = ship.HullBulletBankIndex;
                hasStamp = true;
            }

            if (config != null)
            {
                var entry = config.GetFamilyByConfigIndex(familyIndex);
                family = entry != null ? entry.shipFamilyDefinition : null;
            }

            hullBank = hasStamp
                ? PlanetShipFamilyAssignment.SanitizeSelectableDamageBank(stampedBank)
                : BulletBankProfileUtility.ResolveBankIndexForFamily(family);
        }
    }
}
