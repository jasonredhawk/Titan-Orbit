using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Power-bar numbers for the Orbit Menu "Your Ship" card.
    /// Tree cards stay at the hull you would buy (ship level, no ability purchases, no gear).
    /// This card is the hull you are flying, so the same ten coloured slots also include
    /// bottom-bar ability purchases and moon-store ship parts already equipped.
    /// Drones, rockets, and mines do not change those slots. Upgrade cards stay on the
    /// CARDS tab and are not added here.
    /// Paired with <see cref="ShipFamilyPowerBarNorm"/> (fresh hull) and
    /// <see cref="ShipStatApplyLogic.EvaluatePartsWithGearFleet"/> (live hull).
    /// Client UI only — it reads the local ghost and does not write sim state.
    /// </summary>
    public static class CurrentShipPowerScore
    {
        // Last successful paint. The Orbit Menu rebuilds this card several times a second.
        // Walking the chassis prefab on every paint hitch the menu, so we reuse the
        // breakdown until the hull, abilities, gear, or match roll actually changes.
        static bool s_hasCache;
        static string s_chassisId;
        static int s_shipLevel;
        static int s_abilityHash;
        static int s_gearHash;
        static int s_matchSeed;
        static ShipFamilyPowerScoreBreakdown s_cached;

        /// <summary>
        /// Ten-stat breakdown for the sidebar ship you are flying.
        /// With no ability purchases and no equipped ship parts, this is the same
        /// purchase-time bar the upgrade tree already shows.
        /// </summary>
        /// <param name="chassisId">Family chassis id, or a MEGA catalog id such as <c>MEGA_007</c>.</param>
        /// <param name="shipLevel">Ghosted ship level (1–6, or 7 on a Titan).</param>
        /// <returns>Combined hull. Default when the chassis cannot be resolved.</returns>
        public static ShipFamilyPowerScoreBreakdown ForCurrentShip(string chassisId, int shipLevel)
        {
            // A Titan's catalog id wins over the family ladder slot that shares its branch.
            if (EcsGameBridge.TryGetLocalMegaShipState(out MegaShipState mega) && mega.IsMega)
            {
                chassisId = MegaShipCatalog.FormatChassisId(mega.CatalogIndex);
                // Titans are level 7. A stale family-ladder level must not Extra-Level gear as an L1 part.
                if (shipLevel < ShipFamilyPowerBarNorm.MegaTreeLevel)
                    shipLevel = ShipFamilyPowerBarNorm.MegaTreeLevel;
            }

            if (string.IsNullOrEmpty(chassisId))
                return default;

            shipLevel = Mathf.Max(1, shipLevel);

            // --- What the player has accumulated on this hull ---
            ShipAttributeUpgradeState attrs = default;
            bool haveAttrs = EcsGameBridge.TryGetLocalShipAttributeUpgrades(out attrs);
            int abilityHash = haveAttrs ? HashAbilities(in attrs) : 0;

            int gearHash = 0;
            bool haveShip = TryGetSeededShip(out EntityManager em, out Entity ship);
            if (haveShip)
                gearHash = HashEquippedShipComponents(em, ship);

            // Match roll changes family multipliers without changing the chassis id.
            int matchSeed = unchecked((int)ShipFamilyMatchBonuses.ActiveMatchSeed);
            if (s_hasCache
                && s_shipLevel == shipLevel
                && s_abilityHash == abilityHash
                && s_gearHash == gearHash
                && s_matchSeed == matchSeed
                && string.Equals(s_chassisId, chassisId, System.StringComparison.OrdinalIgnoreCase))
            {
                return s_cached;
            }

            ShipFamilyPowerScoreBreakdown built = Compute(
                chassisId, shipLevel, in attrs, abilityHash, gearHash, haveShip, em, ship);

            // A missing catalog must not stick. The next paint retries once config is loaded.
            if (!built.HasDisplayStats && built.Total <= 0.01f)
            {
                s_hasCache = false;
                return built;
            }

            s_hasCache = true;
            s_chassisId = chassisId;
            s_shipLevel = shipLevel;
            s_abilityHash = abilityHash;
            s_gearHash = gearHash;
            s_matchSeed = matchSeed;
            s_cached = built;
            return built;
        }

        /// <summary>
        /// Builds one breakdown. Titans keep the frozen catalog hull (no ability steps)
        /// and add equipped ship parts. Regular hulls Extra-Level with the player's
        /// ability counts, and fold equipped parts into that same pass when any are equipped.
        /// </summary>
        static ShipFamilyPowerScoreBreakdown Compute(
            string chassisId,
            int shipLevel,
            in ShipAttributeUpgradeState attrs,
            int abilityHash,
            int gearHash,
            bool haveShip,
            EntityManager em,
            Entity ship)
        {
            // Declared up front. An out-var inside && is not "assigned" to the C# compiler
            // on the later Titan branch, which is CS0165.
            ushort megaIndex = 0;
            bool mega = MegaShipCatalog.IsMegaChassisId(chassisId)
                && MegaShipCatalog.TryParseCatalogIndex(chassisId, out megaIndex);

            List<string> extraIds = null;
            List<int> extraFamilies = null;
            bool hasGear = false;
            if (haveShip && gearHash != 0)
            {
                // Allocates two short lists. Only on a cache miss that actually has ship parts.
                ShipComponentStoreVisualScaleLogic.CollectExtraComponents(
                    em, ship, out extraIds, out extraFamilies);
                hasGear = extraIds != null && extraIds.Count > 0;
            }

            if (mega)
                return ComputeMega(megaIndex, shipLevel, hasGear, extraIds, extraFamilies);

            // No purchases yet — same session-cached bar the tree uses for this chassis.
            if (abilityHash == 0 && !hasGear)
                return BaseHull(chassisId, shipLevel);

            ShipAbilityLevelCounts abilities = ShipAttributeUpgradeLogic.ToAbilityLevelCounts(in attrs);
            if (!hasGear)
                return WithAbilitiesOnly(chassisId, shipLevel, in abilities);

            return WithAbilitiesAndGear(chassisId, shipLevel, in abilities, extraIds, extraFamilies);
        }

        /// <summary>
        /// Purchase-time hull: ship level counts, ability purchases and gear do not.
        /// This is the bar the Orbit Menu showed before accumulated stats were folded in.
        /// </summary>
        static ShipFamilyPowerScoreBreakdown BaseHull(string chassisId, int shipLevel)
        {
            if (MegaShipCatalog.IsMegaChassisId(chassisId)
                && MegaShipCatalog.TryParseCatalogIndex(chassisId, out ushort megaIndex))
            {
                MegaShipCatalog mega = MegaShipCatalog.Load();
                return mega != null ? mega.GetPowerBreakdown(megaIndex) : default;
            }

            PlanetShipFamilyConfig config = ShipStatApplyLogic.Config;
            return config != null
                ? config.GetPowerScoreBreakdownForChassisIdAtShipLevel(chassisId, shipLevel)
                : default;
        }

        /// <summary>
        /// Same prefab scan as the purchase-time bar, with this player's ability counts.
        /// No moon-store parts. A failed scan falls back to the purchase-time bar.
        /// </summary>
        static ShipFamilyPowerScoreBreakdown WithAbilitiesOnly(
            string chassisId,
            int shipLevel,
            in ShipAbilityLevelCounts abilities)
        {
            if (!ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family)
                || family == null)
                return BaseHull(chassisId, shipLevel);

            PlanetShipFamilyConfig config = ShipStatApplyLogic.Config;
            ShipFamilyChassisTierEntry tier = config != null ? config.GetTierEntryForChassisId(chassisId) : null;
            if (tier?.prefab == null)
                return BaseHull(chassisId, shipLevel);

            // Extra Level: each part uses shipLevel + the matching ability purchase.
            if (ShipFamilyPowerBarNorm.TryBuildBreakdown(
                    tier.prefab, family, shipLevel, in abilities,
                    out _, out ShipFamilyPowerScoreBreakdown breakdown))
                return breakdown;

            return BaseHull(chassisId, shipLevel);
        }

        /// <summary>
        /// Chassis parts plus equipped ship components, Extra-Leveled together.
        /// A purchased engine adds that part's PerExtra steps. A purchased weapon
        /// does not add a barrel — slot 0 stays the chassis guns, scaled by Fire Power purchases.
        /// </summary>
        static ShipFamilyPowerScoreBreakdown WithAbilitiesAndGear(
            string chassisId,
            int shipLevel,
            in ShipAbilityLevelCounts abilities,
            List<string> extraIds,
            List<int> extraFamilies)
        {
            if (!ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition family)
                || family == null)
                return WithAbilitiesOnly(chassisId, shipLevel, in abilities);

            PlanetShipFamilyConfig config = ShipStatApplyLogic.Config;
            ShipFamilyChassisTierEntry tier = config != null ? config.GetTierEntryForChassisId(chassisId) : null;
            if (tier?.prefab == null)
                return WithAbilitiesOnly(chassisId, shipLevel, in abilities);

            // Raw prefab children first. Store rows are appended after them so Extra Level
            // can tell a bought part from a hull part (a bought gun must not raise DPS).
            ShipFamilyStatsCalculator.SumResult sum = ShipFamilyStatsCalculator.SumFromPrefabHierarchy(
                tier.prefab, family, shipLevel: 1, applyPropulsionAndWeaponRules: false);
            if (extraIds != null && extraIds.Count > 0)
            {
                sum = ShipFamilyStatsCalculator.AppendExtraComponentsAndAggregate(
                    sum, family, extraIds, shipLevel: 1);
            }
            else
            {
                ShipFamilyStatsCalculator.ApplySharedAggregationRules(ref sum, family, shipLevel: 1);
            }

            if (sum.MatchedComponentIds == null || sum.MatchedComponentIds.Count == 0)
                return WithAbilitiesOnly(chassisId, shipLevel, in abilities);

            List<int> fleetIndex = BuildFleetIndex(sum, extraFamilies);
            ShipComponentAbilityStats evaluated = ShipStatApplyLogic.EvaluatePartsWithGearFleet(
                sum.MatchedComponentIds,
                sum.PerComponentStats,
                shipLevel,
                in abilities,
                sum.StoreExtraStartIndex,
                family,
                fleetIndex);

            // Slot 0 is every chassis barrel, not the store weapon.
            float dps = ShipWeaponDpsMath.SumAllGunDps(
                sum.MatchedComponentIds,
                sum.PerComponentStats,
                shipLevel,
                in abilities,
                sum.StoreExtraStartIndex);
            dps = ShipWeaponDpsMath.ApplyFamilyOffenseMuls(dps, family);
            return ShipFamilyPowerScoreBreakdown.FromEvaluatedHull(evaluated, dps);
        }

        /// <summary>
        /// Titan catalog hull plus equipped ship parts. Ability purchases do not
        /// step a Titan — the live sim freezes unique parts and adds gear as
        /// PerExtra × ship level only. No gear keeps the catalog bar unchanged.
        /// </summary>
        static ShipFamilyPowerScoreBreakdown ComputeMega(
            ushort megaIndex,
            int shipLevel,
            bool hasGear,
            List<string> extraIds,
            List<int> extraFamilies)
        {
            MegaShipCatalog catalog = MegaShipCatalog.Load();
            if (catalog == null)
                return default;

            if (!hasGear)
                return catalog.GetPowerBreakdown(megaIndex);

            if (!MegaShipStatsCalculator.TrySumForCatalogIndex(megaIndex, out ShipComponentAbilityStats stats))
                return catalog.GetPowerBreakdown(megaIndex);

            // Hull stays the catalog sum the bar already showed. Each bought part
            // adds its own PerExtra × Titan level, with that part's purchase-moon roll.
            MegaShipStatsCalculator.AddEquippedStoreComponents(
                ref stats, extraIds, shipLevel, extraFamilies);
            stats.maxGems = 0f;

            // Bought weapons unlock a bullet type. They do not add Titan DPS.
            float dps = catalog.GetPowerBreakdown(megaIndex).GetDisplayDps();
            return ShipFamilyPowerScoreBreakdown.FromEvaluatedHull(stats, dps);
        }

        /// <summary>
        /// Parallel to part rows. Hull rows are −1 (the chassis family's roll).
        /// Store rows copy the family slot stamped when that part was bought.
        /// </summary>
        static List<int> BuildFleetIndex(
            ShipFamilyStatsCalculator.SumResult sum,
            List<int> extraFamilies)
        {
            int count = sum.PerComponentStats != null ? sum.PerComponentStats.Count : 0;
            var fleets = new List<int>(count);
            int extraStart = sum.StoreExtraStartIndex;
            int extraI = 0;
            for (int i = 0; i < count; i++)
            {
                if (extraStart != int.MaxValue && i >= extraStart
                    && extraFamilies != null && extraI < extraFamilies.Count)
                {
                    fleets.Add(extraFamilies[extraI]);
                    extraI++;
                }
                else
                {
                    fleets.Add(-1);
                }
            }

            return fleets;
        }

        /// <summary>
        /// Stable fingerprint of the ten bottom-bar purchases. Zero when every ability is still 0,
        /// so a fresh hull keeps the purchase-time bar without a second prefab scan.
        /// </summary>
        static int HashAbilities(in ShipAttributeUpgradeState attrs)
        {
            unchecked
            {
                int hash = 0;
                for (int i = 0; i < ShipFamilyPowerScoreBreakdown.DisplayStatCount; i++)
                {
                    int level = ShipAttributeUpgradeLogic.GetAttributeLevel(in attrs, i);
                    if (level == 0)
                        continue;
                    // Index is mixed in so Fire Power 1 and Bullet Speed 1 do not share a key.
                    hash = hash * 31 + (i + 1) * 397 + level;
                }

                return hash;
            }
        }

        /// <summary>
        /// Fingerprint of equipped ship-component rows only. Drones and consumables
        /// stay out so buying a rocket does not rebuild the bar.
        /// </summary>
        static int HashEquippedShipComponents(EntityManager em, Entity ship)
        {
            if (ship == Entity.Null || !em.Exists(ship) || !em.HasBuffer<EquippedEquipmentElement>(ship))
                return 0;

            var buf = em.GetBuffer<EquippedEquipmentElement>(ship);
            unchecked
            {
                int hash = 17;
                int count = 0;
                for (int i = 0; i < buf.Length; i++)
                {
                    EquippedEquipmentElement row = buf[i];
                    if ((StoreItemType)row.ItemType != StoreItemType.ShipComponent)
                        continue;
                    count++;
                    hash = hash * 31 + row.ComponentId.GetHashCode();
                    hash = hash * 31 + row.SourceFamilyConfigIndex;
                    hash = hash * 31 + row.ItemLevel;
                }

                if (count == 0)
                    return 0;
                return hash * 31 + count;
            }
        }

        /// <summary>
        /// Local hull already recorded at spawn. No extra ship query — the Orbit Menu
        /// paints while gem Instantiates are still running, and a fresh gather hitches.
        /// </summary>
        static bool TryGetSeededShip(out EntityManager em, out Entity ship)
        {
            em = default;
            ship = Entity.Null;
            World world = EcsGameBridge.GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            em = world.EntityManager;
            if (!LocalShipEntitySeed.TryGetSeededShip(em, out ship) || ship == Entity.Null || !em.Exists(ship))
            {
                ship = Entity.Null;
                return false;
            }

            return true;
        }
    }
}
