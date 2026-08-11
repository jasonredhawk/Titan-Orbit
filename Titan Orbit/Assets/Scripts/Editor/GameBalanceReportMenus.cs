using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Optional CSV exporters for fleet composition / outliers / economy.
    /// Prefer the <see cref="RebalanceGame"/> hub Inspector (Refresh Review) for day-to-day work;
    /// these menus write under <c>Titan Orbit/tools/balance/</c> when a spreadsheet dump is needed.
    /// </summary>
    public static class GameBalanceReportMenus
    {
        const string MenuRoot = "TitanOrbit/Balance/Optional CSV/";

        // -------------------------------------------------------------------------
        // Fleet composition
        // -------------------------------------------------------------------------

        /// <summary>
        /// Exports per-chassis part counts + stats CSV and a summary markdown with global/per-family
        /// min/p10/median/mean/p90/max aggregates.
        /// </summary>
        [MenuItem(MenuRoot + "Export Fleet Composition Report")]
        public static void ExportFleetCompositionReport()
        {
            ExportFleetCompositionReportCore(showDialog: true);
        }

        /// <summary>[EDITOR] No dialog — for MCP / batch.</summary>
        [MenuItem(MenuRoot + "Export Fleet Composition Report (Silent)")]
        public static void ExportFleetCompositionReportSilent()
        {
            ExportFleetCompositionReportCore(showDialog: false);
        }

        static void ExportFleetCompositionReportCore(bool showDialog)
        {
            var rows = GameBalanceFleetAnalyzer.ScanAllChassis(out string error);
            if (rows.Count == 0)
            {
                Debug.LogError("[GameBalance] Fleet composition: " + (error ?? "No chassis scanned."));
                if (showDialog)
                    EditorUtility.DisplayDialog("Fleet Composition", error ?? "No chassis scanned.", "OK");
                return;
            }

            string dir = GameBalanceFleetAnalyzer.GetBalanceOutputDirectory();
            string csvPath = Path.Combine(dir, "FleetComposition_Report.csv");
            string mdPath = Path.Combine(dir, "FleetComposition_Summary.md");

            GameBalanceFleetAnalyzer.WriteTextFile(csvPath, BuildCompositionCsv(rows));
            GameBalanceFleetAnalyzer.WriteTextFile(mdPath, BuildCompositionMarkdown(rows));

            Debug.Log($"[GameBalance] Fleet composition: {rows.Count} chassis →\n{csvPath}\n{mdPath}");
            if (showDialog)
            {
                EditorUtility.RevealInFinder(csvPath);
                EditorUtility.DisplayDialog(
                    "Fleet Composition",
                    $"Wrote {rows.Count} chassis rows.\n\n{csvPath}\n{mdPath}",
                    "OK");
            }
        }

        static string BuildCompositionCsv(List<GameBalanceFleetAnalyzer.ChassisRow> rows)
        {
            var sb = new StringBuilder(rows.Count * 200);
            sb.AppendLine(
                "familyId,chassisId,prefabName,shipLevel," +
                "cockpit,wing,engine,thruster,weaponBullet,weaponCannon,tail,hull,other," +
                "propulsion,weapons,cargoParts," +
                "firePower,fireRate,dps,rammingPower,healthCap,energyCap,energyRegen," +
                "moveSpeed,turnSpeed,gemCap,peopleCap,powerScore");

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var s = r.Stats;
                sb.Append(GameBalanceFleetAnalyzer.Csv(r.FamilyId)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.Csv(r.ChassisId)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.Csv(r.PrefabName)).Append(',');
                sb.Append(r.ShipLevel).Append(',');
                sb.Append(r.Cockpit).Append(',');
                sb.Append(r.Wing).Append(',');
                sb.Append(r.Engine).Append(',');
                sb.Append(r.Thruster).Append(',');
                sb.Append(r.WeaponBullet).Append(',');
                sb.Append(r.WeaponCannon).Append(',');
                sb.Append(r.Tail).Append(',');
                sb.Append(r.Hull).Append(',');
                sb.Append(r.Other).Append(',');
                sb.Append(r.Propulsion).Append(',');
                sb.Append(r.Weapons).Append(',');
                sb.Append(r.CargoParts).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.firePower)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.fireRate)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(r.Dps)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.rammingPower)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.healthCap)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.energyCap)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.energyRegen)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.moveSpeed)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.turnSpeed)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.maxGems)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(s.maxPeople)).Append(',');
                sb.Append(GameBalanceFleetAnalyzer.F(r.PowerScore));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        static string BuildCompositionMarkdown(List<GameBalanceFleetAnalyzer.ChassisRow> rows)
        {
            var sb = new StringBuilder(4096);
            sb.Append(GameBalanceTargets.FormatTargetsHeaderMarkdown());
            sb.AppendLine();
            sb.AppendLine($"Scanned chassis: **{rows.Count}**");
            sb.AppendLine();
            AppendAggregateTable(sb, "Global", rows);

            // Per-family sections
            var byFamily = new Dictionary<string, List<GameBalanceFleetAnalyzer.ChassisRow>>(
                StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (!byFamily.TryGetValue(r.FamilyId, out var list))
                {
                    list = new List<GameBalanceFleetAnalyzer.ChassisRow>();
                    byFamily[r.FamilyId] = list;
                }

                list.Add(r);
            }

            var familyNames = new List<string>(byFamily.Keys);
            familyNames.Sort(StringComparer.OrdinalIgnoreCase);
            for (int f = 0; f < familyNames.Count; f++)
                AppendAggregateTable(sb, familyNames[f], byFamily[familyNames[f]]);

            return sb.ToString();
        }

        static void AppendAggregateTable(
            StringBuilder sb,
            string title,
            List<GameBalanceFleetAnalyzer.ChassisRow> rows)
        {
            sb.AppendLine($"## {title} ({rows.Count} chassis)");
            sb.AppendLine();
            sb.AppendLine("| Metric | Min | P10 | Median | Mean | P90 | Max | N |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            void Row(string label, Func<GameBalanceFleetAnalyzer.ChassisRow, float> sel) =>
                sb.AppendLine(GameBalanceFleetAnalyzer.FormatAggregateLine(
                    label, GameBalanceFleetAnalyzer.Aggregate(rows, sel)));

            Row("Wings", r => r.Wing);
            Row("Engines", r => r.Engine);
            Row("Thrusters", r => r.Thruster);
            Row("Propulsion (E+T)", r => r.Propulsion);
            Row("Weapons", r => r.Weapons);
            Row("Cockpits", r => r.Cockpit);
            Row("Cargo parts", r => r.CargoParts);
            Row("Tails", r => r.Tail);
            Row("Hull parts", r => r.Hull);
            Row("DPS", r => r.Dps);
            Row("MoveSpeed", r => r.Stats.moveSpeed);
            Row("TurnSpeed", r => r.Stats.turnSpeed);
            Row("EnergyCap", r => r.Stats.energyCap);
            Row("EnergyRegen", r => r.Stats.energyRegen);
            Row("GemCap", r => r.Stats.maxGems);
            Row("PeopleCap", r => r.Stats.maxPeople);
            Row("HealthCap", r => r.Stats.healthCap);
            Row("PowerScore", r => r.PowerScore);
            Row("Propulsion/Wings", r => r.Wing > 0 ? (float)r.Propulsion / r.Wing : r.Propulsion);
            sb.AppendLine();
        }

        // -------------------------------------------------------------------------
        // Outliers
        // -------------------------------------------------------------------------

        /// <summary>
        /// Scores each chassis against fleet medians; flags Hippo-style propulsion starvation,
        /// wing balloons, cargo freaks, energy insolvency, and weapon extremes.
        /// </summary>
        [MenuItem(MenuRoot + "Export Ship Outlier Report")]
        public static void ExportShipOutlierReport()
        {
            ExportShipOutlierReportCore(showDialog: true);
        }

        /// <summary>[EDITOR] No dialog — for MCP / batch.</summary>
        [MenuItem(MenuRoot + "Export Ship Outlier Report (Silent)")]
        public static void ExportShipOutlierReportSilent()
        {
            ExportShipOutlierReportCore(showDialog: false);
        }

        static void ExportShipOutlierReportCore(bool showDialog)
        {
            var rows = GameBalanceFleetAnalyzer.ScanAllChassis(out string error);
            if (rows.Count == 0)
            {
                Debug.LogError("[GameBalance] Outliers: " + (error ?? "No chassis scanned."));
                if (showDialog)
                    EditorUtility.DisplayDialog("Ship Outliers", error ?? "No chassis scanned.", "OK");
                return;
            }

            float medWings = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Wing);
            float medProp = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Propulsion);
            float medPropPerWing = GameBalanceFleetAnalyzer.MedianOf(
                rows, r => r.Wing > 0 ? (float)r.Propulsion / r.Wing : r.Propulsion);
            float medMoveGlobal = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Stats.moveSpeed);
            float medDpsGlobal = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Dps);
            float wingP90 = GameBalanceFleetAnalyzer.Aggregate(rows, r => r.Wing).P90;
            float propP10 = GameBalanceFleetAnalyzer.Aggregate(rows, r => r.Propulsion).P10;

            // Same-level medians — high tiers have huge DPS; global medians false-flag everything.
            var peopleByLevel = new Dictionary<int, float>();
            var gemsByLevel = new Dictionary<int, float>();
            var moveByLevel = new Dictionary<int, float>();
            var dpsByLevel = new Dictionary<int, float>();
            var weaponsByLevel = new Dictionary<int, float>();
            var energyRegenByLevel = new Dictionary<int, float>();
            var drainByLevel = new Dictionary<int, float>();
            for (int level = 1; level <= 7; level++)
            {
                int lvl = level;
                var slice = rows.FindAll(r => r.ShipLevel == lvl);
                if (slice.Count == 0)
                    continue;
                peopleByLevel[lvl] = GameBalanceFleetAnalyzer.MedianOf(slice, r => r.Stats.maxPeople);
                gemsByLevel[lvl] = GameBalanceFleetAnalyzer.MedianOf(slice, r => r.Stats.maxGems);
                moveByLevel[lvl] = GameBalanceFleetAnalyzer.MedianOf(slice, r => r.Stats.moveSpeed);
                dpsByLevel[lvl] = GameBalanceFleetAnalyzer.MedianOf(slice, r => r.Dps);
                weaponsByLevel[lvl] = GameBalanceFleetAnalyzer.MedianOf(slice, r => r.Weapons);
                energyRegenByLevel[lvl] = GameBalanceFleetAnalyzer.MedianOf(slice, r => r.Stats.energyRegen);
                drainByLevel[lvl] = GameBalanceFleetAnalyzer.MedianOf(slice, r => r.SustainedDrain);
            }

            var flagged = new List<(GameBalanceFleetAnalyzer.ChassisRow row, string flags, string fixClass, float severity)>();

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var flags = new List<string>();
                var fixes = new List<string>();
                float severity = 0f;

                float propPerWing = r.Wing > 0 ? (float)r.Propulsion / r.Wing : r.Propulsion;
                if (medPropPerWing > 0.01f
                    && propPerWing < medPropPerWing * GameBalanceTargets.PropulsionStarvationRatioOfMedian)
                {
                    flags.Add("propulsion_starvation");
                    fixes.Add("needs_extra_engine_or_thruster_stats");
                    severity += (medPropPerWing - propPerWing) / medPropPerWing;
                }

                if (r.Wing + 0.001f >= wingP90 && r.Propulsion <= propP10 + 0.001f && r.Wing >= 2)
                {
                    flags.Add("wing_balloon");
                    fixes.Add("needs_wing_stat_nerf");
                    fixes.Add("structural_prefab");
                    severity += 1f;
                }

                float medMove = moveByLevel.TryGetValue(r.ShipLevel, out float mv) ? mv : medMoveGlobal;
                if (medMove > 0.01f
                    && r.Stats.moveSpeed < medMove * GameBalanceTargets.SlowHullMoveSpeedRatioOfMedian)
                {
                    flags.Add("slow_hull");
                    fixes.Add("needs_extra_engine_or_thruster_stats");
                    severity += (medMove - r.Stats.moveSpeed) / medMove;
                }

                if (peopleByLevel.TryGetValue(r.ShipLevel, out float medPeople)
                    && medPeople > 0.01f
                    && r.Stats.maxPeople > medPeople * GameBalanceTargets.CargoFreakMultiplier)
                {
                    flags.Add("cargo_freak_people");
                    fixes.Add("needs_wing_stat_nerf");
                    severity += r.Stats.maxPeople / medPeople - 1f;
                }

                if (gemsByLevel.TryGetValue(r.ShipLevel, out float medGems)
                    && medGems > 0.01f
                    && r.Stats.maxGems > medGems * GameBalanceTargets.CargoFreakMultiplier)
                {
                    flags.Add("cargo_freak_gems");
                    fixes.Add("needs_wing_stat_nerf");
                    severity += r.Stats.maxGems / medGems - 1f;
                }

                float drain = r.SustainedDrain;
                // [STANDARD] Avoid out-var name `md` — later in this method `md` is the markdown StringBuilder.
                float medDrain = drainByLevel.TryGetValue(r.ShipLevel, out float medDrainAtLevel)
                    ? medDrainAtLevel
                    : drain;
                float medRegen = energyRegenByLevel.TryGetValue(r.ShipLevel, out float medRegenAtLevel)
                    ? medRegenAtLevel
                    : r.Stats.energyRegen;
                // Flag insolvency only when this hull is worse than same-level median regen/drain ratio.
                float regenRatio = drain > 0.01f ? r.Stats.energyRegen / drain : 1f;
                float medRegenRatio = medDrain > 0.01f ? medRegen / medDrain : 1f;
                if (drain > 0.01f
                    && regenRatio < GameBalanceTargets.EnergyInsolvencyRegenFractionOfDrain
                    && regenRatio + 0.001f < medRegenRatio * 0.75f)
                {
                    flags.Add("energy_insolvency");
                    fixes.Add("needs_extra_engine_or_thruster_stats");
                    severity += 0.75f;
                }

                if (drain > 0.01f && r.Stats.energyRegen > drain * 0.95f)
                {
                    flags.Add("infinite_laser");
                    fixes.Add("needs_extra_engine_or_thruster_stats");
                    severity += 0.5f;
                }

                float medWeapons = weaponsByLevel.TryGetValue(r.ShipLevel, out float mw) ? mw : 1f;
                if (r.Weapons <= 0 && medWeapons >= 1f)
                {
                    flags.Add("weaponless");
                    fixes.Add("structural_prefab");
                    severity += 0.5f;
                }

                float medDps = dpsByLevel.TryGetValue(r.ShipLevel, out float mdps) ? mdps : medDpsGlobal;
                if (medDps > 0.01f && r.Dps > medDps * 2.5f)
                {
                    flags.Add("overgunned");
                    severity += r.Dps / medDps - 1f;
                }

                // Single-engine + many wings: Hippo-class structural note
                if (r.Propulsion <= 1 && r.Wing >= Mathf.Max(3, Mathf.RoundToInt(medWings + 1f)))
                {
                    if (!flags.Contains("wing_balloon"))
                        flags.Add("hippo_class_structure");
                    if (!fixes.Contains("structural_prefab"))
                        fixes.Add("structural_prefab");
                    severity += 0.8f;
                }

                if (flags.Count == 0)
                    continue;

                // Dedupe fix classes
                var uniqueFixes = new List<string>();
                for (int f = 0; f < fixes.Count; f++)
                {
                    if (!uniqueFixes.Contains(fixes[f]))
                        uniqueFixes.Add(fixes[f]);
                }

                flagged.Add((r, string.Join("|", flags), string.Join("|", uniqueFixes), severity));
            }

            flagged.Sort((a, b) => b.severity.CompareTo(a.severity));

            string dir = GameBalanceFleetAnalyzer.GetBalanceOutputDirectory();
            string csvPath = Path.Combine(dir, "ShipOutliers_Report.csv");
            string mdPath = Path.Combine(dir, "ShipOutliers_Summary.md");

            var csv = new StringBuilder();
            csv.AppendLine(
                "severity,familyId,chassisId,prefabName,shipLevel,wings,engines,thrusters,propulsion," +
                "weapons,moveSpeed,dps,energyCap,energyRegen,gemCap,peopleCap,flags,fixClass");
            for (int i = 0; i < flagged.Count; i++)
            {
                var (r, flags, fix, sev) = flagged[i];
                csv.Append(GameBalanceFleetAnalyzer.F(sev)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.Csv(r.FamilyId)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.Csv(r.ChassisId)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.Csv(r.PrefabName)).Append(',');
                csv.Append(r.ShipLevel).Append(',');
                csv.Append(r.Wing).Append(',');
                csv.Append(r.Engine).Append(',');
                csv.Append(r.Thruster).Append(',');
                csv.Append(r.Propulsion).Append(',');
                csv.Append(r.Weapons).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.F(r.Stats.moveSpeed)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.F(r.Dps)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.F(r.Stats.energyCap)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.F(r.Stats.energyRegen)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.F(r.Stats.maxGems)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.F(r.Stats.maxPeople)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.Csv(flags)).Append(',');
                csv.Append(GameBalanceFleetAnalyzer.Csv(fix));
                csv.AppendLine();
            }

            var md = new StringBuilder();
            md.Append(GameBalanceTargets.FormatTargetsHeaderMarkdown());
            md.AppendLine();
            md.AppendLine($"# Ship outliers ({flagged.Count} of {rows.Count})");
            md.AppendLine();
            md.AppendLine(
                $"Fleet medians: wings={GameBalanceFleetAnalyzer.F(medWings)}, " +
                $"propulsion={GameBalanceFleetAnalyzer.F(medProp)}, " +
                $"prop/wing={GameBalanceFleetAnalyzer.F(medPropPerWing)}, " +
                $"move={GameBalanceFleetAnalyzer.F(medMoveGlobal)}, dps={GameBalanceFleetAnalyzer.F(medDpsGlobal)} " +
                "(combat/cargo flags use same-level medians)");
            md.AppendLine();
            md.AppendLine("| Severity | Chassis | Flags | Fix class |");
            md.AppendLine("|---:|---|---|---|");
            int show = Mathf.Min(40, flagged.Count);
            for (int i = 0; i < show; i++)
            {
                var (r, flags, fix, sev) = flagged[i];
                md.Append("| ").Append(GameBalanceFleetAnalyzer.F(sev)).Append(" | ");
                md.Append(r.FamilyId).Append('/').Append(r.ChassisId).Append(" | ");
                md.Append(flags).Append(" | ");
                md.Append(fix).AppendLine(" |");
            }

            md.AppendLine();
            md.AppendLine(
                "Fix classes: `needs_extra_engine_or_thruster_stats` (ProfileSet / specialBonuses), " +
                "`needs_wing_stat_nerf` (Wing cargo / stack), `structural_prefab` (manual USC hierarchy — not auto-edited).");

            GameBalanceFleetAnalyzer.WriteTextFile(csvPath, csv.ToString());
            GameBalanceFleetAnalyzer.WriteTextFile(mdPath, md.ToString());

            Debug.Log($"[GameBalance] Outliers: {flagged.Count} flagged →\n{csvPath}");
            if (showDialog)
            {
                EditorUtility.RevealInFinder(csvPath);
                EditorUtility.DisplayDialog(
                    "Ship Outliers",
                    $"Flagged {flagged.Count} / {rows.Count} chassis.\n\n{csvPath}",
                    "OK");
            }
        }

        // -------------------------------------------------------------------------
        // Economy cross-check
        // -------------------------------------------------------------------------

        /// <summary>
        /// Cross-checks asteroid TTK, gem fill, chassis costs, attribute sinks, planet capture
        /// math, and card sidegrades against <see cref="GameBalanceTargets"/>.
        /// </summary>
        [MenuItem(MenuRoot + "Export Economy Cross-Check")]
        public static void ExportEconomyCrossCheck()
        {
            ExportEconomyCrossCheckCore(showDialog: true);
        }

        /// <summary>[EDITOR] No dialog — for MCP / batch.</summary>
        [MenuItem(MenuRoot + "Export Economy Cross-Check (Silent)")]
        public static void ExportEconomyCrossCheckSilent()
        {
            ExportEconomyCrossCheckCore(showDialog: false);
        }

        static void ExportEconomyCrossCheckCore(bool showDialog)
        {
            var rows = GameBalanceFleetAnalyzer.ScanAllChassis(out string error);
            if (rows.Count == 0)
            {
                Debug.LogError("[GameBalance] Economy: " + (error ?? "No chassis scanned."));
                if (showDialog)
                    EditorUtility.DisplayDialog("Economy Cross-Check", error ?? "No chassis scanned.", "OK");
                return;
            }

            var asteroid = Resources.Load<AsteroidSettings>("AsteroidSettings");
            float healthPerSize = asteroid != null ? asteroid.HealthPerSize : 3f;
            float gemsPerSize = asteroid != null ? asteroid.GemsPerSize : 1f;
            float midSize = GameBalanceTargets.MidAsteroidSize;
            float midHp = midSize * healthPerSize;
            float midGems = midSize * gemsPerSize;

            var l1 = rows.FindAll(r => r.ShipLevel == 1);
            var l3 = rows.FindAll(r => r.ShipLevel == 3);
            var l6 = rows.FindAll(r => r.ShipLevel == 6);
            if (l1.Count == 0) l1 = rows;

            float medDpsL1 = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Dps);
            float medDpsL3 = l3.Count > 0 ? GameBalanceFleetAnalyzer.MedianOf(l3, r => r.Dps) : medDpsL1;
            float medDpsL6 = l6.Count > 0 ? GameBalanceFleetAnalyzer.MedianOf(l6, r => r.Dps) : medDpsL1;
            float medGemL1 = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Stats.maxGems);
            float medPeopleL3 = l3.Count > 0
                ? GameBalanceFleetAnalyzer.MedianOf(l3, r => r.Stats.maxPeople)
                : GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Stats.maxPeople);
            float medWing = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Wing);

            float ttkL1 = medDpsL1 > 0.01f ? midHp / medDpsL1 : float.PositiveInfinity;
            float ttkL3 = medDpsL3 > 0.01f ? midHp / medDpsL3 : float.PositiveInfinity;
            float ttkL6 = medDpsL6 > 0.01f ? midHp / medDpsL6 : float.PositiveInfinity;
            float mineFillSec = medGemL1 > 0.01f
                ? medGemL1 / GameBalanceTargets.ReferenceMiningRateGemsPerSecond
                : 0f;
            float chassisTrips = GameBalanceTargets.ChassisCostGemCapMultiplier; // cost/gemCap identity
            int attrFullBarCostL3 = GameBalanceTargets.AttributeUpgradeCostPerShipLevel * 3 * 3; // cost×levels for one attr
            // Ten HUD attributes × full bar at L3
            int attrAllFullL3 = attrFullBarCostL3 * 10;

            float homePop = PlanetPopulationMath.GetMaxPopulation(
                GameBalanceTargets.ReferenceHomePlanetSize,
                GameBalanceTargets.ReferenceHomePlanetLevel);
            float capturePeopleNeeded = homePop;
            float fleetCargo = GameBalanceTargets.CaptureShipCount * medPeopleL3;
            float batchesNeeded = fleetCargo > 0.01f
                ? capturePeopleNeeded / fleetCargo
                : float.PositiveInfinity;

            float suggestHpPerSize = GameBalanceTargets.SuggestHealthPerSizeForMidRock(medDpsL1);

            // Card sidegrade vs second engine (compounding note)
            float cardKineticL3 = CardDeckBalance.KineticDamageMultiplier(3, 2);
            float cardCargoGems = CardDeckBalance.CargoGemAdd(3, 2);
            float cardCost = CardDeckBalance.SuggestedGemCost(3, 2);

            var flags = new List<string>();
            void Check(bool ok, string failMessage)
            {
                if (!ok)
                    flags.Add(failMessage);
            }

            Check(
                ttkL1 >= GameBalanceTargets.MidAsteroidTtkSecondsMin
                && ttkL1 <= GameBalanceTargets.MidAsteroidTtkSecondsMax,
                $"FAIL mid-rock TTK L1={ttkL1:0.0}s not in " +
                $"{GameBalanceTargets.MidAsteroidTtkSecondsMin}-{GameBalanceTargets.MidAsteroidTtkSecondsMax}s " +
                $"(suggest HealthPerSize≈{suggestHpPerSize:0.###}, current={healthPerSize:0.###})");

            Check(
                batchesNeeded >= 4f && batchesNeeded <= 6f,
                $"FAIL capture batches≈{batchesNeeded:0.0} (want 4–6) with L3 peopleCap median={medPeopleL3:0.0}, homePop={homePop:0}");

            Check(
                Mathf.Abs(medGemL1 - GameBalanceTargets.TargetMedianGemCapAtShipLevel1)
                <= GameBalanceTargets.TargetMedianGemCapAtShipLevel1 * 0.4f,
                $"WARN L1 gemCap median={medGemL1:0.0} vs target {GameBalanceTargets.TargetMedianGemCapAtShipLevel1:0}");

            Check(
                Mathf.Abs(chassisTrips - GameBalanceTargets.ChassisCostCargoTripsTarget) < 0.01f,
                $"FAIL chassis cost trips={chassisTrips} (formula drifted from 2×gemCap)");

            Check(
                Mathf.Abs(medWing - GameBalanceTargets.ExpectedMedianWingCount) <= 1.5f,
                $"WARN median wings={medWing:0.0} vs ExpectedMedianWingCount={GameBalanceTargets.ExpectedMedianWingCount:0} " +
                "(update GameBalanceTargets after reviewing FleetComposition_Summary)");

            Check(
                mineFillSec <= GameBalanceTargets.GemFillLoopSecondsMax,
                $"WARN pure mining fill={mineFillSec:0.0}s exceeds soft max {GameBalanceTargets.GemFillLoopSecondsMax:0}s");

            string dir = GameBalanceFleetAnalyzer.GetBalanceOutputDirectory();
            string mdPath = Path.Combine(dir, "EconomyCrossCheck_Report.md");
            string csvPath = Path.Combine(dir, "EconomyCrossCheck_Report.csv");

            var md = new StringBuilder();
            md.Append(GameBalanceTargets.FormatTargetsHeaderMarkdown());
            md.AppendLine();
            md.AppendLine("# Economy cross-check");
            md.AppendLine();
            md.AppendLine("## Asteroids");
            md.AppendLine(
                $"- Settings: HealthPerSize={healthPerSize:0.###}, GemsPerSize={gemsPerSize:0.###} " +
                $"(asset {(asteroid != null ? "loaded" : "MISSING — used defaults")})");
            md.AppendLine(
                $"- Mid Size {midSize}: HP={midHp:0.0}, gems={midGems:0.0}");
            md.AppendLine(
                $"- TTK @ median DPS: L1 {ttkL1:0.0}s (dps {medDpsL1:0.0}), " +
                $"L3 {ttkL3:0.0}s (dps {medDpsL3:0.0}), L6 {ttkL6:0.0}s (dps {medDpsL6:0.0})");
            md.AppendLine(
                $"- Suggested HealthPerSize for ideal TTK: **{suggestHpPerSize:0.###}**");
            md.AppendLine();
            md.AppendLine("## Gems / costs");
            md.AppendLine(
                $"- Median L1 gemCap={medGemL1:0.0}; chassis cost={GameBalanceTargets.ChassisCostGemCapMultiplier:0}×gemCap " +
                $"(≈{chassisTrips:0} cargo trips); pure mining fill≈{mineFillSec:0.0}s @ " +
                $"{GameBalanceTargets.ReferenceMiningRateGemsPerSecond:0} g/s");
            md.AppendLine(
                $"- Attribute full bar one-stat L3 cost={attrFullBarCostL3} gems; all 10 attrs≈{attrAllFullL3} gems");
            md.AppendLine(
                $"- Part price uses powerScore×1.75×(1+(L−1)×0.12) (ShipComponentStoreData) — compare to chassis in Inspector");
            md.AppendLine();
            md.AppendLine("## Capture / people");
            md.AppendLine(
                $"- Home pop (size {GameBalanceTargets.ReferenceHomePlanetSize}, L{GameBalanceTargets.ReferenceHomePlanetLevel}) " +
                $"= {homePop:0} (PlanetPopulationMath)");
            md.AppendLine(
                $"- Median L3 peopleCap={medPeopleL3:0.0}; {GameBalanceTargets.CaptureShipCount} ships cargo={fleetCargo:0.0}; " +
                $"batches to drain full home≈**{batchesNeeded:0.00}** (target 4–6)");
            md.AppendLine(
                $"- Target L3 peopleCap≈{GameBalanceTargets.TargetMedianPeopleCapAtShipLevel3:0.0}");
            md.AppendLine();
            md.AppendLine("## Cards (procedural sidegrades)");
            md.AppendLine(
                $"- Kinetic dmg mult L3/r2={cardKineticL3:0.000}; cargo gem add={cardCargoGems:0.0}; " +
                $"suggested cost={cardCost:0}g");
            md.AppendLine(
                "- Cards multiply combat / add flats; they are **not** a second Engine stack. " +
                "Unused card fields (`miningRateAdd`, deposit speed mults) are still not applied in ShipStatApplyLogic.");
            md.AppendLine();
            md.AppendLine("## Flags");
            if (flags.Count == 0)
                md.AppendLine("- OK — no hard failures (warnings may still appear above as WARN).");
            else
            {
                for (int i = 0; i < flags.Count; i++)
                    md.AppendLine($"- {flags[i]}");
            }

            var csv = new StringBuilder();
            csv.AppendLine("check,value,targetOrNote,status");
            csv.Append("mid_ttk_l1_sec,").Append(ttkL1.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append($"{GameBalanceTargets.MidAsteroidTtkSecondsMin}-{GameBalanceTargets.MidAsteroidTtkSecondsMax}")
                .Append(',')
                .AppendLine(ttkL1 >= GameBalanceTargets.MidAsteroidTtkSecondsMin
                             && ttkL1 <= GameBalanceTargets.MidAsteroidTtkSecondsMax
                    ? "PASS"
                    : "FAIL");
            csv.Append("capture_batches,").Append(batchesNeeded.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append("4-6,").AppendLine(batchesNeeded >= 4f && batchesNeeded <= 6f ? "PASS" : "FAIL");
            csv.Append("median_l3_people,").Append(medPeopleL3.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(GameBalanceTargets.TargetMedianPeopleCapAtShipLevel3.ToString("0.###", CultureInfo.InvariantCulture))
                .AppendLine(",INFO");
            csv.Append("suggest_health_per_size,").Append(suggestHpPerSize.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(",from_median_l1_dps,").AppendLine("INFO");
            csv.Append("current_health_per_size,").Append(healthPerSize.ToString("0.###", CultureInfo.InvariantCulture))
                .AppendLine(",AsteroidSettings,INFO");
            csv.Append("median_wings,").Append(medWing.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(GameBalanceTargets.ExpectedMedianWingCount.ToString("0.###", CultureInfo.InvariantCulture))
                .AppendLine(",WARN_IF_FAR");

            GameBalanceFleetAnalyzer.WriteTextFile(mdPath, md.ToString());
            GameBalanceFleetAnalyzer.WriteTextFile(csvPath, csv.ToString());

            Debug.Log($"[GameBalance] Economy cross-check →\n{mdPath}\nFlags: {flags.Count}");
            if (showDialog)
            {
                EditorUtility.RevealInFinder(mdPath);
                EditorUtility.DisplayDialog(
                    "Economy Cross-Check",
                    $"Wrote report.\nFlags: {flags.Count}\n\n{mdPath}",
                    "OK");
            }
        }
    }
}
