using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server + shared helpers for per-planet MEGA slot buffers: roll any three armed
    /// catalog hulls at match start (firepower &gt; 0), occupy / free unique hulls, and
    /// resolve chassis ids for UI and purchase. Unarmed hulls stay in the catalog.
    /// <see cref="TryFindCatalogOccupant"/> finds the living owner of a catalog row
    /// even when two planets rolled the same hull after the armed pool wrapped.
    /// Paired with <see cref="PlanetMegaShipSlotElement"/> on the planet ghost.
    /// </summary>
    public static class MegaShipPlanetLogic
    {
        /// <summary>Always three L7 slots (left / center / right tree branches).</summary>
        public const int SlotCount = 3;

        /// <summary>
        /// True when this EntityManager is a client world and Instantiates/join gates forbid gathers.
        /// Server worlds always return false so occupancy / death restore stay authoritative.
        /// </summary>
        static bool ShouldRefuseClientGathers(EntityManager em)
        {
            var world = em.World;
            if (world != null && world.IsServer())
                return false;

            return ClientJoinSettleCache.ShouldSkipShipEntityQueries ||
                   ClientJoinSettleCache.ShouldSkipMapBodyQueries;
        }

        /// <summary>
        /// True when this store planet may sell Titans (the level-7 MEGA hulls).
        /// The world must already be level 6 — the highest planet level — and its
        /// surface gem bank (<see cref="PlanetState.CurrentGems"/>) must sit on that
        /// level's cap. Reaching level 6 empties the bank, so a fresh level-6 world
        /// does not sell Titans until players fill the bar again.
        /// Moon-body hits expel those stored gems as pickups. Any drop under the cap
        /// locks the column until deposits put the bank back to max.
        /// Crown turret level 7 is a different gate: it still uses the moon combat
        /// reservoir via <see cref="PlanetaryDefenseMath.IsCrownTurretUnlocked"/>.
        /// </summary>
        /// <param name="planetLevel">
        /// Store planet level from <see cref="PlanetState.PlanetLevel"/>.
        /// </param>
        /// <param name="currentPlanetGems">
        /// Gems stored on the planet. This is the number the moon label shows, not
        /// <see cref="PlanetGemMoonState.CurrentMoonGems"/>.
        /// </param>
        public static bool IsMegaPurchaseUnlocked(int planetLevel, float currentPlanetGems)
        {
            // --- Level 6 only ---
            // A full bar below 6 levels the planet up and zeroes the bank in the same
            // deposit, so Titans cannot open on a world that is still climbing.
            if (planetLevel < PlanetEconomyMath.MaxPlanetLevel)
                return false;

            // --- Bank must sit on the level-6 cap ---
            // Same epsilon the deposit math uses when it decides the bar is full.
            // Level 6 is 100 × 2^5 = 3200. Damage that expels even a few gems fails this.
            float maxGems = PlanetEconomyMath.GetMaxGemsForLevel(planetLevel);
            if (maxGems <= PlanetaryDefenseMath.MoonGemFullEpsilon)
                return false;

            return currentPlanetGems >= maxGems - PlanetaryDefenseMath.MoonGemFullEpsilon;
        }

        /// <summary>
        /// Ensures the planet has a 3-slot MEGA buffer (empty catalog indices until rolled).
        /// </summary>
        public static DynamicBuffer<PlanetMegaShipSlotElement> EnsureSlots(EntityManager em, Entity planetEntity)
        {
            if (!em.HasBuffer<PlanetMegaShipSlotElement>(planetEntity))
                em.AddBuffer<PlanetMegaShipSlotElement>(planetEntity);

            var buffer = em.GetBuffer<PlanetMegaShipSlotElement>(planetEntity);
            if (buffer.Length != SlotCount)
            {
                buffer.Clear();
                for (byte i = 0; i < SlotCount; i++)
                {
                    buffer.Add(new PlanetMegaShipSlotElement
                    {
                        SlotIndex = i,
                        CatalogIndex = 0,
                        OccupiedByNetworkId = 0,
                    });
                }
            }

            return buffer;
        }

        /// <summary>
        /// Writes three catalog indices into the planet's L7 slots (tree branches 0 / 1 / 2).
        /// Any mix of visual families is valid — the match roll draws from the armed pool.
        /// </summary>
        public static void AssignRolledTrio(
            DynamicBuffer<PlanetMegaShipSlotElement> buffer,
            ushort slot0Index,
            ushort slot1Index,
            ushort slot2Index)
        {
            WriteSlot(buffer, 0, slot0Index);
            WriteSlot(buffer, 1, slot1Index);
            WriteSlot(buffer, 2, slot2Index);
        }

        /// <summary>Reads catalog index + occupancy for one L7 branch.</summary>
        public static bool TryGetSlot(
            EntityManager em,
            Entity planetEntity,
            int branchIndex,
            out PlanetMegaShipSlotElement slot)
        {
            slot = default;
            if (branchIndex < 0 || branchIndex >= SlotCount)
                return false;
            if (!em.HasBuffer<PlanetMegaShipSlotElement>(planetEntity))
                return false;

            var buffer = em.GetBuffer<PlanetMegaShipSlotElement>(planetEntity);
            if (branchIndex >= buffer.Length)
                return false;

            slot = buffer[branchIndex];
            return true;
        }

        /// <summary>Finds a planet entity by stable PlanetId.</summary>
        public static bool TryFindPlanetById(EntityManager em, int planetId, out Entity planetEntity)
        {
            planetEntity = Entity.Null;
            if (planetId <= 0)
                return false;
            if (ShouldRefuseClientGathers(em))
                return false;

            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                planetEntity = entities[i];
                return true;
            }

            return false;
        }

        /// <summary>Chassis id for a planet's L7 branch, or null when unrolled / missing.</summary>
        public static bool TryGetChassisId(
            EntityManager em,
            int planetId,
            int branchIndex,
            out string chassisId,
            out ushort catalogIndex,
            out int occupiedByNetworkId)
        {
            chassisId = null;
            catalogIndex = 0;
            occupiedByNetworkId = 0;
            if (!TryFindPlanetById(em, planetId, out Entity planetEntity))
                return false;
            if (!TryGetSlot(em, planetEntity, branchIndex, out var slot))
                return false;

            catalogIndex = slot.CatalogIndex;
            occupiedByNetworkId = slot.OccupiedByNetworkId;
            chassisId = MegaShipCatalog.FormatChassisId(catalogIndex);
            return true;
        }

        /// <summary>
        /// Finds the player flying this unique MEGA hull, if anyone is.
        /// MEGAs are unique in a match: one living owner per catalog index, even when
        /// the armed pool wrapped and two planets rolled the same hull.
        /// </summary>
        /// <param name="em">Server or client EntityManager that has planet ghosts.</param>
        /// <param name="catalogIndex">Index into <c>MegaShipCatalog.entries</c>.</param>
        /// <param name="occupiedByNetworkId">GhostOwner NetworkId of the owner, or 0 when free.</param>
        /// <returns>True when an owner was found (occupancy &gt; 0).</returns>
        public static bool TryFindCatalogOccupant(
            EntityManager em,
            ushort catalogIndex,
            out int occupiedByNetworkId)
        {
            occupiedByNetworkId = 0;
            if (ShouldRefuseClientGathers(em))
                return false;

            // --- Scan every planet's 3-slot MEGA buffer ---
            // [TITAN-ORBIT] Occupancy is stored on the selling planet, but uniqueness is
            // per catalog row. A second planet that rolled the same hull must still show
            // as owned so nobody else can buy the duplicate card.
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetMegaShipSlotElement));
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var buffer = em.GetBuffer<PlanetMegaShipSlotElement>(entities[i]);
                for (int s = 0; s < buffer.Length; s++)
                {
                    var slot = buffer[s];
                    if (slot.CatalogIndex != catalogIndex || slot.OccupiedByNetworkId == 0)
                        continue;

                    occupiedByNetworkId = slot.OccupiedByNetworkId;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Marks a slot occupied by the buyer. Returns false if already taken.</summary>
        public static bool TryOccupySlot(
            EntityManager em,
            int planetId,
            int branchIndex,
            int networkId)
        {
            if (networkId <= 0)
                return false;
            if (!TryFindPlanetById(em, planetId, out Entity planetEntity))
                return false;
            if (!em.HasBuffer<PlanetMegaShipSlotElement>(planetEntity))
                return false;

            var buffer = em.GetBuffer<PlanetMegaShipSlotElement>(planetEntity);
            if (branchIndex < 0 || branchIndex >= buffer.Length)
                return false;

            var slot = buffer[branchIndex];
            if (slot.OccupiedByNetworkId != 0 && slot.OccupiedByNetworkId != networkId)
                return false;

            slot.OccupiedByNetworkId = networkId;
            buffer[branchIndex] = slot;
            return true;
        }

        /// <summary>
        /// Clears one bay only when <paramref name="networkId"/> is the player in it.
        /// A stale ghost must not kick someone who bought that Titan after this hull
        /// stopped being the owner.
        /// </summary>
        /// <param name="em">Server entity manager.</param>
        /// <param name="planetId">Planet that sells the bay.</param>
        /// <param name="branchIndex">L7 branch 0 / 1 / 2.</param>
        /// <param name="networkId">GhostOwner id that is allowed to release it.</param>
        public static void FreeSlotIfOccupiedBy(EntityManager em, int planetId, int branchIndex, int networkId)
        {
            if (networkId <= 0)
                return;
            // TryGetSlot wants the planet entity. PlanetId is the stable match id.
            if (!TryFindPlanetById(em, planetId, out Entity planetEntity))
                return;
            if (!TryGetSlot(em, planetEntity, branchIndex, out PlanetMegaShipSlotElement slot))
                return;
            if (slot.OccupiedByNetworkId != networkId)
                return;
            FreeSlot(em, planetId, branchIndex);
        }

        /// <summary>Clears occupancy for one planet slot (MEGA destroyed or owner left).</summary>
        public static void FreeSlot(EntityManager em, int planetId, int branchIndex)
        {
            if (!TryFindPlanetById(em, planetId, out Entity planetEntity))
                return;
            if (!em.HasBuffer<PlanetMegaShipSlotElement>(planetEntity))
                return;

            var buffer = em.GetBuffer<PlanetMegaShipSlotElement>(planetEntity);
            if (branchIndex < 0 || branchIndex >= buffer.Length)
                return;

            var slot = buffer[branchIndex];
            slot.OccupiedByNetworkId = 0;
            buffer[branchIndex] = slot;
        }

        /// <summary>Clears any slot occupied by this network id (disconnect / abandon).</summary>
        public static void FreeSlotsOccupiedBy(EntityManager em, int networkId)
        {
            // keepBranchIndex -1 matches no real L7 branch, so every bay this player holds is cleared.
            FreeSlotsOccupiedByExcept(em, networkId, keepPlanetId: 0, keepBranchIndex: -1);
        }

        /// <summary>
        /// Drops every Titan bay this player still holds except the one they just claimed.
        /// A player may fly only one Titan. The purchase writes the new bay first, then
        /// calls this so the previous card goes back on sale.
        /// </summary>
        /// <param name="em">Server entity manager. Client gathers during join are skipped.</param>
        /// <param name="networkId">GhostOwner id of the player who just took a Titan.</param>
        /// <param name="keepPlanetId">Planet whose bay must stay occupied.</param>
        /// <param name="keepBranchIndex">L7 branch 0 / 1 / 2 on that planet.</param>
        public static void FreeSlotsOccupiedByExcept(
            EntityManager em,
            int networkId,
            int keepPlanetId,
            int keepBranchIndex)
        {
            if (networkId <= 0)
                return;
            if (ShouldRefuseClientGathers(em))
                return;

            // --- Every planet's three Titan cards ---
            // [TITAN-ORBIT] Occupancy lives on the selling planet, not on the ship.
            // The ship we are updating may not be the ghost that originally claimed
            // the old bay (a newer hull, or a purchase that never stored IsMega).
            // Scanning by owner id releases that bay anyway.
            // PlanetTag + the Titan buffer, same set disconnect cleanup already walks.
            // Planet id is read per entity so a bay can be spared without dropping
            // a planet that has the buffer but a different chunk layout.
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetMegaShipSlotElement));
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                int planetId = 0;
                if (em.HasComponent<PlanetState>(entities[i]))
                    planetId = em.GetComponentData<PlanetState>(entities[i]).PlanetId;
                var buffer = em.GetBuffer<PlanetMegaShipSlotElement>(entities[i]);
                for (int s = 0; s < buffer.Length; s++)
                {
                    var slot = buffer[s];
                    if (slot.OccupiedByNetworkId != networkId)
                        continue;
                    // The bay we just claimed stays taken. Every other card with this
                    // player's id is the Titan they are leaving.
                    if (planetId == keepPlanetId && s == keepBranchIndex)
                        continue;
                    slot.OccupiedByNetworkId = 0;
                    buffer[s] = slot;
                }
            }
        }

        /// <summary>
        /// After a Titan swap, clears <see cref="MegaShipState.IsMega"/> on any older
        /// hull that still says it is a Titan. The live hull
        /// (<paramref name="keepShip"/>) is left alone — it is the ship that just
        /// took the new bay. A leftover ghost used to keep the old card "in service"
        /// after the menu had already moved the player onto the new hull.
        /// </summary>
        /// <param name="em">Server entity manager.</param>
        /// <param name="networkId">Owner whose extra hulls should drop the Titan flag.</param>
        /// <param name="keepShip">The hull that should stay a Titan.</param>
        /// <param name="keepPlanetId">Planet of the bay that hull just claimed.</param>
        /// <param name="keepBranchIndex">Branch of that bay.</param>
        public static void ClearSiblingMegaHulls(
            EntityManager em,
            int networkId,
            Entity keepShip,
            int keepPlanetId,
            int keepBranchIndex)
        {
            if (networkId <= 0)
                return;
            if (ShouldRefuseClientGathers(em))
                return;

            using var query = em.CreateEntityQuery(
                typeof(ShipTag), typeof(GhostOwner), typeof(MegaShipState));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                Entity ship = entities[i];
                if (ship == keepShip)
                    continue;

                var mega = em.GetComponentData<MegaShipState>(ship);
                if (!mega.IsMega)
                    continue;

                // A sibling that still points at the bay we just kept must not
                // clear it. Any other recorded bay is the Titan they left.
                bool sameBay = mega.StorePlanetId == keepPlanetId
                    && mega.MegaSlotIndex == keepBranchIndex;
                if (!sameBay)
                    FreeSlotIfOccupiedBy(em, mega.StorePlanetId, mega.MegaSlotIndex, networkId);

                mega.IsMega = false;
                mega.CatalogIndex = 0;
                mega.StorePlanetId = 0;
                mega.MegaSlotIndex = 0;
                em.SetComponentData(ship, mega);
            }
        }

        /// <summary>
        /// Draws one unused index from <paramref name="pool"/> using <paramref name="rng"/>.
        /// When the unused set is empty, wraps and reuses the same hulls.
        /// </summary>
        public static ushort DrawFromPool(NativeList<ushort> pool, ref NativeList<int> usedFlags, ref Random rng)
        {
            if (pool.Length == 0)
                return 0;

            int unusedCount = 0;
            for (int i = 0; i < pool.Length; i++)
            {
                if (usedFlags[i] == 0)
                    unusedCount++;
            }

            if (unusedCount <= 0)
            {
                for (int i = 0; i < usedFlags.Length; i++)
                    usedFlags[i] = 0;
                unusedCount = pool.Length;
            }

            int pick = rng.NextInt(0, unusedCount);
            int seen = 0;
            for (int i = 0; i < pool.Length; i++)
            {
                if (usedFlags[i] != 0)
                    continue;
                if (seen == pick)
                {
                    usedFlags[i] = 1;
                    return pool[i];
                }

                seen++;
            }

            return pool[0];
        }

        static void WriteSlot(DynamicBuffer<PlanetMegaShipSlotElement> buffer, byte slotIndex, ushort catalogIndex)
        {
            if (slotIndex >= buffer.Length)
                return;

            buffer[slotIndex] = new PlanetMegaShipSlotElement
            {
                SlotIndex = slotIndex,
                CatalogIndex = catalogIndex,
                OccupiedByNetworkId = 0,
            };
        }
    }
}
