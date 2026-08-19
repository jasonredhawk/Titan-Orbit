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
    /// Cruise speed treats Engine and Thruster as the same contributor
    /// (fastest + extraEngineSpeedPercent of the rest).
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
            if (keepManualStats && catalog.uniqueComponents != null)
            {
                for (int i = 0; i < catalog.uniqueComponents.Count; i++)
                {
                    var old = catalog.uniqueComponents[i];
                    if (old == null || string.IsNullOrEmpty(old.displayName))
                        continue;
                    previous[old.displayName] = old.stats;
                    previousBanks[old.displayName] = old.bulletPrefabIndex;
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
                next.Add(row);
            }

            next.Sort(CompareUnique);
            catalog.uniqueComponents = next;
            RecalcAllShipSums(catalog);
            return next.Count;
        }

        /// <summary>
        /// Walks each hull prefab and writes raw sums (cruise = fastest engine/thruster + extra%).
        /// Zeros stay 0 so orange rows stay honest; in-game defaults/minimums live on the catalog.
        /// </summary>
        public static void RecalcAllShipSums(MegaShipCatalog catalog)
        {
            if (catalog?.entries == null)
                return;

            for (int i = 0; i < catalog.entries.Count; i++)
                RecalcShipSum(catalog, catalog.entries[i]);
        }

        /// <summary>Sums one hull from the unique library × prefab name counts.</summary>
        public static MegaShipPartStats RecalcShipSum(
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry)
        {
            var sum = default(MegaShipPartStats);
            if (entry == null)
                return sum;

            var counts = new List<MegaShipComponentCount>(16);
            var propulsionMoves = new List<float>(8);
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

                    // --- Cruise contributors: engines and thrusters are the same kind ---
                    // [TITAN-ORBIT] Regular families already share move/accel aggregation
                    // (ShipPropulsionAggregation). MEGA cruise uses the same idea: collect
                    // moveSpeed from every Engine and Thruster, then max + extra% of the rest.
                    if (ShipFamilyPartTypes.IsPropulsion(partType))
                        propulsionMoves.Add(part.moveSpeed);

                    // Cruise speed is computed from the propulsion list — do not add part.moveSpeed here.
                    var add = part;
                    add.moveSpeed = 0f;
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

            sum.moveSpeed = CombineEngineCruise(propulsionMoves, catalog != null
                ? catalog.GetExtraEngineSpeedPercent()
                : MegaShipCatalog.DefaultExtraEngineSpeedPercent);

            entry.componentCounts = counts;
            entry.hasMissingStats = MegaShipPartStats.HasMissingNonFirepower(sum);
            entry.summedStats = sum;
            return sum;
        }

        /// <summary>
        /// Fastest engine or thruster + <paramref name="extraPercent"/> of every other
        /// propulsion part's moveSpeed. Empty list → 0 (in-game default/minimum fills it).
        /// </summary>
        /// <param name="engineMoves">moveSpeed from every Engine and Thruster on the hull.</param>
        /// <param name="extraPercent">Catalog extraEngineSpeedPercent (0.02 = 2%).</param>
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

            if (MegaShipPartClassifier.IsTaggedWeapon(t))
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
