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
    /// non-weapons <c>Base + PerExtra × ((shipLevel−1) + abilityLevel + (N−1))</c>;
    /// weapons <c>Base + PerExtra × ((shipLevel−1) + abilityLevel)</c> per barrel;
    /// weapon bullet speed <c>Base + PerExtra × abilityLevel</c> only.
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
                    AppendGroupedFieldGrid(sb, parts, StatField.BulletRange, "Range", useStackWeight: true);
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
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.TurnSpeed, "Turn Speed", lv, live.ChassisTurnDeg > 0.01f ? live.ChassisTurnDeg : live.EffectiveStats.turnSpeed);
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
        /// Extra Level grouping: primary supplies Base; extras raise component count only.
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
        /// Appends a clear PRIMARY / EXTRAS parts grid for Extra Level.
        /// Example: <c>1× Engine_1  Base 12</c> then <c>2× Engine_1  count only</c>.
        /// </summary>
        /// <param name="sectionTitle">
        /// Optional inner-panel banner label. Null = default PARTS / STACK.
        /// </param>
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
            // [TITAN-ORBIT] Inner "panel" header so PARTS reads as its own block inside the tip.
            string banner = string.IsNullOrEmpty(sectionTitle)
                ? (useStackWeight ? "PARTS / STACK" : "PARTS")
                : sectionTitle;
            ShipStatTooltipChrome.AppendSectionBanner(sb, banner, "5B9BD5");

            if (rows.Count == 0)
            {
                sb.AppendLine("<color=#5B7A94>No contributing parts.</color>");
                return;
            }

            if (useStackWeight)
            {
                sb.AppendLine("<color=#5B7A94>Base from PRIMARY only · extras raise Extra Level count</color>");
            }
            else
            {
                sb.AppendLine("<color=#5B7A94>flat list (no pool primary)</color>");
            }

            float poolSum = 0f;
            float primarySum = 0f;
            int extraInstances = 0;
            int totalCount = 0;

            // --- PRIMARY block ---
            bool wrotePrimaryHeader = false;
            for (int i = 0; i < rows.Count; i++)
            {
                GroupedPartRow r = rows[i];
                if (r.PrimaryCount <= 0)
                    continue;
                if (!wrotePrimaryHeader)
                {
                    ShipStatTooltipChrome.AppendSubDivider(sb);
                    sb.AppendLine("<color=#5B9BD5>> PRIMARY</color> <color=#5B7A94>(Base)</color>");
                    wrotePrimaryHeader = true;
                }

                primarySum += r.PrimaryContrib;
                poolSum += r.PrimaryContrib;
                totalCount += r.PrimaryCount;
                AppendPartMathLine(
                    sb,
                    r.PrimaryCount,
                    r.DisplayName,
                    r.AuthoredEach,
                    1f,
                    r.PrimaryContrib,
                    unitLabel,
                    highlight: true,
                    countOnly: false);
            }

            if (!wrotePrimaryHeader && useStackWeight)
                sb.AppendLine("<color=#888888>PRIMARY — none</color>");

            // --- EXTRAS block ---
            bool wroteExtraHeader = false;
            for (int i = 0; i < rows.Count; i++)
            {
                GroupedPartRow r = rows[i];
                if (r.ExtraCount <= 0)
                    continue;
                if (!wroteExtraHeader)
                {
                    ShipStatTooltipChrome.AppendSubDivider(sb);
                    sb.AppendLine("<color=#C9A0FF>> EXTRAS</color> <color=#5B7A94>(count toward Extra Level)</color>");
                    wroteExtraHeader = true;
                }

                extraInstances += r.ExtraCount;
                totalCount += r.ExtraCount;
                AppendPartMathLine(
                    sb,
                    r.ExtraCount,
                    r.DisplayName,
                    r.AuthoredEach,
                    0f,
                    0f,
                    unitLabel,
                    highlight: false,
                    countOnly: true);
            }

            if (useStackWeight && !wroteExtraHeader)
                sb.AppendLine("<color=#888888>EXTRAS — none</color>");

            sb.AppendLine();
            if (useStackWeight)
            {
                sb.Append("Primary Base  +").Append(FDetail(primarySum)).Append(" ").Append(unitLabel);
                if (extraInstances > 0)
                {
                    sb.Append("  ·  ").Append(extraInstances.ToString(CultureInfo.InvariantCulture))
                        .Append("× extras (count)");
                }

                sb.Append("  ·  N=").Append(totalCount.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
            }

            sb.Append("<color=#5B7A94>BASE</color>  <color=#AAEEDD>").Append(FDetail(poolSum)).Append("</color> ")
                .Append(unitLabel).AppendLine();
        }

        /// <summary>
        /// One math line: primary <c>1× Name  Base 12.0 = +12 Move</c>,
        /// or extras <c>2× Name  count only</c>.
        /// </summary>
        static void AppendPartMathLine(
            StringBuilder sb,
            int count,
            string displayName,
            float authoredEach,
            float weight,
            float contribTotal,
            string unitLabel,
            bool highlight,
            bool countOnly = false)
        {
            string countStr = count.ToString(CultureInfo.InvariantCulture) + "×";
            sb.Append("<mspace=0.58em>");
            sb.Append(PadRightPlain(countStr, 4));
            if (highlight)
                sb.Append("<color=#AAEEDD>").Append(PadRightPlain(displayName, 14)).Append("</color>");
            else
                sb.Append(PadRightPlain(displayName, 14));

            if (countOnly)
            {
                sb.Append(" <color=#5B7A94>count only</color> (+")
                    .Append(count.ToString(CultureInfo.InvariantCulture))
                    .Append(" Extra Level)");
            }
            else
            {
                sb.Append(" Base ").Append(PadLeftPlain(FDetail(authoredEach), 6));
                if (Mathf.Abs(weight - 1f) > 0.001f)
                    sb.Append(" ").Append(FormatWeight(weight));
                sb.Append(" = +").Append(FDetail(contribTotal)).Append(" ").Append(unitLabel);
            }

            sb.Append("</mspace>");
            sb.AppendLine();
        }

        /// <summary>
        /// Extra Level pipeline: primary Base → + PerExtra × ((shipLv−1)+ability+(N−1)).
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

            int shipLevel = Mathf.Max(1, live.Ship.ShipLevel);
            int perLvl = Mathf.Max(0, shipLevel - 1);
            float perExtra = ReadPerExtraLevel(live.EffectiveStats, field);
            int componentCount = CountFieldPoolMembers(in parts, field);
            int extraLevels = ShipComponentExtraLevelMath.CountExtraLevels(shipLevel, abilityLv, componentCount);

            // --- Pipeline block (Extra Level) ---
            ShipStatTooltipChrome.AppendSectionBanner(sb, "PIPELINE", "7DFFB2");
            if (field == StatField.BulletSpeed)
            {
                // [TITAN-ORBIT] Weapon bullet speed: ability purchases only.
                int speedLevels = ShipComponentExtraLevelMath.CountWeaponBulletSpeedExtraLevels(abilityLv);
                sb.AppendLine("<color=#5B7A94>Base + PerExtra × ability  — no ship level, no N</color>");
                sb.Append("Ability ").Append(abilityLv.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.Append("Extra Levels  ").Append(speedLevels.ToString(CultureInfo.InvariantCulture));
            }
            else if (field == StatField.FirePower || field == StatField.BulletRange)
            {
                // [TITAN-ORBIT] Weapons ignore N — each barrel uses ship + ability only.
                int weaponLevels = ShipComponentExtraLevelMath.CountWeaponExtraLevels(shipLevel, abilityLv);
                sb.AppendLine("<color=#5B7A94>Base + PerExtra × ((shipLv−1) + ability)  — per barrel, no N stack</color>");
                sb.Append("Ship Lv ").Append(shipLevel.ToString(CultureInfo.InvariantCulture));
                sb.Append("  (shipLv−1=").Append(perLvl.ToString(CultureInfo.InvariantCulture)).Append(")");
                sb.Append("  Ability ").Append(abilityLv.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.Append("Extra Levels  ").Append(weaponLevels.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.AppendLine("<color=#5B7A94>Base + PerExtra × ((shipLv−1) + ability + (N−1))</color>");
                sb.Append("Ship Lv ").Append(shipLevel.ToString(CultureInfo.InvariantCulture));
                sb.Append("  (shipLv−1=").Append(perLvl.ToString(CultureInfo.InvariantCulture)).Append(")");
                sb.Append("  Ability ").Append(abilityLv.ToString(CultureInfo.InvariantCulture));
                sb.Append("  N=").Append(componentCount.ToString(CultureInfo.InvariantCulture));
                sb.Append("  (N−1=").Append(Mathf.Max(0, componentCount - 1).ToString(CultureInfo.InvariantCulture)).Append(")");
                sb.AppendLine();
                sb.Append("Extra Levels  ").Append(extraLevels.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append("  × PerExtra ").Append(FDetail(perExtra));
            sb.Append(" -> <b><color=#AAEEDD>").Append(FResult(finalEffective)).Append("</color></b>").AppendLine();
        }

        static void AppendMoveAbilityCard(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs,
            int abilityLv)
        {
            _ = attrs;
            sb.AppendLine("<color=#5B7A94>Extra Level: Base + PerExtra×((shipLv−1)+ability+(N−1)). Accel + OD drain share Move ability.</color>");
            AppendGroupedFieldGrid(sb, parts, StatField.MoveSpeed, "Move", useStackWeight: true, sectionTitle: "MOVE PARTS");
            AppendGroupedFieldGrid(sb, parts, StatField.AccelerationCap, "Accel", useStackWeight: true, sectionTitle: "ACCEL PARTS");

            // --- Mass tax: composition + drag on Move and Accel (snapshot at last rebuild) ---
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
            if (!parts.Valid || parts.Ids == null)
                return 0;

            if (field is StatField.MoveSpeed or StatField.AccelerationCap)
                return ShipComponentStackAggregation.CountPoolMembers(
                    ShipComponentStackAggregation.PropulsionPoolKey, parts.Ids);

            // Prefer the first matching part's pool key; weapons share "Weapon".
            for (int i = 0; i < parts.Ids.Count; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (i < parts.Stats.Count && ReadField(parts.Stats[i], field) <= 0.0001f)
                    continue;
                return ShipComponentStackAggregation.CountPoolMembers(
                    ShipComponentStackAggregation.ResolveStackPoolKey(id), parts.Ids);
            }

            return 0;
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
