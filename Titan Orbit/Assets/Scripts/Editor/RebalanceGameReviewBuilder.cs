using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Fills a <see cref="RebalanceGame"/> asset with fleet aggregates, outliers, and
    /// economy gates — the in-Inspector review surface (CSV export is optional elsewhere).
    /// </summary>
    public static class RebalanceGameReviewBuilder
    {
        /// <summary>
        /// Auto-loads Resources balance SOs and every ShipFamilyDefinition under Prefabs/Ships.
        /// </summary>
        public static void AutoFindReferences(RebalanceGame hub)
        {
            if (hub == null)
                return;

            Undo.RecordObject(hub, "RebalanceGame Auto-Find References");

            hub.partCalcProfileSet = ShipFamilyPartCalcProfileSetEditorUtility.FindOrLoadShared();
            hub.planetShipFamilyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            hub.asteroidSettings = Resources.Load<AsteroidSettings>("AsteroidSettings");
            hub.mapGenerationSettings = Resources.Load<MapGenerationSettings>("MapGenerationSettings");
            hub.gemExplosionSettings = Resources.Load<GemExplosionSettings>("GemExplosionSettings");
            hub.shipRammingSettings = Resources.Load<ShipRammingSettings>("ShipRammingSettings");
            hub.shipCargoMobilitySettings = Resources.Load<ShipCargoMobilitySettings>("ShipCargoMobilitySettings");
            hub.tractorBeamSettings = Resources.Load<TractorBeamSettings>("TractorBeamSettings");
            hub.planetaryDefenseConfig = Resources.Load<PlanetaryDefenseConfig>("PlanetaryDefenseConfig");
            hub.upgradeTree = Resources.Load<UpgradeTree>("UpgradeTree");

            hub.shipFamilies = new List<ShipFamilyDefinition>();
            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition", new[] { "Assets/Prefabs/Ships" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def != null)
                    hub.shipFamilies.Add(def);
            }

            hub.shipFamilies.Sort((a, b) =>
                string.Compare(a != null ? a.familyId : null, b != null ? b.familyId : null,
                    StringComparison.OrdinalIgnoreCase));

            hub.EnsureDefaultBalanceRequests();
            EditorUtility.SetDirty(hub);
        }

        /// <summary>
        /// Scans the fleet and writes aggregates / outliers / economy checks onto the hub asset.
        /// </summary>
        public static bool RefreshReview(RebalanceGame hub, out string errorMessage)
        {
            errorMessage = null;
            if (hub == null)
            {
                errorMessage = "No RebalanceGame asset.";
                return false;
            }

            var rows = GameBalanceFleetAnalyzer.ScanAllChassis(out errorMessage);
            if (rows.Count == 0)
                return false;

            Undo.RecordObject(hub, "RebalanceGame Refresh Review");

            hub.lastChassisCount = rows.Count;
            hub.lastReviewUtc = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            hub.fleetAggregates = BuildAggregates(rows);
            hub.outliers = BuildOutliers(rows);
            hub.economyChecks = BuildEconomyChecks(rows, hub);
            hub.lastReviewSummary = BuildSummaryMarkdown(hub);

            EditorUtility.SetDirty(hub);
            return true;
        }

        /// <summary>
        /// Writes a Cursor-facing prompt next to the hub asset: requests + asset inventory + review snapshot.
        /// Returns absolute path written.
        /// </summary>
        public static string ExportCursorPrompt(RebalanceGame hub)
        {
            if (hub == null)
                return null;

            hub.EnsureDefaultBalanceRequests();

            // Write beside the hub asset when possible (e.g. Assets/Resources/RebalanceGame_Cursor_….md).
            string assetPath = AssetDatabase.GetAssetPath(hub);
            string abs;
            if (!string.IsNullOrEmpty(assetPath))
            {
                string projectRelativeDir = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "Assets/Resources";
                string fileName = hub.name + "_Cursor_Rebalance_Prompt.md";
                string projectRelative = projectRelativeDir + "/" + fileName;
                abs = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(DirectoryGetParentAssets(), projectRelative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            }
            else
            {
                abs = System.IO.Path.Combine(
                    GameBalanceFleetAnalyzer.GetBalanceOutputDirectory(),
                    "RebalanceGame_Cursor_Rebalance_Prompt.md");
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs) ?? Application.dataPath);

            var sb = new StringBuilder(8192);
            sb.AppendLine("# Titan Orbit — RebalanceGame Cursor prompt");
            sb.AppendLine();
            sb.AppendLine("You are rebalancing Titan Orbit ScriptableObject assets from designer requests.");
            sb.AppendLine("Update the linked assets (ProfileSet, ShipFamilyDefinitions, AsteroidSettings, etc.).");
            sb.AppendLine("Do **not** invent a parallel CSV pipeline — after changes, the designer clicks");
            sb.AppendLine("**Refresh Review** on the RebalanceGame asset to see outliers / aggregates in the Inspector.");
            sb.AppendLine();
            sb.AppendLine("## Session notes");
            sb.AppendLine(hub.sessionNotes ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("## GameBalanceTargets (code anchors)");
            sb.AppendLine(GameBalanceTargets.FormatTargetsHeaderMarkdown());
            sb.AppendLine();
            sb.AppendLine("## Power-score cargo weighting");
            sb.AppendLine(
                $"- Gem power contribution = rawGemCap / {ShipFamilyPowerScoreBreakdown.GemCapPowerScoreDivisor:0} " +
                $"(purchase cost still uses raw × 2).");
            sb.AppendLine(
                $"- People power contribution = rawPeopleCap / {ShipFamilyPowerScoreBreakdown.PeopleCapPowerScoreDivisor:0}.");
            sb.AppendLine();
            sb.AppendLine("## Balancing requests (enabled, by priority)");
            sb.AppendLine();

            var reqs = new List<RebalanceGameRequest>();
            if (hub.balanceRequests != null)
            {
                for (int i = 0; i < hub.balanceRequests.Count; i++)
                {
                    var r = hub.balanceRequests[i];
                    if (r != null && r.enabled)
                        reqs.Add(r);
                }
            }

            reqs.Sort((a, b) => b.priority.CompareTo(a.priority));
            for (int i = 0; i < reqs.Count; i++)
            {
                sb.AppendLine($"### {i + 1}. {reqs[i].title} (priority {reqs[i].priority})");
                sb.AppendLine(reqs[i].request ?? string.Empty);
                sb.AppendLine();
            }

            sb.AppendLine("## Linked assets");
            AppendAssetLine(sb, "PartCalcProfileSet", hub.partCalcProfileSet);
            AppendAssetLine(sb, "PlanetShipFamilyConfig", hub.planetShipFamilyConfig);
            AppendAssetLine(sb, "AsteroidSettings", hub.asteroidSettings);
            AppendAssetLine(sb, "MapGenerationSettings", hub.mapGenerationSettings);
            AppendAssetLine(sb, "GemExplosionSettings", hub.gemExplosionSettings);
            AppendAssetLine(sb, "ShipRammingSettings", hub.shipRammingSettings);
            AppendAssetLine(sb, "ShipCargoMobilitySettings", hub.shipCargoMobilitySettings);
            AppendAssetLine(sb, "TractorBeamSettings", hub.tractorBeamSettings);
            AppendAssetLine(sb, "PlanetaryDefenseConfig", hub.planetaryDefenseConfig);
            AppendAssetLine(sb, "UpgradeTree", hub.upgradeTree);
            sb.AppendLine();
            sb.AppendLine($"### Ship families ({hub.shipFamilies?.Count ?? 0})");
            if (hub.shipFamilies != null)
            {
                for (int i = 0; i < hub.shipFamilies.Count; i++)
                    AppendAssetLine(sb, hub.shipFamilies[i] != null ? hub.shipFamilies[i].familyId : "?", hub.shipFamilies[i]);
            }

            sb.AppendLine();
            sb.AppendLine("## Last review snapshot (may be stale — refresh after edits)");
            sb.AppendLine(hub.lastReviewSummary ?? "_No review yet — click Refresh Review on RebalanceGame._");
            sb.AppendLine();
            sb.AppendLine("## Outliers (top 25 by severity)");
            int n = hub.outliers != null ? Mathf.Min(25, hub.outliers.Count) : 0;
            for (int i = 0; i < n; i++)
            {
                var o = hub.outliers[i];
                sb.AppendLine(
                    $"- `{o.severity:0.##}` {o.familyId}/{o.chassisId} L{o.shipLevel} " +
                    $"flags={o.flags} fix={o.fixClass}");
            }

            sb.AppendLine();
            sb.AppendLine("## When done");
            sb.AppendLine("1. Save all modified `.asset` / seed `.cs` files.");
            sb.AppendLine("2. Designer opens RebalanceGame → **Apply Local Pipeline** (if seeds changed) → **Refresh Review**.");
            sb.AppendLine("3. Confirm Economy checks PASS and outliers make sense.");

            System.IO.File.WriteAllText(abs, sb.ToString(), new UTF8Encoding(false));
            AssetDatabase.Refresh();
            EditorGUIUtility.systemCopyBuffer = sb.ToString();
            return abs;
        }

        static string DirectoryGetParentAssets()
        {
            // Application.dataPath is .../Titan Orbit/Assets — project root is parent.
            return System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        static void AppendAssetLine(StringBuilder sb, string label, UnityEngine.Object obj)
        {
            if (obj == null)
            {
                sb.AppendLine($"- **{label}**: _(missing)_");
                return;
            }

            string path = AssetDatabase.GetAssetPath(obj);
            sb.AppendLine($"- **{label}**: `{path}`");
        }

        static List<RebalanceGameAggregateRow> BuildAggregates(List<GameBalanceFleetAnalyzer.ChassisRow> rows)
        {
            var list = new List<RebalanceGameAggregateRow>();
            void Add(string name, Func<GameBalanceFleetAnalyzer.ChassisRow, float> sel)
            {
                var a = GameBalanceFleetAnalyzer.Aggregate(rows, sel);
                list.Add(new RebalanceGameAggregateRow
                {
                    metricName = name,
                    min = a.Min,
                    p10 = a.P10,
                    median = a.Median,
                    mean = a.Mean,
                    p90 = a.P90,
                    max = a.Max,
                    sampleCount = a.Count
                });
            }

            Add("Wings", r => r.Wing);
            Add("Engines", r => r.Engine);
            Add("Thrusters", r => r.Thruster);
            Add("Propulsion", r => r.Propulsion);
            Add("Weapons", r => r.Weapons);
            Add("CargoParts", r => r.CargoParts);
            Add("DPS", r => r.Dps);
            Add("MoveSpeed", r => r.Stats.moveSpeed);
            Add("TurnSpeed", r => r.Stats.turnSpeed);
            Add("EnergyCap", r => r.Stats.energyCap);
            Add("EnergyRegen", r => r.Stats.energyRegen);
            Add("GemCap", r => r.Stats.maxGems);
            Add("PeopleCap", r => r.Stats.maxPeople);
            Add("PowerScore", r => r.PowerScore);
            Add("PropulsionPerWing", r => r.Wing > 0 ? (float)r.Propulsion / r.Wing : r.Propulsion);
            return list;
        }

        static List<RebalanceGameOutlierRow> BuildOutliers(List<GameBalanceFleetAnalyzer.ChassisRow> rows)
        {
            float medWings = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Wing);
            float medPropPerWing = GameBalanceFleetAnalyzer.MedianOf(
                rows, r => r.Wing > 0 ? (float)r.Propulsion / r.Wing : r.Propulsion);
            float medMoveGlobal = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Stats.moveSpeed);
            float medDpsGlobal = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Dps);
            float wingP90 = GameBalanceFleetAnalyzer.Aggregate(rows, r => r.Wing).P90;
            float propP10 = GameBalanceFleetAnalyzer.Aggregate(rows, r => r.Propulsion).P10;

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

            var flagged = new List<RebalanceGameOutlierRow>();
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
                float medDrain = drainByLevel.TryGetValue(r.ShipLevel, out float medDrainAtLevel)
                    ? medDrainAtLevel
                    : drain;
                float medRegen = energyRegenByLevel.TryGetValue(r.ShipLevel, out float medRegenAtLevel)
                    ? medRegenAtLevel
                    : r.Stats.energyRegen;
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

                var uniqueFixes = new List<string>();
                for (int f = 0; f < fixes.Count; f++)
                {
                    if (!uniqueFixes.Contains(fixes[f]))
                        uniqueFixes.Add(fixes[f]);
                }

                flagged.Add(new RebalanceGameOutlierRow
                {
                    severity = severity,
                    familyId = r.FamilyId,
                    chassisId = r.ChassisId,
                    prefabName = r.PrefabName,
                    shipLevel = r.ShipLevel,
                    wings = r.Wing,
                    engines = r.Engine,
                    thrusters = r.Thruster,
                    propulsion = r.Propulsion,
                    weapons = r.Weapons,
                    moveSpeed = r.Stats.moveSpeed,
                    dps = r.Dps,
                    gemCap = r.Stats.maxGems,
                    peopleCap = r.Stats.maxPeople,
                    powerScore = r.PowerScore,
                    flags = string.Join("|", flags),
                    fixClass = string.Join("|", uniqueFixes)
                });
            }

            flagged.Sort((a, b) => b.severity.CompareTo(a.severity));
            return flagged;
        }

        static List<RebalanceGameEconomyCheckRow> BuildEconomyChecks(
            List<GameBalanceFleetAnalyzer.ChassisRow> rows,
            RebalanceGame hub)
        {
            var list = new List<RebalanceGameEconomyCheckRow>();
            var asteroid = hub != null && hub.asteroidSettings != null
                ? hub.asteroidSettings
                : Resources.Load<AsteroidSettings>("AsteroidSettings");
            float healthPerSize = asteroid != null ? asteroid.HealthPerSize : 3f;
            float midSize = GameBalanceTargets.MidAsteroidSize;
            float midHp = midSize * healthPerSize;

            var l1 = rows.FindAll(r => r.ShipLevel == 1);
            var l3 = rows.FindAll(r => r.ShipLevel == 3);
            if (l1.Count == 0)
                l1 = rows;

            float medDpsL1 = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Dps);
            float medGemL1 = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Stats.maxGems);
            float medPeopleL3 = l3.Count > 0
                ? GameBalanceFleetAnalyzer.MedianOf(l3, r => r.Stats.maxPeople)
                : GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Stats.maxPeople);
            float medWing = GameBalanceFleetAnalyzer.MedianOf(rows, r => r.Wing);

            float ttkL1 = medDpsL1 > 0.01f ? midHp / medDpsL1 : float.PositiveInfinity;
            float homePop = PlanetPopulationMath.GetMaxPopulation(
                GameBalanceTargets.ReferenceHomePlanetSize,
                GameBalanceTargets.ReferenceHomePlanetLevel);
            float fleetCargo = GameBalanceTargets.CaptureShipCount * medPeopleL3;
            float batchesNeeded = fleetCargo > 0.01f ? homePop / fleetCargo : float.PositiveInfinity;

            void Add(string id, string value, string note, string status) =>
                list.Add(new RebalanceGameEconomyCheckRow
                {
                    checkId = id,
                    value = value,
                    targetOrNote = note,
                    status = status
                });

            bool ttkOk = ttkL1 >= GameBalanceTargets.MidAsteroidTtkSecondsMin
                         && ttkL1 <= GameBalanceTargets.MidAsteroidTtkSecondsMax;
            Add("mid_ttk_l1_sec",
                ttkL1.ToString("0.###", CultureInfo.InvariantCulture),
                $"{GameBalanceTargets.MidAsteroidTtkSecondsMin}-{GameBalanceTargets.MidAsteroidTtkSecondsMax}",
                ttkOk ? "PASS" : "FAIL");

            bool capOk = batchesNeeded >= 4f && batchesNeeded <= 6f;
            Add("capture_batches",
                batchesNeeded.ToString("0.###", CultureInfo.InvariantCulture),
                "4-6",
                capOk ? "PASS" : "FAIL");

            Add("median_l3_people",
                medPeopleL3.ToString("0.###", CultureInfo.InvariantCulture),
                GameBalanceTargets.TargetMedianPeopleCapAtShipLevel3.ToString("0.###", CultureInfo.InvariantCulture),
                "INFO");

            Add("median_l1_gemCap",
                medGemL1.ToString("0.###", CultureInfo.InvariantCulture),
                GameBalanceTargets.TargetMedianGemCapAtShipLevel1.ToString("0.###", CultureInfo.InvariantCulture),
                Mathf.Abs(medGemL1 - GameBalanceTargets.TargetMedianGemCapAtShipLevel1)
                <= GameBalanceTargets.TargetMedianGemCapAtShipLevel1 * 0.4f
                    ? "PASS"
                    : "WARN");

            Add("median_wings",
                medWing.ToString("0.###", CultureInfo.InvariantCulture),
                GameBalanceTargets.ExpectedMedianWingCount.ToString("0.###", CultureInfo.InvariantCulture),
                Mathf.Abs(medWing - GameBalanceTargets.ExpectedMedianWingCount) <= 1.5f ? "PASS" : "WARN");

            Add("health_per_size",
                healthPerSize.ToString("0.###", CultureInfo.InvariantCulture),
                "AsteroidSettings",
                "INFO");

            // --- Combat loop: energy battery seconds + regen fraction + health vs DPS ---
            float medDrainL1 = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.SustainedDrain);
            float medEnergyCapL1 = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Stats.energyCap);
            float medEnergyRegenL1 = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Stats.energyRegen);
            float medHealthL1 = GameBalanceFleetAnalyzer.MedianOf(l1, r => r.Stats.healthCap);
            float capSeconds = medDrainL1 > 0.01f ? medEnergyCapL1 / medDrainL1 : 0f;
            float regenFrac = medDrainL1 > 0.01f ? medEnergyRegenL1 / medDrainL1 : 0f;
            float healthSeconds = medDpsL1 > 0.01f ? medHealthL1 / medDpsL1 : 0f;

            bool capSecOk = Mathf.Abs(capSeconds - GameBalanceTargets.EnergyBatterySecondsOfSustainedFire) <= 0.75f;
            Add("energy_cap_seconds_l1",
                capSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                GameBalanceTargets.EnergyBatterySecondsOfSustainedFire.ToString("0.###", CultureInfo.InvariantCulture),
                capSecOk ? "PASS" : "WARN");

            bool regenOk = Mathf.Abs(regenFrac - GameBalanceTargets.EnergyRegenFractionOfSustainedDrain) <= 0.08f;
            Add("energy_regen_frac_l1",
                regenFrac.ToString("0.###", CultureInfo.InvariantCulture),
                GameBalanceTargets.EnergyRegenFractionOfSustainedDrain.ToString("0.###", CultureInfo.InvariantCulture),
                regenOk ? "PASS" : "WARN");

            bool hpOk = Mathf.Abs(healthSeconds - GameBalanceTargets.HealthSecondsOfOwnDps) <= 1.0f;
            Add("health_seconds_of_dps_l1",
                healthSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                GameBalanceTargets.HealthSecondsOfOwnDps.ToString("0.###", CultureInfo.InvariantCulture),
                hpOk ? "PASS" : "WARN");

            Add("gem_power_weight",
                $"raw/ {ShipFamilyPowerScoreBreakdown.GemCapPowerScoreDivisor:0}",
                "power score only; purchase uses raw",
                "INFO");

            return list;
        }

        static string BuildSummaryMarkdown(RebalanceGame hub)
        {
            var sb = new StringBuilder();
            sb.Append(GameBalanceTargets.FormatTargetsHeaderMarkdown());
            sb.AppendLine();
            sb.AppendLine($"Reviewed {hub.lastChassisCount} chassis at {hub.lastReviewUtc} UTC.");
            sb.AppendLine($"Outliers: {hub.outliers?.Count ?? 0}.");
            sb.AppendLine();
            sb.AppendLine("### Economy");
            if (hub.economyChecks != null)
            {
                for (int i = 0; i < hub.economyChecks.Count; i++)
                {
                    var c = hub.economyChecks[i];
                    sb.AppendLine($"- [{c.status}] {c.checkId} = {c.value} ({c.targetOrNote})");
                }
            }

            return sb.ToString();
        }
    }
}
