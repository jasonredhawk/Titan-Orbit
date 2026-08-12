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
    /// Tip cards: PARTS (Primary + Extras) then FORMULA
    /// <c>Base = Primary + PerExtra × levels</c>.
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
            /// <summary>Authored field value per instance (Base shown for primary; extras ignored for Base).</summary>
            public float AuthoredEach;
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
                }

                // Flat list mode — every instance is shown as primary for display.
                row.PrimaryCount++;
                row.PrimaryContrib += authored;
                row.AuthoredEach = authored;
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
                            AuthoredEach = authored,
                            ExtraWeight = 0f
                        };
                    }

                    if (isPrimary)
                    {
                        row.AuthoredEach = authored;
                        row.PrimaryCount++;
                        row.PrimaryContrib += authored;
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
                    sb.AppendLine("<color=#5B9BD5>> PRIMARY</color>");
                    wrotePrimary = true;
                }

                AppendPartLine(sb, r.PrimaryCount, r.DisplayName, r.AuthoredEach, unitLabel, isExtra: false);
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
                    sb.AppendLine("<color=#C9A0FF>> EXTRAS</color> <color=#5B7A94>(+ to N)</color>");
                    wroteExtra = true;
                }

                AppendPartLine(sb, r.ExtraCount, r.DisplayName, 0f, unitLabel, isExtra: true);
            }
        }

        /// <summary>
        /// One part row: <c>1× Engine_1   12 Move</c> or <c>2× Engine_1   +2 to N</c>.
        /// </summary>
        static void AppendPartLine(
            StringBuilder sb,
            int count,
            string displayName,
            float authoredEach,
            string unitLabel,
            bool isExtra)
        {
            sb.Append(count.ToString(CultureInfo.InvariantCulture)).Append("× ");
            sb.Append(displayName);
            if (isExtra)
            {
                sb.Append("  <color=#5B7A94>+")
                    .Append(count.ToString(CultureInfo.InvariantCulture))
                    .Append(" to N</color>");
            }
            else
            {
                sb.Append("  <color=#AAEEDD>").Append(FDetail(authoredEach)).Append("</color>");
                if (!string.IsNullOrEmpty(unitLabel))
                    sb.Append(" ").Append(unitLabel);
            }

            sb.AppendLine();
        }

        /// <summary>
        /// Parts breakdown, then Extra Level formula, then Base.
        /// <para>
        /// Primary = authored value on the primary part.
        /// Base = Primary + PerExtra × (levels) — the Extra Level result before mass tax.
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
        /// Writes the Extra Level equation with numbers, then the Base result.
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
            int n = CountFieldPoolMembers(in parts, field);
            ResolvePrimaryAuthored(in parts, field, out float primary, out float perExtra);
            float baseValue = PredictPipelineTotal(field, primary, perExtra, shipLevel, abilityLv, n);
            int levels = CountFormulaLevels(field, shipLevel, abilityLv, n);

            // [TITAN-ORBIT] Turn is authored in definition units; chips show °/s.
            float unitScale = field == StatField.TurnSpeed
                ? ShipPropulsionAggregation.TurnDefinitionToDegreesPerSecond
                : 1f;
            float primaryDisp = primary * unitScale;
            float perExtraDisp = perExtra * unitScale;
            float baseDisp = baseValue * unitScale;

            // --- Formula ---
            ShipStatTooltipChrome.AppendSectionBanner(sb, "FORMULA", "7DFFB2");
            sb.AppendLine(DescribeFormula(field));
            sb.Append("Primary ").Append(FDetail(primaryDisp));
            sb.Append("  PerExtra ").Append(FDetail(perExtraDisp));
            sb.Append("  levels ").Append(levels.ToString(CultureInfo.InvariantCulture));
            sb.Append("  (= ");
            AppendLevelBreakdown(sb, field, shipSteps, abilityLv, n);
            sb.Append(')');
            sb.AppendLine();

            // Base = Primary + PerExtra × levels
            sb.Append("<b>Base</b> = ").Append(FDetail(primaryDisp));
            sb.Append(" + ").Append(FDetail(perExtraDisp));
            sb.Append(" × ").Append(levels.ToString(CultureInfo.InvariantCulture));
            sb.Append(" = <b><color=#AAEEDD>").Append(FResult(baseDisp)).Append("</color></b>");
            if (!string.IsNullOrEmpty(unitLabel))
                sb.Append(" ").Append(unitLabel);
            sb.AppendLine();

            // Mass-taxed live when it differs from Base (Move / Turn).
            if (Mathf.Abs(finalEffective - baseDisp) > 0.05f)
            {
                sb.Append("<color=#5B7A94>After mass tax</color>  ")
                    .Append("<color=#AAEEDD>").Append(FResult(finalEffective)).Append("</color>");
                if (!string.IsNullOrEmpty(unitLabel))
                    sb.Append(" ").Append(unitLabel);
                sb.AppendLine();
            }
        }

        /// <summary>Short formula label for the field kind.</summary>
        static string DescribeFormula(StatField field)
        {
            if (field == StatField.BulletSpeed)
                return "<color=#5B7A94>Base = Primary + PerExtra × ability</color>";
            if (field == StatField.FirePower || field == StatField.BulletRange)
                return "<color=#5B7A94>Base = Primary + PerExtra × ((ship−1) + ability)</color>";
            return "<color=#5B7A94>Base = Primary + PerExtra × ((ship−1) + ability + (N−1))</color>";
        }

        /// <summary>How many Extra Level steps this field uses.</summary>
        static int CountFormulaLevels(StatField field, int shipLevel, int abilityLv, int componentCount)
        {
            if (field == StatField.BulletSpeed)
                return ShipComponentExtraLevelMath.CountWeaponBulletSpeedExtraLevels(abilityLv);
            if (field == StatField.FirePower || field == StatField.BulletRange)
                return ShipComponentExtraLevelMath.CountWeaponExtraLevels(shipLevel, abilityLv);
            return ShipComponentExtraLevelMath.CountExtraLevels(shipLevel, abilityLv, componentCount);
        }

        /// <summary>Inline breakdown of the levels term, e.g. <c>5+4+2</c>.</summary>
        static void AppendLevelBreakdown(
            StringBuilder sb,
            StatField field,
            int shipSteps,
            int abilityLv,
            int componentCount)
        {
            if (field == StatField.BulletSpeed)
            {
                sb.Append(abilityLv.ToString(CultureInfo.InvariantCulture));
                return;
            }

            sb.Append(shipSteps.ToString(CultureInfo.InvariantCulture));
            sb.Append('+').Append(abilityLv.ToString(CultureInfo.InvariantCulture));
            if (field != StatField.FirePower && field != StatField.BulletRange)
                sb.Append('+').Append(Mathf.Max(0, componentCount - 1).ToString(CultureInfo.InvariantCulture));
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
            ShipStatTooltipChrome.AppendSectionBanner(sb, "MASS TAX", "C9A0FF");
            sb.Append("Mass turn drag  -").Append(FDetail(drag)).Append("/s").AppendLine();
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

            // --- Big current value ---
            sb.Append("<size=125%><color=#AAEEDD>").Append(FResult(value)).Append("</color></size>");
            if (!string.IsNullOrEmpty(unit))
                sb.Append("<color=#6A8499>").Append(unit).Append("</color>");
            sb.AppendLine();

            // --- Level / next purchase ---
            sb.Append("<color=#5B7A94>|</color> <color=#B8C8D8>Lv </color>")
                .Append("<color=#E8F4FF>").Append(lv.ToString(CultureInfo.InvariantCulture)).Append("</color>")
                .Append("<color=#5B7A94>/</color>")
                .Append("<color=#B8C8D8>").Append(maxLv.ToString(CultureInfo.InvariantCulture)).Append("</color>");
            if (nextStep > 0.0001f)
            {
                sb.Append("  <color=#5B7A94>*</color>  <color=#B8C8D8>next </color>")
                    .Append("<color=#7DFFB2>+").Append(FResult(nextStep)).Append("</color>");
                if (!moveAbility)
                    sb.Append(" <color=#5B7A94>(+10%)</color>");
                else
                    sb.Append(" <color=#5B7A94>(ability step)</color>");
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

        /// <summary>How many non-cosmetic parts sit in the same Extra Level pool for this field.</summary>
        static int CountFieldPoolMembers(in ShipSpeedometerStatTooltips.PartCache parts, StatField field)
        {
            if (!TryGetFieldPool(in parts, field, out string poolKey, out _))
                return 0;
            return ShipComponentStackAggregation.CountPoolMembers(poolKey, parts.Ids);
        }

        /// <summary>
        /// Authored Primary value and PerExtra from the pool primary part for this field.
        /// </summary>
        static void ResolvePrimaryAuthored(
            in ShipSpeedometerStatTooltips.PartCache parts,
            StatField field,
            out float primaryValue,
            out float perExtra)
        {
            primaryValue = 0f;
            perExtra = 0f;
            if (!TryGetFieldPool(in parts, field, out string poolKey, out List<int> members)
                || members == null
                || members.Count == 0)
            {
                // Fallback: hull EffectiveStats PerExtra (may be multi-pool summed).
                return;
            }

            int primaryLocal = ShipComponentStackAggregation.PickPrimaryLocalIndex(
                poolKey, members, parts.Stats);
            int gi = members[primaryLocal];
            if (gi < 0 || gi >= parts.Stats.Count)
                return;

            ShipComponentAbilityStats s = parts.Stats[gi];
            primaryValue = ReadField(s, field);
            perExtra = ReadPerExtraLevel(s, field);
        }

        /// <summary>
        /// Predicted Extra Level total from Primary (before mass tax / card modifiers).
        /// </summary>
        static float PredictPipelineTotal(
            StatField field,
            float primaryValue,
            float perExtra,
            int shipLevel,
            int abilityLv,
            int componentCount)
        {
            if (field == StatField.BulletSpeed)
            {
                return ShipComponentExtraLevelMath.EvaluateWeaponBulletSpeed(
                    primaryValue, perExtra, abilityLv);
            }

            bool weaponSolo = field == StatField.FirePower || field == StatField.BulletRange;
            return ShipComponentExtraLevelMath.Evaluate(
                primaryValue,
                perExtra,
                shipLevel,
                abilityLv,
                componentCount,
                includeExtraComponentLevels: !weaponSolo);
        }

        /// <summary>
        /// Finds the Extra Level pool that owns this field and its member indices in <paramref name="parts"/>.
        /// </summary>
        static bool TryGetFieldPool(
            in ShipSpeedometerStatTooltips.PartCache parts,
            StatField field,
            out string poolKey,
            out List<int> members)
        {
            poolKey = null;
            members = null;
            if (!parts.Valid || parts.Ids == null || parts.Stats == null)
                return false;

            if (field is StatField.MoveSpeed or StatField.AccelerationCap)
            {
                poolKey = ShipComponentStackAggregation.PropulsionPoolKey;
                members = new List<int>(4);
                for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
                {
                    string id = parts.Ids[i];
                    if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                        continue;
                    if (!ShipComponentAbilityStats.IsPropulsionComponent(id))
                        continue;
                    if (ReadField(parts.Stats[i], field) <= 0.0001f)
                        continue;
                    members.Add(i);
                }

                return members.Count > 0;
            }

            // First contributing part decides the pool (weapons → "Weapon", cockpits → Cockpit, …).
            for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (ReadField(parts.Stats[i], field) <= 0.0001f)
                    continue;

                poolKey = ShipComponentStackAggregation.ResolveStackPoolKey(id);
                members = new List<int>(4);
                for (int j = 0; j < parts.Ids.Count && j < parts.Stats.Count; j++)
                {
                    string idJ = parts.Ids[j];
                    if (string.IsNullOrWhiteSpace(idJ) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(idJ))
                        continue;
                    if (!string.Equals(
                            ShipComponentStackAggregation.ResolveStackPoolKey(idJ),
                            poolKey,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ReadField(parts.Stats[j], field) <= 0.0001f)
                        continue;
                    members.Add(j);
                }

                return members.Count > 0;
            }

            return false;
        }

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

        static string F0(float v) => v.ToString("0", CultureInfo.InvariantCulture);
        static string FDetail(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string FResult(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
