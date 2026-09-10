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
    /// Colour-coded calculation grids for the ten bottom Ship Ability chips.
    /// PARTS table: PRIMARY Base + each part’s own PerExtra × levels (extras add no Base).
    /// MASS TAX table (Move / Accel / Turn): gems / troops / hull → drag → chip.
    /// MEGA hulls skip Extra Level and show static catalog part sums (no +per-buy).
    /// Token colours are shared: violet = start scale, amber = part count N, steel = Primary,
    /// cyan = PerExtra, blue = shipLevel, green = ability, mint = total.
    /// Presentation-only — never writes ECS.
    /// <para>
    /// [TITAN-ORBIT] Intentionally <b>not</b> live: no per-frame HP/energy/speed/cargo vitals.
    /// <see cref="ShipAttributeUpgradeHUD"/> rebuilds these strings only when the ship changes
    /// or an ability is purchased — StringBuilder + TMP mesh work every frame was a major FPS hit.
    /// </para>
    /// <para>
    /// Rich text is shown inside <see cref="ShipStatTooltipChrome"/> (Shift sci-fi frame).
    /// Ends with a small RANK 1 footer from <see cref="ShipPowerBarStatCopy"/> so chips
    /// match the Orbit Menu power-bar hover.
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
        const string HexShip = "5B9BD5";    // shipLevel
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
            /// <summary>How many instances of this id are extras (PerExtra × levels only — no Base).</summary>
            public int ExtraCount;
            /// <summary>Stack pool this row belongs to (Cockpit, Wing, Propulsion, …).</summary>
            public string PoolKey;
            /// <summary>Scale-adjusted field Base (catalog × starting scale) for this id.</summary>
            public float AuthoredEach;
            /// <summary>Family-catalog value before prefab starting scale (0 when unknown).</summary>
            public float CatalogEach;
            /// <summary>
            /// Prefab start-scale multiplier for this field (1 = no mesh scale).
            /// Cockpit at localScale 3 → 3 on Health / Gems / Troops.
            /// </summary>
            public float ScaleFactor;
            /// <summary>[LEGACY] Unused — Extra Stack Weight retired (kept so older tip builders compile).</summary>
            public float ExtraWeight;
            /// <summary>Base contribution from instances marked primary.</summary>
            public float PrimaryContrib;
            /// <summary>Always 0 — extras never add Base (kept so older tip rows compile).</summary>
            public float ExtraContrib;

            /// <summary>Total Base contribution to the pool from this id (primary only).</summary>
            public float ContribTotal => PrimaryContrib + ExtraContrib;

            /// <summary>Total instance count of this id (each instance Extra-Levels with its own PerExtra).</summary>
            public int Count => PrimaryCount + ExtraCount;
        }

        /// <summary>
        /// Chip glance numbers: current effective value, next purchase step, ability level.
        /// Slot 0 (Fire Power) shows sustained DPS — <c>firePower × fireRate</c> — not
        /// damage per shot. The next-buy step is one Extra Level of Fire Power at the
        /// current rate (buying Fire Power does not raise Fire Rate).
        /// Called when the HUD snapshot key changes or a hover card opens — not every frame.
        /// </summary>
        /// <param name="abilityIndex">0–9 (Fire Power … Troop Cap).</param>
        /// <param name="live">Static chassis snapshot from the last HUD rebuild.</param>
        /// <param name="attrs">Bottom-bar Extra Level purchases.</param>
        /// <param name="value">Number painted on the chip (DPS for slot 0).</param>
        /// <param name="nextStep">Green +per-buy; 0 on MEGA or when maxed math is 0.</param>
        /// <param name="abilityLv">Purchased Extra Levels for this slot.</param>
        /// <param name="unitSuffix">TMP suffix on the current value (<c>/s</c>, <c>°/s</c>).</param>
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
                    // --- Fire Power chip = sustained DPS ---
                    // [TITAN-ORBIT] A NightAye cannon (~69 /hit at 0.5/s) and an AstroEagle
                    // gun (~12 /hit at 3/s) look similar as DPS. Raw Fire Power hid that.
                    // Chip paints "12.5 DPS/s" so it is not read as damage-per-hit.
                    unitSuffix = " DPS/s";
                    if (live.IsMega)
                    {
                        var mega = MegaShipCatalog.Load();
                        value = mega != null
                            ? mega.GetPowerBreakdown(live.MegaCatalogIndex).GetDisplayDps()
                            : ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(eff.firePower, eff.fireRate);
                        nextStep = 0f;
                    }
                    else if (live.AllGunDps > 0.0001f)
                    {
                        // Every mount Extra-Leveled, then FP × RoF, then summed.
                        value = live.AllGunDps;
                        nextStep = Mathf.Max(0f, live.AllGunDpsNextStep - live.AllGunDps);
                    }
                    else
                    {
                        float rate = Mathf.Max(0f, eff.fireRate);
                        value = ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(eff.firePower, rate);
                        nextStep = ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(
                            eff.firePowerPerExtraLevel, rate);
                    }
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
                    // Next purchase adds one Extra Level of every propulsion part’s own Move PerExtra.
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

            // MEGAs are not Extra-Level upgradable — hide the green +per-buy on every chip.
            if (live.IsMega)
                nextStep = 0f;
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
            if (live.IsMega)
            {
                AppendMegaAbilityCard(sb, abilityIndex, title, shortLabel, chipVal, unit, in parts, in live, in attrs);
                // Small RANK 1 — same catalog winner the Orbit Menu power bar shows.
                ShipPowerBarStatCopy.AppendRankOneFooter(sb, abilityIndex, megaPool: true, parts.ChassisId);
                return sb.Length > 0 ? sb.ToString() : "<color=#888888>No breakdown available</color>";
            }

            AppendHeader(sb, $"{shortLabel} — {title}", chipVal, unit, lv, maxLv, nextStep, abilityIndex == 6);

            switch (abilityIndex)
            {
                case 0:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.FirePower, "Fire Power", lv, live.EffectiveStats.firePower);
                    AppendRelatedFireExtras(sb, parts, live);
                    BulletBankHudCopy.AppendFullSection(sb, in live, in attrs);
                    break;
                case 1:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.BulletSpeed, "Bullet Speed", lv, live.EffectiveStats.bulletSpeed);
                    // Bullet range has no bottom-HUD ability — abilityLv forced to 0.
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.BulletRange, "Range", 0, live.EffectiveStats.bulletRange);
                    BulletBankHudCopy.AppendFullSection(sb, in live, in attrs);
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
                    BulletBankHudCopy.AppendFullSection(sb, in live, in attrs);
                    break;
                case 5:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.EnergyRegen, "Energy Regen", lv, live.EffectiveStats.energyRegen);
                    BulletBankHudCopy.AppendFullSection(sb, in live, in attrs);
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
                    break;
                case 8:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.MaxGems, "Gem Cap", lv, live.EffectiveStats.maxGems);
                    break;
                case 9:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.MaxPeople, "Troop Cap", lv, live.EffectiveStats.maxPeople);
                    break;
                default:
                    sb.AppendLine("<color=#888888>Unknown ability</color>");
                    break;
            }

            ShipPowerBarStatCopy.AppendRankOneFooter(sb, abilityIndex, megaPool: false, parts.ChassisId);
            return sb.Length > 0 ? sb.ToString() : "<color=#888888>No breakdown available</color>";
        }

        /// <summary>
        /// Groups parts that contribute to <paramref name="field"/>, collapsing identical ids.
        /// When <paramref name="useStackWeight"/> is true (legacy name), newest store extra
        /// is PRIMARY; every instance still shows its own Base.
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
        /// Pool grouping for Extra Level: newest store extra is PRIMARY; every instance
        /// still contributes its own Base + PerExtra (shown on both PRIMARY and EXTRAS lines).
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
                if (ReadField(parts.Stats[i], field) <= 0.0001f
                    && ReadPerExtraLevel(parts.Stats[i], field) <= 0.0001f)
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
                    pool.Key, pool.Value, parts.Stats, parts.StoreExtraStartIndex);
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
                        // Extra adds PerExtra only — Base stays on the primary row.
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
        /// Compact parts list: PRIMARY first, then extras (PerExtra only — no extra Bases).
        /// Prefer <see cref="AppendStatCalcGrid"/> for the colour-coded calculation table.
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

            // --- Other parts in the pool (own Base + own PerExtra) ---
            bool wroteExtra = false;
            for (int i = 0; i < rows.Count; i++)
            {
                GroupedPartRow r = rows[i];
                if (r.ExtraCount <= 0)
                    continue;
                if (!wroteExtra)
                {
                    sb.Append("> ");
                    AppendTint(sb, HexCount, "OTHER PARTS");
                    sb.Append(" ");
                    AppendTint(sb, HexMute, "(");
                    AppendTint(sb, HexPerExtra, "own PerExtra");
                    AppendTint(sb, HexMute, ")");
                    sb.AppendLine();
                    wroteExtra = true;
                }

                AppendPartLine(
                    sb, r.ExtraCount, r.DisplayName, r.AuthoredEach, unitLabel,
                    isExtra: false, r.CatalogEach, r.ScaleFactor, extraPoolKey: null);
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
        /// Colour-coded parts grid: PRIMARY Base + every part’s own PerExtra × levels.
        /// Mass-affected chips (Move / Accel / Turn) append a matching mass-tax grid.
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
            AppendStatCalcGrid(sb, in parts, in live, field, unitLabel, abilityLv, finalEffective);
            if (IsMassAffectedField(field))
                AppendMassTaxGrid(sb, in live, field, finalEffective, writeComposition: true);
        }

        const string GridMspace = "<mspace=0.5em>";
        const string GridMspaceEnd = "</mspace>";
        const int GridNameW = 13;
        const int GridRoleW = 4;
        const int GridNumW = 7;
        const int GridLvW = 5;

        /// <summary>
        /// Colour-coded parts table: PRIMARY Base + every part’s own PerExtra × levels.
        /// Extras show — in BASE (they never add a second Base). Weapon barrels keep Base.
        /// </summary>
        public static void AppendStatCalcGrid(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            StatField field,
            string unitLabel,
            int abilityLv,
            float finalEffective)
        {
            int shipLevel = Mathf.Max(1, live.Ship.ShipLevel);
            int shipSteps = Mathf.Max(0, shipLevel);
            var rows = new List<FieldPoolEval>(8);
            CollectFieldPools(in parts, field, shipLevel, abilityLv, rows);

            float unitScale = field == StatField.TurnSpeed
                ? ShipPropulsionAggregation.TurnDefinitionToDegreesPerSecond
                : 1f;

            ShipStatTooltipChrome.AppendSectionBanner(sb, "PARTS", "5B9BD5");
            sb.AppendLine(DescribeFormula(field, rows.Count > 1));

            if (rows.Count == 0)
            {
                AppendTint(sb, HexMute, "No contributing parts.");
                sb.AppendLine();
                AppendGridTotal(sb, finalEffective, unitLabel);
                return;
            }

            AppendGridHeader(sb, "PART", "ROLE", "BASE", "PERX", "×LV", "ADD");
            ShipStatTooltipChrome.AppendGridRule(sb);

            float running = 0f;
            bool bulletSpeedWeaponLevels = field == StatField.BulletSpeed;
            for (int i = 0; i < rows.Count; i++)
            {
                FieldPoolEval p = rows[i];
                float baseDisp = p.Primary * unitScale;
                float perDisp = p.PerExtra * unitScale;
                float addDisp = p.Evaluated * unitScale;
                running += addDisp;

                string name = p.PoolKey ?? "?";
                if (HasMeaningfulScale(p.Scale))
                    name = TrimGridName(name, GridNameW - 2) + "×" + FDetail(p.Scale);

                string role = p.IsWeaponPool
                    ? (p.IsDisplayPrimary ? "PRI" : "GUN")
                    : (p.IsDisplayPrimary ? "PRI" : "+X");
                string roleHex = p.IsDisplayPrimary ? HexPrimary : HexCount;
                string nameHex = p.IsDisplayPrimary ? HexPrimary : HexCount;
                string baseText = p.IncludeBase ? FGrid(baseDisp) : "—";
                string baseHex = p.IncludeBase ? HexPrimary : HexMute;

                AppendGridRowOpen(sb, name, nameHex, role, roleHex, baseText, baseHex, FGrid(perDisp), HexPerExtra);
                AppendGridLevelsCell(
                    sb,
                    bulletSpeedWeaponLevels && p.IsWeaponPool,
                    shipSteps,
                    abilityLv);
                AppendGridCell(sb, HexResult, FGrid(addDisp), GridNumW, right: true);
                sb.AppendLine();
            }

            float familyMul = ReadFamilyMul(parts.Family, field);
            if (Mathf.Abs(familyMul - 1f) > 0.01f)
            {
                ShipStatTooltipChrome.AppendGridRule(sb);
                float afterMul = running * familyMul;
                AppendGridRow(
                    sb,
                    "family", HexMute,
                    "×", HexMute,
                    FGrid(familyMul), HexMute,
                    "—", HexMute,
                    "—", HexMute,
                    FGrid(afterMul), HexResult);
                running = afterMul;
            }

            ShipStatTooltipChrome.AppendGridRule(sb);
            float shownTotal = IsMassAffectedField(field) ? running : finalEffective;
            if (!IsMassAffectedField(field) && Mathf.Abs(finalEffective - running) > 0.05f)
                shownTotal = finalEffective;
            AppendGridTotal(sb, shownTotal, unitLabel);
        }

        /// <summary>
        /// Colour-coded mass table: gems / troops / hull → totalMass → tax → after-tax chip.
        /// Pass <paramref name="writeComposition"/> false to add another tax row under an
        /// already-printed cargo breakdown (Move card prints Accel tax this way).
        /// </summary>
        public static void AppendMassTaxGrid(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.LiveContext live,
            StatField field,
            float afterTax,
            bool writeComposition = true)
        {
            if (writeComposition)
                ShipStatTooltipChrome.AppendSectionBanner(sb, "MASS TAX", HexMass);
            if (live.Motor.SkipMassTax != 0)
            {
                if (writeComposition)
                {
                    AppendTint(sb, HexResult, "MEGA hulls ignore mass tax.");
                    sb.AppendLine();
                }

                return;
            }

            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float mGem = settings != null ? settings.massPerGem : 0.01f;
            float mPerson = settings != null ? settings.massPerPerson : 0.15f;
            float mSize = settings != null ? settings.massPerComponentSize : 1f;
            float speedW = settings != null ? settings.speedWeightPerMass : 0.1f;
            float accelW = settings != null ? settings.accelWeightPerMass : 0.1f;
            float turnW = settings != null ? settings.turnWeightPerMass : 0.1f;

            float gemMass = live.Ship.CurrentGems * mGem;
            float peopleMass = live.Ship.CurrentPeople * mPerson;
            float sizeMass = live.ComponentSize * mSize;
            float totalMass = live.TotalMass > 0.0001f
                ? live.TotalMass
                : gemMass + peopleMass + sizeMass;

            if (writeComposition)
            {
                AppendGridHeader(sb, "SOURCE", "QTY", "RATE", "MASS", "", "");
                ShipStatTooltipChrome.AppendGridRule(sb);
                AppendGridRow(
                    sb, "Gems", HexCount,
                    FGrid(live.Ship.CurrentGems), HexCount,
                    FGrid(mGem), HexMute,
                    FGrid(gemMass), HexMass,
                    "", HexMute, "", HexMute);
                AppendGridRow(
                    sb, "Troops", HexCount,
                    FGrid(live.Ship.CurrentPeople), HexCount,
                    FGrid(mPerson), HexMute,
                    FGrid(peopleMass), HexMass,
                    "", HexMute, "", HexMute);
                AppendGridRow(
                    sb, "Hull", HexScale,
                    FGrid(live.ComponentSize), HexScale,
                    FGrid(mSize), HexMute,
                    FGrid(sizeMass), HexMass,
                    "", HexMute, "", HexMute);
                ShipStatTooltipChrome.AppendGridRule(sb);
                AppendGridRow(
                    sb, "totalMass", HexMute,
                    "—", HexMute,
                    "—", HexMute,
                    FGrid(totalMass), HexResult,
                    "", HexMute, "", HexMute);
            }

            float weight = field == StatField.MoveSpeed
                ? speedW
                : field == StatField.AccelerationCap
                    ? accelW
                    : turnW;
            // Turn tax is authored in definition units — convert so the grid matches °/s chips.
            float drag = field == StatField.TurnSpeed
                ? ShipMobilityResolution.ComputeTurnDragDegreesPerSecond(totalMass, turnW)
                : totalMass * weight;
            string taxLabel = field == StatField.MoveSpeed
                ? "speed tax"
                : field == StatField.AccelerationCap
                    ? "accel tax"
                    : "turn tax";

            AppendGridRow(
                sb, taxLabel, HexMass,
                FGrid(totalMass), HexResult,
                FGrid(weight), HexMass,
                "-" + FGrid(drag), HexMass,
                "", HexMute, "", HexMute);
            ShipStatTooltipChrome.AppendGridRule(sb);
            string totalUnit = field == StatField.TurnSpeed
                ? "°/s"
                : field == StatField.AccelerationCap
                    ? "Accel"
                    : field == StatField.MoveSpeed
                        ? "Move"
                        : "";
            AppendMassGridTotal(sb, afterTax, totalUnit);
        }

        static void AppendGridHeader(
            StringBuilder sb,
            string c0, string c1, string c2, string c3, string c4, string c5)
        {
            AppendGridCell(sb, HexMute, c0, GridNameW, right: false);
            AppendGridCell(sb, HexMute, c1, GridRoleW, right: true);
            AppendGridCell(sb, HexMute, c2, GridNumW, right: true);
            AppendGridCell(sb, HexMute, c3, GridNumW, right: true);
            if (!string.IsNullOrEmpty(c4))
                AppendGridCell(sb, HexMute, c4, GridLvW, right: true);
            if (!string.IsNullOrEmpty(c5))
                AppendGridCell(sb, HexMute, c5, GridNumW, right: true);
            sb.AppendLine();
        }

        static void AppendGridRow(
            StringBuilder sb,
            string n, string nHex,
            string r, string rHex,
            string a, string aHex,
            string b, string bHex,
            string c, string cHex,
            string d, string dHex)
        {
            AppendGridCell(sb, nHex, n, GridNameW, right: false);
            AppendGridCell(sb, rHex, r, GridRoleW, right: true);
            AppendGridCell(sb, aHex, a, GridNumW, right: true);
            AppendGridCell(sb, bHex, b, GridNumW, right: true);
            if (!string.IsNullOrEmpty(c))
                AppendGridCell(sb, cHex, c, GridLvW, right: true);
            if (!string.IsNullOrEmpty(d))
                AppendGridCell(sb, dHex, d, GridNumW, right: true);
            sb.AppendLine();
        }

        static void AppendGridCell(StringBuilder sb, string hex, string text, int width, bool right)
        {
            string raw = text ?? string.Empty;
            if (raw.Length > width)
                raw = raw.Substring(0, width);
            string padded = right ? raw.PadLeft(width) : raw.PadRight(width);
            sb.Append("<color=#").Append(hex).Append('>')
                .Append(GridMspace).Append(padded).Append(GridMspaceEnd)
                .Append("</color>");
        }

        static void AppendGridTotal(StringBuilder sb, float total, string unitLabel)
        {
            AppendGridCell(sb, HexMute, "TOTAL", GridNameW, right: false);
            AppendGridCell(sb, HexMute, "", GridRoleW, right: true);
            AppendGridCell(sb, HexMute, "", GridNumW, right: true);
            AppendGridCell(sb, HexMute, "", GridNumW, right: true);
            AppendGridCell(sb, HexMute, "", GridLvW, right: true);
            AppendGridCell(sb, HexResult, FGrid(total), GridNumW, right: true);
            if (!string.IsNullOrEmpty(unitLabel))
            {
                sb.Append(" ");
                AppendTint(sb, HexMute, unitLabel);
            }

            sb.AppendLine();
        }

        /// <summary>Mass-table total aligned under the MASS column (4 columns, not 6).</summary>
        static void AppendMassGridTotal(StringBuilder sb, float total, string unitLabel)
        {
            AppendGridCell(sb, HexMute, "TOTAL", GridNameW, right: false);
            AppendGridCell(sb, HexMute, "", GridRoleW, right: true);
            AppendGridCell(sb, HexMute, "", GridNumW, right: true);
            AppendGridCell(sb, HexResult, FGrid(total), GridNumW, right: true);
            if (!string.IsNullOrEmpty(unitLabel))
            {
                sb.Append(" ");
                AppendTint(sb, HexMute, unitLabel);
            }

            sb.AppendLine();
        }

        /// <summary>First four columns of a parts row; caller writes ×LV + ADD.</summary>
        static void AppendGridRowOpen(
            StringBuilder sb,
            string n, string nHex,
            string r, string rHex,
            string a, string aHex,
            string b, string bHex)
        {
            AppendGridCell(sb, nHex, n, GridNameW, right: false);
            AppendGridCell(sb, rHex, r, GridRoleW, right: true);
            AppendGridCell(sb, aHex, a, GridNumW, right: true);
            AppendGridCell(sb, bHex, b, GridNumW, right: true);
        }

        /// <summary>
        /// ×LV cell: blue shipLevel + green ability (weapon bullet speed is ability only).
        /// </summary>
        static void AppendGridLevelsCell(
            StringBuilder sb,
            bool abilityOnly,
            int shipSteps,
            int abilityLv)
        {
            if (abilityOnly)
            {
                AppendGridCell(sb, HexAbility, abilityLv.ToString(CultureInfo.InvariantCulture), GridLvW, right: true);
                return;
            }

            string ship = shipSteps.ToString(CultureInfo.InvariantCulture);
            string ab = abilityLv.ToString(CultureInfo.InvariantCulture);
            string visible = ship + "+" + ab;
            int pad = Mathf.Max(0, GridLvW - visible.Length);
            if (pad > 0)
                sb.Append(GridMspace).Append(new string(' ', pad)).Append(GridMspaceEnd);
            sb.Append("<color=#").Append(HexShip).Append('>')
                .Append(GridMspace).Append(ship).Append(GridMspaceEnd)
                .Append("</color>");
            sb.Append("<color=#").Append(HexMute).Append('>')
                .Append(GridMspace).Append('+').Append(GridMspaceEnd)
                .Append("</color>");
            sb.Append("<color=#").Append(HexAbility).Append('>')
                .Append(GridMspace).Append(ab).Append(GridMspaceEnd)
                .Append("</color>");
        }

        static string FGrid(float v) =>
            v.ToString("0.##", CultureInfo.InvariantCulture);

        static string TrimGridName(string name, int max)
        {
            if (string.IsNullOrEmpty(name) || name.Length <= max)
                return name ?? string.Empty;
            return name.Substring(0, max);
        }

        /// <summary>
        /// One Extra Level line for a chip field. Each contributing part is its own line
        /// (engine PerExtra ≠ thruster PerExtra). Gem Cap still sums Cockpit + Wing.
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
            public bool IsDisplayPrimary;
            /// <summary>True when this row adds Base (primary, or every weapon barrel).</summary>
            public bool IncludeBase;
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
        /// One-line caption above the parts grid. Extra Bases are never in this formula.
        /// </summary>
        static string DescribeFormula(StatField field, bool multiPool)
        {
            _ = multiPool;
            string primary = Tint(HexPrimary, "Primary Base");
            string perExtra = Tint(HexPerExtra, "PerExtra");
            string levels = field == StatField.BulletSpeed
                ? Tint(HexAbility, "ability")
                : Tint(HexMute, "(") + Tint(HexShip, "ship") + Tint(HexMute, "+") + Tint(HexAbility, "ability") + Tint(HexMute, ")");
            return primary
                   + Tint(HexMute, " + Σ ")
                   + perExtra
                   + Tint(HexMute, " × ")
                   + levels;
        }

        /// <summary>
        /// Builds one eval per contributing part. Only the pool primary (and every weapon
        /// barrel) includes Base; extras are PerExtra × levels. Matches
        /// <see cref="ShipComponentExtraLevelMath.AggregateAndEvaluate"/>.
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

            // --- Group so we can mark the newest store extra as the display primary ---
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

            var primaryGlobals = new HashSet<int>();
            foreach (KeyValuePair<string, List<int>> pair in groups)
            {
                int primaryLocal = ShipComponentStackAggregation.PickPrimaryLocalIndex(
                    pair.Key, pair.Value, parts.Stats, parts.StoreExtraStartIndex);
                primaryGlobals.Add(pair.Value[primaryLocal]);
            }

            foreach (KeyValuePair<string, List<int>> pair in groups)
            {
                bool isWeapon = ShipComponentStackAggregation.IsWeaponPoolKey(pair.Key);
                for (int m = 0; m < pair.Value.Count; m++)
                {
                    int gi = pair.Value[m];
                    if (gi < 0 || gi >= parts.Stats.Count)
                        continue;

                    // Cache stats are already catalog × starting scale (same as the motor).
                    ShipComponentAbilityStats s = parts.Stats[gi];
                    float primary = ReadField(s, field);
                    float perExtra = ReadPerExtraLevel(s, field);
                    if (primary <= 0.0001f && perExtra <= 0.0001f)
                        continue;

                    string id = gi < parts.Ids.Count ? parts.Ids[gi] : string.Empty;
                    bool includeBase = isWeapon || primaryGlobals.Contains(gi);
                    int levels = CountPoolLevels(isWeapon, field, shipLevel, abilityLv, 1);
                    float evaluated = EvaluatePoolField(
                        isWeapon, field, primary, perExtra, shipLevel, abilityLv, 1, includeBase);
                    float scale = ShipComponentAbilityStatsMath.GetScaleMultiplier(
                        ReadLocalScale(in parts, gi), id, ToScaleChannel(field));
                    string label = ResolvePartName(parts.Family, id);
                    if (string.IsNullOrWhiteSpace(label))
                        label = pair.Key;

                    into.Add(new FieldPoolEval
                    {
                        PoolKey = label,
                        Scale = scale,
                        Primary = includeBase ? primary : 0f,
                        PerExtra = perExtra,
                        ComponentCount = 1,
                        Levels = levels,
                        Evaluated = evaluated,
                        IsWeaponPool = isWeapon,
                        IsDisplayPrimary = primaryGlobals.Contains(gi),
                        IncludeBase = includeBase
                    });
                }
            }

            into.Sort((a, b) =>
            {
                if (a.IsDisplayPrimary != b.IsDisplayPrimary)
                    return a.IsDisplayPrimary ? -1 : 1;
                return b.Evaluated.CompareTo(a.Evaluated);
            });
        }

        /// <summary>
        /// Extra Level steps for one part. Weapon bullet speed is ability only.
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
            int componentCount,
            bool includeBase)
        {
            if (isWeaponPool && field == StatField.BulletSpeed)
            {
                return ShipComponentExtraLevelMath.EvaluateWeaponBulletSpeed(
                    includeBase ? primary : 0f, perExtra, abilityLv);
            }

            return ShipComponentExtraLevelMath.Evaluate(
                primary,
                perExtra,
                shipLevel,
                abilityLv,
                componentCount,
                includeExtraComponentLevels: !isWeaponPool,
                includeBase: includeBase);
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
            AppendStatCalcGrid(sb, in parts, in live, StatField.MoveSpeed, "Move", abilityLv, live.CruiseMaxSpeed);
            AppendStatCalcGrid(sb, in parts, in live, StatField.AccelerationCap, "Accel", abilityLv, live.TaxedAccel);
            AppendMassTaxGrid(sb, in live, StatField.MoveSpeed, live.CruiseMaxSpeed, writeComposition: true);
            AppendMassTaxGrid(sb, in live, StatField.AccelerationCap, live.TaxedAccel, writeComposition: false);

            if (live.OverdriveCapacityMult > 1.001f)
            {
                ShipStatTooltipChrome.AppendSectionBanner(sb, "OVERDRIVE", "FFCC66");
                sb.Append("<color=#FFCC66>Bar max  ").Append(FResult(live.BarMaxSpeed)).Append("</color>")
                    .AppendLine();
            }

            float moveStep = live.MoveStepPreview;
            if (moveStep <= 0.0001f)
                moveStep = Mathf.Max(0f, parts.Propulsion.moveSpeedPerExtraLevel);
            AppendTint(sb, HexAbility, "NEXT BUY");
            sb.Append("  +");
            AppendTint(sb, HexPerExtra, FDetail(moveStep));
            AppendTint(sb, HexMute, " Move (Σ PerExtra)");
            sb.AppendLine();
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
            // Chip / power-bar Fire Power lane uses this product (DPS), not /hit alone.
            float dps = live.Weapon.BulletDamage * live.Weapon.FireRate;
            float chipDps = live.AllGunDps > 0.0001f
                ? live.AllGunDps
                : ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(
                    live.EffectiveStats.firePower, live.EffectiveStats.fireRate);
            sb.Append("Chip DPS  ").Append(FResult(chipDps)).Append("/s  ")
                .Append("<color=#5B7A94>(all guns)</color>").AppendLine();
            sb.Append("Hull avg  ").Append(FResult(live.Weapon.BulletDamage)).Append("/hit  ");
            sb.Append(FResult(dps)).Append("/s  ");
            sb.Append("<color=#5B7A94>").Append(FResult(live.Weapon.FireRate)).Append("/s</color>").AppendLine();

            AppendStatCalcGrid(sb, in parts, in live, StatField.RammingPower, "RAM", 0, live.EffectiveStats.rammingPower);

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

        /// <summary>
        /// Static mass-tax drag on turn (from the last chip/tip snapshot).
        /// Uses the same ×10 definition→°/s scale as drive so the line matches the chip.
        /// </summary>
        static void AppendTurnMassTax(StringBuilder sb, in ShipSpeedometerStatTooltips.LiveContext live)
        {
            // --- MEGA: no cargo tax on yaw ---
            if (live.Motor.SkipMassTax != 0)
            {
                ShipStatTooltipChrome.AppendSectionBanner(sb, "MASS TAX", HexMass);
                AppendTint(sb, HexMass, "MEGA hulls ignore mass tax.");
                sb.AppendLine();
                return;
            }

            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            if (settings == null)
                return;

            // [TITAN-ORBIT] turnWeight is definition units; drag must be °/s like the chip.
            float drag = ShipMobilityResolution.ComputeTurnDragDegreesPerSecond(
                live.TotalMass, settings.turnWeightPerMass);
            ShipStatTooltipChrome.AppendSectionBanner(sb, "MASS TAX", HexMass);
            AppendTint(sb, HexMass, "Mass turn drag  -" + FDetail(drag) + "/s");
            sb.AppendLine();
        }

        /// <summary>
        /// MEGA details card: catalog part sums only. No Extra Level, no Lv / +next,
        /// no ability purchases. Cruise speed uses fastest engine/thruster + extra%.
        /// </summary>
        static void AppendMegaAbilityCard(
            StringBuilder sb,
            int abilityIndex,
            string title,
            string shortLabel,
            float chipVal,
            string unit,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs)
        {
            _ = parts;
            // --- Readout (no purchase language) ---
            ShipStatTooltipChrome.AppendSectionBanner(sb, "READOUT", "7EC8FF");
            sb.Append("<b><color=#E8F4FF>").Append(shortLabel).Append(" — ").Append(title)
                .Append("</color></b>").AppendLine();
            sb.Append("<size=125%>");
            AppendTint(sb, HexResult, FResult(chipVal));
            sb.Append("</size>");
            if (!string.IsNullOrEmpty(unit))
                AppendTint(sb, HexMute, unit);
            sb.AppendLine();
            AppendTint(sb, HexMute, "MEGA hull — static catalog (not Extra Level)");
            sb.AppendLine();

            StatField field = abilityIndex switch
            {
                0 => StatField.FirePower,
                1 => StatField.BulletSpeed,
                2 => StatField.HealthCap,
                3 => StatField.HealthRegen,
                4 => StatField.EnergyCap,
                5 => StatField.EnergyRegen,
                6 => StatField.MoveSpeed,
                7 => StatField.TurnSpeed,
                8 => StatField.MaxGems,
                9 => StatField.MaxPeople,
                _ => StatField.FirePower
            };

            if (abilityIndex == 8)
            {
                // Gem cap is forced to 0 on every MEGA (MegaShipStatsCalculator).
                ShipStatTooltipChrome.AppendSectionBanner(sb, "CATALOG", HexMute);
                AppendTint(sb, HexMute, "MEGA hulls cannot carry gems.");
                sb.AppendLine();
                AppendTotalLine(sb, 0f, unit);
                return;
            }

            if (abilityIndex == 6)
            {
                AppendMegaMoveCard(sb, in live, chipVal);
                return;
            }

            AppendMegaCatalogParts(sb, in live, field, unit);
            AppendTotalLine(sb, chipVal, unit);

            if (abilityIndex == 0)
            {
                // Chip / power bar use per-gun catalog DPS, not summed-rate × summed-damage.
                ShipStatTooltipChrome.AppendSectionBanner(sb, "RELATED", "FFAA66");
                var mega = MegaShipCatalog.Load();
                float chipDps = mega != null
                    ? mega.GetPowerBreakdown(live.MegaCatalogIndex).GetDisplayDps()
                    : ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(
                        live.EffectiveStats.firePower, live.EffectiveStats.fireRate);
                sb.Append("Chip DPS  ").Append(FResult(chipDps)).Append("/s").AppendLine();
                float dps = live.Weapon.BulletDamage * live.Weapon.FireRate;
                sb.Append("Hull avg  ").Append(FResult(live.Weapon.BulletDamage)).Append("/hit  ");
                sb.Append(FResult(dps)).Append("/s").AppendLine();
                BulletBankHudCopy.AppendFullSection(sb, in live, in attrs);
            }
            else if (abilityIndex == 1 || abilityIndex == 4 || abilityIndex == 5)
            {
                if (abilityIndex == 1)
                    AppendMegaCatalogParts(sb, in live, StatField.BulletRange, "Range");
                BulletBankHudCopy.AppendFullSection(sb, in live, in attrs);
            }
            else if (abilityIndex == 7)
            {
                AppendTurnMassTax(sb, live);
            }
        }

        /// <summary>
        /// Lists unique MEGA parts that contribute <paramref name="field"/>, grouped by name.
        /// Values are raw catalog numbers — hull defaults/minimums apply only to the TOTAL.
        /// </summary>
        static void AppendMegaCatalogParts(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.LiveContext live,
            StatField field,
            string unitLabel)
        {
            ShipStatTooltipChrome.AppendSectionBanner(sb, "PARTS", "5B9BD5");
            var catalog = MegaShipCatalog.Load();
            if (catalog == null
                || !catalog.TryGetEntry(live.MegaCatalogIndex, out MegaShipCatalogEntry entry)
                || entry?.componentCounts == null)
            {
                sb.AppendLine("<color=#5B7A94>No catalog parts.</color>");
                return;
            }

            bool wrote = false;
            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry unique)
                    || unique == null)
                    continue;

                float each = ReadMegaField(unique.stats, field);
                if (each <= 0.0001f)
                    continue;

                wrote = true;
                AppendTint(sb, HexCount, count.count.ToString(CultureInfo.InvariantCulture) + "×");
                sb.Append(" ").Append(count.displayName).Append("  ");
                AppendTint(sb, HexResult, FDetail(each));
                if (!string.IsNullOrEmpty(unitLabel))
                {
                    sb.Append(" ");
                    AppendTint(sb, HexMute, unitLabel);
                }

                if (count.count > 1)
                {
                    sb.Append("  ");
                    AppendTint(sb, HexMute, "→ ");
                    AppendTint(sb, HexResult, FResult(each * count.count));
                }

                sb.AppendLine();
            }

            if (!wrote)
                sb.AppendLine("<color=#5B7A94>No contributing parts.</color>");
        }

        /// <summary>
        /// MEGA cruise: fastest Engine/Thruster + extra% of the rest — same as
        /// <see cref="MegaShipComponentInventory.CombineEngineCruise"/>.
        /// </summary>
        static void AppendMegaMoveCard(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.LiveContext live,
            float chipVal)
        {
            ShipStatTooltipChrome.AppendSectionBanner(sb, "MOVE PARTS", "5B9BD5");
            var catalog = MegaShipCatalog.Load();
            if (catalog == null
                || !catalog.TryGetEntry(live.MegaCatalogIndex, out MegaShipCatalogEntry entry)
                || entry?.componentCounts == null)
            {
                sb.AppendLine("<color=#5B7A94>No catalog parts.</color>");
                AppendTotalLine(sb, chipVal, "Move");
                return;
            }

            float extraPercent = catalog.GetExtraEngineSpeedPercent();
            var moves = new List<float>(8);
            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry unique)
                    || unique == null)
                    continue;
                if (!ShipFamilyPartTypes.IsPropulsion(unique.partType))
                    continue;
                if (unique.stats.moveSpeed <= 0.0001f)
                    continue;

                for (int n = 0; n < count.count; n++)
                    moves.Add(unique.stats.moveSpeed);

                AppendTint(sb, HexCount, count.count.ToString(CultureInfo.InvariantCulture) + "×");
                sb.Append(" ").Append(count.displayName).Append("  ");
                AppendTint(sb, HexResult, FDetail(unique.stats.moveSpeed));
                sb.Append(" ");
                AppendTint(sb, HexMute, unique.partType);
                sb.AppendLine();
            }

            ShipStatTooltipChrome.AppendSectionBanner(sb, "CRUISE", "7DFFB2");
            AppendTint(sb, HexMute, "fastest + ");
            AppendTint(sb, HexPerExtra, (extraPercent * 100f).ToString("0.##", CultureInfo.InvariantCulture) + "%");
            AppendTint(sb, HexMute, " of other engines/thrusters");
            sb.AppendLine();
            float combined = MegaShipComponentInventory.CombineEngineCruise(moves, extraPercent);
            if (combined > 0.0001f)
            {
                AppendTint(sb, HexMute, "raw  ");
                AppendTint(sb, HexResult, FResult(combined));
                sb.AppendLine();
            }

            AppendMegaCatalogParts(sb, in live, StatField.AccelerationCap, "Accel");
            AppendTurnMassTax(sb, live);
            AppendTotalLine(sb, chipVal, "Move");
        }

        /// <summary>Reads one HUD field from a raw MEGA unique-component block.</summary>
        static float ReadMegaField(in MegaShipPartStats s, StatField field) =>
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
                StatField.MaxGems => 0f,
                StatField.MaxPeople => s.maxPeople,
                StatField.AccelerationCap => s.accelerationCap,
                StatField.BulletRange => s.bulletRange,
                StatField.RammingPower => s.rammingPower,
                _ => 0f
            };

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
            if (string.IsNullOrWhiteSpace(componentId))
                return false;

            // Foreign moon-store parts live on another family’s catalog row.
            ShipComponentAbilityStats catalog;
            if (!ShipFamilyStatsCalculator.TryResolveComponentStats(family, componentId, out catalog))
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

            if (BulletBankProfileUtility.TryFindComponentInAnyFamily(componentId, out ShipFamilyComponentEntry any)
                && any != null)
                return ShipComponentStoreData.GetDisplayName(any);

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
