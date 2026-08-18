using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Sums static MEGA part stats from the catalog unique-component library × prefab
    /// name counts. No Extra Level, no attribute upgrades.
    /// HUD chips and orbit-menu power bars call here so they never Extra-Level a MEGA
    /// as if it were a regular L7 family hull.
    /// </summary>
    public static class MegaShipStatsCalculator
    {
        /// <summary>
        /// Uses <see cref="MegaShipCatalogEntry.summedStats"/> when present; otherwise
        /// walks the prefab against the unique library. Forces <c>maxGems = 0</c>.
        /// </summary>
        public static bool SumFromEntry(
            MegaShipCatalogEntry entry,
            MegaShipCatalog catalog,
            out ShipComponentAbilityStats summed)
        {
            summed = default;
            if (entry == null)
                return false;

            if (!IsEffectivelyZero(entry.summedStats))
            {
                MegaShipPartStats resolved = catalog != null
                    ? catalog.ResolveRuntimeStats(entry.summedStats)
                    : entry.summedStats;
                summed = resolved.ToAbilityStats();
                summed.maxGems = 0f;
                return true;
            }

            return SumFromPrefab(entry.prefab, catalog, out summed);
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
        /// HUD / speedometer helper: catalog sum for a MEGA chassis index.
        /// Returns false when the catalog or hull row is missing.
        /// </summary>
        /// <param name="catalogIndex">Index into <see cref="MegaShipCatalog.entries"/>.</param>
        /// <param name="summed">Runtime-resolved catalog totals (gem cap forced to 0).</param>
        /// <returns>True when the catalog row existed and summed.</returns>
        public static bool TrySumForCatalogIndex(int catalogIndex, out ShipComponentAbilityStats summed)
        {
            summed = default;
            var catalog = MegaShipCatalog.Load();
            if (catalog == null || !catalog.TryGetEntry(catalogIndex, out MegaShipCatalogEntry entry)
                || entry == null)
                return false;

            return SumFromEntry(entry, catalog, out summed);
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
