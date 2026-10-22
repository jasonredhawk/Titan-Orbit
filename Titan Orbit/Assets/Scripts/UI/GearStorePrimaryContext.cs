using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using Unity.Entities;

namespace TitanOrbit.UI
{
    /// <summary>
    /// One loadout snapshot for the gear grid. Prepared when the store builds or
    /// refreshes, then each card asks whether it is the best of its type.
    /// Menu only — not a per-frame path.
    /// </summary>
    static class GearStorePrimaryContext
    {
        static readonly List<string> Ids = new List<string>(24);
        static readonly List<ShipComponentAbilityStats> Stats = new List<ShipComponentAbilityStats>(24);
        static readonly List<ShipFamilySpecialBonuses> Fleets = new List<ShipFamilySpecialBonuses>(24);
        static ShipSpeedometerStatTooltips.PartCache Cache;
        static ShipFamilySpecialBonuses PurchaseFleet;
        static bool Ready;

        /// <summary>
        /// Reads the local ship and the open moon. Cards stay unclassified when
        /// either is missing, so the grid keeps the full STAT numbers.
        /// </summary>
        public static void Prepare()
        {
            Ready = false;
            Ids.Clear();
            Stats.Clear();
            Fleets.Clear();
            PurchaseFleet = ShipFamilySpecialBonuses.Identity;

            if (!TryResolvePurchaseFleet(out PurchaseFleet))
                return;

            World world = EcsGameBridge.GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return;

            EntityManager em = world.EntityManager;
            if (!TryResolveLocalShip(em, out Entity ship) || !em.HasComponent<ShipState>(ship))
                return;

            ShipState shipState = em.GetComponentData<ShipState>(ship);
            if (!TryResolveChassisId(shipState, out string chassisId))
                return;
            if (!ShipSpeedometerStatTooltips.TryRefreshPartCache(
                    em, ship, chassisId, shipState.ShipLevel, ref Cache)
                || !Cache.Valid
                || Cache.Ids == null
                || Cache.Stats == null)
                return;

            int idCount = Cache.Ids.Count;
            int statCount = Cache.Stats.Count;
            int n = idCount < statCount ? idCount : statCount;
            for (int i = 0; i < n; i++)
            {
                Ids.Add(Cache.Ids[i]);
                Stats.Add(Cache.Stats[i]);
                Fleets.Add(FleetForRow(i, shipState, Cache.Family));
            }

            Ready = true;
        }

        public static ShipComponentStackAggregation.GearCardRole Classify(ShipFamilyComponentEntry entry)
        {
            if (!Ready || entry == null)
                return ShipComponentStackAggregation.GearCardRole.Unclassified;

            return ShipComponentStackAggregation.ClassifyStoreCard(
                entry.componentId,
                entry.stats,
                PurchaseFleet,
                Ids,
                Stats,
                Fleets);
        }

        /// <summary>
        /// Best owned piece of this card’s type. True when that piece is the card itself.
        /// False when nothing of the type is owned yet.
        /// </summary>
        public static bool TryGetOwnedPrimary(
            ShipFamilyComponentEntry entry,
            out string componentId,
            out ShipComponentAbilityStats raw,
            out ShipFamilySpecialBonuses fleet)
        {
            componentId = null;
            raw = default;
            fleet = ShipFamilySpecialBonuses.Identity;
            if (!Ready || entry == null)
                return false;
            if (!ShipComponentStackAggregation.TryFindOwnedPrimary(
                    entry.componentId, Ids, Stats, Fleets, out int index))
                return false;

            componentId = Ids[index];
            raw = Stats[index];
            fleet = Fleets[index];
            return !string.IsNullOrWhiteSpace(componentId);
        }

        /// <summary>
        /// The owned piece this card would unseat. False when this card already
        /// is that piece, or when nothing of the type is owned yet.
        /// </summary>
        public static bool TryGetDisplacedPrimary(
            ShipFamilyComponentEntry entry,
            out string componentId,
            out ShipComponentAbilityStats raw,
            out ShipFamilySpecialBonuses fleet)
        {
            componentId = null;
            raw = default;
            fleet = ShipFamilySpecialBonuses.Identity;
            if (!Ready || entry == null)
                return false;
            if (!ShipComponentStackAggregation.TryFindDisplacedPrimary(
                    entry.componentId,
                    entry.stats,
                    PurchaseFleet,
                    Ids,
                    Stats,
                    Fleets,
                    out int index))
                return false;

            componentId = Ids[index];
            raw = Stats[index];
            fleet = Fleets[index];
            return !string.IsNullOrWhiteSpace(componentId);
        }

        /// <summary>Hull catalog row for an owned part, when the open moon's family does not list it.</summary>
        public static bool TryGetHullComponentEntry(string componentId, out ShipFamilyComponentEntry entry)
        {
            entry = null;
            if (!Ready || Cache.Family == null)
                return false;
            return Cache.Family.TryGetComponentEntry(componentId, out entry) && entry != null;
        }

        static ShipFamilySpecialBonuses FleetForRow(
            int index,
            ShipState shipState,
            ShipFamilyDefinition hullFamily)
        {
            int familyIndex = -1;
            if (Cache.ComponentFleetFamilyIndex != null
                && index < Cache.ComponentFleetFamilyIndex.Count)
                familyIndex = Cache.ComponentFleetFamilyIndex[index];

            if (familyIndex >= 0)
                return MegaShipStatsCalculator.ResolveFleetBonuses(familyIndex);
            if (hullFamily != null)
                return hullFamily.ResolveSpecialBonuses();
            return MegaShipStatsCalculator.ResolveFleetBonuses(shipState.ShipFamilyConfigIndex);
        }

        static bool TryResolvePurchaseFleet(out ShipFamilySpecialBonuses fleet)
        {
            fleet = ShipFamilySpecialBonuses.Identity;
            int planetId = OrbitStationEcsContext.StorePlanetId;
            if (planetId <= 0
                || !EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState planet))
                return false;

            int familyIndex = planet.IsHomePlanet
                ? PlanetShipFamilyAssignment.HomeFamilyConfigIndex
                : (planet.ShipFamilyConfigIndex > 0
                    ? planet.ShipFamilyConfigIndex
                    : PlanetShipFamilyAssignment.HomeFamilyConfigIndex);
            fleet = MegaShipStatsCalculator.ResolveFleetBonuses(familyIndex);
            return true;
        }

        static bool TryResolveChassisId(ShipState shipState, out string chassisId)
        {
            if (EcsGameBridge.TryGetLocalMegaShipState(out MegaShipState mega))
            {
                chassisId = MegaShipCatalog.FormatChassisId(mega.CatalogIndex);
                return true;
            }

            return ShipStatApplyLogic.TryResolveChassisId(
                shipState.Team,
                shipState.ShipLevel,
                shipState.BranchIndex,
                out chassisId,
                allowFallback: true,
                shipState.ShipFamilyConfigIndex);
        }

        static bool TryResolveLocalShip(EntityManager em, out Entity ship)
        {
            if (LocalShipEntitySeed.TryGetSeededShip(em, out ship)
                && ship != Entity.Null
                && em.Exists(ship))
                return true;

            World world = EcsGameBridge.GetLocalPlayerShipWorld();
            return EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out ship)
                   && ship != Entity.Null
                   && em.Exists(ship);
        }
    }
}
