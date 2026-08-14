using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Telemetry-style calculation cards for the ten bottom Ship Ability chips.
    /// Builds grouped part grids (N× same component) and walks Extra Level math:
    /// Tip cards: PARTS (Primary + Extras) then FORMULA.
    /// Each part type (Cockpit, Wing, …) is its own Extra Level pool; the chip is the sum.
    /// Starting prefab <c>localScale</c> multiplies that pool’s Base / PerExtra (Cockpit at 3
    /// → 3× Health / Gems / People). Mass tax is only shown for Move / Accel / Turn.
    /// Token colours are shared: violet = start scale, amber = part count N, steel = Primary,
    /// cyan = PerExtra, blue = ship−1, green = ability, mint = total.
    /// Presentation-only — never writes ECS.
    /// <para>
    /// [TITAN-ORBIT] Intentionally <b>not</b> live: no per-frame HP/energy/speed/cargo vitals.
    /// <see cref="ShipAttributeUpgradeHUD"/> rebuilds these strings only when the ship changes
    /// or an ability is purchased — StringBuilder + TMP mesh work every frame was a major FPS hit.
    /// </para>
    /// <para>
    /// Rich text is shown inside <see cref="ShipStatTooltipChrome"/> (Shift sci-fi frame).
    /// Paired with <see cref="ShipAttributeUpgradeHUD"/> chips and
    /// <see cref="ShipSpeedometerStatTooltips"/> (shared <see cref="ShipSpeedometerStatTooltips.PartCache"/>).
    /// </para>
    /// </summary>
    public static class ShipAbilityStatBreakdown
    {
        /// <summary>
        /// Reused across tip rebuilds so purchase/hover does not allocate a fresh 1 KB builder
        /// every call. [STANDARD] Not thread-safe — UI main thread only.
        /// </summary>
        static readonly StringBuilder s_BuildSb = new StringBuilder(1024);

        // --- Token colours (same hex in PARTS rows and FORMULA lines) ---
        // [TITAN-ORBIT] Players mix up “2× parts” with “×1.7 start scale”. Each idea
        // keeps one colour so the caption is the legend.
        const string HexScale = "C9A0FF";   // prefab start size
        const string HexCount = "FFB347";   // how many parts (N, 2×, N−1)
        const string HexPrimary = "B8C8D8"; // catalog / scaled Primary
        const string HexPerExtra = "7EC8FF"; // PerExtra step
        const string HexShip = "5B9BD5";    // (shipLevel − 1)
        const string HexAbility = "7DFFB2"; // bottom-HUD purchases
        const string HexResult = "AAEEDD";  // line total / chip
        const string HexMass = "FF8A8A";    // cargo drag (Move / Turn only)
        const string HexMute = "5B7A94";    // labels, operators, unused terms

        /// <summary>Which authored float on a part contributes to a chip primary.</summary>
        public enum StatField
        {
            FirePower = 0,
            BulletSpeed = 1,
            HealthCap = 2,
            HealthRegen = 3,
            EnergyCap = 4,
            EnergyRegen = 5,
            MoveSpeed = 6,
            TurnSpeed = 7,
            MaxGems = 8,
            MaxPeople = 9,
            AccelerationCap = 10,
            BulletRange = 11,
            RammingPower = 12
        }

        /// <summary>One collapsed row in the parts grid (identical componentIds merged).</summary>
        public struct GroupedPartRow
        {
            public string ComponentId;
            public string DisplayName;
            /// <summary>How many instances of this id are the pool primary (0 or 1 usually).</summary>
            public int PrimaryCount;
            /// <summary>How many instances of this id are extras (count toward Extra Level only).</summary>
            public int ExtraCount;
            /// <summary>Stack pool this row belongs to (Cockpit, Wing, Propulsion, …).</summary>
            public string PoolKey;
            /// <summary>
            /// Scale-adjusted field value used as Primary (catalog × starting scale).
            /// Extras ignore this for Base — they only raise N.
            /// </summary>
            public float AuthoredEach;
            /// <summary>Family-catalog value before prefab starting scale (0 when unknown).</summary>
            public float CatalogEach;
            /// <summary>
            /// Prefab start-scale multiplier for this field (1 = no mesh scale).
            /// Cockpit at localScale 3 → 3 on Health / Gems / People.
            /// </summary>
            public float ScaleFactor;
            /// <summary>[LEGACY] Unused — Extra Stack Weight retired (kept so older tip builders compile).</summary>
            public float ExtraWeight;
            /// <summary>Primary Base contribution (extras add 0 Base).</summary>
            public float PrimaryContrib;
            /// <summary>Always 0 under Extra Level (extras raise count, not Base).</summary>
            public float ExtraContrib;

            /// <summary>Total Base contribution to the pool from this id (primary only).</summary>
            public float ContribTotal => PrimaryContrib + ExtraContrib;

            /// <summary>Total instance count N (Extra Level uses (N−1) extras in the multiplier).</summary>
            public int Count => PrimaryCount + ExtraCount;
        }

        /// <summary>
        /// Chip glance numbers: current effective value, next purchase step, ability level.
        /// </summary>
        public static void ResolveChipDisplay(
            int abilityIndex,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs,
            out float value,
            out float nextStep,
            out int abilityLv,
            out string unitSuffix)
        {
            abilityLv = Mathf.Max(0, ShipAttributeUpgradeLogic.GetAttributeLevel(in attrs, abilityIndex));
            unitSuffix = string.Empty;
            ShipComponentAbilityStats eff = live.EffectiveStats;

            switch (abilityIndex)
            {
                case 0:
                    value = Mathf.Max(0f, eff.firePower);
                    unitSuffix = "/hit";
                    nextStep = Mathf.Max(0f, eff.firePowerPerExtraLevel);
                    break;
                case 1:
                    value = Mathf.Max(0f, eff.bulletSpeed);
                    nextStep = Mathf.Max(0f, eff.bulletSpeedPerExtraLevel);
                    break;
                case 2:
                    value = Mathf.Max(0f, eff.healthCap);
                    nextStep = Mathf.Max(0f, eff.healthCapPerExtraLevel);
                    break;
                case 3:
                    value = Mathf.Max(0f, eff.healthRegen);
                    unitSuffix = "/s";
                    nextStep = Mathf.Max(0f, eff.healthRegenPerExtraLevel);
                    break;
                case 4:
                    value = Mathf.Max(0f, eff.energyCap);
                    nextStep = Mathf.Max(0f, eff.energyCapPerExtraLevel);
                    break;
                case 5:
                    value = Mathf.Max(0f, eff.energyRegen);
                    unitSuffix = "/s";
                    nextStep = Mathf.Max(0f, eff.energyRegenPerExtraLevel);
                    break;
                case 6:
                    // [TITAN-ORBIT] Static snapshot cruise (mass-taxed at last rebuild), not per-frame
                    // flight speed. HUD rebuilds chips only on ship purchase / ability upgrade.
                    value = Mathf.Max(0f, live.CruiseMaxSpeed > 0.01f
                        ? live.CruiseMaxSpeed
                        : live.ChassisMaxSpeed);
                    // Next purchase adds one Extra Level of primary Move PerExtraLevel.
                    nextStep = Mathf.Max(0f, live.MoveStepPreview);
                    if (nextStep <= 0.0001f)
                        nextStep = Mathf.Max(0f, eff.moveSpeedPerExtraLevel);
                    break;
                case 7:
                    // [TITAN-ORBIT] Static post–mass-tax turn from the same snapshot.
                    float chassisTurn = live.ChassisTurnDeg > 0.01f ? live.ChassisTurnDeg : eff.turnSpeed;
                    value = Mathf.Max(0f, live.TaxedTurnDeg > 0.01f ? live.TaxedTurnDeg : chassisTurn);
                    unitSuffix = "°/s";
                    nextStep = Mathf.Max(0f, ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(
                        eff.turnSpeedPerExtraLevel));
                    break;
                case 8:
                    value = Mathf.Max(0f, eff.maxGems);
                    nextStep = Mathf.Max(0f, eff.maxGemsPerExtraLevel);
                    break;
                case 9:
                    value = Mathf.Max(0f, eff.maxPeople);
                    nextStep = Mathf.Max(0f, eff.maxPeoplePerExtraLevel);
                    break;
                default:
                    value = 0f;
                    nextStep = 0f;
                    break;
            }
        }

        /// <summary>
        /// Full TMP card for one ability index (0–9). Call only on hover-enter or when the
        /// ship / ability snapshot key changes — not every Update.
        /// </summary>
        public static string BuildForAbilityIndex(
            int abilityIndex,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs)
        {
            // --- Reuse builder (main-thread UI only) ---
            StringBuilder sb = s_BuildSb;
            sb.Clear();

            int lv = Mathf.Max(0, ShipAttributeUpgradeLogic.GetAttributeLevel(in attrs, abilityIndex));
            int maxLv = ShipAttributeUpgradeLogic.GetMaxUpgrades(Mathf.Max(1, live.Ship.ShipLevel));
            string title = abilityIndex >= 0 && abilityIndex < ShipAbilityCategoryColors.PowerBreakdownStatFullLabels.Length
                ? ShipAbilityCategoryColors.PowerBreakdownStatFullLabels[abilityIndex]
                : "Ability";
            string shortLabel = abilityIndex >= 0 && abilityIndex < ShipAbilityCategoryColors.PowerBreakdownStatLabels.Length
                ? ShipAbilityCategoryColors.PowerBreakdownStatLabels[abilityIndex]
                : "?";

            ResolveChipDisplay(abilityIndex, in live, in attrs, out float chipVal, out float nextStep, out _, out string unit);
            AppendHeader(sb, $"{shortLabel} — {title}", chipVal, unit, lv, maxLv, nextStep, abilityIndex == 6);

            switch (abilityIndex)
            {
                case 0:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.FirePower, "Fire Power", lv, live.EffectiveStats.firePower);
                    AppendRelatedFireExtras(sb, parts, live);
                    break;
                case 1:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.BulletSpeed, "Bullet Speed", lv, live.EffectiveStats.bulletSpeed);
                    // Bullet range has no bottom-HUD ability — abilityLv forced to 0.
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.BulletRange, "Range", 0, live.EffectiveStats.bulletRange);
                    break;
                case 2:
                    // Cap only — no live HP vitals (those changed every frame and forced TMP rebuilds).
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.HealthCap, "Health Cap", lv, live.EffectiveStats.healthCap);
                    break;
                case 3:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.HealthRegen, "Health Regen", lv, live.EffectiveStats.healthRegen);
                    break;
                case 4:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.EnergyCap, "Energy Cap", lv, live.EffectiveStats.energyCap);
                    break;
                case 5:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.EnergyRegen, "Energy Regen", lv, live.EffectiveStats.energyRegen);
                    break;
                case 6:
                    AppendMoveAbilityCard(sb, parts, live, attrs, lv);
                    break;
                case 7:
                    // finalEffective = post–mass-tax °/s (formula Base is converted for display).
                    float turnLive = live.TaxedTurnDeg > 0.01f
                        ? live.TaxedTurnDeg
                        : (live.ChassisTurnDeg > 0.01f ? live.ChassisTurnDeg : live.EffectiveStats.turnSpeed);
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.TurnSpeed, "°/s", lv, turnLive);
                    AppendTurnMassTax(sb, live);
                    break;
                case 8:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.MaxGems, "Max Gems", lv, live.EffectiveStats.maxGems);
                    break;
                case 9:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.MaxPeople, "Max People", lv, live.EffectiveStats.maxPeople);
                    break;
                default:
                    sb.AppendLine("<color=#888888>Unknown ability</color>");
                    break;
            }

            return sb.Length > 0 ? sb.ToString() : "<color=#888888>No breakdown available</color>";
        }

        /// <summary>
        /// Groups parts that contribute to <paramref name="field"/>, collapsing identical ids.
        /// When <paramref name="useStackWeight"/> is true (legacy name), uses primary-per-pool
        /// Extra Level grouping: primary supplies Primary; extras raise N only.
        /// </summary>
        public static void CollectGroupedRows(
            in ShipSpeedometerStatTooltips.PartCache parts,
            StatField field,
            bool useStackWeight,
            List<GroupedPartRow> into)
        {
            into.Clear();
            if (!parts.Valid || parts.Ids == null || parts.Stats == null)
                return;

            if (useStackWeight)
            {
                CollectStackedGroupedRows(in parts, field, into);
                return;
            }

            var map = new Dictionary<string, GroupedPartRow>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;

                float authored = ReadField(parts.Stats[i], field);
                if (authored <= 0.0001f)
                    continue;

                if (!map.TryGetValue(id, out GroupedPartRow row))
                {
                    row = new GroupedPartRow
                    {
                        ComponentId = id,
                        DisplayName = ResolvePartName(parts.Family, id),
                        AuthoredEach = authored,
                        ExtraWeight = 0f
                    };
                    FillScaleFields(in parts, i, field, authored, ref row);
                }

                // Flat list mode — every instance is shown as primary for display.
                row.PrimaryCount++;
                row.PrimaryContrib += authored;
                row.AuthoredEach = authored;
                FillScaleFields(in parts, i, field, authored, ref row);
                map[id] = row;
            }

            into.AddRange(map.Values);
            into.Sort((a, b) => b.ContribTotal.CompareTo(a.ContribTotal));
        }

        /// <summary>
        /// Primary-per-pool grouping for Extra Level: primary Base; extras count only.
        /// </summary>
        static void CollectStackedGroupedRows(
            in ShipSpeedometerStatTooltips.PartCache parts,
            StatField field,
            List<GroupedPartRow> into)
        {
            var pools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (ReadField(parts.Stats[i], field) <= 0.0001f)
                    continue;

                string key = field is StatField.MoveSpeed or StatField.AccelerationCap
                    ? ShipComponentStackAggregation.PropulsionPoolKey
                    : ShipComponentStackAggregation.ResolveStackPoolKey(id);
                if (field is StatField.MoveSpeed or StatField.AccelerationCap)
                {
                    if (!ShipComponentAbilityStats.IsPropulsionComponent(id))
                        continue;
                }

                if (!pools.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>(4);
                    pools[key] = list;
                }

                list.Add(i);
            }

            var map = new Dictionary<string, GroupedPartRow>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<int>> pool in pools)
            {
                int primaryLocal = ShipComponentStackAggregation.PickPrimaryLocalIndex(
                    pool.Key, pool.Value, parts.Stats);
                int primaryGlobal = pool.Value[primaryLocal];

                for (int m = 0; m < pool.Value.Count; m++)
                {
                    int gi = pool.Value[m];
                    string id = parts.Ids[gi];
                    float authored = ReadField(parts.Stats[gi], field);
                    bool isPrimary = gi == primaryGlobal;

                    if (!map.TryGetValue(id, out GroupedPartRow row))
                    {
                        row = new GroupedPartRow
                        {
                            ComponentId = id,
                            DisplayName = ResolvePartName(parts.Family, id),
                            PoolKey = pool.Key,
                            AuthoredEach = authored,
                            ExtraWeight = 0f
                        };
                    }

                    if (isPrimary)
                    {
                        row.AuthoredEach = authored;
                        row.PrimaryCount++;
                        row.PrimaryContrib += authored;
                        FillScaleFields(in parts, gi, field, authored, ref row);
                    }
                    else
                    {
                        // [TITAN-ORBIT] Extra Base is ignored — only raises numberOfComponents.
                        row.ExtraCount++;
                        row.ExtraContrib = 0f;
                    }

                    map[id] = row;
                }
            }

            into.AddRange(map.Values);
            into.Sort((a, b) =>
            {
                bool aPri = a.PrimaryCount > 0;
                bool bPri = b.PrimaryCount > 0;
                if (aPri != bPri)
                    return aPri ? -1 : 1;
                return b.ContribTotal.CompareTo(a.ContribTotal);
            });
        }

        /// <summary>
        /// Parts list only: PRIMARY part(s) then EXTRAS (count toward Extra Level).
        /// Does not print Base — that comes from <see cref="AppendExtraLevelFormula"/>.
        /// </summary>
        public static void AppendGroupedFieldGrid(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            StatField field,
            string unitLabel,
            bool useStackWeight,
            string sectionTitle = null)
        {
            var rows = new List<GroupedPartRow>(8);
            CollectGroupedRows(in parts, field, useStackWeight, rows);
            string banner = string.IsNullOrEmpty(sectionTitle)
                ? "PARTS"
                : sectionTitle;
            ShipStatTooltipChrome.AppendSectionBanner(sb, banner, "5B9BD5");

            if (rows.Count == 0)
            {
                sb.AppendLine("<color=#5B7A94>No contributing parts.</color>");
                return;
            }

            // --- PRIMARY part(s) ---
            bool wrotePrimary = false;
            for (int i = 0; i < rows.Count; i++)
            {
                GroupedPartRow r = rows[i];
                if (r.PrimaryCount <= 0)
                    continue;
                if (!wrotePrimary)
                {
                    sb.Append("> ");
                    AppendTint(sb, HexPrimary, "PRIMARY");
                    sb.AppendLine();
                    wrotePrimary = true;
                }

                AppendPartLine(
                    sb, r.PrimaryCount, r.DisplayName, r.AuthoredEach, unitLabel,
                    isExtra: false, r.CatalogEach, r.ScaleFactor, extraPoolKey: null);
            }

            if (!wrotePrimary && useStackWeight)
                sb.AppendLine("<color=#888888>PRIMARY — none</color>");

            // --- EXTRAS (raise N only) ---
            bool wroteExtra = false;
            for (int i = 0; i < rows.Count; i++)
            {
                GroupedPartRow r = rows[i];
                if (r.ExtraCount <= 0)
                    continue;
                if (!wroteExtra)
                {
                    sb.Append("> ");
                    AppendTint(sb, HexCount, "EXTRAS");
                    sb.Append(" ");
                    AppendTint(sb, HexMute, "(");
                    AppendTint(sb, HexCount, "+ to N");
                    AppendTint(sb, HexMute, ")");
                    sb.AppendLine();
                    wroteExtra = true;
                }

                AppendPartLine(
                    sb, r.ExtraCount, r.DisplayName, 0f, unitLabel,
                    isExtra: true, catalogEach: 0f, scaleFactor: 1f, extraPoolKey: r.PoolKey);
            }
        }

        /// <summary>
        /// One part row: <c>1× Cockpit  10 ×3 → 30 Health</c> or <c>2× Wing_1   +2 to Wing</c>.
        /// Starting scale is shown only when it actually changes the catalog number.
        /// </summary>
        static void AppendPartLine(
            StringBuilder sb,
            int count,
            string displayName,
            float authoredEach,
            string unitLabel,
            bool isExtra,
            float catalogEach,
            float scaleFactor,
            string extraPoolKey)
        {
            AppendTint(sb, HexCount, count.ToString(CultureInfo.InvariantCulture) + "×");
            sb.Append(" ").Append(displayName);
            if (isExtra)
            {
                string pool = string.IsNullOrEmpty(extraPoolKey) ? "N" : extraPoolKey;
                sb.Append("  ");
                AppendTint(sb, HexCount, "+" + count.ToString(CultureInfo.InvariantCulture) + " N");
                AppendTint(sb, HexMute, " (" + pool + ")");
            }
            else
            {
                // --- Catalog × starting scale → Primary used by Extra Level ---
                if (HasMeaningfulScale(scaleFactor) && catalogEach > 0.0001f)
                {
                    sb.Append("  ");
                    AppendTint(sb, HexPrimary, FDetail(catalogEach));
                    sb.Append(" ");
                    AppendTint(sb, HexScale, "Scale ×" + FDetail(scaleFactor));
                    sb.Append(" → ");
                    AppendTint(sb, HexResult, FDetail(authoredEach));
                }
                else
                {
                    sb.Append("  ");
                    AppendTint(sb, HexResult, FDetail(authoredEach));
                }

                if (!string.IsNullOrEmpty(unitLabel))
                {
                    sb.Append(" ");
                    AppendTint(sb, HexMute, unitLabel);
                }
            }

            sb.AppendLine();
        }

        /// <summary>
        /// Parts breakdown, then Extra Level formula, then Base.
        /// <para>
        /// Primary / PerExtra start as family catalog numbers, then multiply by prefab
        /// starting scale (Cockpit at 3 → ×3). Base is the Extra Level result before mass tax:
        /// <c>(Primary × Scale) + (PerExtra × Scale) × levels</c>.
        /// </para>
        /// </summary>
        static void AppendTenPercentPipeline(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs,
            StatField field,
            string unitLabel,
            int abilityLv,
            float finalEffective)
        {
            _ = attrs;
            AppendGroupedFieldGrid(sb, parts, field, unitLabel, useStackWeight: true);
            AppendExtraLevelFormula(sb, in parts, in live, field, unitLabel, abilityLv, finalEffective);
        }

        /// <summary>
        /// One Extra Level pool’s numbers for a chip field. Cockpit and Wing are separate
        /// pools — Max Gems is their sum, not a single primary.
        /// </summary>
        struct FieldPoolEval
        {
            public string PoolKey;
            public float Scale;
            public float Primary;
            public float PerExtra;
            public int ComponentCount;
            public int Levels;
            public float Evaluated;
            public bool IsWeaponPool;
        }

        /// <summary>
        /// Writes one arithmetic line per part-type pool, then the running total.
        /// <para>
        /// [TITAN-ORBIT] The chip is the <b>sum of every pool</b> (Cockpit gems + Wing gems),
        /// then an optional family multiplier. Older cards only showed the first pool and
        /// labeled the leftover “After mass tax” — mass only hits Move / Accel / Turn.
        /// </para>
        /// </summary>
        static void AppendExtraLevelFormula(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            StatField field,
            string unitLabel,
            int abilityLv,
            float finalEffective)
        {
            int shipLevel = Mathf.Max(1, live.Ship.ShipLevel);
            int shipSteps = Mathf.Max(0, shipLevel - 1);
            var pools = new List<FieldPoolEval>(4);
            CollectFieldPools(in parts, field, shipLevel, abilityLv, pools);

            // [TITAN-ORBIT] Turn is authored in definition units; chips show °/s.
            float unitScale = field == StatField.TurnSpeed
                ? ShipPropulsionAggregation.TurnDefinitionToDegreesPerSecond
                : 1f;

            ShipStatTooltipChrome.AppendSectionBanner(sb, "FORMULA", "7DFFB2");
            AppendFormulaLegend(sb, field);
            sb.AppendLine(DescribeFormula(field, pools.Count > 1));

            float running = 0f;
            if (pools.Count == 0)
            {
                // No prefab parts for this field — hull may still have a family fallback.
                running = finalEffective;
                AppendTint(sb, HexMute, "fallback");
                sb.Append("  ");
                AppendTint(sb, HexResult, FResult(finalEffective));
                if (!string.IsNullOrEmpty(unitLabel))
                {
                    sb.Append(" ");
                    AppendTint(sb, HexMute, unitLabel);
                }
                sb.AppendLine();
                AppendTotalLine(sb, finalEffective, unitLabel);
                return;
            }

            // --- One line per pool: Primary + PerExtra × levels = result ---
            for (int i = 0; i < pools.Count; i++)
            {
                FieldPoolEval p = pools[i];
                float primaryDisp = p.Primary * unitScale;
                float perExtraDisp = p.PerExtra * unitScale;
                float evalDisp = p.Evaluated * unitScale;
                running += evalDisp;

                AppendTint(sb, HexPrimary, p.PoolKey);
                if (HasMeaningfulScale(p.Scale))
                {
                    sb.Append(" ");
                    AppendTint(sb, HexScale, "Scale ×" + FDetail(p.Scale));
                }

                sb.Append("  ");
                // Cache Primary / PerExtra are already × scale — this line always equals Evaluated.
                AppendTint(sb, HexPrimary, FDetail(primaryDisp));
                AppendTint(sb, HexMute, " + ");
                AppendTint(sb, HexPerExtra, FDetail(perExtraDisp));
                AppendTint(sb, HexMute, " × ");
                AppendTint(sb, HexMute, p.Levels.ToString(CultureInfo.InvariantCulture));
                AppendTint(sb, HexMute, " = ");
                AppendTint(sb, HexResult, FResult(evalDisp));
                sb.Append(" ");
                AppendTint(sb, HexMute, "(");
                AppendPoolLevelBreakdown(sb, p.IsWeaponPool, field, shipSteps, abilityLv, p.ComponentCount);
                AppendTint(sb, HexMute, ")");
                sb.AppendLine();
            }

            // --- Family identity mul (1 = skip) ---
            float familyMul = ReadFamilyMul(parts.Family, field);
            if (Mathf.Abs(familyMul - 1f) > 0.01f)
            {
                float afterMul = running * familyMul;
                AppendTint(sb, HexMute, "× family ");
                AppendTint(sb, HexMute, FDetail(familyMul));
                AppendTint(sb, HexMute, " = ");
                AppendTint(sb, HexResult, FResult(afterMul));
                sb.AppendLine();
                running = afterMul;
            }

            // --- Mass tax only on Move / Accel / Turn ---
            if (IsMassAffectedField(field) && Mathf.Abs(finalEffective - running) > 0.05f)
            {
                float drag = running - finalEffective;
                AppendTint(sb, HexMass, "− mass  " + FDetail(drag));
                AppendTint(sb, HexMute, " = ");
                AppendTint(sb, HexResult, FResult(finalEffective));
                sb.AppendLine();
                running = finalEffective;
            }

            AppendTotalLine(sb, running, unitLabel);
        }

        /// <summary>Final readout that should match the chip header.</summary>
        static void AppendTotalLine(StringBuilder sb, float total, string unitLabel)
        {
            sb.Append("<b>");
            AppendTint(sb, HexMute, "TOTAL");
            sb.Append("</b>  <b>");
            AppendTint(sb, HexResult, FResult(total));
            sb.Append("</b>");
            if (!string.IsNullOrEmpty(unitLabel))
            {
                sb.Append(" ");
                AppendTint(sb, HexMute, unitLabel);
            }
            sb.AppendLine();
        }

        /// <summary>
        /// One-line colour key. Each word matches the tint used on numbers below
        /// (violet <c>Scale</c> is the prefab start size — not part count <c>N</c>).
        /// </summary>
        static void AppendFormulaLegend(StringBuilder sb, StatField field)
        {
            AppendTint(sb, HexScale, "Scale");
            sb.Append("  ");
            AppendTint(sb, HexCount, "N");
            sb.Append("  ");
            AppendTint(sb, HexShip, "ship−1");
            sb.Append("  ");
            AppendTint(sb, HexAbility, "ability");
            sb.Append("  ");
            AppendTint(sb, HexPrimary, "Primary");
            sb.Append("  ");
            AppendTint(sb, HexPerExtra, "PerExtra");
            sb.Append("  ");
            AppendTint(sb, HexResult, "TOTAL");
            if (IsMassAffectedField(field))
            {
                sb.Append("  ");
                AppendTint(sb, HexMass, "mass");
            }

            sb.AppendLine();
        }

        /// <summary>
        /// Colour-coded equation. Words use the same tints as the numbers — including
        /// <c>Scale</c>, so a violet ×1.7 is not mistaken for amber 2× N.
        /// </summary>
        static string DescribeFormula(StatField field, bool multiPool)
        {
            string primary = Tint(HexPrimary, "Primary");
            string perExtra = Tint(HexPerExtra, "PerExtra");
            string scale = Tint(HexScale, "Scale");
            string ship = Tint(HexShip, "ship−1");
            string ability = Tint(HexAbility, "ability");
            string n = Tint(HexCount, "N−1");
            string plus = Tint(HexMute, " + ");
            string times = Tint(HexMute, " × ");
            string open = Tint(HexMute, "(");
            string close = Tint(HexMute, ")");

            // Primary and PerExtra are catalog × Scale before Extra Level runs.
            string scaledPrimary = open + primary + times + scale + close;
            string scaledPerExtra = open + perExtra + times + scale + close;

            string core;
            if (field == StatField.BulletSpeed)
                core = scaledPrimary + plus + scaledPerExtra + times + ability;
            else if (field == StatField.FirePower || field == StatField.BulletRange)
                core = scaledPrimary + plus + scaledPerExtra + times + open + ship + plus + ability + close;
            else
                core = scaledPrimary + plus + scaledPerExtra + times + open + ship + plus + ability + plus + n + close;

            if (multiPool)
                return Tint(HexMute, "Each part type: ") + core + Tint(HexMute, "  — then add");
            return core;
        }

        /// <summary>
        /// Builds one eval per stack pool that actually contributes this field.
        /// Uses the same primary + N as <see cref="ShipComponentExtraLevelMath.AggregateAndEvaluate"/>.
        /// </summary>
        static void CollectFieldPools(
            in ShipSpeedometerStatTooltips.PartCache parts,
            StatField field,
            int shipLevel,
            int abilityLv,
            List<FieldPoolEval> into)
        {
            into.Clear();
            if (!parts.Valid || parts.Ids == null || parts.Stats == null)
                return;

            // --- Group every non-cosmetic part by pool (Cockpit / Wing / Propulsion / …) ---
            var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;

                string key = field is StatField.MoveSpeed or StatField.AccelerationCap
                    ? ShipComponentStackAggregation.PropulsionPoolKey
                    : ShipComponentStackAggregation.ResolveStackPoolKey(id);
                if (field is StatField.MoveSpeed or StatField.AccelerationCap
                    && !ShipComponentAbilityStats.IsPropulsionComponent(id))
                    continue;

                if (!groups.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>(4);
                    groups[key] = list;
                }

                list.Add(i);
            }

            foreach (KeyValuePair<string, List<int>> pair in groups)
            {
                int primaryLocal = ShipComponentStackAggregation.PickPrimaryLocalIndex(
                    pair.Key, pair.Value, parts.Stats);
                int gi = pair.Value[primaryLocal];
                if (gi < 0 || gi >= parts.Stats.Count)
                    continue;

                // Cache stats are already catalog × starting scale (same as the motor).
                ShipComponentAbilityStats s = parts.Stats[gi];
                float primary = ReadField(s, field);
                float perExtra = ReadPerExtraLevel(s, field);
                if (primary <= 0.0001f && perExtra <= 0.0001f)
                    continue;

                string id = gi < parts.Ids.Count ? parts.Ids[gi] : string.Empty;
                bool isWeapon = ShipComponentStackAggregation.IsWeaponPoolKey(pair.Key);
                int n = pair.Value.Count;
                int levels = CountPoolLevels(isWeapon, field, shipLevel, abilityLv, n);
                float evaluated = EvaluatePoolField(isWeapon, field, primary, perExtra, shipLevel, abilityLv, n);
                float scale = ShipComponentAbilityStatsMath.GetScaleMultiplier(
                    ReadLocalScale(in parts, gi), id, ToScaleChannel(field));

                into.Add(new FieldPoolEval
                {
                    PoolKey = pair.Key,
                    Scale = scale,
                    Primary = primary,
                    PerExtra = perExtra,
                    ComponentCount = n,
                    Levels = levels,
                    Evaluated = evaluated,
                    IsWeaponPool = isWeapon
                });
            }

            into.Sort((a, b) => b.Evaluated.CompareTo(a.Evaluated));
        }

        /// <summary>
        /// Extra Level steps for one pool. Weapons skip <c>(N−1)</c>; weapon bullet speed is ability only.
        /// </summary>
        static int CountPoolLevels(
            bool isWeaponPool,
            StatField field,
            int shipLevel,
            int abilityLv,
            int componentCount)
        {
            if (isWeaponPool && field == StatField.BulletSpeed)
                return ShipComponentExtraLevelMath.CountWeaponBulletSpeedExtraLevels(abilityLv);
            if (isWeaponPool)
                return ShipComponentExtraLevelMath.CountWeaponExtraLevels(shipLevel, abilityLv);
            return ShipComponentExtraLevelMath.CountExtraLevels(shipLevel, abilityLv, componentCount);
        }

        /// <summary>Same Extra Level evaluate the motor uses for this pool + field.</summary>
        static float EvaluatePoolField(
            bool isWeaponPool,
            StatField field,
            float primary,
            float perExtra,
            int shipLevel,
            int abilityLv,
            int componentCount)
        {
            if (isWeaponPool && field == StatField.BulletSpeed)
            {
                return ShipComponentExtraLevelMath.EvaluateWeaponBulletSpeed(
                    primary, perExtra, abilityLv);
            }

            return ShipComponentExtraLevelMath.Evaluate(
                primary,
                perExtra,
                shipLevel,
                abilityLv,
                componentCount,
                includeExtraComponentLevels: !isWeaponPool);
        }

        /// <summary>
        /// Inline <c>ship+ability+(N−1)</c> with the same tints as the FORMULA caption.
        /// </summary>
        static void AppendPoolLevelBreakdown(
            StringBuilder sb,
            bool isWeaponPool,
            StatField field,
            int shipSteps,
            int abilityLv,
            int componentCount)
        {
            if (isWeaponPool && field == StatField.BulletSpeed)
            {
                AppendTint(sb, HexAbility, abilityLv.ToString(CultureInfo.InvariantCulture));
                return;
            }

            AppendTint(sb, HexShip, shipSteps.ToString(CultureInfo.InvariantCulture));
            AppendTint(sb, HexMute, "+");
            AppendTint(sb, HexAbility, abilityLv.ToString(CultureInfo.InvariantCulture));
            if (!isWeaponPool)
            {
                AppendTint(sb, HexMute, "+");
                AppendTint(sb, HexCount, Mathf.Max(0, componentCount - 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>True for Move / Accel / Turn — cargo mass actually changes the chip.</summary>
        static bool IsMassAffectedField(StatField field) =>
            field is StatField.MoveSpeed or StatField.AccelerationCap or StatField.TurnSpeed;

        /// <summary>Family identity multiplier for this field (unset / 0 → 1).</summary>
        static float ReadFamilyMul(ShipFamilyDefinition family, StatField field)
        {
            if (family == null)
                return 1f;

            ShipFamilySpecialBonuses b = family.specialBonuses;
            float m = field switch
            {
                StatField.FirePower => b.firePowerMul,
                StatField.BulletSpeed => b.bulletSpeedMul,
                StatField.HealthCap => b.healthCapMul,
                StatField.HealthRegen => b.healthRegenMul,
                StatField.EnergyCap => b.energyCapMul,
                StatField.EnergyRegen => b.energyRegenMul,
                StatField.MoveSpeed => b.moveSpeedMul,
                StatField.TurnSpeed => b.turnSpeedMul,
                StatField.MaxGems => b.maxGemsMul,
                StatField.MaxPeople => b.maxPeopleMul,
                StatField.AccelerationCap => b.accelerationMul,
                StatField.BulletRange => b.bulletRangeMul,
                StatField.RammingPower => b.rammingMul,
                _ => 1f
            };
            return m > 0.0001f ? m : 1f;
        }

        static void AppendMoveAbilityCard(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs,
            int abilityLv)
        {
            _ = attrs;
            // Move: parts → Base formula → mass tax.
            AppendGroupedFieldGrid(sb, parts, StatField.MoveSpeed, "Move", useStackWeight: true, sectionTitle: "MOVE PARTS");
            AppendExtraLevelFormula(
                sb, in parts, in live, StatField.MoveSpeed, "Move", abilityLv, live.CruiseMaxSpeed);

            AppendGroupedFieldGrid(sb, parts, StatField.AccelerationCap, "Accel", useStackWeight: true, sectionTitle: "ACCEL PARTS");
            AppendExtraLevelFormula(
                sb, in parts, in live, StatField.AccelerationCap, "Accel", abilityLv, live.TaxedAccel);

            // --- Mass tax detail (gems / people / hull size) ---
            ShipSpeedometerStatTooltips.AppendMassTaxEffectsBreakdown(
                sb, in live, includeMove: true, includeAccel: true);

            // --- Capacity ceilings (static — no "now flying at X" vitals) ---
            ShipStatTooltipChrome.AppendSectionBanner(sb, "CAPACITY", "7EC8FF");
            float cruise = live.CruiseMaxSpeed > 0.01f ? live.CruiseMaxSpeed : live.ChassisMaxSpeed;
            sb.Append("Cruise max  ").Append(FResult(cruise)).AppendLine();
            if (live.OverdriveCapacityMult > 1.001f)
                sb.Append("<color=#FFCC66>OVERDRIVE bar ").Append(FResult(live.BarMaxSpeed)).Append("</color>")
                    .AppendLine();

            float moveStep = live.MoveStepPreview;
            if (moveStep <= 0.0001f)
                moveStep = Mathf.Max(0f, parts.Propulsion.moveSpeedPerExtraLevel);
            sb.Append("Purchased  Lv").Append(abilityLv.ToString(CultureInfo.InvariantCulture));
            sb.Append(" x +").Append(FDetail(moveStep)).Append(" Move/buy").AppendLine();
        }

        /// <summary>
        /// Related weapon DPS + max ramming at full cruise (not current flight speed).
        /// </summary>
        static void AppendRelatedFireExtras(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live)
        {
            ShipStatTooltipChrome.AppendSectionBanner(sb, "RELATED", "FFAA66");
            float dps = live.Weapon.BulletDamage * live.Weapon.FireRate;
            sb.Append("Hull avg  ").Append(FResult(live.Weapon.BulletDamage)).Append("/hit  ");
            sb.Append(FResult(dps)).Append("/s  ");
            sb.Append("<color=#5B7A94>").Append(FResult(live.Weapon.FireRate)).Append("/s</color>").AppendLine();

            AppendGroupedFieldGrid(sb, parts, StatField.RammingPower, "RAM", useStackWeight: true, sectionTitle: "RAM PARTS");

            // [TITAN-ORBIT] Max impact at full cruise — RamAsteroidDamage on LiveContext is filled
            // with that static estimate by ShipSpeedometerHUD (not current speed).
            float impactSpeed = live.CruiseMaxSpeed > 0.01f ? live.CruiseMaxSpeed : live.ChassisMaxSpeed;
            ShipStatTooltipChrome.AppendSectionBanner(sb, "MAX IMPACT", "FFCC66");
            sb.Append("At full cruise  ").Append(FDetail(impactSpeed)).Append("/s").AppendLine();
            sb.Append("RAM  ").Append(FDetail(live.RamRating))
                .Append(" x m").Append(FDetail(live.TotalMass))
                .Append(" -> ast ").Append(FResult(live.RamAsteroidDamage))
                .Append("  hull ").Append(FResult(live.RamSelfDamage)).AppendLine();
        }

        /// <summary>Static mass-tax drag on turn (from the last chip/tip snapshot).</summary>
        static void AppendTurnMassTax(StringBuilder sb, in ShipSpeedometerStatTooltips.LiveContext live)
        {
            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            if (settings == null)
                return;
            float drag = live.TotalMass * settings.turnWeightPerMass;
            ShipStatTooltipChrome.AppendSectionBanner(sb, "MASS TAX", HexMass);
            AppendTint(sb, HexMass, "Mass turn drag  -" + FDetail(drag) + "/s");
            sb.AppendLine();
        }

        /// <summary>
        /// Telemetry-style header: title, big readout, Lv / next step, then a tech divider.
        /// [TITAN-ORBIT] Rich-text colors match <see cref="ShipStatTooltipChrome"/> cyan readout language.
        /// </summary>
        static void AppendHeader(
            StringBuilder sb,
            string title,
            float value,
            string unit,
            int lv,
            int maxLv,
            float nextStep,
            bool moveAbility)
        {
            // --- Readout block (body starts below chrome caption — no duplicate title bar) ---
            ShipStatTooltipChrome.AppendSectionBanner(sb, "READOUT", "7EC8FF");
            sb.Append("<b><color=#E8F4FF>").Append(title).Append("</color></b>").AppendLine();

            // --- Big current value (same mint as FORMULA totals) ---
            sb.Append("<size=125%>");
            AppendTint(sb, HexResult, FResult(value));
            sb.Append("</size>");
            if (!string.IsNullOrEmpty(unit))
                AppendTint(sb, HexMute, unit);
            sb.AppendLine();

            // --- Level / next purchase (ability green, PerExtra cyan) ---
            AppendTint(sb, HexMute, "| ");
            AppendTint(sb, HexAbility, "Lv ");
            AppendTint(sb, HexAbility, lv.ToString(CultureInfo.InvariantCulture));
            AppendTint(sb, HexMute, "/");
            AppendTint(sb, HexMute, maxLv.ToString(CultureInfo.InvariantCulture));
            if (nextStep > 0.0001f)
            {
                sb.Append("  ");
                AppendTint(sb, HexMute, "*  next ");
                AppendTint(sb, HexPerExtra, "+" + FResult(nextStep));
                if (!moveAbility)
                    AppendTint(sb, HexMute, " (PerExtra)");
                else
                    AppendTint(sb, HexMute, " (ability step)");
            }

            sb.AppendLine();
        }

        static float ReadField(in ShipComponentAbilityStats s, StatField field) =>
            field switch
            {
                StatField.FirePower => s.firePower,
                StatField.BulletSpeed => s.bulletSpeed,
                StatField.HealthCap => s.healthCap,
                StatField.HealthRegen => s.healthRegen,
                StatField.EnergyCap => s.energyCap,
                StatField.EnergyRegen => s.energyRegen,
                StatField.MoveSpeed => s.moveSpeed,
                StatField.TurnSpeed => s.turnSpeed,
                StatField.MaxGems => s.maxGems,
                StatField.MaxPeople => s.maxPeople,
                StatField.AccelerationCap => ShipPropulsionAggregation.GetPropulsionAccelerationContribution(s, 0),
                StatField.BulletRange => s.bulletRange,
                StatField.RammingPower => s.rammingPower,
                _ => 0f
            };

        /// <summary>Primary PerExtraLevel step for the chip field (from evaluated hull stats).</summary>
        static float ReadPerExtraLevel(in ShipComponentAbilityStats s, StatField field) =>
            field switch
            {
                StatField.FirePower => s.firePowerPerExtraLevel,
                StatField.BulletSpeed => s.bulletSpeedPerExtraLevel,
                StatField.HealthCap => s.healthCapPerExtraLevel,
                StatField.HealthRegen => s.healthRegenPerExtraLevel,
                StatField.EnergyCap => s.energyCapPerExtraLevel,
                StatField.EnergyRegen => s.energyRegenPerExtraLevel,
                StatField.MoveSpeed => s.moveSpeedPerExtraLevel,
                StatField.TurnSpeed => s.turnSpeedPerExtraLevel,
                StatField.MaxGems => s.maxGemsPerExtraLevel,
                StatField.MaxPeople => s.maxPeoplePerExtraLevel,
                StatField.AccelerationCap => s.accelerationCapPerExtraLevel,
                StatField.BulletRange => s.bulletRangePerExtraLevel,
                StatField.RammingPower => s.rammingPowerPerExtraLevel,
                _ => 0f
            };

        /// <summary>
        /// Writes catalog + starting-scale fields onto a grouped row from one part index.
        /// </summary>
        static void FillScaleFields(
            in ShipSpeedometerStatTooltips.PartCache parts,
            int index,
            StatField field,
            float scaledValue,
            ref GroupedPartRow row)
        {
            string id = index >= 0 && index < parts.Ids.Count ? parts.Ids[index] : row.ComponentId;
            ShipComponentScaleChannel channel = ToScaleChannel(field);
            float scale = ShipComponentAbilityStatsMath.GetScaleMultiplier(
                ReadLocalScale(in parts, index), id, channel);
            row.ScaleFactor = scale;

            if (TryReadCatalogField(parts.Family, id, field, out float catalog, out _))
            {
                row.CatalogEach = catalog;
                return;
            }

            // Cache already holds scaled stats — recover catalog when scale is known.
            if (HasMeaningfulScale(scale) && scale > 0.0001f)
                row.CatalogEach = scaledValue / scale;
            else
                row.CatalogEach = scaledValue;
        }

        /// <summary>Family-catalog Base / PerExtra for one field (unscaled).</summary>
        static bool TryReadCatalogField(
            ShipFamilyDefinition family,
            string componentId,
            StatField field,
            out float catalogPrimary,
            out float catalogPerExtra)
        {
            catalogPrimary = 0f;
            catalogPerExtra = 0f;
            if (family == null || string.IsNullOrWhiteSpace(componentId))
                return false;
            if (!family.TryGetStatsForComponent(componentId, out ShipComponentAbilityStats catalog))
                return false;

            catalogPrimary = ReadField(catalog, field);
            catalogPerExtra = ReadPerExtraLevel(catalog, field);
            return catalogPrimary > 0.0001f || catalogPerExtra > 0.0001f;
        }

        /// <summary>Prefab start scale for a cached part, or (1,1,1) when the list is short.</summary>
        static Vector3 ReadLocalScale(in ShipSpeedometerStatTooltips.PartCache parts, int index)
        {
            if (parts.LocalScales == null || index < 0 || index >= parts.LocalScales.Count)
                return Vector3.one;
            return parts.LocalScales[index];
        }

        /// <summary>Maps a chip field onto the same scale channel <see cref="ShipComponentAbilityStatsMath.ScaleStatsByTransform"/> uses.</summary>
        static ShipComponentScaleChannel ToScaleChannel(StatField field) =>
            field switch
            {
                StatField.FirePower => ShipComponentScaleChannel.FirePower,
                StatField.BulletSpeed => ShipComponentScaleChannel.BulletSpeed,
                StatField.HealthCap => ShipComponentScaleChannel.Health,
                StatField.HealthRegen => ShipComponentScaleChannel.Health,
                StatField.EnergyCap => ShipComponentScaleChannel.Energy,
                StatField.EnergyRegen => ShipComponentScaleChannel.Energy,
                StatField.MoveSpeed => ShipComponentScaleChannel.MoveOrAccel,
                StatField.TurnSpeed => ShipComponentScaleChannel.Turn,
                StatField.MaxGems => ShipComponentScaleChannel.Capacity,
                StatField.MaxPeople => ShipComponentScaleChannel.Capacity,
                StatField.AccelerationCap => ShipComponentScaleChannel.MoveOrAccel,
                StatField.BulletRange => ShipComponentScaleChannel.BulletRange,
                StatField.RammingPower => ShipComponentScaleChannel.Ramming,
                _ => ShipComponentScaleChannel.Capacity
            };

        /// <summary>True when starting scale is worth showing (not ~×1).</summary>
        static bool HasMeaningfulScale(float scale) => Mathf.Abs(scale - 1f) > 0.01f;

        static string ResolvePartName(ShipFamilyDefinition family, string componentId)
        {
            if (family != null
                && family.TryGetComponentEntry(componentId, out ShipFamilyComponentEntry entry)
                && entry != null)
                return ShipComponentStoreData.GetDisplayName(entry);
            return ShipComponentStoreData.FormatComponentId(componentId);
        }

        static string FormatWeight(float weight)
        {
            float pct = weight * 100f;
            if (Mathf.Abs(pct - 100f) < 0.05f)
                return "×100%";
            if (Mathf.Abs(pct - 10f) < 0.05f)
                return "×10%";
            if (Mathf.Abs(pct - Mathf.Round(pct)) < 0.05f)
                return "×" + Mathf.RoundToInt(pct).ToString(CultureInfo.InvariantCulture) + "%";
            return "×" + pct.ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }

        static string PadRightPlain(string s, int width)
        {
            s ??= string.Empty;
            if (s.Length >= width)
                return s.Substring(0, width);
            return s + new string(' ', width - s.Length);
        }

        static string PadLeftPlain(string s, int width)
        {
            s ??= string.Empty;
            if (s.Length >= width)
                return s;
            return new string(' ', width - s.Length) + s;
        }

        /// <summary>Wraps <paramref name="text"/> in a TMP colour tag (hex without #).</summary>
        static string Tint(string hex, string text) =>
            string.Concat("<color=#", hex, ">", text, "</color>");

        /// <summary>Appends a TMP colour span. Same hex as <see cref="Tint"/>.</summary>
        static void AppendTint(StringBuilder sb, string hex, string text)
        {
            sb.Append("<color=#").Append(hex).Append('>').Append(text).Append("</color>");
        }

        static string F0(float v) => v.ToString("0", CultureInfo.InvariantCulture);
        static string FDetail(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string FResult(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
