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
    /// Builds grouped part grids (N× same component) and walks parts → stack → ship tier →
    /// ability purchases → live modifiers. Presentation-only — never writes ECS.
    /// <para>
    /// Rich text is shown inside <see cref="ShipStatTooltipChrome"/> (Shift sci-fi frame).
    /// Paired with <see cref="ShipAttributeUpgradeHUD"/> chips and
    /// <see cref="ShipSpeedometerStatTooltips"/> (shared <see cref="ShipSpeedometerStatTooltips.PartCache"/>).
    /// </para>
    /// </summary>
    public static class ShipAbilityStatBreakdown
    {
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
            /// <summary>How many instances of this id are extras (weighted).</summary>
            public int ExtraCount;
            /// <summary>Authored field value per instance (before weight).</summary>
            public float AuthoredEach;
            /// <summary>Weight applied to each extra (primary always uses 1).</summary>
            public float ExtraWeight;
            /// <summary>PrimaryCount × AuthoredEach × 1.</summary>
            public float PrimaryContrib;
            /// <summary>ExtraCount × AuthoredEach × ExtraWeight.</summary>
            public float ExtraContrib;

            /// <summary>Total contribution to the pool from this id.</summary>
            public float ContribTotal => PrimaryContrib + ExtraContrib;

            /// <summary>Total instance count.</summary>
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
                    nextStep = NextTenPercentStep(value, abilityLv);
                    break;
                case 1:
                    value = Mathf.Max(0f, eff.bulletSpeed);
                    nextStep = NextTenPercentStep(value, abilityLv);
                    break;
                case 2:
                    value = Mathf.Max(0f, eff.healthCap);
                    nextStep = NextTenPercentStep(value, abilityLv);
                    break;
                case 3:
                    value = Mathf.Max(0f, eff.healthRegen);
                    unitSuffix = "/s";
                    nextStep = NextTenPercentStep(value, abilityLv);
                    break;
                case 4:
                    value = Mathf.Max(0f, eff.energyCap);
                    nextStep = NextTenPercentStep(value, abilityLv);
                    break;
                case 5:
                    value = Mathf.Max(0f, eff.energyRegen);
                    unitSuffix = "/s";
                    nextStep = NextTenPercentStep(value, abilityLv);
                    break;
                case 6:
                    // [TITAN-ORBIT] Chip shows live cruise after mass tax (+ territory), matching the
                    // speedometer SPD ceiling when OVERDRIVE is off — not pre-tax chassis Move.
                    value = Mathf.Max(0f, live.CruiseMaxSpeed > 0.01f
                        ? live.CruiseMaxSpeed
                        : live.ChassisMaxSpeed);
                    // Next purchase still adds a chassis PerAbilityLevel step (subtractive tax
                    // does not shrink that delta), so preview stays MoveStepPreview.
                    nextStep = Mathf.Max(0f, live.MoveStepPreview);
                    if (nextStep <= 0.0001f)
                        nextStep = Mathf.Max(0f, eff.moveSpeedPerAbilityLevel);
                    break;
                case 7:
                    // [TITAN-ORBIT] Show post–mass-tax turn when available; +10% step still from chassis.
                    float chassisTurn = live.ChassisTurnDeg > 0.01f ? live.ChassisTurnDeg : eff.turnSpeed;
                    value = Mathf.Max(0f, live.TaxedTurnDeg > 0.01f ? live.TaxedTurnDeg : chassisTurn);
                    unitSuffix = "°/s";
                    nextStep = NextTenPercentStep(chassisTurn, abilityLv);
                    break;
                case 8:
                    value = Mathf.Max(0f, eff.maxGems);
                    nextStep = NextTenPercentStep(value, abilityLv);
                    break;
                case 9:
                    value = Mathf.Max(0f, eff.maxPeople);
                    nextStep = NextTenPercentStep(value, abilityLv);
                    break;
                default:
                    value = 0f;
                    nextStep = 0f;
                    break;
            }
        }

        /// <summary>
        /// Next +10% purchase adds MultiplierPerLevel of the <b>pre-ability</b> base.
        /// Approximate: current / (1 + 0.1×Lv) × 0.1.
        /// </summary>
        static float NextTenPercentStep(float currentEffective, int abilityLv)
        {
            float mult = 1f + Mathf.Max(0, abilityLv) * ShipAttributeUpgradeLogic.MultiplierPerLevel;
            float pre = currentEffective / Mathf.Max(0.0001f, mult);
            return pre * ShipAttributeUpgradeLogic.MultiplierPerLevel;
        }

        /// <summary>Full TMP card for one ability index (0–9).</summary>
        public static string BuildForAbilityIndex(
            int abilityIndex,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs)
        {
            var sb = new StringBuilder(1024);
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
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.HealthCap, "Health Cap", lv, live.EffectiveStats.healthCap);
                    ShipStatTooltipChrome.AppendSectionBanner(sb, "LIVE", "7EC8FF");
                    sb.Append("Live HP  ").Append(FResult(live.Ship.Health))
                        .Append(" / ").Append(FResult(live.Ship.MaxHealth)).AppendLine();
                    break;
                case 3:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.HealthRegen, "Health Regen", lv, live.EffectiveStats.healthRegen);
                    break;
                case 4:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.EnergyCap, "Energy Cap", lv, live.EffectiveStats.energyCap);
                    ShipStatTooltipChrome.AppendSectionBanner(sb, "LIVE", "7EC8FF");
                    sb.Append("Live Energy  ").Append(FResult(live.Ship.CurrentEnergy))
                        .Append(" / ").Append(FResult(live.Ship.MaxEnergy)).AppendLine();
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
                    ShipStatTooltipChrome.AppendSectionBanner(sb, "LIVE", "7EC8FF");
                    sb.Append("Live gems  ").Append(F0(live.Ship.CurrentGems)).AppendLine();
                    break;
                case 9:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.MaxPeople, "Max People", lv, live.EffectiveStats.maxPeople);
                    ShipStatTooltipChrome.AppendSectionBanner(sb, "LIVE", "7EC8FF");
                    sb.Append("Live people  ").Append(F0(live.Ship.CurrentPeople)).AppendLine();
                    break;
                default:
                    sb.AppendLine("<color=#888888>Unknown ability</color>");
                    break;
            }

            return sb.Length > 0 ? sb.ToString() : "<color=#888888>No breakdown available</color>";
        }

        /// <summary>
        /// Groups parts that contribute to <paramref name="field"/>, collapsing identical ids.
        /// When <paramref name="useStackWeight"/> is true, uses stack pool formula B (primary ×1,
        /// extras × extraStackWeight).
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
                        ExtraWeight = 1f
                    };
                }

                // No stack weight — every instance is a full add (treat as "primary" for display).
                row.PrimaryCount++;
                row.PrimaryContrib += authored;
                row.AuthoredEach = authored;
                map[id] = row;
            }

            into.AddRange(map.Values);
            into.Sort((a, b) => b.ContribTotal.CompareTo(a.ContribTotal));
        }

        /// <summary>Formula B grouping across stack pools for one stat field.</summary>
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
                    float weight = isPrimary
                        ? 1f
                        : ShipComponentStackAggregation.ResolveExtraStackWeight(parts.Stats[gi], id);

                    if (!map.TryGetValue(id, out GroupedPartRow row))
                    {
                        row = new GroupedPartRow
                        {
                            ComponentId = id,
                            DisplayName = ResolvePartName(parts.Family, id),
                            AuthoredEach = authored,
                            ExtraWeight = isPrimary
                                ? ShipComponentStackAggregation.ResolveExtraStackWeight(parts.Stats[gi], id)
                                : weight
                        };
                        // Prefer storing the extra weight even when first sighting is primary.
                        if (isPrimary)
                        {
                            row.ExtraWeight = ShipComponentStackAggregation.ResolveExtraStackWeight(
                                parts.Stats[gi], id);
                        }
                    }

                    row.AuthoredEach = authored;
                    if (isPrimary)
                    {
                        row.PrimaryCount++;
                        row.PrimaryContrib += authored; // ×100%
                    }
                    else
                    {
                        row.ExtraCount++;
                        row.ExtraWeight = weight;
                        row.ExtraContrib += authored * weight;
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
        /// Appends a clear PRIMARY / EXTRAS parts grid.
        /// Example: <c>1× Engine_1  base 12 ×100% = +12</c> then <c>2× Engine_1  base 12 ×10% = +2.4</c>.
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
                sb.AppendLine("<color=#5B7A94>primary x100% of base · extras x their extraStackWeight</color>");
            }
            else
            {
                sb.AppendLine("<color=#5B7A94>full sum — no stack weight</color>");
            }

            float poolSum = 0f;
            float primarySum = 0f;
            float extrasSum = 0f;
            int extraInstances = 0;

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
                    sb.AppendLine("<color=#5B9BD5>> PRIMARY</color> <color=#5B7A94>(x100% of base)</color>");
                    wrotePrimaryHeader = true;
                }

                primarySum += r.PrimaryContrib;
                poolSum += r.PrimaryContrib;
                AppendPartMathLine(
                    sb,
                    r.PrimaryCount,
                    r.DisplayName,
                    r.AuthoredEach,
                    1f,
                    r.PrimaryContrib,
                    unitLabel,
                    highlight: true);
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
                    sb.AppendLine("<color=#C9A0FF>> EXTRAS</color> <color=#5B7A94>(each x own weight)</color>");
                    wroteExtraHeader = true;
                }

                extrasSum += r.ExtraContrib;
                poolSum += r.ExtraContrib;
                extraInstances += r.ExtraCount;
                AppendPartMathLine(
                    sb,
                    r.ExtraCount,
                    r.DisplayName,
                    r.AuthoredEach,
                    r.ExtraWeight,
                    r.ExtraContrib,
                    unitLabel,
                    highlight: false);
            }

            if (useStackWeight && !wroteExtraHeader)
                sb.AppendLine("<color=#888888>EXTRAS — none</color>");

            sb.AppendLine();
            if (useStackWeight && (primarySum > 0.0001f || extrasSum > 0.0001f))
            {
                sb.Append("Primary  +").Append(FDetail(primarySum)).Append(" ").Append(unitLabel);
                if (extraInstances > 0)
                {
                    sb.Append("  +  ").Append(extraInstances.ToString(CultureInfo.InvariantCulture))
                        .Append("× extras +").Append(FDetail(extrasSum)).Append(" ").Append(unitLabel);
                }

                sb.AppendLine();
            }

            sb.Append("<color=#5B7A94>POOL</color>  <color=#AAEEDD>").Append(FDetail(poolSum)).Append("</color> ")
                .Append(unitLabel).AppendLine();
        }

        /// <summary>
        /// One math line: <c>2× Name  base 12.0 ×10% = +2.4 Move</c>.
        /// </summary>
        static void AppendPartMathLine(
            StringBuilder sb,
            int count,
            string displayName,
            float authoredEach,
            float weight,
            float contribTotal,
            string unitLabel,
            bool highlight)
        {
            string countStr = count.ToString(CultureInfo.InvariantCulture) + "×";
            sb.Append("<mspace=0.58em>");
            sb.Append(PadRightPlain(countStr, 4));
            if (highlight)
                sb.Append("<color=#AAEEDD>").Append(PadRightPlain(displayName, 14)).Append("</color>");
            else
                sb.Append(PadRightPlain(displayName, 14));
            sb.Append(" base ").Append(PadLeftPlain(FDetail(authoredEach), 6));
            sb.Append(" ").Append(FormatWeight(weight));
            if (count > 1 && Mathf.Abs(weight - 1f) > 0.001f)
            {
                // Show per-extra then total: 2 × (12×10%) = +2.4
                float eachContrib = authoredEach * weight;
                sb.Append(" → ").Append(count.ToString(CultureInfo.InvariantCulture))
                    .Append("×").Append(FDetail(eachContrib));
            }

            sb.Append(" = +").Append(FDetail(contribTotal)).Append(" ").Append(unitLabel);
            sb.Append("</mspace>");
            sb.AppendLine();
        }

        /// <summary>+10% ability pipeline: parts → tier → ability × → equals chip.</summary>
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
            float growth = parts.Family != null
                ? parts.Family.ResolveShipLevelStatGrowthFraction()
                : ShipFamilyDefinition.DefaultShipLevelStatGrowthFraction;

            // Reconstruct pool from grid sum already printed — approximate tier from final/ability.
            float abilityMult = 1f + abilityLv * ShipAttributeUpgradeLogic.MultiplierPerLevel;
            float afterAbility = finalEffective;
            float afterTier = afterAbility / Mathf.Max(0.0001f, abilityMult);
            float poolEst = afterTier / Mathf.Max(0.0001f, 1f + perLvl * growth);

            // --- Pipeline block (tier → ability purchases) ---
            ShipStatTooltipChrome.AppendSectionBanner(sb, "PIPELINE", "7DFFB2");
            sb.Append("Ship Lv ").Append(shipLevel.ToString(CultureInfo.InvariantCulture));
            if (perLvl > 0)
            {
                sb.Append("  +").Append(F0(growth * 100f)).Append("% x").Append(perLvl.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -> ").Append(FDetail(afterTier)).AppendLine();
            }
            else
                sb.Append("  ").Append(FDetail(poolEst)).Append(" <color=#5B7A94>(no tier growth)</color>").AppendLine();

            sb.Append("Ability Lv").Append(abilityLv.ToString(CultureInfo.InvariantCulture));
            sb.Append("  x").Append(FDetail(abilityMult));
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
            sb.AppendLine("<color=#5B7A94>PRIMARY x100% of base; each EXTRA at xextraStackWeight. Accel + OD drain share stacking.</color>");
            AppendGroupedFieldGrid(sb, parts, StatField.MoveSpeed, "Move", useStackWeight: true, sectionTitle: "MOVE PARTS");
            AppendGroupedFieldGrid(sb, parts, StatField.AccelerationCap, "Accel", useStackWeight: true, sectionTitle: "ACCEL PARTS");

            // --- Mass tax: composition + drag on Move and Accel (replaces the old one-liner) ---
            ShipSpeedometerStatTooltips.AppendMassTaxEffectsBreakdown(
                sb, in live, includeMove: true, includeAccel: true);

            ShipStatTooltipChrome.AppendSectionBanner(sb, "LIVE FLIGHT", "7EC8FF");
            if (live.OverdriveCapacityMult > 1.001f)
                sb.Append("<color=#FFCC66>OVERDRIVE bar ").Append(FResult(live.BarMaxSpeed)).Append("</color>")
                    .AppendLine();

            float moveStep = live.MoveStepPreview;
            if (moveStep <= 0.0001f)
                moveStep = Mathf.Max(0f, parts.Propulsion.moveSpeedPerAbilityLevel);
            sb.Append("Purchased  Lv").Append(abilityLv.ToString(CultureInfo.InvariantCulture));
            sb.Append(" x +").Append(FDetail(moveStep)).Append(" Move/buy").AppendLine();
            sb.Append("Now  ").Append(FResult(live.CurrentSpeed))
                .Append(" / ").Append(FResult(live.LiveMaxSpeed)).AppendLine();
        }

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
            sb.Append("RAM live  ").Append(FDetail(live.RamRating))
                .Append(" x m").Append(FDetail(live.TotalMass))
                .Append(" x v").Append(FDetail(live.CurrentSpeed))
                .Append(" -> ast ").Append(FResult(live.RamAsteroidDamage))
                .Append("  hull ").Append(FResult(live.RamSelfDamage)).AppendLine();
        }

        static void AppendTurnMassTax(StringBuilder sb, in ShipSpeedometerStatTooltips.LiveContext live)
        {
            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            if (settings == null)
                return;
            float drag = live.TotalMass * settings.turnWeightPerMass;
            ShipStatTooltipChrome.AppendSectionBanner(sb, "LIVE", "7EC8FF");
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
