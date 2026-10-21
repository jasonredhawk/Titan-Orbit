using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Moon-dock pricing and display helpers for purchasable ship-family components (equipment slots).
    /// Converts raw <see cref="ShipComponentAbilityStats"/> into level-scaled effective stats, power scores,
    /// gem prices, and HUD description strings. Paired with <see cref="ShipFamilyDefinition"/> component entries.
    /// </summary>
    public static class ShipComponentStoreData
    {
        /// <summary>Gems charged per power point when pricing moon-dock components.</summary>
        public const float GemCostPerPowerPoint = 1.75f;
        /// <summary>Price multiplier per ship level above 1 (12% per level).</summary>
        public const float LevelPriceScalePerLevel = 0.12f;
        /// <summary>Floor gem price so zero-stat placeholder parts still cost something.</summary>
        public const int MinimumComponentGemPrice = 8;

        /// <summary>
        /// Extra Level evaluation for a <b>single</b> component (store card / solo preview).
        /// <c>value = base + this part’s perExtra × ((shipLevel−1) + abilityLevel)</c> with abilityLevel = 0.
        /// Then applies cargo mobility penalties.
        /// <para>
        /// [TITAN-ORBIT] Full hulls with multiple parts must use
        /// <see cref="ShipComponentExtraLevelMath.AggregateAndEvaluate"/> so each part keeps
        /// its own PerExtra (engine Move ≠ thruster Accel). <paramref name="shipLevelStatGrowthFraction"/> is
        /// ignored (legacy % tier growth retired).
        /// </para>
        /// </summary>
        public static ShipComponentAbilityStats GetEffectiveStatsAtShipLevel(
            ShipComponentAbilityStats stats,
            int shipLevel,
            float shipLevelStatGrowthFraction = -1f)
        {
            _ = shipLevelStatGrowthFraction;

            // --- Single-part Extra Level (count = 1, no ability purchases) ---
            var attrs = default(ShipAbilityLevelCounts);
            ShipComponentAbilityStats evaluated = ShipComponentExtraLevelMath.EvaluatePool(
                stats,
                componentCount: 1,
                shipLevel,
                in attrs,
                isWeaponPool: false);

            return ShipComponentExtraLevelMath.ApplyMobilityPenalties(evaluated, shipLevel);
        }

        /// <summary>
        /// Single-component Extra Level with an explicit weapon flag (divides fire power when true).
        /// </summary>
        public static ShipComponentAbilityStats GetEffectiveStatsAtShipLevel(
            ShipComponentAbilityStats stats,
            int shipLevel,
            string componentId,
            float shipLevelStatGrowthFraction = -1f)
        {
            _ = shipLevelStatGrowthFraction;
            var attrs = default(ShipAbilityLevelCounts);
            bool weapon = ShipComponentAbilityStats.IsWeaponComponent(componentId);
            ShipComponentAbilityStats evaluated = ShipComponentExtraLevelMath.EvaluatePool(
                stats,
                componentCount: 1,
                shipLevel,
                in attrs,
                isWeaponPool: weapon);
            return ShipComponentExtraLevelMath.ApplyMobilityPenalties(evaluated, shipLevel);
        }


        /// <summary>Single scalar power number for gem pricing (sum of breakdown categories).</summary>
        public static float GetComponentPowerScore(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            int planetOrHullBankIndex = -1)
        {
            if (entry == null)
                return 0f;
            return GetPowerBreakdown(entry, shipLevel, family, planetOrHullBankIndex).Total;
        }

        public static ShipFamilyPowerScoreBreakdown GetPowerBreakdown(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            int planetOrHullBankIndex = -1)
        {
            if (entry == null)
                return default;
            ShipComponentAbilityStats effective = GetEffectiveStatsForDisplay(
                entry, shipLevel, family, planetOrHullBankIndex);
            return ShipFamilyPowerScoreBreakdown.FromSummedShipStats(effective);
        }

        /// <summary>Level-scaled stats plus optional bullet-bank profile overlay (currently no-op stub).</summary>
        public static ShipComponentAbilityStats GetEffectiveStatsForDisplay(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            int planetOrHullBankIndex = -1)
        {
            if (entry == null)
                return default;
            // [TITAN-ORBIT] Solo store-card preview: Extra Level with count 1 + weapon divide when needed.
            // Family fleet is baked into the power number. The gear card text shows
            // the catalog value and this adjusted value side by side.
            ShipComponentAbilityStats effective = GetEffectiveStatsAtShipLevel(
                entry.stats, shipLevel, entry.componentId);
            if (family != null)
                effective = family.ApplySpecialBonuses(effective);
            return BulletBankProfileUtility.ApplyProfileToComponentStats(
                effective, entry, family, planetOrHullBankIndex);
        }

        /// <summary>Gem cost from power score × level multiplier, floored at <see cref="MinimumComponentGemPrice"/>.</summary>
        public static int GetComponentGemPrice(ShipFamilyComponentEntry entry, int shipLevel)
        {
            float power = GetComponentPowerScore(entry, shipLevel);
            if (power < 0.01f)
                return MinimumComponentGemPrice;
            float levelMult = 1f + Mathf.Max(0, shipLevel - 1) * LevelPriceScalePerLevel;
            return Mathf.Max(MinimumComponentGemPrice, Mathf.RoundToInt(power * GemCostPerPowerPoint * levelMult));
        }

        /// <summary>
        /// Player-facing title from Component ID (<c>SpaceExcalibur_Tiny_Thrusters</c> → Tiny Thrusters).
        /// </summary>
        public static string GetDisplayName(ShipFamilyComponentEntry entry)
        {
            if (entry == null)
                return "Component";
            return FormatComponentId(entry.componentId);
        }

        /// <summary>
        /// Family prefix stripped, underscores / CamelCase spaced.
        /// <c>SpaceExcalibur_Tiny_Thrusters</c> → Tiny Thrusters.
        /// </summary>
        public static string FormatComponentId(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return "Component";
            string formatted = DisplayNameFormatting.FormatComponentDisplayName(componentId);
            return string.IsNullOrWhiteSpace(formatted) ? "Component" : formatted;
        }

        /// <summary>Unicode glyph for compact list rows when no sprite is available.</summary>
        public static string GetIconGlyph(ShipFamilyComponentEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                return "?";
            entry.EnsureStatCategories();
            string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(entry.componentId);
            // [TITAN-ORBIT] Emoji-like glyphs map USC part families to quick-scan icons.
            switch (partType)
            {
                case "Weapon": return "\u2694";
                case "Engine": return "\u2699";
                case "Thruster": return "\u25B2";
                case "Wing": return "\u25C7";
                case "Cockpit": return "\u25CE";
                case "Cargo": return "\u25A3";
                case "Arm": return "\u2692";
                default:
                    return entry.componentId.Trim().Substring(0, 1).ToUpperInvariant();
            }
        }

        public static Sprite GetMenuPreviewSprite(ShipFamilyDefinition family, ShipFamilyComponentEntry entry, TeamManager.Team team = TeamManager.Team.None)
        {
            if (entry != null)
            {
                Sprite theatrical = entry.GetTheatricalMenuPreviewSprite(team);
                if (theatrical != null)
                    return theatrical;
                if (entry.GetMenuPreviewSprite(team) != null)
                    return entry.GetMenuPreviewSprite(team);
            }
            if (family != null && entry != null && !string.IsNullOrWhiteSpace(entry.componentId))
                return family.GetMenuPreviewSpriteForComponent(entry.componentId, team);
            return null;
        }

        /// <summary>Index into orbit-station ability color palette (offense=0, health=2, …).</summary>
        public static int GetAbilityColorStatIndex(ShipFamilyComponentEntry entry)
        {
            if (entry == null)
                return 0;
            entry.EnsureStatCategories();
            if (entry.statCategories != null)
            {
                // [TITAN-ORBIT] First matching category wins — designers list primary role first.
                if (entry.statCategories.Contains(ShipComponentStatCategory.Offense))
                    return 0;
                if (entry.statCategories.Contains(ShipComponentStatCategory.Health))
                    return 2;
                if (entry.statCategories.Contains(ShipComponentStatCategory.Energy))
                    return 4;
                if (entry.statCategories.Contains(ShipComponentStatCategory.Movement))
                    return 6;
                if (entry.statCategories.Contains(ShipComponentStatCategory.Capacity))
                    return 8;
            }
            return 0;
        }

        /// <summary>Human-readable multi-line stat summary for moon-dock tooltips (top N non-zero stats).</summary>
        public static string GetStatsDescription(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            int maxLines = 4,
            int planetOrHullBankIndex = -1)
        {
            if (entry == null)
                return string.Empty;

            ShipComponentAbilityStats adjusted = GetEffectiveStatsForDisplay(
                entry, shipLevel, family, planetOrHullBankIndex);
            ShipComponentAbilityStats original = BulletBankProfileUtility.ApplyProfileToComponentStats(
                GetEffectiveStatsAtShipLevel(entry.stats, shipLevel, entry.componentId),
                entry, family, planetOrHullBankIndex);

            var lines = new List<string>(maxLines);
            // --- Pick top non-zero stats in fixed priority order ---
            TryAddFleetLine(lines, "Fire", original.firePower, adjusted.firePower, maxLines);
            TryAddFleetLine(lines, "Bullet", original.bulletSpeed, adjusted.bulletSpeed, maxLines);
            TryAddFleetLine(lines, "Range", original.bulletRange, adjusted.bulletRange, maxLines);
            TryAddFleetLine(lines, "Fire rate", original.fireRate, adjusted.fireRate, maxLines);
            TryAddFleetLine(lines, "Ram", original.rammingPower, adjusted.rammingPower, maxLines);
            TryAddFleetLine(lines, "Health", original.healthCap, adjusted.healthCap, maxLines);
            TryAddFleetLine(lines, "Regen", original.healthRegen, adjusted.healthRegen, maxLines);
            TryAddFleetLine(lines, "Energy", original.energyCap, adjusted.energyCap, maxLines);
            TryAddFleetLine(lines, "E.Regen", original.energyRegen, adjusted.energyRegen, maxLines);
            TryAddFleetLine(lines, "Speed", original.moveSpeed, adjusted.moveSpeed, maxLines);
            TryAddFleetLine(lines, "Accel", original.accelerationCap, adjusted.accelerationCap, maxLines);
            // Added gear contributes Turn PerExtra × ship level, not the part's Turn Base.
            TryAddFleetLine(
                lines,
                "Turn",
                GetAddedGearTurnGain(entry, shipLevel, family, applyFamilyFleet: false),
                GetAddedGearTurnGain(entry, shipLevel, family, applyFamilyFleet: true),
                maxLines);
            TryAddFleetLine(lines, "Gems", original.maxGems, adjusted.maxGems, maxLines);
            TryAddFleetLine(lines, "Tractor", original.tractorBeamDistance, adjusted.tractorBeamDistance, maxLines);
            TryAddFleetLine(lines, "Troops", original.maxPeople, adjusted.maxPeople, maxLines);

            if (lines.Count == 0)
                return "No stat bonus";

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                    sb.Append('\n');
                sb.Append(lines[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Moon-dock equipment card ability list (TMP rich text).
        /// Propulsion shows base and cumulative hull gain (e.g. <c>+6 base Move → +1.5 cumulative</c>).
        /// </summary>
        public static string BuildAbilityDescriptionRichText(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            int maxLines = 14,
            int planetOrHullBankIndex = -1)
        {
            return BuildAbilityDescription(
                entry, shipLevel, family, maxLines, richText: true, planetOrHullBankIndex);
        }

        /// <summary>Plain-text variant (legacy equipment panel / short tooltips).</summary>
        public static string BuildAbilityDescriptionPlain(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            int maxLines = 8)
        {
            return BuildAbilityDescription(
                entry, shipLevel, family, maxLines, richText: false, planetOrHullBankIndex: -1);
        }

        /// <summary>
        /// Builds the Gear-tab card description: weapon bullet type (when this part is a gun),
        /// then authored Extra-Level stats. Propulsion also shows real hull gain
        /// (e.g. <c>+6 base Move → +1.5 cumulative</c>). No long stacking essays.
        /// Called when a card is created and again when the Gear tab refreshes prices.
        /// </summary>
        static string BuildAbilityDescription(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family,
            int maxLines,
            bool richText,
            int planetOrHullBankIndex = -1)
        {
            if (entry == null)
                return string.Empty;

            // Extra Level + bullet bank. Fleet is applied next so the card can
            // show the catalog number and the moon-adjusted number side by side.
            ShipComponentAbilityStats leveled = GetEffectiveStatsAtShipLevel(
                entry.stats, shipLevel, entry.componentId);
            ShipFamilySpecialBonuses fleet = family != null
                ? family.ResolveSpecialBonuses()
                : ShipFamilySpecialBonuses.Identity;
            ShipComponentAbilityStats original = BulletBankProfileUtility.ApplyProfileToComponentStats(
                leveled, entry, family, planetOrHullBankIndex);
            ShipComponentAbilityStats adjusted = fleet.IsIdentity
                ? original
                : BulletBankProfileUtility.ApplyProfileToComponentStats(
                    fleet.Apply(leveled), entry, family, planetOrHullBankIndex);
            entry.EnsureStatCategories();
            if (entry.statCategories == null || entry.statCategories.Count == 0)
                entry.statCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(entry.componentId);

            var lines = new List<AbilityLine>(16);
            bool isPropulsion = ShipComponentAbilityStats.IsPropulsionComponent(entry.componentId);
            bool isEngine = ShipFamilyPartTypes.IsEngineLikeName(entry.componentId);

            // --- Weapon bullet type ---
            // [TITAN-ORBIT] Gear-tab weapon cards list which bullet bank this part fires
            // (Fireballs, Rift, …) next to Fire Power / Speed / Range. Authored
            // entry.bulletPrefabIndex overrides the family default when set.
            TryQueueWeaponBulletType(lines, entry, family, planetOrHullBankIndex);

            // --- Offense / Health / Energy (full gain = authored; they sum) ---
            TryQueueFleet(lines, original.firePower, adjusted.firePower, "Fire Power", 0, richText);
            TryQueueFleet(lines, original.bulletSpeed, adjusted.bulletSpeed, "Bullet Speed", 1, richText);
            TryQueueFleet(lines, original.bulletRange, adjusted.bulletRange, "Bullet Range", 1, richText);
            TryQueueFleet(lines, original.fireRate, adjusted.fireRate, "Fire Rate", 1, richText);
            TryQueueFleet(lines, original.rammingPower, adjusted.rammingPower, "Ramming", 0, richText);
            TryQueueFleet(lines, original.healthCap, adjusted.healthCap, "Health Cap", 2, richText);
            TryQueueFleet(lines, original.healthRegen, adjusted.healthRegen, "Health Regen", 3, richText);
            TryQueueFleet(lines, original.energyCap, adjusted.energyCap, "Energy Cap", 4, richText);
            TryQueueFleet(lines, original.energyRegen, adjusted.energyRegen, "Energy Regen", 5, richText);

            // --- Movement: catalog number, then the fleet-adjusted number ---
            if (isPropulsion)
            {
                TryGetPropulsionCumulativeGain(
                    family, entry, shipLevel, out float cumulativeMove, out float cumulativeAccel);

                TryQueueFleetOrHull(lines, original.moveSpeed, adjusted.moveSpeed, cumulativeMove, "Move", 6, richText);
                TryQueueFleetOrHull(lines, original.accelerationCap, adjusted.accelerationCap, cumulativeAccel, "Accel", 6, richText);
            }
            else
            {
                TryQueueFleet(lines, original.moveSpeed, adjusted.moveSpeed, "Move", 6, richText);
                TryQueueFleet(lines, original.accelerationCap, adjusted.accelerationCap, "Accel", 6, richText);
            }

            // Same extra-part rule as Move / Accel: the hull keeps its Turn Base.
            // Buying this piece adds only its Turn PerExtra × ship level.
            float turnOriginal = GetAddedGearTurnGain(entry, shipLevel, family, applyFamilyFleet: false);
            float turnAdjusted = GetAddedGearTurnGain(entry, shipLevel, family, applyFamilyFleet: true);
            TryQueueFleetOrHull(lines, original.turnSpeed, adjusted.turnSpeed, turnAdjusted, "Turn", 7, richText);

            // [TITAN-ORBIT] Absolute OVERDRIVE energy/sec on engines (not a multiplier of ExtraSpeedPercent).
            // More drain is worse, so a +% on this line is amber, not green.
            if (isEngine)
                TryQueueFleet(
                    lines,
                    original.extraSpeedEnergyDrain,
                    adjusted.extraSpeedEnergyDrain,
                    "Overdrive Drain/s",
                    4,
                    richText,
                    higherIsBetter: false);

            if (isEngine && adjusted.extraSpeedPercent > 0.0001f)
            {
                float pctOriginal = original.extraSpeedPercent * 100f;
                float pctAdjusted = adjusted.extraSpeedPercent * 100f;
                string od = TryBuildFleetArrow(pctOriginal, pctAdjusted, richText, higherIsBetter: true, out string odArrow)
                    ? $"Overdrive +{FormatStatValue(pctOriginal)}%{odArrow} speed/thrust"
                    : $"Overdrive +{FormatStatValue(pctAdjusted)}% speed/thrust";
                TryQueueNote(lines, od, 6);
            }

            // --- Capacity ---
            TryQueueFleet(lines, original.maxGems, adjusted.maxGems, "Gem Cap", 8, richText);
            TryQueueFleet(lines, original.tractorBeamDistance, adjusted.tractorBeamDistance, "Tractor Dist", 8, richText);
            TryQueueFleet(lines, original.tractorBeamPower, adjusted.tractorBeamPower, "Tractor Power", 8, richText);
            TryQueueFleet(lines, original.maxPeople, adjusted.maxPeople, "Troop Cap", 9, richText);

            if (lines.Count == 0)
                return richText ? "<color=#888888>—</color>" : "No stat bonus";

            // --- Compile visible lines ---
            // TMP cards use category tints; legacy callers ask for plain text.
            // maxLines is a card-height budget (14 on Gear, 8 on short tooltips).
            var sb = new StringBuilder(256);
            int written = 0;
            for (int i = 0; i < lines.Count && written < maxLines; i++)
            {
                AbilityLine line = lines[i];
                if (written > 0)
                    sb.Append('\n');

                if (richText)
                {
                    Color c = ResolveAbilityLineColor(line.ColorIndex);
                    sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>');
                    sb.Append(line.Text);
                    sb.Append("</color>");
                }
                else
                {
                    sb.Append(line.Text);
                }

                written++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Turn added when this component is equipped as gear (Titan or regular hull).
        /// <c>PerExtra × shipLevel</c> only — the part's Turn Base stays off the hull,
        /// matching an extra engine's Move step and an extra thruster's Accel step.
        /// Family turn multiplier and level mobility drag match the gear card's other numbers.
        /// </summary>
        public static float GetAddedGearTurnGain(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            bool applyFamilyFleet = true)
        {
            if (entry == null)
                return 0f;

            int level = Mathf.Max(1, shipLevel);
            // --- PerExtra steps only (level 1 still applies 1×) ---
            float gain = ShipComponentExtraLevelMath.Evaluate(
                entry.stats.turnSpeed,
                entry.stats.turnSpeedPerExtraLevel,
                level,
                abilityLevel: 0,
                componentCount: 1,
                includeExtraComponentLevels: true,
                includeBase: false);
            var wrapped = default(ShipComponentAbilityStats);
            wrapped.turnSpeed = gain;
            wrapped = ShipComponentExtraLevelMath.ApplyMobilityPenalties(wrapped, level);
            if (applyFamilyFleet && family != null)
                wrapped = family.ApplySpecialBonuses(wrapped);
            return Mathf.Max(0f, wrapped.turnSpeed);
        }

        /// <summary>
        /// How much hull top speed / accel this propulsion part contributes:
        /// aggregated result with the part minus without it (same rules as flight).
        /// Move and Accel Extra Level each propulsion part with that part’s PerExtra, then sum.
        /// </summary>
        public static void TryGetPropulsionCumulativeGain(
            ShipFamilyDefinition family,
            ShipFamilyComponentEntry entry,
            int shipLevel,
            out float cumulativeMoveGain,
            out float cumulativeAccelGain)
        {
            cumulativeMoveGain = 0f;
            cumulativeAccelGain = 0f;
            if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                return;

            float growth = family != null
                ? family.ResolveShipLevelStatGrowthFraction()
                : ShipFamilyDefinition.DefaultShipLevelStatGrowthFraction;

            // Solo part (no family list) — authored effective values are the gain.
            if (family?.components == null)
            {
                ShipComponentAbilityStats solo = GetEffectiveStatsAtShipLevel(entry.stats, shipLevel, growth);
                cumulativeMoveGain = Mathf.Max(0f, solo.moveSpeed);
                cumulativeAccelGain = Mathf.Max(
                    0f,
                    ShipPropulsionAggregation.GetPropulsionAccelerationContribution(
                        entry.stats, Mathf.Max(0, shipLevel - 1)));
                return;
            }

            var idsWith = new List<string>(8);
            var statsWith = new List<ShipComponentAbilityStats>(8);
            var idsWithout = new List<string>(8);
            var statsWithout = new List<ShipComponentAbilityStats>(8);

            string targetId = entry.componentId.Trim();
            bool sawTarget = false;

            for (int i = 0; i < family.components.Count; i++)
            {
                ShipFamilyComponentEntry e = family.components[i];
                if (e == null || string.IsNullOrWhiteSpace(e.componentId))
                    continue;
                if (!ShipComponentAbilityStats.IsPropulsionComponent(e.componentId))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(e.componentId))
                    continue;

                idsWith.Add(e.componentId);
                statsWith.Add(e.stats);

                bool isTarget = string.Equals(e.componentId, targetId, StringComparison.OrdinalIgnoreCase);
                if (isTarget)
                {
                    sawTarget = true;
                    continue;
                }

                idsWithout.Add(e.componentId);
                statsWithout.Add(e.stats);
            }

            // Target not in family list — treat as an extra store copy on top of existing propulsion.
            if (!sawTarget)
            {
                idsWith.Add(targetId);
                statsWith.Add(entry.stats);
                idsWithout.Clear();
                statsWithout.Clear();
                for (int i = 0; i < family.components.Count; i++)
                {
                    ShipFamilyComponentEntry e = family.components[i];
                    if (e == null || string.IsNullOrWhiteSpace(e.componentId))
                        continue;
                    if (!ShipComponentAbilityStats.IsPropulsionComponent(e.componentId))
                        continue;
                    if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(e.componentId))
                        continue;
                    idsWithout.Add(e.componentId);
                    statsWithout.Add(e.stats);
                }
            }

            ShipPropulsionAggregation.Result with =
                ShipPropulsionAggregation.ComputeThrusterPropulsion(idsWith, statsWith, shipLevel);
            ShipPropulsionAggregation.Result without =
                ShipPropulsionAggregation.ComputeThrusterPropulsion(idsWithout, statsWithout, shipLevel);

            cumulativeMoveGain = Mathf.Max(0f, with.topMoveSpeed - without.topMoveSpeed);
            cumulativeAccelGain = Mathf.Max(0f, with.sumAcceleration - without.sumAcceleration);
        }

        /// <summary>[LEGACY] Prefer <see cref="TryGetPropulsionCumulativeGain"/>.</summary>
        public static void TryGetPropulsionActualGain(
            ShipFamilyDefinition family,
            ShipFamilyComponentEntry entry,
            int shipLevel,
            out float actualMoveGain,
            out float actualAccelGain) =>
            TryGetPropulsionCumulativeGain(family, entry, shipLevel, out actualMoveGain, out actualAccelGain);

        /// <summary>One colored line in a moon-dock ability description.</summary>
        struct AbilityLine
        {
            public string Text;
            public int ColorIndex;
        }

        /// <summary>
        /// Adds <c>Bullet Type  Fireballs</c> (offense tint) when this part is a weapon
        /// and the VFX bank has a category name for it. Non-weapons skip this line.
        /// </summary>
        static void TryQueueWeaponBulletType(
            List<AbilityLine> lines,
            ShipFamilyComponentEntry entry,
            ShipFamilyDefinition family,
            int planetOrHullBankIndex = -1)
        {
            if (entry == null || !ShipComponentAbilityStats.IsWeaponComponent(entry.componentId))
                return;

            string typeName = BulletBankProfileUtility.FormatComponentBulletTypeName(
                entry, family, planetOrHullBankIndex);
            if (string.IsNullOrEmpty(typeName))
                return;

            // Color index 1 = offense orange — same rail as Bullet Speed / Fire Rate.
            TryQueueNote(lines, "Bullet Type  " + typeName, 1);
        }

        /// <summary>
        /// Gear-card line. When this moon's fleet roll changes the number, the
        /// player sees the catalog value, then the adjusted value, then +% or −%.
        /// A 1× roll stays a single number.
        /// </summary>
        static void TryQueueFleet(
            List<AbilityLine> lines,
            float original,
            float adjusted,
            string label,
            int colorIndex,
            bool richText,
            bool higherIsBetter = true)
        {
            if (Mathf.Abs(original) < 0.05f && Mathf.Abs(adjusted) < 0.05f)
                return;

            string text = $"+{FormatStatValue(Mathf.Abs(original) >= 0.05f ? original : adjusted)} {label}";
            if (TryBuildFleetArrow(original, adjusted, richText, higherIsBetter, out string arrow))
                text = $"+{FormatStatValue(original)} {label}{arrow}";

            lines.Add(new AbilityLine
            {
                Text = text,
                ColorIndex = colorIndex,
            });
        }

        /// <summary>
        /// Same as <see cref="TryQueueFleet"/>, and when hull aggregation changes
        /// the gain (extra engines do not add a second Base) appends that hull number.
        /// </summary>
        static void TryQueueFleetOrHull(
            List<AbilityLine> lines,
            float original,
            float adjusted,
            float hullGain,
            string label,
            int colorIndex,
            bool richText)
        {
            bool fleet = TryBuildFleetArrow(original, adjusted, richText, higherIsBetter: true, out string arrow);
            bool hullDiffers = Mathf.Abs(hullGain - adjusted) >= 0.05f && Mathf.Abs(hullGain) >= 0.05f;
            if (!fleet)
            {
                TryQueueBaseAndCumulative(lines, original, hullGain, label, colorIndex);
                return;
            }

            if (Mathf.Abs(original) < 0.05f && Mathf.Abs(adjusted) < 0.05f && !hullDiffers)
                return;

            string text = $"+{FormatStatValue(original)} {label}{arrow}";
            if (hullDiffers)
                text += $" · hull +{FormatStatValue(hullGain)}";

            lines.Add(new AbilityLine
            {
                Text = text,
                ColorIndex = colorIndex,
            });
        }

        /// <summary>
        /// <c> → +11.5 +15%</c> when the fleet roll moves the number.
        /// The percent keeps the line's stat colour. <paramref name="higherIsBetter"/>
        /// is retained so drain callers stay source-compatible; it no longer recolors.
        /// </summary>
        static bool TryBuildFleetArrow(
            float original,
            float adjusted,
            bool richText,
            bool higherIsBetter,
            out string arrow)
        {
            arrow = string.Empty;
            if (Mathf.Abs(original) < 0.0001f)
                return false;
            if (Mathf.Abs(adjusted - original) < 0.05f)
                return false;

            float signed = ((adjusted / original) - 1f) * 100f;
            if (Mathf.Abs(signed) <= 0.05f)
                return false;

            string body = Mathf.Abs(signed).ToString("0.#", CultureInfo.InvariantCulture);
            string pct = signed > 0f ? "+" + body + "%" : "-" + body + "%";
            _ = higherIsBetter;
            if (richText)
                arrow = " → +" + FormatStatValue(adjusted) + " " + pct;
            else
            {
                arrow = " → +" + FormatStatValue(adjusted) + " (" + pct + ")";
            }

            return true;
        }

        static void TryQueue(List<AbilityLine> lines, float value, string label, int colorIndex)
        {
            if (Mathf.Abs(value) < 0.05f)
                return;
            lines.Add(new AbilityLine
            {
                Text = $"+{FormatStatValue(value)} {label}",
                ColorIndex = colorIndex,
            });
        }

        /// <summary>
        /// <c>+6 base Move → +1.5 cumulative</c> when hull gain differs from the part's base;
        /// otherwise <c>+6 base Move</c>.
        /// </summary>
        static void TryQueueBaseAndCumulative(
            List<AbilityLine> lines,
            float baseValue,
            float cumulativeValue,
            string label,
            int colorIndex)
        {
            if (Mathf.Abs(baseValue) < 0.05f && Mathf.Abs(cumulativeValue) < 0.05f)
                return;

            if (Mathf.Abs(baseValue) < 0.05f)
            {
                lines.Add(new AbilityLine
                {
                    Text = $"+{FormatStatValue(cumulativeValue)} {label} cumulative",
                    ColorIndex = colorIndex,
                });
                return;
            }

            if (Mathf.Abs(cumulativeValue - baseValue) < 0.05f)
            {
                lines.Add(new AbilityLine
                {
                    Text = $"+{FormatStatValue(baseValue)} base {label}",
                    ColorIndex = colorIndex,
                });
                return;
            }

            lines.Add(new AbilityLine
            {
                Text = $"+{FormatStatValue(baseValue)} base {label} → +{FormatStatValue(cumulativeValue)} cumulative",
                ColorIndex = colorIndex,
            });
        }

        static void TryQueueNote(List<AbilityLine> lines, string note, int colorIndex)
        {
            if (string.IsNullOrWhiteSpace(note))
                return;
            lines.Add(new AbilityLine
            {
                Text = note,
                ColorIndex = colorIndex,
            });
        }

        static Color ResolveAbilityLineColor(int colorIndex)
        {
            // Mirror ShipAbilityCategoryColors.PowerBreakdown pairs without a UI assembly ref.
            switch (Mathf.Clamp(colorIndex, 0, 9))
            {
                case 0:
                case 1:
                    return new Color(0.9f, 0.35f, 0.2f, 1f);
                case 2:
                case 3:
                    return new Color(0.2f, 0.85f, 0.4f, 1f);
                case 4:
                case 5:
                    return new Color(0.95f, 0.8f, 0.2f, 1f);
                case 6:
                case 7:
                    return new Color(0.2f, 0.7f, 0.95f, 1f);
                default:
                    return new Color(0.65f, 0.4f, 0.9f, 1f);
            }
        }

        private static void TryAddFleetLine(
            List<string> lines, string label, float original, float adjusted, int maxLines)
        {
            if (lines.Count >= maxLines || (Mathf.Abs(original) < 0.05f && Mathf.Abs(adjusted) < 0.05f))
                return;
            if (TryBuildFleetArrow(original, adjusted, richText: false, higherIsBetter: true, out string arrow))
                lines.Add($"{label} +{FormatStatValue(original)}{arrow}");
            else
                lines.Add($"{label} +{FormatStatValue(Mathf.Abs(adjusted) >= 0.05f ? adjusted : original)}");
        }

        private static void TryAddLine(List<string> lines, string label, float value, int maxLines)
        {
            if (lines.Count >= maxLines || Mathf.Abs(value) < 0.05f)
                return;
            lines.Add($"{label} +{FormatStatValue(value)}");
        }

        private static string FormatStatValue(float value)
        {
            if (Mathf.Abs(value - Mathf.Round(value)) < 0.05f)
                return Mathf.RoundToInt(value).ToString();
            return value.ToString("0.#");
        }

        /// <summary>
        /// Full Extra Level hover copy for compact GEAR tiles.
        /// A fleet roll prints the catalog number, then the adjusted number.
        /// One short formula line — not the full hull essay.
        /// </summary>
        public static string BuildExtraLevelTooltipRichText(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family,
            int planetOrHullBankIndex = -1)
        {
            if (entry == null)
                return string.Empty;

            var sb = new StringBuilder(320);
            sb.Append("<b>EXTRA LEVEL</b>\n");
            bool weapon = ShipComponentAbilityStats.IsWeaponComponent(entry.componentId);
            bool propulsion = ShipComponentAbilityStats.IsPropulsionComponent(entry.componentId);
            ShipComponentAbilityStats adjusted = GetEffectiveStatsForDisplay(
                entry, shipLevel, family, planetOrHullBankIndex);
            ShipComponentAbilityStats original = BulletBankProfileUtility.ApplyProfileToComponentStats(
                GetEffectiveStatsAtShipLevel(entry.stats, shipLevel, entry.componentId),
                entry, family, planetOrHullBankIndex);
            int lv = Mathf.Max(1, shipLevel);

            void Line(string label, float before, float after, bool abilityOnly)
            {
                if (Mathf.Abs(before) < 0.05f && Mathf.Abs(after) < 0.05f)
                    return;
                if (TryBuildFleetArrow(before, after, richText: true, higherIsBetter: true, out string arrow))
                    sb.Append('+').Append(FormatStatValue(before)).Append(' ').Append(label).Append(arrow);
                else
                    sb.Append('+').Append(FormatStatValue(after)).Append(' ').Append(label);
                sb.Append("  <color=#7EC8FF>·  Extra Lv ");
                if (abilityOnly)
                    sb.Append("(ability only)</color>\n");
                else if (weapon && (label == "Fire Power" || label == "Fire Rate" || label == "Bullet Range"))
                    sb.Append("(Base + PerExtra × shipLv)</color>\n");
                else
                    sb.Append("(Base + PerExtra × shipLv)</color>\n");
            }

            Line("Fire Power", original.firePower, adjusted.firePower, false);
            Line("Fire Rate", original.fireRate, adjusted.fireRate, false);
            Line("Bullet Range", original.bulletRange, adjusted.bulletRange, false);
            Line("Bullet Speed", original.bulletSpeed, adjusted.bulletSpeed, weapon);
            Line("Health", original.healthCap, adjusted.healthCap, false);
            Line("Energy", original.energyCap, adjusted.energyCap, false);
            Line("Move", original.moveSpeed, adjusted.moveSpeed, false);
            Line("Accel", original.accelerationCap, adjusted.accelerationCap, false);
            float turnBefore = GetAddedGearTurnGain(entry, lv, family, applyFamilyFleet: false);
            float turnGain = GetAddedGearTurnGain(entry, lv, family, applyFamilyFleet: true);
            if (Mathf.Abs(turnBefore) >= 0.05f || Mathf.Abs(turnGain) >= 0.05f)
            {
                if (TryBuildFleetArrow(turnBefore, turnGain, richText: true, higherIsBetter: true, out string turnArrow))
                    sb.Append('+').Append(FormatStatValue(turnBefore)).Append(" Turn").Append(turnArrow);
                else
                    sb.Append('+').Append(FormatStatValue(turnGain)).Append(" Turn");
                sb.Append("  <color=#7EC8FF>·  Extra Lv (PerExtra × shipLv)</color>\n");
            }
            Line("Ramming", original.rammingPower, adjusted.rammingPower, false);
            Line("Gem Cap", original.maxGems, adjusted.maxGems, false);
            Line("Troop Cap", original.maxPeople, adjusted.maxPeople, false);

            if (propulsion)
            {
                TryGetPropulsionCumulativeGain(family, entry, lv, out float cumMove, out float cumAccel);
                if (Mathf.Abs(cumMove) > 0.01f || Mathf.Abs(cumAccel) > 0.01f)
                {
                    sb.Append("<color=#AAEEDD>Hull gain  Move +")
                        .Append(FormatStatValue(cumMove))
                        .Append("  Accel +")
                        .Append(FormatStatValue(cumAccel))
                        .Append("</color>\n");
                }
            }

            sb.Append("<color=#5B7A94>Lv ").Append(lv).Append(" purchase preview.</color>");
            return sb.ToString().TrimEnd();
        }
    }

}