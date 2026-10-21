using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Simulation;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Telemetry-style calculation cards for the ten bottom Ship Ability chips.
    /// Colour-coded calculation grids for the ten bottom Ship Ability chips.
    /// PARTS table: one row per component. BASE and PERX are catalog numbers.
    /// The SCALE column is that part’s prefab size (including ×1) and multiplies the row.
    /// The BONUS column is this match’s family roll for that stat: a green +% adds,
    /// an amber −% reduces, and ×1 means the roll does not change the ship.
    /// The same roll is printed on every part — it is per family, not per component.
    /// Extras add no Base. There is no second line under a part for scale.
    /// MASS TAX table (Move / Accel / Turn): gems / troops / hull → drag → chip.
    /// MEGA hulls skip Extra Level on catalog parts and show static unique-component
    /// sums (no +per-buy). Equipped moon-store gear adds PerExtra × shipLevel only
    /// (no Base) and appears in a GEAR section on Titan cards.
    /// Those Titan numbers include the purchase planet's fleet roll (home world ×1,
    /// every other family this match's bell curve). The MEGA part cache has no family
    /// asset, so the roll is <see cref="MegaShipStatsCalculator.ResolveFleetBonuses"/>
    /// on <c>ShipState.ShipFamilyConfigIndex</c> — the same multiply the motor applies.
    /// The Fire Power card prints rate of fire in the DPS product so a parts total
    /// (damage per shot) and the chip (damage per second) multiply in view.
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
        /// <summary>
        /// Formula-line colour for the word "fleet". The percent cell uses the
        /// stat's own colour. Stock stays muted ×1.
        /// </summary>
        const string HexFleet = "E6C36A";

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
                        // Each cycled gun uses the selected bullet type's damage and
                        // rate. Catalog DPS stays only when that sum cannot be built.
                        value = SumMegaSelectedDps(in live);
                        if (value <= 0.0001f)
                        {
                            var mega = MegaShipCatalog.Load();
                            value = mega != null
                                ? mega.GetPowerBreakdown(live.MegaCatalogIndex).GetDisplayDps()
                                : ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(eff.firePower, eff.fireRate);
                            if (live.AllGunDps > 0.0001f)
                                value = live.AllGunDps;
                        }

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
                    unitSuffix = "deg/s";
                    nextStep = Mathf.Max(0f, eff.turnSpeedPerExtraLevel);
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

            AppendHeader(sb, $"{shortLabel} - {title}", chipVal, unit, lv, maxLv, nextStep, abilityIndex == 6);

            switch (abilityIndex)
            {
                case 0:
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.FirePower, "Fire Power", lv, live.EffectiveStats.firePower);
                    AppendFireDpsEquation(sb, in parts, in live, lv);
                    AppendRelatedFireExtras(sb, parts, live, lv);
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
                    AppendTenPercentPipeline(sb, parts, live, attrs, StatField.TurnSpeed, "deg/s", lv, turnLive);
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
        /// When <paramref name="useStackWeight"/> is true (legacy name), the highest base
        /// of that type is PRIMARY; every other instance is an extra.
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
        /// Pool grouping for Extra Level: highest base of the type is PRIMARY; every other
        /// instance contributes PerExtra (shown on the EXTRAS line).
        /// </summary>
        static void CollectStackedGroupedRows(
            in ShipSpeedometerStatTooltips.PartCache parts,
            StatField field,
            List<GroupedPartRow> into)
        {
            ShipPropulsionAggregation.ClassifyPropulsionRoles(
                parts.Ids, out bool hasEngines, out bool hasThrusters);
            ShipFamilySpecialBonuses hull = parts.Family != null
                ? parts.Family.ResolveSpecialBonuses()
                : ShipFamilySpecialBonuses.Identity;
            ShipFamilySpecialBonuses[] fleets = BuildPartFleets(in parts, in hull);
            var pools = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (ReadField(parts.Stats[i], field) <= 0.0001f
                    && ReadPerExtraLevel(parts.Stats[i], field) <= 0.0001f)
                    continue;

                if (field == StatField.MoveSpeed
                    && !ShipPropulsionAggregation.ContributesMoveSpeed(id, hasEngines))
                    continue;
                if (field == StatField.AccelerationCap
                    && !ShipPropulsionAggregation.ContributesAcceleration(id, hasThrusters))
                    continue;

                string key = ShipComponentStackAggregation.ResolveStackPoolKey(id);

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
                int primaryLocal = PickDisplayedPrimaryLocal(
                    pool.Key, pool.Value, in parts, fleets);
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
                sb.AppendLine("<color=#888888>PRIMARY - none</color>");

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
        /// One part row: <c>1× Cockpit  10 × scale 3 = 30 Health</c>.
        /// Every part prints its prefab scale, including ×1, so a unit-size part
        /// is visible next to a cockpit that is three times catalog.
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
            AppendTint(sb, HexCount, count.ToString(CultureInfo.InvariantCulture) + "x");
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
                // Scale is always printed. A ×1 part still shows the factor so it
                // is not mistaken for a part whose size was left out of the card.
                float shownScale = scaleFactor > 0.0001f ? scaleFactor : 1f;
                sb.Append("  ");
                if (catalogEach > 0.0001f)
                {
                    AppendTint(sb, HexPrimary, FDetail(catalogEach));
                    sb.Append(" ");
                    AppendTint(sb, HexMute, "× scale ");
                    AppendTint(sb, HexScale, FDetail(shownScale));
                    sb.Append(" = ");
                    AppendTint(sb, HexResult, FDetail(authoredEach));
                }
                else
                {
                    AppendTint(sb, HexResult, FDetail(authoredEach));
                    sb.Append("  ");
                    AppendTint(sb, HexMute, "scale ");
                    AppendTint(sb, HexScale, "×" + FDetail(shownScale));
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
        /// Width of the SCALE column on the parts grid. Wide enough for <c>×1.875</c>.
        /// Mass-tax tables do not use this column.
        /// </summary>
        const int GridScaleW = 7;

        /// <summary>
        /// Width of the fleet BONUS column. Wide enough for <c>+100%</c>, <c>-50%</c>,
        /// or stock <c>×1</c>, with room so BONUS does not sit against SCALE.
        /// Mass-tax tables skip it.
        /// </summary>
        const int GridFleetW = 11;

        /// <summary>PART ROLE BASE PERX xLV SCALE BONUS ADD, in monospace characters.</summary>
        const int PartsGridColumns =
            GridNameW + GridRoleW + GridNumW + GridNumW + GridLvW + GridScaleW + GridFleetW + GridNumW;

        /// <summary>Mass-tax grid: SOURCE QTY RATE MASS. No level, scale, or bonus columns.</summary>
        const int MassGridColumns = GridNameW + GridRoleW + GridNumW + GridNumW;

        /// <summary>
        /// Colour-coded parts table. One row per component — scale is a column, not a second line.
        /// BASE and PERX are family-catalog values (before prefab size). SCALE is that part’s
        /// prefab multiplier, including ×1. BONUS is that row’s roll: chassis rows share
        /// the hull, and a purchased part shows the moon it was bought at. ADD is
        /// <c>(Base + PerExtra × levels) × scale × fleet</c>.
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
            // --- Fleet roll for this stat ---
            // Chassis rows share the hull roll. A purchased part uses the moon
            // it was bought at, so an engine from another fleet can read +15%
            // while the rest of the ship stays on its own roll.
            ShipFamilySpecialBonuses hullFleet = live.IsMega
                ? MegaShipStatsCalculator.ResolveFleetBonuses(live.Ship.ShipFamilyConfigIndex)
                : (parts.Family != null
                    ? parts.Family.ResolveSpecialBonuses()
                    : ShipFamilySpecialBonuses.Identity);
            CollectFieldPools(in parts, field, shipLevel, abilityLv, rows, in hullFleet);

            ShipStatTooltipChrome.AppendSectionBanner(sb, "PARTS", "5B9BD5");
            sb.AppendLine(DescribeFormula(field, rows.Count > 1));

            if (rows.Count == 0)
            {
                AppendTint(sb, HexMute, "No contributing parts.");
                sb.AppendLine();
                AppendGridTotal(sb, finalEffective, unitLabel);
                return;
            }

            AppendPartsCalcHeader(sb);
            ShipStatTooltipChrome.AppendGridRule(sb, PartsGridColumns);

            // --- Fleet roll for this stat ---
            // [TITAN-ORBIT] Home planets stay 1×. Every other family is rolled once
            // per match in [0.5, 2]. Chassis rows share that hull roll. A purchased
            // part shows the moon it was bought at, and ADD already includes it.
            // Summing those ADD cells equals the motor — the percents do not stack
            // onto parts that did not buy them.

            float running = 0f;
            bool bulletSpeedWeaponLevels = field == StatField.BulletSpeed;
            for (int i = 0; i < rows.Count; i++)
            {
                FieldPoolEval p = rows[i];
                // Cache stats are already catalog × prefab scale. Split them back apart
                // so the row shows both factors and ADD is their product with levels and fleet.
                float scale = p.Scale > 0.0001f ? p.Scale : 1f;
                float catalogBase = p.Primary / scale;
                float catalogPer = p.PerExtra / scale;
                float addDisp = p.Evaluated * (p.FleetMul > 0.0001f ? p.FleetMul : 1f);
                running += addDisp;

                // Name cell is 13 characters. Scale and fleet stay in their own columns
                // so a long part name cannot clip either factor off the end of the row.
                string name = p.PoolKey ?? "?";

                string role = p.IsWeaponPool
                    ? (p.IsDisplayPrimary ? "PRI" : "GUN")
                    : (p.IsDisplayPrimary ? "PRI" : "+X");
                string roleHex = p.IsDisplayPrimary ? HexPrimary : HexCount;
                string nameHex = p.IsDisplayPrimary ? HexPrimary : HexCount;
                string baseText = p.IncludeBase ? FGrid(catalogBase) : "-";
                string baseHex = p.IncludeBase ? HexPrimary : HexMute;

                AppendGridRowOpen(sb, name, nameHex, role, roleHex, baseText, baseHex, FGrid(catalogPer), HexPerExtra);
                AppendGridLevelsCell(
                    sb,
                    bulletSpeedWeaponLevels && p.IsWeaponPool,
                    shipSteps,
                    abilityLv);
                AppendGridCell(sb, HexScale, FormatScaleColumn(scale), GridScaleW, right: true);
                AppendFleetBonusCell(sb, p.FleetMul > 0.0001f ? p.FleetMul : 1f, field);
                AppendGridCell(sb, HexResult, FGrid(addDisp), GridNumW, right: true);
                sb.AppendLine();
            }

            ShipStatTooltipChrome.AppendGridRule(sb, PartsGridColumns);
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
                    AppendTint(sb, HexResult, "Titan hulls ignore mass tax.");
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
                ShipStatTooltipChrome.AppendGridRule(sb, MassGridColumns);
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
                ShipStatTooltipChrome.AppendGridRule(sb, MassGridColumns);
                AppendGridRow(
                    sb, "totalMass", HexMute,
                    "-", HexMute,
                    "-", HexMute,
                    FGrid(totalMass), HexResult,
                    "", HexMute, "", HexMute);
            }

            float weight = field == StatField.MoveSpeed
                ? speedW
                : field == StatField.AccelerationCap
                    ? accelW
                    : turnW;
            // Turn weight is already °/s, so mass × weight is the yaw drag on the chip.
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
            ShipStatTooltipChrome.AppendGridRule(sb, MassGridColumns);
            string totalUnit = field == StatField.TurnSpeed
                ? "deg/s"
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

        /// <summary>
        /// Parts-grid total. Blank cells keep the number under ADD, including SCALE and BONUS.
        /// Mass tax uses <see cref="AppendMassGridTotal"/> so its four columns stay put.
        /// </summary>
        static void AppendGridTotal(StringBuilder sb, float total, string unitLabel)
        {
            AppendGridCell(sb, HexMute, "TOTAL", GridNameW, right: false);
            AppendGridCell(sb, HexMute, "", GridRoleW, right: true);
            AppendGridCell(sb, HexMute, "", GridNumW, right: true);
            AppendGridCell(sb, HexMute, "", GridNumW, right: true);
            AppendGridCell(sb, HexMute, "", GridLvW, right: true);
            // Blank SCALE and BONUS cells keep the total under ADD.
            AppendGridCell(sb, HexMute, "", GridScaleW, right: true);
            AppendGridCell(sb, HexMute, "", GridFleetW, right: true);
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

        /// <summary>First four columns of a parts row; caller writes ×LV, SCALE, BONUS, then ADD.</summary>
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

        /// <summary>
        /// Parts-grid header: PART ROLE BASE PERX xLV SCALE BONUS ADD.
        /// SCALE is the prefab multiplier for that row. BONUS is the match roll
        /// for this stat. Mass-tax tables keep their own header.
        /// </summary>
        static void AppendPartsCalcHeader(StringBuilder sb)
        {
            AppendGridCell(sb, HexMute, "PART", GridNameW, right: false);
            AppendGridCell(sb, HexMute, "ROLE", GridRoleW, right: true);
            AppendGridCell(sb, HexMute, "BASE", GridNumW, right: true);
            AppendGridCell(sb, HexMute, "PERX", GridNumW, right: true);
            AppendGridCell(sb, HexMute, "xLV", GridLvW, right: true);
            AppendGridCell(sb, HexMute, "SCALE", GridScaleW, right: true);
            AppendGridCell(sb, HexMute, "BONUS", GridFleetW, right: true);
            AppendGridCell(sb, HexMute, "ADD", GridNumW, right: true);
            sb.AppendLine();
        }

        /// <summary>
        /// One fleet BONUS cell. <paramref name="mul"/> is the match roll already
        /// resolved for this stat (1 when the family does not change it).
        /// The percent uses that stat's own colour. Muted <c>×1</c> is stock.
        /// </summary>
        static void AppendFleetBonusCell(StringBuilder sb, float mul, StatField field)
        {
            FormatFleetBonus(mul, HexForStatField(field), out string text, out string hex);
            AppendGridCell(sb, hex, text, GridFleetW, right: true);
        }

        /// <summary>
        /// Player-facing fleet roll. Signed percent so an add and a reduction
        /// do not both look like a raw multiplier. Stock stays <c>×1</c>, same
        /// idea as the SCALE column always printing ×1.
        /// </summary>
        /// <param name="mul">Resolved multiplier. Zero or negative is treated as 1.</param>
        /// <param name="typeHex">That stat's own colour. Stock ×1 stays muted.</param>
        /// <param name="text">Cell text (<c>+12.5%</c>, <c>-20%</c>, or <c>×1</c>).</param>
        /// <param name="hex">TMP colour without the leading <c>#</c>.</param>
        static void FormatFleetBonus(float mul, string typeHex, out string text, out string hex)
        {
            float m = mul > 0.0001f ? mul : 1f;
            float signed = (m - 1f) * 100f;
            // Same dead-zone as FamilyStatHudCopy.FormatSignedPercent so a 1.000×
            // roll does not flicker between ×1 and +0%.
            if (signed > 0.05f)
            {
                text = "+" + signed.ToString("0.#", CultureInfo.InvariantCulture) + "%";
                hex = string.IsNullOrEmpty(typeHex) ? FamilyStatHudCopy.HexMuted : typeHex;
                return;
            }

            if (signed < -0.05f)
            {
                text = "-" + Mathf.Abs(signed).ToString("0.#", CultureInfo.InvariantCulture) + "%";
                hex = string.IsNullOrEmpty(typeHex) ? FamilyStatHudCopy.HexMuted : typeHex;
                return;
            }

            text = "×1";
            hex = FamilyStatHudCopy.HexMuted;
        }

        /// <summary>
        /// Power-bar colour for one chip field. Fire Power stays the light offense
        /// orange; the other stats keep the slot they already use on the hull card.
        /// </summary>
        static string HexForStatField(StatField field)
        {
            int index = field switch
            {
                StatField.FirePower => 0,
                StatField.BulletSpeed => 1,
                StatField.HealthCap => 2,
                StatField.HealthRegen => 3,
                StatField.EnergyCap => 4,
                StatField.EnergyRegen => 5,
                StatField.MoveSpeed => 6,
                StatField.TurnSpeed => 7,
                StatField.MaxGems => 8,
                StatField.MaxPeople => 9,
                StatField.AccelerationCap => 6,
                StatField.BulletRange => 1,
                StatField.RammingPower => 0,
                _ => 0
            };
            return ColorUtility.ToHtmlStringRGB(ShipStatPalette.GetStatColor(index));
        }

        /// <summary>
        /// Fire-rate fleet roll under the DPS caption. The parts table above is
        /// damage per shot, so its BONUS column cannot also be cadence.
        /// Prints even at ×1 so a stock rate is visible next to a damage bonus.
        /// Called only when the Fire Power details card rebuilds — not every frame.
        /// Titans read the purchase planet via <see cref="ResolveMegaFleet"/> —
        /// their part cache has no family asset, so <c>parts.Family</c> would always
        /// print ×1 while the shots-per-second number already includes the roll.
        /// A family or gear bullet that brings its own rate prints ×1, matching the shot.
        /// </summary>
        static void AppendFireRateFleetLine(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live)
        {
            // --- Which roll this cadence actually used ---
            float rateMul = 1f;
            if (live.IsMega)
            {
                bool replacesRate = TryReadSelectedFamilyGun(
                        in live, out _, out _, out _, out _, out float familyRate)
                    && familyRate > 0.01f;
                if (!replacesRate)
                    rateMul = ReadFireRateMul(ResolveMegaFleet(in live));
            }
            else if (parts.Family != null)
            {
                rateMul = ReadFireRateMul(parts.Family.ResolveSpecialBonuses());
            }

            // Fire Rate uses the darker offense slot, same as Bullet Speed on the hull card.
            FormatFleetBonus(
                rateMul,
                ColorUtility.ToHtmlStringRGB(ShipStatPalette.GetStatColor(1)),
                out string text,
                out string hex);
            AppendTint(sb, HexFleet, "FLEET BONUS");
            AppendTint(sb, HexMute, "  fire rate  ");
            AppendTint(sb, hex, text);
            sb.AppendLine();
        }

        /// <summary>
        /// Scale cell text. Always includes ×1 so a unit-size part is visible next to a ×3 cockpit.
        /// </summary>
        static string FormatScaleColumn(float scale)
        {
            float shown = scale > 0.0001f ? scale : 1f;
            return "×" + FEq(shown);
        }

        /// <summary>
        /// One Extra Level line for a chip field. Each contributing part is its own line
        /// (engine PerExtra ≠ thruster PerExtra). Gem Cap still sums Cockpit + Wing.
        /// </summary>
        struct FieldPoolEval
        {
            public string PoolKey;
            /// <summary>
            /// Prefab factor already inside Primary and PerExtra.
            /// The SCALE column divides it back out so the row can show catalog × scale.
            /// </summary>
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
            /// <summary>
            /// Fleet multiplier for this row only. Chassis rows share the hull roll.
            /// A purchased part uses the family slot stamped at purchase. 0 is treated as 1.
            /// </summary>
            public float FleetMul;
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
            string primary = Tint(HexPrimary, "Base");
            string perExtra = Tint(HexPerExtra, "PerExtra");
            string scale = Tint(HexScale, "scale");
            string fleet = Tint(HexFleet, "fleet");
            string levels = field == StatField.BulletSpeed
                ? Tint(HexAbility, "ability")
                : Tint(HexMute, "(") + Tint(HexShip, "ship") + Tint(HexMute, "+") + Tint(HexAbility, "ability") + Tint(HexMute, ")");
            // Same multiply the SCALE and BONUS columns apply on every part row.
            return Tint(HexMute, "(")
                   + primary
                   + Tint(HexMute, " + ")
                   + perExtra
                   + Tint(HexMute, " × ")
                   + levels
                   + Tint(HexMute, ") × ")
                   + scale
                   + Tint(HexMute, " × ")
                   + fleet;
        }

        /// <summary>
        /// One fleet roll per cached part. A purchase stamp is that moon.
        /// −1, or a missing stamp, is the hull roll already resolved by the caller.
        /// </summary>
        static ShipFamilySpecialBonuses[] BuildPartFleets(
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipFamilySpecialBonuses hullFleet)
        {
            int n = parts.Stats != null ? parts.Stats.Count : 0;
            var fleets = new ShipFamilySpecialBonuses[n];
            for (int i = 0; i < n; i++)
            {
                int stamped = -1;
                if (parts.ComponentFleetFamilyIndex != null && i < parts.ComponentFleetFamilyIndex.Count)
                    stamped = parts.ComponentFleetFamilyIndex[i];
                fleets[i] = stamped >= 0
                    ? MegaShipStatsCalculator.ResolveFleetBonuses(stamped)
                    : hullFleet;
            }

            return fleets;
        }

        /// <summary>
        /// Display primary for one pool. Weapons keep the unbuffed pick so a firepower
        /// roll does not reshuffle gun rows. Every other pool lets a purchase own the
        /// seat even when its base is lower than the hull. Among those purchases the
        /// higher fleet-adjusted score wins.
        /// </summary>
        static int PickDisplayedPrimaryLocal(
            string poolKey,
            List<int> members,
            in ShipSpeedometerStatTooltips.PartCache parts,
            ShipFamilySpecialBonuses[] fleets)
        {
            if (ShipComponentStackAggregation.IsWeaponPoolKey(poolKey))
            {
                return ShipComponentStackAggregation.PickPrimaryLocalIndex(
                    poolKey, members, parts.Stats, parts.StoreExtraStartIndex);
            }

            return ShipComponentStackAggregation.PickPrimaryLocalIndex(
                poolKey, members, parts.Stats, parts.StoreExtraStartIndex, fleets);
        }

        /// <summary>
        /// Builds one eval per contributing part. Only the pool primary (and every weapon
        /// barrel) includes Base; extras are PerExtra × levels. A fleet bonus takes that
        /// primary seat only when its buffed score beats the unbuffed primary. Turn uses
        /// one hull-wide Base — the highest <c>turnSpeed</c>, with the same bonus rule —
        /// and every other turn part is PerExtra only.
        /// Matches <see cref="ShipComponentExtraLevelMath.AggregateAndEvaluate"/>.
        /// </summary>
        static void CollectFieldPools(
            in ShipSpeedometerStatTooltips.PartCache parts,
            StatField field,
            int shipLevel,
            int abilityLv,
            List<FieldPoolEval> into,
            in ShipFamilySpecialBonuses hullFleet)
        {
            into.Clear();
            if (!parts.Valid || parts.Ids == null || parts.Stats == null)
                return;

            ShipFamilySpecialBonuses[] fleets = BuildPartFleets(in parts, in hullFleet);

            // --- Group so the highest base of each type is the display primary ---
            ShipPropulsionAggregation.ClassifyPropulsionRoles(
                parts.Ids, out bool hasEngines, out bool hasThrusters);
            var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parts.Ids.Count && i < parts.Stats.Count; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (field == StatField.MoveSpeed
                    && !ShipPropulsionAggregation.ContributesMoveSpeed(id, hasEngines))
                    continue;
                if (field == StatField.AccelerationCap
                    && !ShipPropulsionAggregation.ContributesAcceleration(id, hasThrusters))
                    continue;

                string key = ShipComponentStackAggregation.ResolveStackPoolKey(id);

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
                int primaryLocal = PickDisplayedPrimaryLocal(
                    pair.Key, pair.Value, in parts, fleets);
                primaryGlobals.Add(pair.Value[primaryLocal]);
            }

            // Turn Base is one part. A purchase that authors turn owns it.
            int bestTurnIndex = field == StatField.TurnSpeed
                ? ShipComponentExtraLevelMath.PickBestTurnBaseIndex(
                    parts.Ids, parts.Stats, parts.StoreExtraStartIndex, fleets)
                : -1;

            foreach (KeyValuePair<string, List<int>> pair in groups)
            {
                bool isWeapon = ShipComponentStackAggregation.IsWeaponPoolKey(pair.Key);
                for (int m = 0; m < pair.Value.Count; m++)
                {
                    int gi = pair.Value[m];
                    if (gi < 0 || gi >= parts.Stats.Count)
                        continue;

                    // Bought guns are bullet types, not extra Fire Power on the DPS chip.
                    if (isWeapon
                        && gi >= parts.StoreExtraStartIndex
                        && field == StatField.FirePower)
                        continue;

                    // Cache stats are already catalog × starting scale (same as the motor).
                    ShipComponentAbilityStats s = parts.Stats[gi];
                    float primary = ReadField(s, field);
                    float perExtra = ReadPerExtraLevel(s, field);
                    if (primary <= 0.0001f && perExtra <= 0.0001f)
                        continue;

                    string id = gi < parts.Ids.Count ? parts.Ids[gi] : string.Empty;
                    bool includeBase = field == StatField.TurnSpeed
                        ? gi == bestTurnIndex
                        : isWeapon || primaryGlobals.Contains(gi);
                    bool displayPrimary = field == StatField.TurnSpeed
                        ? includeBase
                        : primaryGlobals.Contains(gi);
                    int levels = CountPoolLevels(isWeapon, field, shipLevel, abilityLv, 1);
                    float evaluated = EvaluatePoolField(
                        isWeapon, field, primary, perExtra, shipLevel, abilityLv, 1, includeBase);
                    float scale = ShipComponentAbilityStatsMath.GetScaleMultiplier(
                        ReadLocalScale(in parts, gi), id, ToScaleChannel(field));
                    string label = ResolvePartName(parts.Family, id);
                    if (string.IsNullOrWhiteSpace(label))
                        label = pair.Key;

                    int stampedFamily = -1;
                    if (parts.ComponentFleetFamilyIndex != null && gi < parts.ComponentFleetFamilyIndex.Count)
                        stampedFamily = parts.ComponentFleetFamilyIndex[gi];
                    float fleetMul = stampedFamily >= 0
                        ? ReadBonusFieldMul(MegaShipStatsCalculator.ResolveFleetBonuses(stampedFamily), field)
                        : ReadBonusFieldMul(hullFleet, field);

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
                        IsDisplayPrimary = displayPrimary,
                        IncludeBase = includeBase,
                        FleetMul = fleetMul
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

        /// <summary>
        /// Family multiplier for this field. During a match, home planets are 1×
        /// and other planets use the seed roll. Unset values become 1.
        /// Regular hulls pass the chassis family. Titans pass
        /// <see cref="ResolveMegaFleet"/> instead — their part cache leaves Family null.
        /// </summary>
        static float ReadFamilyMul(ShipFamilyDefinition family, StatField field)
        {
            if (family == null)
                return 1f;

            return ReadBonusFieldMul(family.ResolveSpecialBonuses(), field);
        }

        /// <summary>
        /// Fleet bonuses for the planet a Titan was bought on.
        /// Home slot is 1×. Every other family is this match's roll.
        /// Same call as <see cref="MegaShipStatsCalculator.ApplyFleetBonuses"/>,
        /// so the card and the motor share one number.
        /// </summary>
        static ShipFamilySpecialBonuses ResolveMegaFleet(in ShipSpeedometerStatTooltips.LiveContext live)
        {
            return MegaShipStatsCalculator.ResolveFleetBonuses(live.Ship.ShipFamilyConfigIndex);
        }

        /// <summary>
        /// One Titan stat's fleet multiplier. Zero or negative authored rolls become 1
        /// so an unset asset cannot wipe the card.
        /// </summary>
        static float ReadMegaFleetMul(in ShipSpeedometerStatTooltips.LiveContext live, StatField field)
        {
            return ReadBonusFieldMul(ResolveMegaFleet(in live), field);
        }

        /// <summary>
        /// Fire-rate roll. Not a <see cref="StatField"/> — cadence has its own multiplier
        /// next to fire power. Unset becomes 1.
        /// </summary>
        static float ReadFireRateMul(in ShipFamilySpecialBonuses bonuses)
        {
            return bonuses.fireRateMul > 0.0001f ? bonuses.fireRateMul : 1f;
        }

        /// <summary>
        /// Maps one chip field onto the family's authored multiplier.
        /// Shared by regular hulls and Titan cards so both print the same roll.
        /// </summary>
        static float ReadBonusFieldMul(in ShipFamilySpecialBonuses bonuses, StatField field)
        {
            float m = field switch
            {
                StatField.FirePower => bonuses.firePowerMul,
                StatField.BulletSpeed => bonuses.bulletSpeedMul,
                StatField.HealthCap => bonuses.healthCapMul,
                StatField.HealthRegen => bonuses.healthRegenMul,
                StatField.EnergyCap => bonuses.energyCapMul,
                StatField.EnergyRegen => bonuses.energyRegenMul,
                StatField.MoveSpeed => bonuses.moveSpeedMul,
                StatField.TurnSpeed => bonuses.turnSpeedMul,
                StatField.MaxGems => bonuses.maxGemsMul,
                StatField.MaxPeople => bonuses.maxPeopleMul,
                StatField.AccelerationCap => bonuses.accelerationMul,
                StatField.BulletRange => bonuses.bulletRangeMul,
                StatField.RammingPower => bonuses.rammingMul,
                _ => 1f
            };
            return m > 0.0001f ? m : 1f;
        }

        /// <summary>
        /// Fleet percent beside a Titan catalog number, then the product the row adds.
        /// <paramref name="each"/> is one copy before the roll. <paramref name="copies"/>
        /// is how many of those copies add (1 when the hull keeps the fastest, not the sum).
        /// The percent is per family, so it does not stack — each row's arrow is that row only.
        /// ×1 with a single copy prints the percent and skips the arrow, because the catalog
        /// number is already the add.
        /// </summary>
        static void AppendMegaFleetProduct(
            StringBuilder sb, float each, int copies, float fleetMul, StatField field)
        {
            float mul = fleetMul > 0.0001f ? fleetMul : 1f;
            int n = copies > 0 ? copies : 1;
            sb.Append(' ');
            FormatFleetBonus(mul, HexForStatField(field), out string text, out string hex);
            AppendTint(sb, hex, text);
            if (n > 1 || Mathf.Abs(mul - 1f) > 0.0005f)
            {
                sb.Append("  ");
                AppendTint(sb, HexMute, "-> ");
                AppendTint(sb, HexResult, FResult(each * n * mul));
            }
        }

        static void AppendMoveAbilityCard(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs,
            int abilityLv)
        {
            _ = attrs;
            float moveCruise = live.CruiseMaxSpeed;
            float accelCruise = live.TaxedAccel;
            if (live.TerritoryMult > 1.001f)
            {
                moveCruise /= live.TerritoryMult;
                accelCruise /= live.TerritoryMult;
            }

            AppendStatCalcGrid(sb, in parts, in live, StatField.MoveSpeed, "Move", abilityLv, moveCruise);
            AppendStatCalcGrid(sb, in parts, in live, StatField.AccelerationCap, "Accel", abilityLv, accelCruise);
            AppendMassTaxGrid(sb, in live, StatField.MoveSpeed, moveCruise, writeComposition: true);
            AppendMassTaxGrid(sb, in live, StatField.AccelerationCap, accelCruise, writeComposition: false);

            if (live.TerritoryMult > 1.001f)
            {
                sb.Append("Friendly territory  x").Append(FResult(live.TerritoryMult))
                    .Append("  -> ").Append(FResult(live.CruiseMaxSpeed)).AppendLine();
            }

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
            AppendTint(sb, HexMute, " Move (sum PerExtra)");
            sb.AppendLine();
        }

        /// <summary>
        /// One chassis barrel used to explain Fire Power × rate of fire.
        /// Fire Power and rate are after family scalars and before the bullet-type
        /// rate multiplier. A Titan row already folds copy-count into Fire Power.
        /// <see cref="XyScale"/> is average |X|+|Y| (damage and bullet size).
        /// <see cref="RateScale"/> is 1/|Z| (shots per second). Both are 1 when
        /// this row has no prefab scale (catalog Titans).
        /// </summary>
        struct DpsGunRow
        {
            public string Name;
            public float FirePower;
            public float FireRate;
            public float XyScale;
            public float RateScale;
            public float AxisZ;
            public bool HasPrefabScale;
        }

        /// <summary>
        /// DPS block under the Fire Power parts total.
        /// The chip is damage per second; the grid total above it is damage per shot.
        /// This prints weapon scale, then
        /// <c>Fire Power × rate of fire × this weapon's rate = DPS</c>
        /// so a 3.75 Fire Power and a 7.31 chip are one multiplication.
        /// Prefab weapon scale is the lever combat uses: average |X|+|Y| multiplies
        /// damage, and the bullet grows with that damage. 1/|Z| multiplies fire rate
        /// (Z 2 is half rate, Z 0.9 is faster than 1). Those factors are already inside
        /// the parts total — this block shows the multiply instead of leaving it baked in.
        /// The bullet-type factor is the Rate token on the Multipliers line
        /// (Plasma, Fireballs, …). It is omitted when that type does not change cadence.
        /// Called only when the Fire Power details card rebuilds — not every frame.
        /// </summary>
        /// <param name="firePowerAbilityLv">Bottom-HUD Fire Power purchases. Mega passes 0.</param>
        static void AppendFireDpsEquation(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            int firePowerAbilityLv)
        {
            // --- Numbers the player already sees ---
            // Grid TOTAL is damage per shot. The readout / chip is all-gun DPS.
            // Titans replace the catalog product with the selected bullet type:
            // that type's damage and its own shots per second, per gun barrel.
            float firePower = Mathf.Max(0f, live.EffectiveStats.firePower);
            float dps = live.AllGunDps > 0.0001f
                ? live.AllGunDps
                : ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(
                    firePower, live.EffectiveStats.fireRate);
            if (live.IsMega)
            {
                SumMegaSelectedVolley(in live, out float volleyDamage, out float selectedDps);
                if (selectedDps > 0.0001f)
                {
                    firePower = volleyDamage > 0.01f ? volleyDamage : firePower;
                    dps = selectedDps;
                }
            }

            if (firePower <= 0.01f || dps <= 0.01f)
                return;

            // Bullet type (Plasma, …). Rate ×1 is identity — the product skips it.
            // Fire Power on the grid already includes this type's fire-power scalar.
            float weaponRate = 1f;
            float weaponFp = 1f;
            string weaponName = "Weapon";
            if (BulletBankCombatLogic.TryGetProfile(live.FireBankIndex, out BulletBankProfile profile)
                && profile != null)
            {
                weaponRate = BulletBankCombatLogic.SafeMul(profile.statModifiers.fireRateMultiplier);
                weaponFp = BulletBankCombatLogic.SafeMul(profile.statModifiers.firePowerMultiplier);
                var bank = BulletBankCombatLogic.Bank;
                if (bank != null)
                {
                    string named = bank.GetCategoryName(live.FireBankIndex);
                    if (!string.IsNullOrEmpty(named))
                        weaponName = named;
                }
            }

            if (weaponRate <= 0.0001f)
                weaponRate = 1f;

            // Titan volley numbers already include this bullet type's damage and rate.
            // Leaving the profile multipliers here would apply them a second time.
            if (live.IsMega)
            {
                weaponRate = 1f;
                weaponFp = 1f;
            }

            // Hull shots/s before the bullet type. Multiplying back by the weapon
            // rate returns the cadence that turns Fire Power into the chip.
            float liveRate = dps / firePower;
            float hullRate = liveRate / weaponRate;

            var guns = new List<DpsGunRow>(4);
            CollectDpsGuns(in parts, in live, firePowerAbilityLv, guns);
            bool ratesDiffer = GunsHaveDifferentRates(guns);
            bool scalesDiffer = GunsHaveDifferentScales(guns);
            // One shared barrel size can be factored out of the hull totals.
            // Mixed sizes stay on their own rows so we never divide by the wrong axis.
            float xyScale = 1f;
            float rateScale = 1f;
            float axisZ = 1f;
            bool haveScale = false;
            if (guns.Count > 0 && guns[0].HasPrefabScale && !scalesDiffer)
            {
                xyScale = guns[0].XyScale > 0.0001f ? guns[0].XyScale : 1f;
                rateScale = guns[0].RateScale > 0.0001f ? guns[0].RateScale : 1f;
                axisZ = guns[0].AxisZ > 0.0001f ? guns[0].AxisZ : 1f;
                haveScale = true;
            }
            else if (guns.Count == 0)
            {
                haveScale = TryReadSharedWeaponScale(in parts, out xyScale, out rateScale, out axisZ);
            }

            ShipStatTooltipChrome.AppendSectionBanner(sb, "DPS", "FFAA66");
            AppendTint(sb, HexMute, "Fire Power  ×  rate of fire");
            if (live.IsMega && !string.IsNullOrEmpty(weaponName))
            {
                AppendTint(sb, HexMute, "  ·  ");
                AppendTint(sb, "FFAA66", weaponName);
            }
            else if (!Mathf.Approximately(weaponRate, 1f))
            {
                AppendTint(sb, HexMute, "  ×  ");
                AppendTint(sb, "FFAA66", weaponName);
                AppendTint(sb, HexMute, " rate");
            }

            sb.AppendLine();
            // The parts grid's BONUS column is damage per shot. Fire rate is a
            // second match roll and it changes this DPS product, so it is named here.
            AppendFireRateFleetLine(sb, in parts, in live);

            // --- Weapon scale (prefab localScale on the barrel) ---
            // XY fattens the hit and the bullet. Z divides cadence. Unit scale is
            // still printed so a ×1 barrel is visible next to rate of fire.
            if (haveScale && !scalesDiffer)
                AppendWeaponScaleLine(sb, xyScale, axisZ, rateScale);
            else if (scalesDiffer)
            {
                AppendTint(sb, HexMute, "Weapon scale differs per barrel.");
                sb.AppendLine();
            }

            // Per-barrel lines when cadence or size is not one shared number.
            // Fire Power here includes the bullet type's damage scalar.
            if (ratesDiffer || scalesDiffer)
            {
                AppendTint(sb, HexMute, "Each barrel. The rate below is weighted by Fire Power.");
                sb.AppendLine();
                for (int i = 0; i < guns.Count; i++)
                    AppendGunScaleRow(sb, guns[i], weaponFp);
            }

            // Factor XY out of damage and 1/Z out of cadence when every barrel shares them.
            float damageBeforeScale = firePower;
            float rateBeforeScale = hullRate;
            if (haveScale && !scalesDiffer)
            {
                if (!Mathf.Approximately(xyScale, 1f))
                    damageBeforeScale = firePower / xyScale;
                if (!Mathf.Approximately(rateScale, 1f))
                    rateBeforeScale = hullRate / rateScale;
            }

            if (haveScale && !scalesDiffer && !Mathf.Approximately(xyScale, 1f))
            {
                AppendTint(sb, HexMute, "Fire Power  ");
                AppendTint(sb, HexPrimary, FEq(damageBeforeScale));
                AppendTint(sb, HexMute, "  ×  XY ");
                AppendTint(sb, HexScale, FEq(xyScale));
                AppendTint(sb, HexMute, "  =  ");
                AppendTint(sb, HexResult, FResult(firePower));
                sb.AppendLine();
            }

            // Rate of fire: catalog cadence × Z scale, then the bullet type when it is not ×1.
            AppendTint(sb, HexMute, "Rate of fire  ");
            if (haveScale && !scalesDiffer && !Mathf.Approximately(rateScale, 1f))
            {
                AppendTint(sb, HexPrimary, FEq(rateBeforeScale));
                AppendTint(sb, HexMute, "/s  ×  ");
                AppendTint(sb, HexScale, FEq(rateScale));
                AppendTint(sb, HexMute, "  =  ");
                AppendTint(sb, HexResult, FResult(hullRate));
                AppendTint(sb, HexMute, "/s");
            }
            else
            {
                AppendTint(sb, HexResult, FResult(hullRate));
                AppendTint(sb, HexMute, "/s");
            }

            if (!Mathf.Approximately(weaponRate, 1f))
            {
                AppendTint(sb, HexMute, "  ×  ");
                AppendTint(sb, "FFAA66", weaponName);
                sb.Append(" ");
                AppendTint(sb, HexResult, FResult(weaponRate));
                AppendTint(sb, HexMute, "  =  ");
                AppendTint(sb, HexResult, FResult(liveRate));
                AppendTint(sb, HexMute, "/s");
            }

            sb.AppendLine();

            AppendTint(sb, HexMute, "Fire Power  ");
            AppendTint(sb, HexResult, FResult(firePower));
            AppendTint(sb, HexMute, "  ×  ");
            if (Mathf.Approximately(weaponRate, 1f) && (scalesDiffer || !haveScale || Mathf.Approximately(rateScale, 1f)))
                AppendTint(sb, HexMute, "Rate of fire  ");
            AppendTint(sb, HexResult, FResult(liveRate));
            AppendTint(sb, HexMute, "/s  =  ");
            AppendTint(sb, HexResult, FResult(dps));
            AppendTint(sb, HexMute, " DPS/s");
            sb.AppendLine();
        }

        /// <summary>
        /// One legend line: XY changes damage and bullet size, Z changes fire rate.
        /// Matches <see cref="ShipComponentAbilityStatsMath.GetWeaponFirePowerScale"/>
        /// and <see cref="ShipComponentAbilityStatsMath.GetWeaponFireRateScale"/>.
        /// </summary>
        static void AppendWeaponScaleLine(StringBuilder sb, float xyScale, float axisZ, float rateScale)
        {
            AppendTint(sb, HexMute, "Weapon scale  XY ");
            AppendTint(sb, HexScale, FEq(xyScale));
            AppendTint(sb, HexMute, "  Z ");
            AppendTint(sb, HexScale, FEq(axisZ));
            sb.AppendLine();
            AppendTint(sb, HexMute, "XY  damage ×");
            AppendTint(sb, HexScale, FEq(xyScale));
            AppendTint(sb, HexMute, "  bullet size ×");
            AppendTint(sb, HexScale, FEq(xyScale));
            AppendTint(sb, HexMute, "    Z  fire rate ×");
            AppendTint(sb, HexScale, FEq(rateScale));
            sb.AppendLine();
        }

        /// <summary>
        /// One barrel whose size or cadence is not the hull's shared number.
        /// Shows that barrel's XY and Z, then damage and rate with the scale multiplied in.
        /// </summary>
        static void AppendGunScaleRow(StringBuilder sb, DpsGunRow gun, float weaponFp)
        {
            float xy = gun.XyScale > 0.0001f ? gun.XyScale : 1f;
            float rateScale = gun.RateScale > 0.0001f ? gun.RateScale : 1f;
            float shownFp = gun.FirePower * weaponFp;
            float shownRate = gun.FireRate;

            AppendTint(sb, HexCount, string.IsNullOrEmpty(gun.Name) ? "Gun" : gun.Name);
            sb.Append("  ");
            if (gun.HasPrefabScale)
            {
                AppendTint(sb, HexMute, "XY ");
                AppendTint(sb, HexScale, FEq(xy));
                AppendTint(sb, HexMute, "  Z ");
                AppendTint(sb, HexScale, FEq(gun.AxisZ > 0.0001f ? gun.AxisZ : 1f));
                sb.Append("  ");
            }

            if (gun.HasPrefabScale && !Mathf.Approximately(xy, 1f))
            {
                AppendTint(sb, HexPrimary, FEq(shownFp / xy));
                AppendTint(sb, HexMute, " × ");
                AppendTint(sb, HexScale, FEq(xy));
                AppendTint(sb, HexMute, " = ");
            }

            AppendTint(sb, HexResult, FResult(shownFp));

            AppendTint(sb, HexMute, "   ");
            if (gun.HasPrefabScale && !Mathf.Approximately(rateScale, 1f))
            {
                AppendTint(sb, HexPrimary, FEq(shownRate / rateScale));
                AppendTint(sb, HexMute, " × ");
                AppendTint(sb, HexScale, FEq(rateScale));
                AppendTint(sb, HexMute, " = ");
            }

            AppendTint(sb, HexResult, FResult(shownRate));
            AppendTint(sb, HexMute, "/s");
            sb.AppendLine();
        }

        /// <summary>
        /// Reads the first chassis barrel's prefab scale when the DPS gun list is empty.
        /// False when no weapon transform was cached.
        /// </summary>
        static bool TryReadSharedWeaponScale(
            in ShipSpeedometerStatTooltips.PartCache parts,
            out float xyScale,
            out float rateScale,
            out float axisZ)
        {
            xyScale = 1f;
            rateScale = 1f;
            axisZ = 1f;
            if (!parts.Valid || parts.Ids == null)
                return false;

            int n = parts.Ids.Count;
            int chassisCount = parts.StoreExtraStartIndex >= 0
                ? Mathf.Min(n, parts.StoreExtraStartIndex)
                : n;
            for (int i = 0; i < chassisCount; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || !ShipComponentAbilityStatsMath.IsWeaponComponent(id))
                    continue;

                ReadWeaponScale(ReadLocalScale(in parts, i), out xyScale, out rateScale, out axisZ);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Splits a barrel <c>localScale</c> the way
        /// <see cref="ShipComponentAbilityStatsMath.ScaleStatsByTransform"/> does.
        /// </summary>
        static void ReadWeaponScale(Vector3 localScale, out float xyScale, out float rateScale, out float axisZ)
        {
            xyScale = ShipComponentAbilityStatsMath.GetWeaponFirePowerScale(localScale);
            rateScale = ShipComponentAbilityStatsMath.GetWeaponFireRateScale(localScale);
            axisZ = Mathf.Abs(localScale.z);
            if (xyScale <= 0.0001f)
                xyScale = 1f;
            if (rateScale <= 0.0001f)
                rateScale = 1f;
            if (axisZ <= 0.0001f)
                axisZ = 1f;
        }

        /// <summary>
        /// True when two barrels do not share a shots-per-second value.
        /// A damage-weighted average still closes the DPS line; these rows show why.
        /// </summary>
        static bool GunsHaveDifferentRates(List<DpsGunRow> guns)
        {
            if (guns == null || guns.Count < 2)
                return false;

            float first = guns[0].FireRate;
            for (int i = 1; i < guns.Count; i++)
            {
                if (Mathf.Abs(guns[i].FireRate - first) > 0.02f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when two barrels do not share XY (damage / bullet size) or Z (fire rate).
        /// A shared scale can be one multiply on the hull totals. Mixed scales cannot.
        /// </summary>
        static bool GunsHaveDifferentScales(List<DpsGunRow> guns)
        {
            if (guns == null || guns.Count < 2 || !guns[0].HasPrefabScale)
                return false;

            float xy = guns[0].XyScale;
            float rate = guns[0].RateScale;
            for (int i = 1; i < guns.Count; i++)
            {
                if (!guns[i].HasPrefabScale)
                    return true;
                if (Mathf.Abs(guns[i].XyScale - xy) > 0.02f
                    || Mathf.Abs(guns[i].RateScale - rate) > 0.02f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Chassis barrels that feed all-gun DPS. Moon-store guns are skipped —
        /// a purchase unlocks a bullet type and does not add a barrel.
        /// Mega hulls walk the catalog instead of the family part cache.
        /// Rates here are hull cadence (family scalar included, bullet type not yet).
        /// </summary>
        static void CollectDpsGuns(
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            int firePowerAbilityLv,
            List<DpsGunRow> into)
        {
            into.Clear();
            if (live.IsMega)
            {
                CollectMegaDpsGuns(in live, into);
                return;
            }

            if (!parts.Valid || parts.Ids == null || parts.Stats == null)
                return;

            int shipLevel = Mathf.Max(1, live.Ship.ShipLevel);
            int ability = Mathf.Max(0, firePowerAbilityLv);
            float familyFp = ReadFamilyMul(parts.Family, StatField.FirePower);
            float familyRate = 1f;
            if (parts.Family != null)
            {
                float rolledRate = parts.Family.ResolveSpecialBonuses().fireRateMul;
                if (rolledRate > 0.0001f)
                    familyRate = rolledRate;
            }

            int n = Mathf.Min(parts.Ids.Count, parts.Stats.Count);
            int chassisCount = parts.StoreExtraStartIndex >= 0
                ? Mathf.Min(n, parts.StoreExtraStartIndex)
                : n;

            for (int i = 0; i < chassisCount; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id) || ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (!ShipComponentAbilityStatsMath.IsWeaponComponent(id))
                    continue;

                ShipComponentAbilityStats stats = parts.Stats[i];
                if (stats.firePower <= 0.01f && stats.firePowerPerExtraLevel <= 0.01f)
                    continue;

                // Same Extra Level path as all-gun DPS: ship + Fire Power on damage,
                // ship level only on cadence (buying Fire Power does not speed the gun).
                float fp = ShipComponentExtraLevelMath.Evaluate(
                    stats.firePower,
                    stats.firePowerPerExtraLevel,
                    shipLevel,
                    ability,
                    componentCount: 1,
                    includeExtraComponentLevels: false);
                float rate = ShipComponentExtraLevelMath.Evaluate(
                    stats.fireRate,
                    stats.fireRatePerExtraLevel,
                    shipLevel,
                    abilityLevel: 0,
                    componentCount: 1,
                    includeExtraComponentLevels: false);
                if (fp <= 0.0001f)
                    continue;

                string name = ResolvePartName(parts.Family, id);
                ReadWeaponScale(ReadLocalScale(in parts, i), out float xy, out float zRate, out float axisZ);
                into.Add(new DpsGunRow
                {
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    FirePower = fp * familyFp,
                    FireRate = Mathf.Max(0f, rate) * familyRate,
                    XyScale = xy,
                    RateScale = zRate,
                    AxisZ = axisZ,
                    HasPrefabScale = true
                });
            }
        }

        /// <summary>
        /// Titan barrels from the catalog. Cycled guns take the selected bullet
        /// type's damage and fire rate (family level-7, or the catalog gun).
        /// Copy count scales Fire Power, not shots per second. Lasers and other
        /// authored banks keep their own rate and are not retuned by the B-key.
        /// </summary>
        static void CollectMegaDpsGuns(
            in ShipSpeedometerStatTooltips.LiveContext live,
            List<DpsGunRow> into)
        {
            var catalog = MegaShipCatalog.Load();
            if (catalog == null
                || !catalog.TryGetEntry(live.MegaCatalogIndex, out MegaShipCatalogEntry entry)
                || entry?.componentCounts == null)
                return;

            // Planet family combat rolls. Home Titans stay at the catalog gun.
            // A family or gear bullet replaces gun damage and rate; fleet stays
            // on the default gun only, matching the shot override.
            ShipFamilySpecialBonuses fleet = MegaShipStatsCalculator.ResolveFleetBonuses(
                live.Ship.ShipFamilyConfigIndex);
            float firePowerMul = fleet.firePowerMul > 0.0001f ? fleet.firePowerMul : 1f;
            float fireRateMul = fleet.fireRateMul > 0.0001f ? fleet.fireRateMul : 1f;
            bool familyGun = TryReadSelectedFamilyGun(
                in live, out _, out _, out _, out float familyFirepower, out float familyFireRate);
            float bankFp = 1f;
            float bankRate = 1f;
            if (BulletBankCombatLogic.TryGetProfile(live.FireBankIndex, out BulletBankProfile profile)
                && profile != null)
            {
                bankFp = BulletBankCombatLogic.SafeMul(profile.statModifiers.firePowerMultiplier);
                bankRate = BulletBankCombatLogic.SafeMul(profile.statModifiers.fireRateMultiplier);
            }

            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry unique)
                    || unique == null)
                    continue;

                MegaShipPartStats stats = catalog.ResolveRuntimeStats(unique.stats);
                if (stats.firePower <= 0.01f)
                    continue;

                bool cycledGun = IsBulletGunPart(unique);
                float shot;
                float rate;
                if (cycledGun && familyGun)
                {
                    shot = familyFirepower * bankFp;
                    float partRate = familyFireRate > 0.01f
                        ? familyFireRate
                        : stats.fireRate * fireRateMul;
                    rate = partRate * bankRate;
                }
                else if (cycledGun)
                {
                    shot = stats.firePower * firePowerMul * bankFp;
                    rate = Mathf.Max(0f, stats.fireRate) * fireRateMul * bankRate;
                }
                else
                {
                    shot = stats.firePower * firePowerMul;
                    rate = Mathf.Max(0f, stats.fireRate) * fireRateMul;
                }

                into.Add(new DpsGunRow
                {
                    Name = count.displayName,
                    FirePower = shot * count.count,
                    FireRate = rate,
                    XyScale = 1f,
                    RateScale = 1f,
                    AxisZ = 1f,
                    HasPrefabScale = false
                });
            }
        }

        /// <summary>
        /// Selected-bullet volley: damage per shot across barrels, and sustained DPS.
        /// Gun barrels use the current bullet type. Other weapons keep their own rate.
        /// </summary>
        static void SumMegaSelectedVolley(
            in ShipSpeedometerStatTooltips.LiveContext live,
            out float volleyDamage,
            out float dps)
        {
            var guns = new List<DpsGunRow>(4);
            CollectMegaDpsGuns(in live, guns);
            volleyDamage = 0f;
            dps = 0f;
            for (int i = 0; i < guns.Count; i++)
            {
                volleyDamage += guns[i].FirePower;
                dps += guns[i].FirePower * Mathf.Max(0f, guns[i].FireRate);
            }
        }

        static float SumMegaSelectedDps(in ShipSpeedometerStatTooltips.LiveContext live)
        {
            SumMegaSelectedVolley(in live, out _, out float dps);
            return dps;
        }

        /// <summary>
        /// Related weapon DPS, the RAM chip, and the full-cruise impact that chip becomes.
        /// The parts total is grind per second at 1× mass. Asteroid floaters use
        /// chip × mass ratio × speed. Called when the Fire Power details card rebuilds.
        /// </summary>
        /// <param name="firePowerAbilityLv">Bottom-HUD Fire Power purchases (stand-in for RAM).</param>
        static void AppendRelatedFireExtras(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            int firePowerAbilityLv)
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

            // [TITAN-ORBIT] RAM Extra Level = shipLevel + Fire Power (same as EvaluatePool).
            // The grid total is the chip (grind /s). A stock-cruise ram is 2× that chip.
            AppendStatCalcGrid(
                sb, in parts, in live, StatField.RammingPower, "chip",
                firePowerAbilityLv, live.EffectiveStats.rammingPower);

            AppendRamCruiseImpact(sb, in live);
        }

        /// <summary>
        /// Full-cruise ram the player should compare with the first asteroid floater.
        /// Chip × (1 + cruise / stock cruise). Stock cruise is before Move Speed purchases, so that speed is 2× the chip.
        /// </summary>
        static void AppendRamCruiseImpact(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.LiveContext live)
        {
            // --- Same rating the collision system multiplies ---
            // [TITAN-ORBIT] Prefer the speedometer rating (motor power × global × bank).
            // Fall back to the parts-grid chip when that snapshot has not filled yet.
            float rating = live.RamRating > 0.001f
                ? live.RamRating
                : ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(
                    live.EffectiveStats.rammingPower);
            float impactSpeed = live.CruiseMaxSpeed > 0.01f ? live.CruiseMaxSpeed : live.ChassisMaxSpeed;
            float stockSpeed = live.BaseCruiseSpeed > 0.01f ? live.BaseCruiseSpeed : impactSpeed;
            ShipComponentRammingSuggestions.RamCruiseImpactPreview hit =
                ShipComponentRammingSuggestions.BuildCruiseImpactPreview(rating, impactSpeed, stockSpeed);

            ShipStatTooltipChrome.AppendSectionBanner(sb, "FULL CRUISE IMPACT", "FFCC66");
            AppendTint(sb, HexMute, "Chip is grind/s. Hit is chip x (1 + current speed / stock). At stock cruise that is 2x. OVERDRIVE goes higher.");
            sb.AppendLine();

            sb.Append("Chip  ").Append(FResult(hit.Rating));
            AppendTint(sb, HexMute, "   grind/s");
            sb.AppendLine();

            sb.Append("Grind  ").Append(FResult(hit.GrindDps)).Append("/s");
            AppendTint(sb, HexMute, "   chip, while held");
            sb.AppendLine();

            sb.Append("Cruise  ").Append(FResult(hit.CruiseSpeed)).Append("/s");
            sb.Append("    Stock  ").Append(FResult(hit.BaseShipSpeed)).Append("/s");
            sb.Append("    Speed  1 + current/")
                .Append(FResult(hit.BaseShipSpeed))
                .Append("  at cruise  ");
            AppendTint(sb, "FFCC66", FResult(hit.SpeedMultiplier) + "x");
            sb.AppendLine();

            // The floater on the first bump. Later grind pulses add on top of this.
            sb.Append("<b>");
            AppendTint(sb, "FFAA66", "Asteroid  " + FResult(hit.AsteroidDamage));
            sb.Append("</b>");
            AppendTint(sb, HexMute, "   first contact");
            sb.AppendLine();
            sb.Append("Hull  ").Append(FResult(hit.SelfDamage));
            AppendTint(sb, HexMute, "   " + FResult(ShipComponentRammingSuggestions.SelfToAsteroidDamageRatio) + "x asteroid, capped to rock HP");
            sb.AppendLine();
        }

        /// <summary>
        /// Static mass-tax drag on turn (from the last chip/tip snapshot).
        /// Drag is totalMass × turnWeight, already in °/s, so the line matches the chip.
        /// </summary>
        static void AppendTurnMassTax(StringBuilder sb, in ShipSpeedometerStatTooltips.LiveContext live)
        {
            // --- MEGA: no cargo tax on yaw ---
            if (live.Motor.SkipMassTax != 0)
            {
                ShipStatTooltipChrome.AppendSectionBanner(sb, "MASS TAX", HexMass);
                AppendTint(sb, HexMass, "Titan hulls ignore mass tax.");
                sb.AppendLine();
                return;
            }

            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            if (settings == null)
                return;

            // [TITAN-ORBIT] turnWeight is already °/s, same unit as the chip.
            float drag = ShipMobilityResolution.ComputeTurnDragDegreesPerSecond(
                live.TotalMass, settings.turnWeightPerMass);
            ShipStatTooltipChrome.AppendSectionBanner(sb, "MASS TAX", HexMass);
            AppendTint(sb, HexMass, "Mass turn drag  -" + FDetail(drag) + "/s");
            sb.AppendLine();
        }

        /// <summary>
        /// MEGA details card: catalog part sums only. No Extra Level, no Lv / +next,
        /// no ability purchases. Cruise speed uses fastest authored Move + extra% of the rest,
        /// then the purchase planet's move roll. Every other stat prints that same roll
        /// beside the catalog number so the arrow matches the chip.
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
            // --- Readout (no purchase language) ---
            ShipStatTooltipChrome.AppendSectionBanner(sb, "READOUT", "7EC8FF");
            sb.Append("<b><color=#E8F4FF>").Append(shortLabel).Append(" - ").Append(title)
                .Append("</color></b>").AppendLine();
            sb.Append("<size=125%>");
            AppendTint(sb, HexResult, FResult(chipVal));
            sb.Append("</size>");
            if (!string.IsNullOrEmpty(unit))
                AppendTint(sb, HexMute, unit);
            sb.AppendLine();
                AppendTint(sb, HexMute, "Titan hull - static catalog; gear is PerExtra only");
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
                AppendTint(sb, HexMute, "Titan hulls cannot carry gems.");
                sb.AppendLine();
                AppendTotalLine(sb, 0f, unit);
                return;
            }

            if (abilityIndex == 6)
            {
                AppendMegaMoveCard(sb, in parts, in live, chipVal);
                return;
            }

            if (field == StatField.FirePower)
                AppendMegaGunFirepowerGrid(sb, in live);
            else
                AppendMegaCatalogParts(sb, in live, field, unit);
            AppendMegaGearParts(sb, in parts, in live, field, unit);
            AppendTotalLine(sb, chipVal, unit);

            if (abilityIndex == 0)
            {
                // Chip / power bar use per-gun catalog DPS, not summed-rate × summed-damage.
                // Rate of fire sits here so Fire Power × shots/s × weapon rate = that chip.
                AppendFireDpsEquation(sb, in parts, in live, 0);
                ShipStatTooltipChrome.AppendSectionBanner(sb, "RELATED", "FFAA66");
                float chipDps = SumMegaSelectedDps(in live);
                if (chipDps <= 0.0001f)
                {
                    var mega = MegaShipCatalog.Load();
                    chipDps = live.AllGunDps > 0.0001f
                        ? live.AllGunDps
                        : mega != null
                            ? mega.GetPowerBreakdown(live.MegaCatalogIndex).GetDisplayDps()
                            : ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(
                                live.EffectiveStats.firePower, live.EffectiveStats.fireRate);
                }

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
        /// Fire Power parts grid for Titan guns. Same columns as a regular ship
        /// (PART, ROLE, BASE, PERX, ×LV, SCALE, BONUS, ADD). Each copy is its own row.
        /// The default Titan bank lists each gun part at its catalog firepower times the
        /// purchase planet's fire-power roll. A family or gear bank lists that weapon at
        /// maxed level 7 (ship 7 + ability 7) and prints ×1 in BONUS — that shot
        /// replaces catalog damage, so the planet roll stays on the Titan's own guns.
        /// Lasers, missiles, and snipers stay off this grid and show the roll on their own line.
        /// Rebuilds when the selected bullet bank changes.
        /// </summary>
        static void AppendMegaGunFirepowerGrid(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.LiveContext live)
        {
            // --- Purchase-planet roll, not the part-cache family ---
            // [TITAN-ORBIT] The MEGA part cache leaves Family null (the hull is not a
            // family chassis). Reading that asset always printed ×1 while ADD and the
            // chip already included the motor's roll. ResolveFleetBonuses is that roll.
            float fleet = ReadMegaFleetMul(in live, StatField.FirePower);

            var catalog = MegaShipCatalog.Load();
            MegaShipCatalogEntry entry = null;
            bool haveEntry = catalog != null
                && catalog.TryGetEntry(live.MegaCatalogIndex, out entry)
                && entry?.componentCounts != null;

            bool familyGun = TryReadSelectedFamilyGun(
                in live, out string familyName, out float familyBase, out float familyPer, out _, out _);

            ShipStatTooltipChrome.AppendSectionBanner(sb, "PARTS", "5B9BD5");
            sb.AppendLine(DescribeFormula(StatField.FirePower, false));

            bool wroteGun = false;
            float running = 0f;
            if (familyGun || (haveEntry && CatalogHasBulletGun(catalog, entry)))
            {
                AppendPartsCalcHeader(sb);
                ShipStatTooltipChrome.AppendGridRule(sb, PartsGridColumns);
                if (familyGun)
                {
                    // This bank's damage is the family weapon, not catalog × fleet.
                    // The column still prints ×1 so the multiply is visible as identity.
                    AppendTint(sb, HexMute, "Family weapon keeps its own damage. Fleet fire power stays on the Titan gun.");
                    sb.AppendLine();
                    int copies = haveEntry ? CountBulletGuns(catalog, entry) : 1;
                    WriteMegaGunCopies(
                        sb,
                        ref wroteGun,
                        ref running,
                        string.IsNullOrEmpty(familyName) ? "Weapon" : familyName,
                        familyBase,
                        familyPer,
                        useLevels: true,
                        MegaShipStatApplyLogic.TitanFamilyWeaponShipLevel,
                        MegaShipStatApplyLogic.TitanFamilyWeaponAbilityLevel,
                        1f,
                        1f,
                        copies);
                }
                else
                {
                    for (int i = 0; i < entry.componentCounts.Count; i++)
                    {
                        MegaShipComponentCount count = entry.componentCounts[i];
                        if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                            continue;
                        if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry unique)
                            || !IsBulletGunPart(unique))
                            continue;
                        float each = unique.stats.firePower;
                        if (each <= 0.0001f)
                            continue;

                        WriteMegaGunCopies(
                            sb,
                            ref wroteGun,
                            ref running,
                            count.displayName,
                            each,
                            0f,
                            useLevels: false,
                            0,
                            0,
                            1f,
                            fleet,
                            count.count);
                    }
                }

                if (wroteGun)
                {
                    ShipStatTooltipChrome.AppendGridRule(sb, PartsGridColumns);
                    AppendGridTotal(sb, running, "Fire");
                }
            }

            bool wroteOther = haveEntry && AppendMegaNonGunFirepowerLines(sb, catalog, entry, fleet);
            if (!wroteGun && !wroteOther)
                sb.AppendLine("<color=#5B7A94>No contributing parts.</color>");
        }

        /// <summary>
        /// Selected family or gear gun, before the copy count. False on the Titan's own bullet bank,
        /// which keeps each gun part's catalog firepower.
        /// </summary>
        static bool TryReadSelectedFamilyGun(
            in ShipSpeedometerStatTooltips.LiveContext live,
            out string name,
            out float baseFirePower,
            out float perExtra,
            out float firepower,
            out float fireRate)
        {
            name = null;
            baseFirePower = 0f;
            perExtra = 0f;
            firepower = 0f;
            fireRate = 0f;
            World world = EcsGameBridge.GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
                return false;

            EntityManager em = world.EntityManager;
            if (!LocalShipEntitySeed.TryGetSeededShip(em, out Entity ship)
                || ship == Entity.Null
                || !em.Exists(ship))
                return false;

            if (!MegaShipStatApplyLogic.TryResolveTitanFamilyWeaponFirepower(
                    em, ship, live.FireBankIndex,
                    out firepower, out baseFirePower, out perExtra, out fireRate, out _, out _))
                return false;

            var bank = BulletBankCombatLogic.Bank;
            if (bank != null)
                name = bank.GetCategoryName(live.FireBankIndex);
            return true;
        }

        static bool IsBulletGunPart(MegaShipComponentEntry unique)
        {
            if (unique == null || unique.isLaser)
                return false;
            string partType = unique.partType;
            if (ShipFamilyPartTypes.IsWeaponCannonProfile(partType))
                return false;
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponMissile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, ShipFamilyPartTypes.WeaponSniper, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponBullet, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Weapon", StringComparison.OrdinalIgnoreCase))
                return true;
            return unique.isWeapon && string.IsNullOrEmpty(partType);
        }

        static bool CatalogHasBulletGun(MegaShipCatalog catalog, MegaShipCatalogEntry entry)
        {
            return CountBulletGuns(catalog, entry) > 0;
        }

        static int CountBulletGuns(MegaShipCatalog catalog, MegaShipCatalogEntry entry)
        {
            if (catalog == null || entry?.componentCounts == null)
                return 0;
            int n = 0;
            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry unique)
                    || !IsBulletGunPart(unique)
                    || unique.stats.firePower <= 0.0001f)
                    continue;
                n += count.count;
            }

            return n;
        }

        /// <summary>
        /// One barrel, same columns as <see cref="AppendStatCalcGrid"/>.
        /// The first barrel is PRI; every further copy is GUN. ADD is one shot.
        /// </summary>
        static float AppendGunCalcRow(
            StringBuilder sb,
            string name,
            float catalogBase,
            float catalogPer,
            bool useLevels,
            int shipSteps,
            int abilityLv,
            float scale,
            float fleet,
            bool primary)
        {
            float scaleMul = scale > 0.0001f ? scale : 1f;
            float fleetMul = fleet > 0.0001f ? fleet : 1f;
            float levels = useLevels ? shipSteps + abilityLv : 0f;
            float add = (catalogBase + catalogPer * levels) * scaleMul * fleetMul;
            string role = primary ? "PRI" : "GUN";
            string roleHex = primary ? HexPrimary : HexCount;

            AppendGridRowOpen(
                sb, name, roleHex, role, roleHex,
                FGrid(catalogBase), HexPrimary,
                useLevels ? FGrid(catalogPer) : "-", useLevels ? HexPerExtra : HexMute);
            if (useLevels)
                AppendGridLevelsCell(sb, false, shipSteps, abilityLv);
            else
                AppendGridCell(sb, HexMute, "-", GridLvW, right: true);
            AppendGridCell(sb, HexScale, FormatScaleColumn(scaleMul), GridScaleW, right: true);
            AppendFleetBonusCell(sb, fleetMul, StatField.FirePower);
            AppendGridCell(sb, HexResult, FGrid(add), GridNumW, right: true);
            sb.AppendLine();
            return add;
        }

        static void WriteMegaGunCopies(
            StringBuilder sb,
            ref bool wroteGun,
            ref float running,
            string name,
            float catalogBase,
            float catalogPer,
            bool useLevels,
            int shipSteps,
            int abilityLv,
            float scale,
            float fleet,
            int copies)
        {
            int n = copies > 0 ? copies : 1;
            for (int c = 0; c < n; c++)
            {
                bool primary = !wroteGun && c == 0;
                running += AppendGunCalcRow(
                    sb, name, catalogBase, catalogPer, useLevels, shipSteps, abilityLv, scale, fleet, primary);
                wroteGun = true;
            }
        }

        /// <summary>
        /// Lasers and other non-gun weapons that still have firepower.
        /// Copy count is not a multiplier here — each part keeps its own shot.
        /// <paramref name="fleetMul"/> is the purchase planet's fire-power roll.
        /// Those barrels keep the catalog shot times that roll (the family-weapon
        /// override does not replace them).
        /// </summary>
        static bool AppendMegaNonGunFirepowerLines(
            StringBuilder sb,
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry,
            float fleetMul)
        {
            bool wrote = false;
            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry unique)
                    || unique == null
                    || IsBulletGunPart(unique))
                    continue;

                float each = unique.stats.firePower;
                if (each <= 0.0001f)
                    continue;

                if (!wrote)
                {
                    AppendTint(sb, HexMute, "Other weapons keep their own shot");
                    sb.AppendLine();
                }

                wrote = true;
                sb.Append(count.displayName).Append("  ");
                AppendTint(sb, HexResult, FDetail(each));
                // One shot, then the planet roll. Copies are not a second multiply.
                AppendMegaFleetProduct(sb, each, 1, fleetMul, StatField.FirePower);
                sb.Append(" ");
                AppendTint(sb, HexMute, "Fire");
                sb.AppendLine();
            }

            return wrote;
        }

        /// <summary>
        /// Lists unique MEGA parts that contribute <paramref name="field"/>, grouped by name.
        /// The first number is the catalog value. The fleet percent beside it is the
        /// purchase planet's roll for this stat, and the arrow is catalog × copies × that roll.
        /// Bullet speed and range keep the fastest part (copies do not add). Hull
        /// defaults/minimums still apply only to the TOTAL, after this list.
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

            // Same roll on every row. Summing the arrows equals sum(parts) × fleet.
            // Speed and range take the fastest arrow instead — copies of one gun do not add.
            float fleetMul = ReadMegaFleetMul(in live, field);
            bool fastestWins = field is StatField.BulletSpeed or StatField.BulletRange;
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
                AppendTint(sb, HexCount, count.count.ToString(CultureInfo.InvariantCulture) + "x");
                sb.Append(" ").Append(count.displayName).Append("  ");
                AppendTint(sb, HexResult, FDetail(each));
                if (!string.IsNullOrEmpty(unitLabel))
                {
                    sb.Append(" ");
                    AppendTint(sb, HexMute, unitLabel);
                }

                if (fastestWins && count.count > 1)
                {
                    sb.Append(" ");
                    AppendTint(sb, HexMute, "fastest");
                }

                AppendMegaFleetProduct(sb, each, fastestWins ? 1 : count.count, fleetMul, field);
                sb.AppendLine();
            }

            if (!wrote)
                sb.AppendLine("<color=#5B7A94>No contributing parts.</color>");
        }

        /// <summary>
        /// Lists equipped moon-store ship components that contribute
        /// <paramref name="field"/> after PerExtra × Titan tier (no Base), then the
        /// purchase planet's fleet roll. Hidden when the Titan has no LOADOUT gear
        /// for this chip. Each store row is one copy — the arrow is that copy × fleet.
        /// </summary>
        static void AppendMegaGearParts(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
            in ShipSpeedometerStatTooltips.LiveContext live,
            StatField field,
            string unitLabel)
        {
            if (!parts.Valid
                || parts.Ids == null
                || parts.Stats == null
                || parts.StoreExtraStartIndex == int.MaxValue
                || field == StatField.MaxGems)
                return;

            int start = parts.StoreExtraStartIndex;
            if (start < 0 || start >= parts.Ids.Count)
                return;

            int level = Mathf.Max(1, live.Ship.ShipLevel);
            // Gear is folded into the hull sum, then the whole hull is rolled.
            // Showing the roll on this line keeps it in the same multiply as catalog parts.
            float fleetMul = ReadMegaFleetMul(in live, field);
            bool wroteHeader = false;
            int n = Mathf.Min(parts.Ids.Count, parts.Stats.Count);
            for (int i = start; i < n; i++)
            {
                string id = parts.Ids[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (field == StatField.FirePower
                    && ShipComponentAbilityStats.IsWeaponComponent(id))
                    continue;

                ShipComponentAbilityStats extra = MegaShipStatsCalculator.EvaluateLoadoutExtra(
                    parts.Stats[i], id, level);
                extra.maxGems = 0f;
                float value = ReadField(extra, field);
                if (value <= 0.0001f)
                    continue;

                if (!wroteHeader)
                {
                    ShipStatTooltipChrome.AppendSectionBanner(sb, "GEAR", "FFAA66");
                    AppendTint(sb, HexMute, "LOADOUT extras add PerExtra x Titan tier (no Base), then fleet");
                    sb.AppendLine();
                    wroteHeader = true;
                }

                string label = ShipComponentStoreData.FormatComponentId(id);
                if (BulletBankProfileUtility.TryFindComponentInAnyFamily(id, out ShipFamilyComponentEntry entry)
                    && entry != null)
                    label = ShipComponentStoreData.GetDisplayName(entry);

                AppendTint(sb, HexCount, "1x");
                sb.Append(" ").Append(label).Append("  ");
                AppendTint(sb, HexResult, FDetail(value));
                if (!string.IsNullOrEmpty(unitLabel))
                {
                    sb.Append(" ");
                    AppendTint(sb, HexMute, unitLabel);
                }

                AppendMegaFleetProduct(sb, value, 1, fleetMul, field);
                sb.AppendLine();
            }
        }

        /// <summary>
        /// MEGA cruise: fastest Move + extra% of the rest — same as
        /// <see cref="MegaShipComponentInventory.CombineEngineCruise"/> — after each
        /// part's speed is multiplied by the purchase planet's move roll.
        /// The formula is linear, so rolling every part then combining matches
        /// combining the catalog speeds and rolling the result once.
        /// Every unique part with moveSpeed is listed (wing / hull / cockpit / engine / …).
        /// </summary>
        static void AppendMegaMoveCard(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.PartCache parts,
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

            // One roll for every Move part. Home Titans stay ×1.
            float moveFleet = ReadMegaFleetMul(in live, StatField.MoveSpeed);
            float extraPercent = catalog.GetExtraEngineSpeedPercent();
            var moves = new List<float>(8);
            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry unique)
                    || unique == null
                    || !MegaShipComponentInventory.ContributesCruiseMove(unique.stats))
                    continue;

                float rawMove = unique.stats.moveSpeed;
                float afterFleet = rawMove * moveFleet;
                for (int n = 0; n < count.count; n++)
                    moves.Add(afterFleet);

                AppendTint(sb, HexCount, count.count.ToString(CultureInfo.InvariantCulture) + "x");
                sb.Append(" ").Append(count.displayName).Append("  ");
                AppendTint(sb, HexResult, FDetail(rawMove));
                sb.Append(" ");
                AppendTint(sb, HexMute, unique.partType);
                if (count.count > 1)
                {
                    sb.Append(" ");
                    AppendTint(sb, HexMute, "each");
                }

                // Arrow is one copy after the roll. Copies stay separate inputs to cruise.
                AppendMegaFleetProduct(sb, rawMove, 1, moveFleet, StatField.MoveSpeed);
                sb.AppendLine();
            }

            ShipStatTooltipChrome.AppendSectionBanner(sb, "CRUISE", "7DFFB2");
            AppendTint(sb, HexMute, "fastest + ");
            AppendTint(sb, HexPerExtra, (extraPercent * 100f).ToString("0.##", CultureInfo.InvariantCulture) + "%");
            AppendTint(sb, HexMute, " of other Move parts");
            if (Mathf.Abs(moveFleet - 1f) > 0.0005f)
                AppendTint(sb, HexMute, "  (after fleet)");
            sb.AppendLine();
            float combined = MegaShipComponentInventory.CombineEngineCruise(moves, extraPercent);
            if (combined > 0.0001f)
            {
                AppendTint(sb, HexMute, "cruise  ");
                AppendTint(sb, HexResult, FResult(combined));
                sb.AppendLine();
            }

            AppendMegaCatalogParts(sb, in live, StatField.AccelerationCap, "Accel");
            AppendMegaGearParts(sb, in parts, in live, StatField.MoveSpeed, "Move");
            AppendMegaGearParts(sb, in parts, in live, StatField.AccelerationCap, "Accel");
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
                return "x100%";
            if (Mathf.Abs(pct - 10f) < 0.05f)
                return "x10%";
            if (Mathf.Abs(pct - Mathf.Round(pct)) < 0.05f)
                return "x" + Mathf.RoundToInt(pct).ToString(CultureInfo.InvariantCulture) + "%";
            return "x" + pct.ToString("0.##", CultureInfo.InvariantCulture) + "%";
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

        /// <summary>
        /// Equation operand. Keeps a third decimal when two decimals would not multiply back
        /// (1.875 × 2 must still read as the parts total).
        /// </summary>
        static string FEq(float v)
        {
            string two = v.ToString("0.##", CultureInfo.InvariantCulture);
            if (float.TryParse(two, NumberStyles.Float, CultureInfo.InvariantCulture, out float rounded)
                && Mathf.Abs(rounded - v) < 0.001f)
                return two;
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
