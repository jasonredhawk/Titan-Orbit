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
    /// </summary>
    public static class MegaShipStatsCalculator
    {
        /// <summary>
        /// Uses <see cref="MegaShipCatalogEntry.summedStats"/> when present; otherwise
        /// walks the prefab against the unique library. Forces <c>maxGems = 0</c>.
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
        /// Gem cap stays 0. Weapon extras add fire power / rate steps and keep the fastest
        /// bullet speed / range instead of summing travel stats.
        /// </summary>
        /// <param name="entry">Catalog hull row.</param>
        /// <param name="catalog">Unique-component library used for runtime defaults.</param>
        /// <param name="extraComponentIds">Equipped store component ids, or null when none.</param>
        /// <param name="shipLevel">Titan chassis level (almost always 7). Extras Extra-Level at this tier.</param>
        /// <param name="summed">Resolved hull + gear totals. Gem cap is 0.</param>
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
                MegaShipPartStats resolved = catalog != null
                    ? catalog.ResolveRuntimeStats(raw)
                    : raw;
                summed = resolved.ToAbilityStats();
                summed.maxGems = 0f;
                AddEquippedStoreComponents(ref summed, extraComponentIds, shipLevel);
                return true;
            }

            if (!SumFromPrefab(entry.prefab, catalog, out summed))
                return false;

            AddEquippedStoreComponents(ref summed, extraComponentIds, shipLevel);
            return true;
        }

        /// <summary>
        /// Walks every classified child of <paramref name="prefab"/> and adds the unique
        /// component stats for that child name (type-table fallback). Forces <c>maxGems = 0</c>.
        /// </summary>
        public static bool SumFromPrefab(
            GameObject prefab,
            MegaShipCatalog catalog,
            out ShipComponentAbilityStats summed)
        {
            summed = default;
            if (prefab == null || catalog == null)
                return false;

            var scratch = new MegaShipCatalogEntry { prefab = prefab };
            MegaShipComponentInventory.RecalcShipSum(catalog, scratch);
            MegaShipPartStats resolved = catalog.ResolveRuntimeStats(scratch.summedStats);
            summed = resolved.ToAbilityStats();
            summed.maxGems = 0f;
            return true;
        }

        /// <summary>
        /// HUD / speedometer helper: catalog sum for a MEGA chassis index (no store extras).
        /// Returns false when the catalog or hull row is missing.
        /// </summary>
        /// <param name="catalogIndex">Index into <see cref="MegaShipCatalog.entries"/>.</param>
        /// <param name="summed">Runtime-resolved catalog totals (gem cap forced to 0).</param>
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
        /// <param name="summed">Hull + gear totals (gem cap forced to 0).</param>
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
        /// Weapon extras add damage / rate steps and take the faster travel stats (max, not sum)
        /// so a long-range store gun does not get added onto every Titan barrel's speed.
        /// Gem cap stays 0.
        /// </summary>
        /// <param name="hull">In/out catalog totals. Gem cap is forced to 0 after the merge.</param>
        /// <param name="extraComponentIds">Equipped store component ids.</param>
        /// <param name="shipLevel">Titan chassis level (1-based).</param>
        public static void AddEquippedStoreComponents(
            ref ShipComponentAbilityStats hull,
            IReadOnlyList<string> extraComponentIds,
            int shipLevel)
        {
            // --- No extras: still pin gem cap ---
            if (extraComponentIds == null || extraComponentIds.Count == 0)
            {
                hull.maxGems = 0f;
                return;
            }

            for (int i = 0; i < extraComponentIds.Count; i++)
            {
                string id = extraComponentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                // Any-family lookup — extras keep the catalog they were bought from.
                if (!ShipFamilyStatsCalculator.TryResolveComponentStats(null, id, out ShipComponentAbilityStats raw))
                    continue;

                ShipComponentAbilityStats extra = EvaluateLoadoutExtra(raw, id, shipLevel);

                // Weapon travel is one hull value (fastest barrel), not a sum of every gun.
                float keepSpeed = Mathf.Max(hull.bulletSpeed, extra.bulletSpeed);
                float keepRange = Mathf.Max(hull.bulletRange, extra.bulletRange);
                hull.AddInPlace(extra);
                hull.bulletSpeed = keepSpeed;
                hull.bulletRange = keepRange;
            }

            hull.maxGems = 0f;
        }

        /// <summary>
        /// Extra Level for one LOADOUT ship component on a Titan: PerExtra × shipLevel,
        /// no Base. Ability purchases stay 0. Weapons still use the weapon Extra Level
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
        /// Extra-Leveled sustained DPS from equipped store weapons only. Fire power is
        /// PerExtra × shipLevel (no Base). Cadence uses the extra’s catalog fire-rate Base
        /// when PerExtra rate is 0 so a LOADOUT gun still has a real shots/sec.
        /// </summary>
        /// <param name="extraComponentIds">Equipped store component ids.</param>
        /// <param name="shipLevel">Titan chassis level used to Extra-Level extras.</param>
        /// <returns>0 when there are no weapon extras.</returns>
        public static float SumEquippedWeaponDps(
            IReadOnlyList<string> extraComponentIds,
            int shipLevel)
        {
            if (extraComponentIds == null || extraComponentIds.Count == 0)
                return 0f;

            float dps = 0f;
            for (int i = 0; i < extraComponentIds.Count; i++)
            {
                string id = extraComponentIds[i];
                if (string.IsNullOrWhiteSpace(id)
                    || !ShipComponentAbilityStats.IsWeaponComponent(id))
                    continue;
                if (!ShipFamilyStatsCalculator.TryResolveComponentStats(null, id, out ShipComponentAbilityStats raw))
                    continue;

                ShipComponentAbilityStats extra = EvaluateLoadoutExtra(raw, id, shipLevel);
                // PerExtra-only rate is often 0 — keep the catalog cadence so extra FP still scores DPS.
                float rate = extra.fireRate > 0.01f ? extra.fireRate : raw.fireRate;
                dps += ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(extra.firePower, rate);
            }

            return dps;
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
