using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Sums static MEGA / Titan part stats from the catalog unique-component library × prefab
    /// name counts. Hull parts stay frozen (no Extra Level, no bottom-bar abilities).
    /// Moon-store LOADOUT extras add PerExtra × shipLevel only (no second Base) so a
    /// purchased cockpit raises Titan health by that part’s Extra Level steps, not by
    /// copying another catalog Base onto the frozen hull.
    /// HUD chips and orbit-menu power bars call here so they never Extra-Level the Titan hull
    /// as if it were a regular L7 family chassis.
    /// Gem cargo is the exception: each cockpit, wing, and cargo copy uses the regular
    /// starter formula at the live ship level with no gem-ability purchases.
    /// </summary>
    public static class MegaShipStatsCalculator
    {
        /// <summary>
        /// Regular-ship starter gem base (<c>ShipFamilyDefinition</c> default <c>maxGems</c>).
        /// A hold part whose type-table gem base is still 0 uses this, so an old catalog
        /// asset carries gems before anyone re-seeds the inspector.
        /// </summary>
        public const float HoldGemBase = 8f;

        /// <summary>
        /// Regular-ship starter gem step (<c>maxGemsPerExtraLevel</c>).
        /// Titans have no gem ability bar, so each hold copy is
        /// <c>base + this × shipLevel</c>.
        /// </summary>
        public const float HoldGemPerShipLevel = 2f;

        /// <summary>
        /// Uses <see cref="MegaShipCatalogEntry.summedStats"/> when present; otherwise
        /// walks the prefab against the unique library. Gem cap is recomputed from
        /// cockpit / wing / cargo copy counts so a stored sum of 0 still holds cargo.
        /// Catalog-only — no moon-store extras. Prefer
        /// <see cref="SumFromEntry(MegaShipCatalogEntry, MegaShipCatalog, IReadOnlyList{string}, int, out ShipComponentAbilityStats)"/>
        /// when the live ship has equipped gear.
        /// </summary>
        public static bool SumFromEntry(
            MegaShipCatalogEntry entry,
            MegaShipCatalog catalog,
            out ShipComponentAbilityStats summed)
        {
            return SumFromEntry(entry, catalog, extraComponentIds: null, shipLevel: 7, out summed);
        }

        /// <summary>
        /// Catalog hull sum plus equipped moon-store <see cref="StoreItemType.ShipComponent"/> rows.
        /// Hull numbers stay frozen. Each extra adds <b>PerExtra × shipLevel only</b>
        /// (no Base — the Titan already owns the hull Bases). Ability purchases stay 0.
        /// Gem cap is the hold-part formula at <paramref name="shipLevel"/>, plus each
        /// purchased cockpit, wing, or cargo's PerExtra × that level. Weapon extras add
        /// fire power / rate steps and keep the fastest bullet speed / range instead of
        /// summing travel stats.
        /// </summary>
        /// <param name="entry">Catalog hull row.</param>
        /// <param name="catalog">Unique-component library used for runtime defaults.</param>
        /// <param name="extraComponentIds">Equipped store component ids, or null when none.</param>
        /// <param name="shipLevel">Titan chassis level (almost always 7). Hold gems and extras use this tier.</param>
        /// <param name="summed">Resolved hull + gear totals, including gem cap.</param>
        /// <returns>True when the hull row existed and summed.</returns>
        public static bool SumFromEntry(
            MegaShipCatalogEntry entry,
            MegaShipCatalog catalog,
            IReadOnlyList<string> extraComponentIds,
            int shipLevel,
            out ShipComponentAbilityStats summed)
        {
            summed = default;
            if (entry == null)
                return false;

            if (!IsEffectivelyZero(entry.summedStats))
            {
                // --- Live cruise from unique parts ---
                // [TITAN-ORBIT] Stored summedStats.moveSpeed used to keep only
                // Engine / Thruster. Recompute from every unique row that authored
                // Move so a wing (or any other part) applies without a catalog refresh.
                MegaShipPartStats raw = entry.summedStats;
                if (MegaShipComponentInventory.TryComputeCruiseFromUniqueParts(
                        catalog, entry, out float cruise))
                    raw.moveSpeed = cruise;
                summed = ResolveScaledHull(entry, catalog, raw, shipLevel);
                AddEquippedStoreComponents(ref summed, extraComponentIds, shipLevel);
                return true;
            }

            if (!TrySumRawFromPrefab(entry.prefab, catalog, out MegaShipCatalogEntry walked))
                return false;

            // The walk has no family of its own. Keep this hull's visual line so the
            // stat scale matches Craizan / Leopard / Okamoto, and keep stored part
            // counts when the asset already listed them.
            walked.visualFamily = entry.visualFamily;
            if (entry.componentCounts != null && entry.componentCounts.Count > 0)
                walked.componentCounts = entry.componentCounts;
            summed = ResolveScaledHull(walked, catalog, walked.summedStats, shipLevel);
            AddEquippedStoreComponents(ref summed, extraComponentIds, shipLevel);
            return true;
        }

        /// <summary>
        /// Turns a raw component sum into the numbers a Titan actually uses.
        /// Family stat scale runs first, and only on health and energy (including
        /// regen). Catalog defaults then fill zeros and minimums raise a glass hull.
        /// Gem cargo is recomputed from hold copies and is not multiplied — the
        /// stored sum often still has gems at 0, but troop and gem caps stay authored.
        /// Fire power, ramming, cruise, and turn are not touched by the family knob.
        /// </summary>
        static ShipComponentAbilityStats ResolveScaledHull(
            MegaShipCatalogEntry entry,
            MegaShipCatalog catalog,
            in MegaShipPartStats raw,
            int shipLevel)
        {
            float scale = catalog != null
                ? catalog.GetStatScaleForEntry(entry)
                : MegaShipCatalog.DefaultFamilyStatScale;
            MegaShipPartStats scaled = MegaShipPartStats.ApplyFamilyStatScale(raw, scale);
            MegaShipPartStats resolved = catalog != null
                ? catalog.ResolveRuntimeStats(scaled)
                : scaled;
            ShipComponentAbilityStats summed = resolved.ToAbilityStats();
            // Hold size is not health or energy. Copy counts keep the authored cargo.
            summed.maxGems = SumHoldGemCap(entry, catalog, shipLevel);
            return summed;
        }

        /// <summary>
        /// Walks the prefab and returns a scratch hull row with the raw unique-part
        /// sum and the name counts. No family scale and no runtime defaults yet.
        /// False when the prefab or catalog is missing.
        /// </summary>
        static bool TrySumRawFromPrefab(
            GameObject prefab,
            MegaShipCatalog catalog,
            out MegaShipCatalogEntry scratch)
        {
            scratch = null;
            if (prefab == null || catalog == null)
                return false;

            scratch = new MegaShipCatalogEntry { prefab = prefab };
            MegaShipComponentInventory.RecalcShipSum(catalog, scratch);
            return true;
        }

        /// <summary>
        /// Walks every classified child of <paramref name="prefab"/> and adds the unique
        /// component stats for that child name (type-table fallback). Gem cap uses the
        /// same hold-part formula as <see cref="SumFromEntry"/>.
        /// </summary>
        public static bool SumFromPrefab(
            GameObject prefab,
            MegaShipCatalog catalog,
            out ShipComponentAbilityStats summed)
        {
            return SumFromPrefab(prefab, catalog, shipLevel: 7, out summed);
        }

        /// <summary>
        /// Same walk as <see cref="SumFromPrefab(GameObject, MegaShipCatalog, out ShipComponentAbilityStats)"/>
        /// with the live Titan ship level on gem cargo.
        /// </summary>
        public static bool SumFromPrefab(
            GameObject prefab,
            MegaShipCatalog catalog,
            int shipLevel,
            out ShipComponentAbilityStats summed)
        {
            summed = default;
            if (prefab == null || catalog == null)
                return false;

            if (!TrySumRawFromPrefab(prefab, catalog, out MegaShipCatalogEntry scratch))
                return false;

            // No hull row here, so there is no visual family to scale. Callers that
            // know the Titan (SumFromEntry) apply GetStatScaleForEntry themselves.
            MegaShipPartStats resolved = catalog.ResolveRuntimeStats(scratch.summedStats);
            summed = resolved.ToAbilityStats();
            summed.maxGems = SumHoldGemCap(scratch, catalog, shipLevel);
            return true;
        }

        /// <summary>
        /// HUD / speedometer helper: catalog sum for a MEGA chassis index (no store extras).
        /// Returns false when the catalog or hull row is missing.
        /// </summary>
        /// <param name="catalogIndex">Index into <see cref="MegaShipCatalog.entries"/>.</param>
        /// <param name="summed">Runtime-resolved catalog totals, including gem cap at level 7.</param>
        /// <returns>True when the catalog row existed and summed.</returns>
        public static bool TrySumForCatalogIndex(int catalogIndex, out ShipComponentAbilityStats summed)
        {
            return TrySumForCatalogIndex(catalogIndex, extraComponentIds: null, shipLevel: 7, out summed);
        }

        /// <summary>
        /// Live Titan helper: catalog hull plus equipped moon-store extras Extra-Leveled
        /// at <paramref name="shipLevel"/>. Orbit-menu tree bars keep the extras-free
        /// overload so a docked Titan does not change the RANK 1 catalog ceiling.
        /// </summary>
        /// <param name="catalogIndex">Index into <see cref="MegaShipCatalog.entries"/>.</param>
        /// <param name="extraComponentIds">Equipped store component ids, or null when none.</param>
        /// <param name="shipLevel">Titan chassis level used to Extra-Level extras.</param>
        /// <param name="summed">Hull + gear totals, including gem cap at <paramref name="shipLevel"/>.</param>
        /// <returns>True when the catalog row existed and summed.</returns>
        public static bool TrySumForCatalogIndex(
            int catalogIndex,
            IReadOnlyList<string> extraComponentIds,
            int shipLevel,
            out ShipComponentAbilityStats summed)
        {
            summed = default;
            var catalog = MegaShipCatalog.Load();
            if (catalog == null || !catalog.TryGetEntry(catalogIndex, out MegaShipCatalogEntry entry)
                || entry == null)
                return false;

            return SumFromEntry(entry, catalog, extraComponentIds, shipLevel, out summed);
        }

        /// <summary>
        /// Extra-Levels each moon-store ship component and adds it onto a frozen Titan hull.
        /// <para>
        /// [TITAN-ORBIT] Regular-ship stacked extras do not copy a second Base into the hull —
        /// they add that part’s PerExtra × (shipLevel + ability). Titans have no ability
        /// bar, so LOADOUT gear is <c>PerExtra × shipLevel</c> only. The catalog unique-parts
        /// already own Base. <see cref="EvaluateLoadoutExtra"/> is the shared evaluate.
        /// </para>
        /// Weapon extras do not add damage or cadence — they unlock a bullet type.
        /// They still take the faster travel stats (max, not sum) so a long-range
        /// store gun does not get added onto every Titan barrel's speed.
        /// A purchased cockpit, wing, or cargo adds its gem PerExtra × ship level
        /// on top of the hull hold. Other purchases add no gems.
        /// </summary>
        /// <param name="hull">In/out catalog totals. Hull gem cap is kept; hold gear adds to it.</param>
        /// <param name="extraComponentIds">Equipped store component ids.</param>
        /// <param name="shipLevel">Titan chassis level (1-based).</param>
        /// <param name="gearFamilyConfigIndices">
        /// Parallel to <paramref name="extraComponentIds"/>. Each entry is the
        /// purchase planet's family slot. Null leaves extras unscaled so a caller
        /// can still multiply the whole Titan. When set, that roll hits this
        /// component only — the catalog hull is not multiplied again.
        /// </param>
        public static void AddEquippedStoreComponents(
            ref ShipComponentAbilityStats hull,
            IReadOnlyList<string> extraComponentIds,
            int shipLevel,
            IReadOnlyList<int> gearFamilyConfigIndices = null)
        {
            if (extraComponentIds == null || extraComponentIds.Count == 0)
                return;

            for (int i = 0; i < extraComponentIds.Count; i++)
            {
                string id = extraComponentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                // Any-family lookup — extras keep the catalog they were bought from.
                if (!ShipFamilyStatsCalculator.TryResolveComponentStats(null, id, out ShipComponentAbilityStats raw))
                    continue;

                ShipComponentAbilityStats extra = EvaluateLoadoutExtra(raw, id, shipLevel);

                // Purchase-moon fleet on this part's contribution. The Titan hull
                // was already rolled. Do not multiply that hull by this part's bonus.
                if (gearFamilyConfigIndices != null && i < gearFamilyConfigIndices.Count)
                {
                    ShipFamilySpecialBonuses gearFleet = ResolveFleetBonuses(gearFamilyConfigIndices[i]);
                    if (!gearFleet.IsIdentity)
                        extra = gearFleet.Apply(extra);
                }

                // Purchased weapon: bullet-type unlock only on the frozen hull sum.
                // Energy and other non-damage fields still merge. Catalog gun damage
                // stays here. When that bank is selected, Titan shots use
                // MegaShipStatApplyLogic.TryResolveTitanFamilyWeaponFirepower
                // (base + per-extra × 14) instead of this hull firepower.
                if (ShipComponentAbilityStats.IsWeaponComponent(id))
                {
                    extra.firePower = 0f;
                    extra.firePowerPerExtraLevel = 0f;
                    extra.fireRate = 0f;
                    extra.fireRatePerExtraLevel = 0f;
                }

                // Gem cargo is cockpit, wing, and cargo only — an engine that authors
                // a gem step must not fill the Titan hold.
                if (!IsHoldComponent(id))
                {
                    extra.maxGems = 0f;
                    extra.maxGemsPerExtraLevel = 0f;
                }

                // Weapon travel is one hull value (fastest barrel), not a sum of every gun.
                float keepSpeed = Mathf.Max(hull.bulletSpeed, extra.bulletSpeed);
                float keepRange = Mathf.Max(hull.bulletRange, extra.bulletRange);
                hull.AddInPlace(extra);
                hull.bulletSpeed = keepSpeed;
                hull.bulletRange = keepRange;
            }
        }

        /// <summary>
        /// Extra Level for one LOADOUT ship component on a Titan: PerExtra × shipLevel,
        /// no Base. Turn uses that same step as Move and Accel — a purchased fin adds
        /// its <c>turnSpeedPerExtraLevel</c> × Titan tier, not its Turn Base.
        /// Ability purchases stay 0. Weapons still use the weapon Extra Level
        /// rules (bullet speed is ability-only, so a Titan extra does not add travel).
        /// </summary>
        /// <param name="raw">Catalog Base / PerExtra for this component id.</param>
        /// <param name="componentId">Store component id (picks weapon vs non-weapon pool).</param>
        /// <param name="shipLevel">Titan chassis level (1-based).</param>
        public static ShipComponentAbilityStats EvaluateLoadoutExtra(
            in ShipComponentAbilityStats raw,
            string componentId,
            int shipLevel)
        {
            int level = Mathf.Max(1, shipLevel);
            var noAbilities = default(ShipAbilityLevelCounts);
            bool weapon = ShipComponentAbilityStats.IsWeaponComponent(componentId);
            return ShipComponentExtraLevelMath.EvaluatePool(
                raw,
                componentCount: 1,
                level,
                in noAbilities,
                isWeaponPool: weapon,
                includeBase: false);
        }

        /// <summary>
        /// Fleet bonuses for the ship family a Titan was bought with.
        /// Home slot (config index 0) is exactly 1× during a match. Every other
        /// planet's family keeps its authored multipliers, then this match replaces
        /// 3–6 of them with a bell-curve roll (centered on 1×, tails half to double).
        /// With no match seed yet, this is the family's authored multipliers — the same
        /// numbers a regular hull on that planet would use.
        /// </summary>
        /// <param name="familyConfigIndex">
        /// Ghosted <c>ShipState.ShipFamilyConfigIndex</c> stamped from the store planet.
        /// </param>
        public static ShipFamilySpecialBonuses ResolveFleetBonuses(int familyConfigIndex)
        {
            // --- Same family the planet sold ---
            // [TITAN-ORBIT] Titans are rolled onto planets. The purchase copies that
            // planet's family index onto the ship. The roll is per family slot, so
            // two planets that share Cosmic Shark share one set of multipliers.
            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            PlanetShipFamilyConfig.ShipFamilyEntry entry = config != null
                ? config.GetFamilyByConfigIndex(familyConfigIndex)
                : null;
            ShipFamilyDefinition family = entry != null ? entry.shipFamilyDefinition : null;
            if (family != null)
                return family.ResolveSpecialBonuses();

            return ShipFamilyMatchBonuses.ResolveIndex(familyConfigIndex);
        }

        /// <summary>
        /// Multiplies a Titan hull by <see cref="ResolveFleetBonuses"/>.
        /// The family's gem-cap roll scales hold gems the same way it scales health.
        /// </summary>
        /// <param name="stats">Catalog hull. Gear added after this keeps its own purchase-moon roll.</param>
        /// <param name="familyConfigIndex">Planet family slot this Titan was added with.</param>
        public static ShipComponentAbilityStats ApplyFleetBonuses(
            ShipComponentAbilityStats stats,
            int familyConfigIndex)
        {
            return ResolveFleetBonuses(familyConfigIndex).Apply(stats);
        }

        /// <summary>True for cockpit, wing, and cargo — the parts that hold gems.</summary>
        public static bool IsHoldPartType(string partType)
        {
            if (string.IsNullOrWhiteSpace(partType))
                return false;
            return string.Equals(partType, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase)
                   || ShipFamilyPartTypes.IsCargoProfile(partType);
        }

        /// <summary>True when a moon-store component id is a cockpit, wing, or cargo.</summary>
        public static bool IsHoldComponent(string componentId)
        {
            return IsHoldPartType(ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId));
        }

        /// <summary>
        /// Gems one cockpit, wing, or cargo copy holds at <paramref name="shipLevel"/>
        /// with no gem-ability purchases: authored type-table base (or <see cref="HoldGemBase"/>)
        /// plus <see cref="HoldGemPerShipLevel"/> × ship level. A level-7 copy of the starter
        /// base holds 22, the same as one regular cockpit that never bought Gem Cap.
        /// </summary>
        public static float GemCapPerHoldCopy(MegaShipCatalog catalog, string partType, int shipLevel)
        {
            float authored = 0f;
            if (catalog != null && IsHoldPartType(partType))
                authored = catalog.GetStatsForPartType(partType).maxGems;
            float baseGems = authored > 0.01f ? authored : HoldGemBase;
            return baseGems + HoldGemPerShipLevel * Mathf.Max(1, shipLevel);
        }

        /// <summary>
        /// Sums <see cref="GemCapPerHoldCopy"/> × copy count for every cockpit, wing,
        /// and cargo row on the hull. Guns, engines, and armor add nothing.
        /// </summary>
        public static float SumHoldGemCap(MegaShipCatalogEntry entry, MegaShipCatalog catalog, int shipLevel)
        {
            if (entry?.componentCounts == null || catalog == null)
                return 0f;

            float total = 0f;
            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!TryResolveHoldPartType(catalog, count.displayName, out string partType))
                    continue;
                total += count.count * GemCapPerHoldCopy(catalog, partType, shipLevel);
            }

            return total;
        }

        /// <summary>
        /// Part profile for one unique-component name. Prefers the catalog row's
        /// authored type, then the name heuristic used when the row has none.
        /// </summary>
        static bool TryResolveHoldPartType(MegaShipCatalog catalog, string displayName, out string partType)
        {
            partType = null;
            if (catalog != null
                && catalog.TryGetUniqueComponent(displayName, out MegaShipComponentEntry row)
                && row != null
                && !string.IsNullOrWhiteSpace(row.partType))
            {
                partType = row.partType;
            }
            else
            {
                partType = MegaShipPartClassifier.ResolvePartType(displayName);
            }

            return IsHoldPartType(partType);
        }

        /// <summary>True when a stored sum was never written (all zeros).</summary>
        static bool IsEffectivelyZero(in MegaShipPartStats s)
        {
            return s.firePower <= 0.01f
                   && s.healthCap <= 0.01f
                   && s.energyCap <= 0.01f
                   && s.moveSpeed <= 0.01f
                   && s.maxPeople <= 0.01f;
        }

    }
}
