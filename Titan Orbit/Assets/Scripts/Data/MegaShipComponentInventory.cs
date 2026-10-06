using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One unique MEGA part name (Armor1, TurretBarrel, …) with editable
    /// <see cref="MegaShipPartStats"/>. Shared by every hull that uses that name.
    /// </summary>
    [Serializable]
    public class MegaShipComponentEntry
    {
        /// <summary>Prefab asset name — the unique key (not the instance name with (1)).</summary>
        public string displayName;

        /// <summary>Part profile id (<see cref="ShipFamilyPartTypes"/>).</summary>
        public string partType;

        /// <summary>True when this row is a tagged MEGA weapon prefab (Gun / Cannon / Missile / Sniper).</summary>
        public bool isWeapon;

        /// <summary>
        /// Hitscan cannon laser instead of a projectile. Independent of
        /// <see cref="partType"/> — check this on any unique weapon to burn a beam.
        /// </summary>
        [Tooltip("Hitscan laser (no bullet). Unchecked weapons fire their normal projectile / missile / sniper.")]
        public bool isLaser;

        /// <summary>
        /// BulletVfxBank category this weapon fires. -1 inherits the catalog type-table
        /// bank for <see cref="partType"/> (guns / cannons / missiles / snipers).
        /// </summary>
        [BulletVfxBankCategory(true, "Type table default")]
        [Tooltip("Bullet bank this unique weapon fires. Type table default follows the catalog Gun/Cannon/Missile/Sniper bank. Rockets seek like store ALT rockets.")]
        public int bulletPrefabIndex = MegaShipCatalog.InheritTypeTableBankIndex;

        /// <summary>Per-name stats. Seeded from the type table; then hand-tunable.</summary>
        public MegaShipPartStats stats;
    }

    /// <summary>How many times a unique part name appears on one hull (no stats).</summary>
    [Serializable]
    public class MegaShipComponentCount
    {
        public string displayName;
        public int count;
    }

    /// <summary>
    /// Builds the catalog-wide unique component library from MEGA prefabs and
    /// sums each hull from those shared rows × how many times the name appears.
    /// Cruise speed is every unique part whose authored <c>moveSpeed</c> is &gt; 0
    /// (not only Engine / Thruster): fastest + extraEngineSpeedPercent of the rest.
    /// </summary>
    public static class MegaShipComponentInventory
    {
        /// <summary>
        /// Scans every hull prefab into <see cref="MegaShipCatalog.uniqueComponents"/>
        /// (one row per child name). Matching names keep hand-edited stats when
        /// <paramref name="keepManualStats"/> is true. Then rewrites every ship's
        /// <see cref="MegaShipCatalogEntry.summedStats"/>.
        /// </summary>
        public static int RefreshAll(MegaShipCatalog catalog, bool keepManualStats = true)
        {
            if (catalog == null)
                return 0;

            var previous = new Dictionary<string, MegaShipPartStats>(StringComparer.OrdinalIgnoreCase);
            var previousBanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var previousLaser = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var previousWeapons = new Dictionary<string, MegaShipComponentEntry>(StringComparer.OrdinalIgnoreCase);
            if (catalog.uniqueComponents != null)
            {
                for (int i = 0; i < catalog.uniqueComponents.Count; i++)
                {
                    var old = catalog.uniqueComponents[i];
                    if (old == null || string.IsNullOrEmpty(old.displayName))
                        continue;
                    if (keepManualStats)
                    {
                        previous[old.displayName] = old.stats;
                        previousBanks[old.displayName] = old.bulletPrefabIndex;
                    }

                    previousLaser[old.displayName] = old.isLaser;

                    // Reset/refresh must not drop guns just because tags are missing.
                    if (old.isWeapon || ShipFamilyPartTypes.IsWeapon(old.partType))
                        previousWeapons[old.displayName] = old;
                }
            }

            var byName = new Dictionary<string, MegaShipComponentEntry>(StringComparer.OrdinalIgnoreCase);
            if (catalog.entries != null)
            {
                for (int i = 0; i < catalog.entries.Count; i++)
                {
                    var entry = catalog.entries[i];
                    if (entry?.prefab == null)
                        continue;
                    CollectUniqueNames(entry.prefab, catalog, byName);
                }
            }

            var next = new List<MegaShipComponentEntry>(byName.Count);
            foreach (var pair in byName)
            {
                var row = pair.Value;
                if (keepManualStats && previous.TryGetValue(row.displayName, out MegaShipPartStats kept))
                    row.stats = kept;
                if (keepManualStats && previousBanks.TryGetValue(row.displayName, out int keptBank))
                    row.bulletPrefabIndex = keptBank;
                if (previousLaser.TryGetValue(row.displayName, out bool keptLaser))
                    row.isLaser = keptLaser;
                next.Add(row);
            }

            if (previousWeapons.Count > 0)
            {
                for (int i = 0; i < next.Count; i++)
                {
                    MegaShipComponentEntry row = next[i];
                    if (row != null && !string.IsNullOrEmpty(row.displayName))
                        previousWeapons.Remove(row.displayName);
                }

                foreach (var leftover in previousWeapons)
                {
                    MegaShipComponentEntry kept = leftover.Value;
                    if (kept == null)
                        continue;
                    if (!keepManualStats && catalog != null)
                        kept.stats = catalog.GetStatsForPartType(kept.partType);
                    next.Add(kept);
                }
            }

            next.Sort(CompareUnique);
            catalog.uniqueComponents = next;
            RecalcAllShipSums(catalog);
            return next.Count;
        }

        /// <summary>
        /// Walks each hull prefab and writes raw sums (cruise = fastest part with
        /// moveSpeed + extra% of every other part that also authored Move).
        /// Zeros stay 0 so orange rows stay honest; in-game defaults/minimums live on the catalog.
        /// </summary>
        public static void RecalcAllShipSums(MegaShipCatalog catalog)
        {
            if (catalog?.entries == null)
                return;

            for (int i = 0; i < catalog.entries.Count; i++)
                RecalcShipSum(catalog, catalog.entries[i]);
        }

        /// <summary>
        /// Sums one hull from the unique library × prefab child names.
        /// Cruise is written after the walk: fastest authored Move + extra% of the rest,
        /// from every classified child that has moveSpeed (any part type).
        /// Called from Refresh Unique Components and from
        /// <see cref="MegaShipStatsCalculator.SumFromPrefab"/> when a stored sum is empty.
        /// </summary>
        /// <param name="catalog">Unique-component library and extra-percent.</param>
        /// <param name="entry">Hull row to rewrite (counts + summedStats).</param>
        /// <returns>The raw sum written onto <paramref name="entry"/>.</returns>
        public static MegaShipPartStats RecalcShipSum(
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry)
        {
            var sum = default(MegaShipPartStats);
            if (entry == null)
                return sum;

            var counts = new List<MegaShipComponentCount>(16);
            var cruiseMoves = new List<float>(8);
            float engineAccelSum = 0f;
            bool anyThruster = false;
            if (entry.prefab != null && catalog != null)
            {
                var tallies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var root = entry.prefab.transform;
                var all = entry.prefab.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Transform t = all[i];
                    if (!TryClassifyChild(t, root, out string partType, out _))
                        continue;

                    string id = MegaShipPartClassifier.GetPrefabAssetName(t);
                    MegaShipPartStats part = catalog.TryGetUniqueComponent(id, out MegaShipComponentEntry row)
                        && row != null
                        ? row.stats
                        : catalog.GetStatsForPartType(partType);

                    // --- Cruise = any part that authored Move; Accel stays thruster-owned ---
                    // [TITAN-ORBIT] Regular ships mask Move to engines. Titans do not:
                    // a wing / hull / cockpit with moveSpeed must raise cruise the same
                    // way an engine does. Zero Move stays out so empty plates do not
                    // steal the "fastest" slot. Accel is still thrusters (engines only
                    // when the hull has none).
                    bool isEngine = ShipFamilyPartTypes.IsEngineProfile(partType);
                    bool isThruster = ShipFamilyPartTypes.IsThrusterProfile(partType);
                    if (ContributesCruiseMove(part))
                        cruiseMoves.Add(part.moveSpeed);
                    if (isEngine)
                        engineAccelSum += part.accelerationCap;
                    else if (isThruster)
                        anyThruster = true;

                    // Cruise is written after the walk. Engines do not add Accel when thrusters exist.
                    var add = part;
                    add.moveSpeed = 0f;
                    if (isEngine)
                        add.accelerationCap = 0f;
                    sum = MegaShipPartStats.Sum(sum, add);

                    string key = row != null ? row.displayName : id;
                    if (!tallies.TryGetValue(key, out int n))
                        n = 0;
                    tallies[key] = n + 1;
                }

                foreach (var pair in tallies)
                    counts.Add(new MegaShipComponentCount { displayName = pair.Key, count = pair.Value });
                counts.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
            }

            sum.moveSpeed = CombineEngineCruise(cruiseMoves, catalog != null
                ? catalog.GetExtraEngineSpeedPercent()
                : MegaShipCatalog.DefaultExtraEngineSpeedPercent);
            if (!anyThruster)
                sum.accelerationCap += engineAccelSum;

            entry.componentCounts = counts;
            entry.hasMissingStats = MegaShipPartStats.HasMissingNonFirepower(sum);
            entry.summedStats = sum;
            return sum;
        }

        /// <summary>
        /// True when this unique-part block should add its authored Move to Titan cruise.
        /// Part type does not matter — a wing or hull plate with <c>moveSpeed</c> counts
        /// the same as an engine. Zero stays out so empty plates do not steal "fastest".
        /// </summary>
        /// <param name="stats">Unique-component or type-table block for one prefab child.</param>
        /// <returns>True when <c>moveSpeed</c> is authored above the zero epsilon.</returns>
        public static bool ContributesCruiseMove(in MegaShipPartStats stats)
        {
            return stats.moveSpeed > 0.0001f;
        }

        /// <summary>
        /// Fills <paramref name="into"/> with one moveSpeed per copy of each unique
        /// hull part that authored Move. Uses <see cref="MegaShipCatalogEntry.componentCounts"/>
        /// so dedicated / player builds can recompute cruise without walking the prefab.
        /// </summary>
        /// <param name="catalog">Unique-component library (name → stats).</param>
        /// <param name="entry">Hull row with name counts.</param>
        /// <param name="into">Destination list. Cleared on success.</param>
        /// <returns>False when catalog, entry, or counts are missing (keep a stored sum).</returns>
        public static bool TryCollectCruiseMovesFromCounts(
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry,
            List<float> into)
        {
            if (into == null || catalog == null || entry?.componentCounts == null)
                return false;

            into.Clear();
            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry unique)
                    || unique == null
                    || !ContributesCruiseMove(unique.stats))
                    continue;

                for (int n = 0; n < count.count; n++)
                    into.Add(unique.stats.moveSpeed);
            }

            return true;
        }

        /// <summary>
        /// Live Titan cruise from unique-library Move values × hull name counts.
        /// Same formula as <see cref="RecalcShipSum"/> so a stale stored
        /// <see cref="MegaShipCatalogEntry.summedStats"/> still picks up a wing
        /// (or any other part) the designer just gave moveSpeed.
        /// </summary>
        /// <param name="catalog">Unique-component library and extra-percent.</param>
        /// <param name="entry">Hull row with name counts.</param>
        /// <param name="cruise">Fastest Move + extra% of the rest, or 0 when none authored.</param>
        /// <returns>False when counts are missing — caller should keep the stored sum.</returns>
        public static bool TryComputeCruiseFromUniqueParts(
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry,
            out float cruise)
        {
            cruise = 0f;
            var moves = new List<float>(16);
            if (!TryCollectCruiseMovesFromCounts(catalog, entry, moves))
                return false;

            cruise = CombineEngineCruise(
                moves,
                catalog != null
                    ? catalog.GetExtraEngineSpeedPercent()
                    : MegaShipCatalog.DefaultExtraEngineSpeedPercent);
            return true;
        }

        /// <summary>
        /// Fastest authored Move + <paramref name="extraPercent"/> of every other
        /// contributor's moveSpeed. Empty list → 0 (in-game default/minimum fills it).
        /// </summary>
        /// <param name="engineMoves">moveSpeed from every cruise contributor (any part type).</param>
        /// <param name="extraPercent">Catalog extraEngineSpeedPercent (0.05 = 5%).</param>
        public static float CombineEngineCruise(List<float> engineMoves, float extraPercent)
        {
            if (engineMoves == null || engineMoves.Count == 0)
                return 0f;

            float max = 0f;
            float extraSum = 0f;
            for (int i = 0; i < engineMoves.Count; i++)
            {
                float v = engineMoves[i];
                if (v > max)
                    max = v;
            }

            bool skippedMax = false;
            for (int i = 0; i < engineMoves.Count; i++)
            {
                float v = engineMoves[i];
                if (!skippedMax && Mathf.Approximately(v, max))
                {
                    skippedMax = true;
                    continue;
                }

                extraSum += v;
            }

            return max + extraSum * Mathf.Clamp01(extraPercent);
        }

        /// <summary>Adds every classified child name on <paramref name="prefab"/> into <paramref name="byName"/>.</summary>
        public static void CollectUniqueNames(
            GameObject prefab,
            MegaShipCatalog catalog,
            Dictionary<string, MegaShipComponentEntry> byName)
        {
            if (prefab == null || byName == null)
                return;

            var root = prefab.transform;
            var all = prefab.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (!TryClassifyChild(t, root, out string partType, out bool isWeapon))
                    continue;

                string id = MegaShipPartClassifier.GetPrefabAssetName(t);
                if (string.IsNullOrEmpty(id) || byName.ContainsKey(id))
                    continue;

                byName[id] = new MegaShipComponentEntry
                {
                    displayName = id,
                    partType = partType,
                    isWeapon = isWeapon,
                    isLaser = ShipFamilyPartTypes.IsWeaponCannonProfile(partType),
                    bulletPrefabIndex = MegaShipCatalog.InheritTypeTableBankIndex,
                    stats = catalog != null
                        ? catalog.GetStatsForPartType(partType)
                        : default,
                };
            }
        }

        /// <summary>True when this child is a gameplay part (not a collider / turret base).</summary>
        public static bool TryClassifyChild(
            Transform t,
            Transform root,
            out string partType,
            out bool isWeapon)
        {
            partType = null;
            isWeapon = false;
            if (t == null || t == root)
                return false;

            if (MegaShipPartClassifier.IsWeaponAssemblyRoot(t, root))
            {
                partType = MegaShipPartClassifier.ResolvePartType(t);
                isWeapon = true;
                return true;
            }

            string id = MegaShipPartClassifier.GetPrefabAssetName(t);
            if (MegaShipPartClassifier.ShouldIgnore(id) || MegaShipPartClassifier.ShouldIgnore(t.name))
                return false;

            partType = MegaShipPartClassifier.ResolvePartType(t);
            if (string.Equals(partType, ShipFamilyPartTypes.Ignore, StringComparison.OrdinalIgnoreCase))
                return false;

            isWeapon = false;
            if (ShipFamilyPartTypes.IsWeapon(partType))
                return false;

            return true;
        }

        static int CompareUnique(MegaShipComponentEntry a, MegaShipComponentEntry b)
        {
            if (a == null && b == null)
                return 0;
            if (a == null)
                return 1;
            if (b == null)
                return -1;
            if (a.isWeapon != b.isWeapon)
                return a.isWeapon ? -1 : 1;
            int type = string.Compare(a.partType, b.partType, StringComparison.OrdinalIgnoreCase);
            if (type != 0)
                return type;
            return string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
