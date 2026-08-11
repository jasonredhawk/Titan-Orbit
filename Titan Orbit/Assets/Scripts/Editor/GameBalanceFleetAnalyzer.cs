using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Shared fleet scan for balance reports: walks every ShipFamilyDefinition upgrade-tree
    /// prefab, counts contributing parts by <see cref="ShipFamilyPartTypes"/>, and sums ability stats
    /// with the same rules as <see cref="ShipFamilyUpgradeTreeStatScanner"/>.
    /// Reports write under <c>tools/balance/</c> (repo-relative via Assets parent).
    /// </summary>
    public static class GameBalanceFleetAnalyzer
    {
        /// <summary>One chassis row used by composition / outlier / economy reports.</summary>
        public sealed class ChassisRow
        {
            public string FamilyId;
            public string ChassisId;
            public string PrefabName;
            public int ShipLevel;
            public int Cockpit;
            public int Wing;
            public int Engine;
            public int Thruster;
            public int WeaponBullet;
            public int WeaponCannon;
            public int Tail;
            public int Hull;
            public int Other;
            public int Propulsion => Engine + Thruster;
            public int Weapons => WeaponBullet + WeaponCannon;
            public int CargoParts => Cockpit + Wing;
            public ShipComponentAbilityStats Stats;
            public float PowerScore;
            public float Dps => Mathf.Max(0f, Stats.firePower) * Mathf.Max(0f, Stats.fireRate);
            public float SustainedDrain =>
                ShipComponentWeaponSuggestions.ComputeSustainedEnergyDrain(Stats.firePower, Stats.fireRate);
        }

        /// <summary>Min / max / mean / median / p10 / p90 for a float series.</summary>
        public struct StatAggregate
        {
            public float Min;
            public float Max;
            public float Mean;
            public float Median;
            public float P10;
            public float P90;
            public int Count;
        }

        /// <summary>Repo path ending in Titan Orbit/tools/balance (creates folder).</summary>
        public static string GetBalanceOutputDirectory()
        {
            // Assets → project root (…/Titan Orbit) → tools/balance
            string assets = Application.dataPath;
            string projectRoot = Directory.GetParent(assets)?.FullName ?? assets;
            string dir = Path.Combine(projectRoot, "tools", "balance");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Loads every family under Prefabs/Ships and scans upgrade-tree chassis.</summary>
        public static List<ChassisRow> ScanAllChassis(out string errorMessage)
        {
            errorMessage = null;
            var rows = new List<ChassisRow>();
            var profileSet = ShipFamilyPartCalcProfileSetEditorUtility.FindOrLoadShared();

            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition", new[] { "Assets/Prefabs/Ships" });
            if (guids == null || guids.Length == 0)
            {
                errorMessage = "No ShipFamilyDefinition assets under Assets/Prefabs/Ships.";
                return rows;
            }

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def == null || def.upgradeTree == null)
                    continue;

                string familyId = def.familyId != null ? def.familyId.Trim() : string.Empty;
                if (string.IsNullOrEmpty(familyId))
                    continue;

                for (int t = 0; t < def.upgradeTree.Count; t++)
                {
                    ShipFamilyChassisTierEntry tier = def.upgradeTree[t];
                    if (tier?.prefab == null)
                        continue;

                    if (!TryBuildChassisRow(def, familyId, tier, profileSet, out ChassisRow row))
                        continue;
                    rows.Add(row);
                }
            }

            if (rows.Count == 0)
                errorMessage = "No upgrade-tree prefabs found to scan.";
            return rows;
        }

        /// <summary>Builds one chassis row (part counts + summed L1 stats).</summary>
        public static bool TryBuildChassisRow(
            ShipFamilyDefinition def,
            string familyId,
            ShipFamilyChassisTierEntry tier,
            ShipFamilyPartCalcProfileSet profileSet,
            out ChassisRow row)
        {
            row = null;
            if (def == null || tier?.prefab == null || string.IsNullOrEmpty(familyId))
                return false;

            ShipComponentAbilityStats stats =
                ShipFamilyUpgradeTreeStatScanner.SumStatsForPrefabAsset(tier.prefab, def, familyId);
            CountPartsOnPrefab(tier.prefab, def, familyId, profileSet, out PartCounts counts);

            int shipLevel = Mathf.Max(1, tier.minHomePlanetLevel);
            // Apply tier growth so L3 people/gems match runtime GetEffectiveStatsAtShipLevel shape.
            ShipComponentAbilityStats atTier = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(
                stats,
                shipLevel,
                def.shipLevelStatGrowthFraction);

            row = new ChassisRow
            {
                FamilyId = familyId,
                ChassisId = !string.IsNullOrWhiteSpace(tier.chassisId) ? tier.chassisId.Trim() : tier.prefab.name,
                PrefabName = tier.prefab.name,
                ShipLevel = shipLevel,
                Cockpit = counts.Cockpit,
                Wing = counts.Wing,
                Engine = counts.Engine,
                Thruster = counts.Thruster,
                WeaponBullet = counts.WeaponBullet,
                WeaponCannon = counts.WeaponCannon,
                Tail = counts.Tail,
                Hull = counts.Hull,
                Other = counts.Other,
                Stats = atTier,
                PowerScore = ShipFamilyPowerScoreBreakdown.FromSummedShipStats(atTier).GetUpgradeTreeSortPowerScore(),
            };
            return true;
        }

        struct PartCounts
        {
            public int Cockpit;
            public int Wing;
            public int Engine;
            public int Thruster;
            public int WeaponBullet;
            public int WeaponCannon;
            public int Tail;
            public int Hull;
            public int Other;
        }

        /// <summary>
        /// Counts distinct contributing component ids under the prefab (same naming as Scan).
        /// Cosmetics with contributesAbilityStats=false are skipped.
        /// </summary>
        static void CountPartsOnPrefab(
            GameObject prefabAsset,
            ShipFamilyDefinition def,
            string familyId,
            ShipFamilyPartCalcProfileSet profileSet,
            out PartCounts counts)
        {
            counts = default;
            if (prefabAsset == null)
                return;

            string path = AssetDatabase.GetAssetPath(prefabAsset);
            GameObject root = !string.IsNullOrEmpty(path)
                ? PrefabUtility.LoadPrefabContents(path)
                : prefabAsset;
            if (root == null)
                return;

            bool loaded = !string.IsNullOrEmpty(path);
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var transforms = root.GetComponentsInChildren<Transform>(true);
                string prefix = familyId + "_";

                foreach (var t in transforms)
                {
                    if (t == null || string.IsNullOrEmpty(t.name))
                        continue;
                    if (!t.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string componentId = t.name.Substring(prefix.Length);
                    if (string.IsNullOrWhiteSpace(componentId))
                        continue;
                    if (!seen.Add(componentId))
                        continue;

                    // Prefer family table presence; skip unknown ids (no ability contribution).
                    if (def != null && !def.TryGetStatsForComponent(componentId, out _))
                        continue;

                    if (profileSet != null && !profileSet.ContributesAbilityStats(componentId))
                        continue;

                    string partType = profileSet != null
                        ? profileSet.ResolvePartType(componentId)
                        : ShipFamilyPartTypes.InferFromComponentName(componentId);
                    partType = ShipFamilyPartTypes.Normalize(partType, componentId);

                    if (string.Equals(partType, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase))
                        counts.Cockpit++;
                    else if (string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
                        counts.Wing++;
                    else if (ShipFamilyPartTypes.IsEngineProfile(partType))
                        counts.Engine++;
                    else if (ShipFamilyPartTypes.IsThrusterProfile(partType))
                        counts.Thruster++;
                    else if (string.Equals(partType, ShipFamilyPartTypes.WeaponBullet, StringComparison.OrdinalIgnoreCase))
                        counts.WeaponBullet++;
                    else if (ShipFamilyPartTypes.IsWeaponCannonProfile(partType))
                        counts.WeaponCannon++;
                    else if (ShipFamilyPartTypes.IsTurn(partType))
                        counts.Tail++;
                    else if (string.Equals(partType, ShipFamilyPartTypes.Hull, StringComparison.OrdinalIgnoreCase))
                        counts.Hull++;
                    else
                        counts.Other++;
                }
            }
            finally
            {
                if (loaded)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>Aggregate a float selector over rows.</summary>
        public static StatAggregate Aggregate(IReadOnlyList<ChassisRow> rows, Func<ChassisRow, float> selector)
        {
            var agg = new StatAggregate();
            if (rows == null || rows.Count == 0 || selector == null)
                return agg;

            var values = new List<float>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
                values.Add(selector(rows[i]));
            values.Sort();

            agg.Count = values.Count;
            agg.Min = values[0];
            agg.Max = values[values.Count - 1];
            float sum = 0f;
            for (int i = 0; i < values.Count; i++)
                sum += values[i];
            agg.Mean = sum / values.Count;
            agg.Median = PercentileSorted(values, 0.5f);
            agg.P10 = PercentileSorted(values, 0.1f);
            agg.P90 = PercentileSorted(values, 0.9f);
            return agg;
        }

        /// <summary>Linear-interpolation percentile on a pre-sorted list.</summary>
        public static float PercentileSorted(IReadOnlyList<float> sorted, float p)
        {
            if (sorted == null || sorted.Count == 0)
                return 0f;
            if (sorted.Count == 1)
                return sorted[0];
            float clamped = Mathf.Clamp01(p);
            float idx = clamped * (sorted.Count - 1);
            int lo = Mathf.FloorToInt(idx);
            int hi = Mathf.CeilToInt(idx);
            if (lo == hi)
                return sorted[lo];
            float t = idx - lo;
            return Mathf.Lerp(sorted[lo], sorted[hi], t);
        }

        /// <summary>Median of a selector; 0 when empty.</summary>
        public static float MedianOf(IReadOnlyList<ChassisRow> rows, Func<ChassisRow, float> selector)
        {
            return Aggregate(rows, selector).Median;
        }

        /// <summary>CSV-escape a cell.</summary>
        public static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        /// <summary>Invariant culture float for CSV.</summary>
        public static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Writes text atomically (UTF-8).</summary>
        public static void WriteTextFile(string absolutePath, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? GetBalanceOutputDirectory());
            File.WriteAllText(absolutePath, contents ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>Formats one aggregate line for markdown.</summary>
        public static string FormatAggregateLine(string label, StatAggregate a)
        {
            return $"| {label} | {F(a.Min)} | {F(a.P10)} | {F(a.Median)} | {F(a.Mean)} | {F(a.P90)} | {F(a.Max)} | {a.Count} |";
        }
    }
}
