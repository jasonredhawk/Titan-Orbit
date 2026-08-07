using System;
using System.Collections.Generic;
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
        /// Applies family ship-tier growth to every base stat:
        /// <c>effective = base × (1 + (shipLevel−1) × growthFraction)</c>.
        /// Default growth is <see cref="ShipFamilyDefinition.DefaultShipLevelStatGrowthFraction"/> (10%).
        /// Optional mobility penalties on move / accel / turn from <see cref="ShipCargoMobilitySettings"/>.
        /// <para>
        /// [TITAN-ORBIT] <c>*PerAbilityLevel</c> fields are passed through unchanged — they are
        /// bottom-HUD ability steps authored on parts / ProfileSet, not ship-tier curves.
        /// </para>
        /// <para>
        /// [TITAN-ORBIT] Intentional exception: <c>bulletSpeed</c> does <b>not</b> grow with ship level.
        /// Faster bullets come from attribute upgrades / Shard cards, not ship tier.
        /// </para>
        /// <para>
        /// [TITAN-ORBIT] <c>bulletRange</c> <b>does</b> grow with ship level (unlike bulletSpeed).
        /// </para>
        /// </summary>
        public static ShipComponentAbilityStats GetEffectiveStatsAtShipLevel(
            ShipComponentAbilityStats stats,
            int shipLevel,
            float shipLevelStatGrowthFraction = -1f)
        {
            int perLvl = Mathf.Max(0, shipLevel - 1);
            float frac = shipLevelStatGrowthFraction > 0.0001f
                ? shipLevelStatGrowthFraction
                : ShipFamilyDefinition.DefaultShipLevelStatGrowthFraction;
            // --- Ship-tier scale: +frac of base per level above 1 (family-tunable, default 10%) ---
            float tierMul = 1f + perLvl * frac;

            float moveAtLevel = stats.moveSpeed * tierMul;
            float accelAtLevel = stats.accelerationCap * tierMul;
            float turnAtLevel = stats.turnSpeed * tierMul;

            // --- Level mobility drag from settings (0% = leave linear growth alone) ---
            ShipCargoMobilitySettings mobility = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float moveScaled = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(
                moveAtLevel, perLvl, mobility.levelMaxSpeedPenaltyFractionPerLevel);
            float accelScaled = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(
                accelAtLevel, perLvl, mobility.levelAccelPenaltyFractionPerLevel);
            float turnScaled = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(
                turnAtLevel, perLvl, mobility.levelTurnPenaltyFractionPerLevel);

            // --- Tier growth on bases; pass through PerAbilityLevel for HUD ability apply ---
            // bulletSpeed: base only (no tierMul) — see method summary.
            return new ShipComponentAbilityStats
            {
                firePower = stats.firePower * tierMul,
                firePowerPerAbilityLevel = stats.firePowerPerAbilityLevel,
                bulletSpeed = stats.bulletSpeed,
                bulletSpeedPerAbilityLevel = stats.bulletSpeedPerAbilityLevel,
                bulletRange = stats.bulletRange * tierMul,
                bulletRangePerAbilityLevel = stats.bulletRangePerAbilityLevel,
                fireRate = stats.fireRate * tierMul,
                fireRatePerAbilityLevel = stats.fireRatePerAbilityLevel,
                rammingPower = stats.rammingPower * tierMul,
                rammingPowerPerAbilityLevel = stats.rammingPowerPerAbilityLevel,
                healthCap = stats.healthCap * tierMul,
                healthCapPerAbilityLevel = stats.healthCapPerAbilityLevel,
                healthRegen = stats.healthRegen * tierMul,
                healthRegenPerAbilityLevel = stats.healthRegenPerAbilityLevel,
                energyCap = stats.energyCap * tierMul,
                energyCapPerAbilityLevel = stats.energyCapPerAbilityLevel,
                energyRegen = stats.energyRegen * tierMul,
                energyRegenPerAbilityLevel = stats.energyRegenPerAbilityLevel,
                moveSpeed = moveScaled,
                moveSpeedPerAbilityLevel = stats.moveSpeedPerAbilityLevel,
                accelerationCap = accelScaled,
                accelerationCapPerAbilityLevel = stats.accelerationCapPerAbilityLevel,
                extraSpeedPercent = stats.extraSpeedPercent * tierMul,
                extraSpeedPercentPerAbilityLevel = stats.extraSpeedPercentPerAbilityLevel,
                extraSpeedEnergyDrain = stats.extraSpeedEnergyDrain * tierMul,
                extraSpeedEnergyDrainPerAbilityLevel = stats.extraSpeedEnergyDrainPerAbilityLevel,
                turnSpeed = turnScaled,
                turnSpeedPerAbilityLevel = stats.turnSpeedPerAbilityLevel,
                maxGems = stats.maxGems * tierMul,
                maxGemsPerAbilityLevel = stats.maxGemsPerAbilityLevel,
                tractorBeamDistance = stats.tractorBeamDistance * tierMul,
                tractorBeamDistancePerAbilityLevel = stats.tractorBeamDistancePerAbilityLevel,
                tractorBeamPower = stats.tractorBeamPower * tierMul,
                tractorBeamPowerPerAbilityLevel = stats.tractorBeamPowerPerAbilityLevel,
                maxPeople = stats.maxPeople * tierMul,
                maxPeoplePerAbilityLevel = stats.maxPeoplePerAbilityLevel
            };
        }

        /// <summary>Single scalar power number for gem pricing (sum of breakdown categories).</summary>
        public static float GetComponentPowerScore(ShipFamilyComponentEntry entry, int shipLevel, ShipFamilyDefinition family = null)
        {
            if (entry == null)
                return 0f;
            return GetPowerBreakdown(entry, shipLevel, family).Total;
        }

        public static ShipFamilyPowerScoreBreakdown GetPowerBreakdown(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null)
        {
            if (entry == null)
                return default;
            ShipComponentAbilityStats effective = GetEffectiveStatsForDisplay(entry, shipLevel, family);
            return ShipFamilyPowerScoreBreakdown.FromSummedShipStats(effective);
        }

        /// <summary>Level-scaled stats plus optional bullet-bank profile overlay (currently no-op stub).</summary>
        public static ShipComponentAbilityStats GetEffectiveStatsForDisplay(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null)
        {
            if (entry == null)
                return default;
            float growth = family != null
                ? family.ResolveShipLevelStatGrowthFraction()
                : ShipFamilyDefinition.DefaultShipLevelStatGrowthFraction;
            ShipComponentAbilityStats effective = GetEffectiveStatsAtShipLevel(entry.stats, shipLevel, growth);
            return BulletBankProfileUtility.ApplyProfileToComponentStats(effective, entry, family);
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

        /// <summary>Resolves player-facing title with displayName → formatted componentId fallback.</summary>
        public static string GetDisplayName(ShipFamilyComponentEntry entry)
        {
            if (entry == null)
                return "Component";
            if (!string.IsNullOrWhiteSpace(entry.displayName))
                return entry.displayName.Trim();
            return FormatComponentId(entry.componentId);
        }

        public static string FormatComponentId(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return "Component";
            string id = componentId.Trim().Replace('_', ' ');
            return id;
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
                case "Arm": return "\u2692";
                default:
                    return entry.componentId.Trim().Substring(0, 1).ToUpperInvariant();
            }
        }

        public static Sprite GetMenuPreviewSprite(ShipFamilyDefinition family, ShipFamilyComponentEntry entry, TeamManager.Team team = TeamManager.Team.None)
        {
            if (entry != null && entry.GetMenuPreviewSprite(team) != null)
                return entry.GetMenuPreviewSprite(team);
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
        public static string GetStatsDescription(ShipFamilyComponentEntry entry, int shipLevel, ShipFamilyDefinition family = null, int maxLines = 4)
        {
            if (entry == null)
                return string.Empty;

            ShipComponentAbilityStats s = GetEffectiveStatsForDisplay(entry, shipLevel, family);
            var lines = new List<string>(maxLines);
            // --- Pick top non-zero stats in fixed priority order ---
            TryAddLine(lines, "Fire", s.firePower, maxLines);
            TryAddLine(lines, "Bullet", s.bulletSpeed, maxLines);
            TryAddLine(lines, "Range", s.bulletRange, maxLines);
            TryAddLine(lines, "Fire rate", s.fireRate, maxLines);
            TryAddLine(lines, "Ram", s.rammingPower, maxLines);
            TryAddLine(lines, "Health", s.healthCap, maxLines);
            TryAddLine(lines, "Regen", s.healthRegen, maxLines);
            TryAddLine(lines, "Energy", s.energyCap, maxLines);
            TryAddLine(lines, "E.Regen", s.energyRegen, maxLines);
            TryAddLine(lines, "Speed", s.moveSpeed, maxLines);
            TryAddLine(lines, "Accel", s.accelerationCap, maxLines);
            TryAddLine(lines, "Turn", s.turnSpeed, maxLines);
            TryAddLine(lines, "Gems", s.maxGems, maxLines);
            TryAddLine(lines, "Tractor", s.tractorBeamDistance, maxLines);
            TryAddLine(lines, "People", s.maxPeople, maxLines);

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
            int maxLines = 14)
        {
            return BuildAbilityDescription(entry, shipLevel, family, maxLines, richText: true);
        }

        /// <summary>Plain-text variant (legacy equipment panel / short tooltips).</summary>
        public static string BuildAbilityDescriptionPlain(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            int maxLines = 8)
        {
            return BuildAbilityDescription(entry, shipLevel, family, maxLines, richText: false);
        }

        /// <summary>
        /// Builds a short ability list: authored value, and for propulsion also the real hull gain
        /// (e.g. <c>+6 base Move → +1.5 cumulative</c>). No long stacking essays.
        /// </summary>
        static string BuildAbilityDescription(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family,
            int maxLines,
            bool richText)
        {
            if (entry == null)
                return string.Empty;

            ShipComponentAbilityStats s = GetEffectiveStatsForDisplay(entry, shipLevel, family);
            entry.EnsureStatCategories();
            if (entry.statCategories == null || entry.statCategories.Count == 0)
                entry.statCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(entry.componentId);

            var lines = new List<AbilityLine>(16);
            bool isPropulsion = ShipComponentAbilityStats.IsPropulsionComponent(entry.componentId);
            bool isEngine = ShipFamilyPartTypes.IsEngineLikeName(entry.componentId);

            // --- Offense / Health / Energy (full gain = authored; they sum) ---
            TryQueue(lines, s.firePower, "Fire Power", 0);
            TryQueue(lines, s.bulletSpeed, "Bullet Speed", 1);
            TryQueue(lines, s.bulletRange, "Bullet Range", 1);
            TryQueue(lines, s.fireRate, "Fire Rate", 1);
            TryQueue(lines, s.rammingPower, "Ramming", 0);
            TryQueue(lines, s.healthCap, "Health Cap", 2);
            TryQueue(lines, s.healthRegen, "Health Regen", 3);
            TryQueue(lines, s.energyCap, "Energy Cap", 4);
            TryQueue(lines, s.energyRegen, "Energy Regen", 5);

            // --- Movement: base on the part + cumulative hull change from aggregation ---
            if (isPropulsion)
            {
                TryGetPropulsionCumulativeGain(
                    family, entry, shipLevel, out float cumulativeMove, out float cumulativeAccel);

                TryQueueBaseAndCumulative(lines, s.moveSpeed, cumulativeMove, "Move", 6);
                TryQueueBaseAndCumulative(lines, s.accelerationCap, cumulativeAccel, "Accel", 6);
            }
            else
            {
                TryQueue(lines, s.moveSpeed, "Move", 6);
                TryQueue(lines, s.accelerationCap, "Accel", 6);
            }

            TryQueue(lines, s.turnSpeed, "Turn", 7);

            // [TITAN-ORBIT] Absolute OVERDRIVE energy/sec on engines (not a multiplier of ExtraSpeedPercent).
            if (isEngine)
                TryQueue(lines, s.extraSpeedEnergyDrain, "Overdrive Drain/s", 4);

            if (isEngine && s.extraSpeedPercent > 0.0001f)
            {
                float pct = s.extraSpeedPercent * 100f;
                TryQueueNote(
                    lines,
                    $"Overdrive +{FormatStatValue(pct)}% speed/thrust",
                    6);
            }

            // --- Capacity ---
            TryQueue(lines, s.maxGems, "Gem Cap", 8);
            TryQueue(lines, s.tractorBeamDistance, "Tractor Dist", 8);
            TryQueue(lines, s.tractorBeamPower, "Tractor Power", 8);
            TryQueue(lines, s.maxPeople, "People Cap", 9);

            if (lines.Count == 0)
                return richText ? "<color=#888888>—</color>" : "No stat bonus";

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
        /// How much hull top speed / accel this propulsion part contributes:
        /// aggregated result with the part minus without it (same rules as flight).
        /// Accel sums fully; Move uses primary + half Speed/Lvl on extras.
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
    }

}
