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

        /// <summary>
        /// PerExtra × ship level only, no Base and no ability purchases.
        /// This is the gain of a stacked extra before the moon's fleet roll.
        /// </summary>
        public static ShipComponentAbilityStats GetMarginalStatsAtShipLevel(
            ShipComponentAbilityStats stats,
            int shipLevel,
            string componentId)
        {
            var attrs = default(ShipAbilityLevelCounts);
            bool weapon = ShipComponentAbilityStats.IsWeaponComponent(componentId);
            return ShipComponentExtraLevelMath.EvaluatePool(
                stats,
                componentCount: 1,
                shipLevel,
                in attrs,
                isWeaponPool: weapon,
                includeBase: false);
        }

        /// <summary>
        /// Post-bonus total for one part, the same number a primary card puts in FINAL.
        /// Base plus PerExtra × ship level, then this part's fleet roll and bullet bank.
        /// Used for the OLD column: the primary this card would replace.
        /// </summary>
        public static ShipComponentAbilityStats GetDisplayedAdjustedStats(
            ShipComponentAbilityStats raw,
            int shipLevel,
            string componentId,
            ShipFamilySpecialBonuses fleet,
            ShipFamilyComponentEntry entry,
            ShipFamilyDefinition family,
            int planetOrHullBankIndex)
        {
            ShipComponentAbilityStats leveled = GetEffectiveStatsAtShipLevel(raw, shipLevel, componentId);
            ShipComponentAbilityStats rolled = fleet.IsIdentity ? leveled : fleet.Apply(leveled);
            if (entry == null)
                return rolled;
            return BulletBankProfileUtility.ApplyProfileToComponentStats(
                rolled, entry, family, planetOrHullBankIndex);
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
        /// Each stat is one row: VAL, BONUS, and FINAL.
        /// <see cref="ShipComponentStackAggregation.GearCardRole.Each"/> fills those columns
        /// with PerExtra × ship level (then the moon roll) and labels the header EACH.
        /// <see cref="ShipComponentStackAggregation.GearCardRole.Primary"/> keeps the full
        /// part numbers, labels the header BEST, and adds OLD when this card would
        /// replace a different primary. FINAL stays that part's post-bonus total.
        /// When hull aggregation keeps less than FINAL, a following hull row is the amount
        /// that lands on the ship. Each cards omit that row — the columns are already the gain.
        /// </summary>
        public static string BuildAbilityDescriptionRichText(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family = null,
            int maxLines = 14,
            int planetOrHullBankIndex = -1,
            ShipComponentStackAggregation.GearCardRole role = ShipComponentStackAggregation.GearCardRole.Unclassified,
            bool hasDisplacedPrimary = false,
            ShipComponentAbilityStats displacedAdjusted = default,
            bool oldPrimaryIsThisCard = false,
            bool includeBulletTypeLine = true)
        {
            return BuildAbilityDescription(
                entry, shipLevel, family, maxLines, richText: true, planetOrHullBankIndex, role,
                hasDisplacedPrimary, displacedAdjusted, oldPrimaryIsThisCard, includeBulletTypeLine);
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
        /// Builds the Gear-tab card description as a column grid.
        /// The bullet type sits under the card title, so this grid is stats only
        /// unless <paramref name="includeBulletTypeLine"/> is true.
        /// </summary>
        static string BuildAbilityDescription(
            ShipFamilyComponentEntry entry,
            int shipLevel,
            ShipFamilyDefinition family,
            int maxLines,
            bool richText,
            int planetOrHullBankIndex = -1,
            ShipComponentStackAggregation.GearCardRole role = ShipComponentStackAggregation.GearCardRole.Unclassified,
            bool hasDisplacedPrimary = false,
            ShipComponentAbilityStats displacedAdjusted = default,
            bool oldPrimaryIsThisCard = false,
            bool includeBulletTypeLine = true)
        {
            if (entry == null)
                return string.Empty;

            // Extra Level + bullet bank. Fleet is applied next so the card can
            // show the catalog number and the moon-adjusted number side by side.
            // EACH cards show PerExtra × ship level instead of the part's full total.
            bool each = role == ShipComponentStackAggregation.GearCardRole.Each;
            ShipComponentAbilityStats fullLeveled = GetEffectiveStatsAtShipLevel(
                entry.stats, shipLevel, entry.componentId);
            ShipComponentAbilityStats leveled = each
                ? GetMarginalStatsAtShipLevel(entry.stats, shipLevel, entry.componentId)
                : fullLeveled;
            ShipFamilySpecialBonuses fleet = family != null
                ? family.ResolveSpecialBonuses()
                : ShipFamilySpecialBonuses.Identity;
            ShipComponentAbilityStats fullOriginal = BulletBankProfileUtility.ApplyProfileToComponentStats(
                fullLeveled, entry, family, planetOrHullBankIndex);
            ShipComponentAbilityStats fullAdjusted = fleet.IsIdentity
                ? fullOriginal
                : BulletBankProfileUtility.ApplyProfileToComponentStats(
                    fleet.Apply(fullLeveled), entry, family, planetOrHullBankIndex);
            ShipComponentAbilityStats original = each
                ? BulletBankProfileUtility.ApplyProfileToComponentStats(
                    leveled, entry, family, planetOrHullBankIndex)
                : fullOriginal;
            ShipComponentAbilityStats adjusted = each
                ? (fleet.IsIdentity
                    ? original
                    : BulletBankProfileUtility.ApplyProfileToComponentStats(
                        fleet.Apply(leveled), entry, family, planetOrHullBankIndex))
                : fullAdjusted;
            entry.EnsureStatCategories();
            if (entry.statCategories == null || entry.statCategories.Count == 0)
                entry.statCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(entry.componentId);

            var lines = new List<GearStatRow>(16);
            bool isPropulsion = ShipComponentAbilityStats.IsPropulsionComponent(entry.componentId);
            bool isEngine = ShipFamilyPartTypes.IsEngineLikeName(entry.componentId);

            // The gear card prints the bullet type under the title. Other callers
            // can still ask for the old line above the stat grid.
            if (includeBulletTypeLine)
                TryQueueWeaponBulletType(lines, entry, family, planetOrHullBankIndex);

            // Every primary card keeps OLD, including when that number matches this card.
            // DLTA is this part's post-bonus total minus OLD. Extras keep a real FINAL.
            bool showOld = role == ShipComponentStackAggregation.GearCardRole.Primary;
            bool hasOldBaseline = hasDisplacedPrimary || oldPrimaryIsThisCard;

            // Shown numbers are the marginal gain on EACH cards, the full part otherwise.
            // A row stays when the full part has the stat, even if the extra step is +0.
            void Queue(float fullOriginal, float fullAdjusted, float showOriginal, float showAdjusted,
                string label, int colorIndex, float oldAdjusted = 0f, bool higherIsBetter = true)
            {
                bool fullVisible = Mathf.Abs(fullOriginal) >= 0.05f || Mathf.Abs(fullAdjusted) >= 0.05f;
                bool showVisible = Mathf.Abs(showOriginal) >= 0.05f || Mathf.Abs(showAdjusted) >= 0.05f;
                if (!fullVisible && !showVisible)
                    return;
                float baseline = oldPrimaryIsThisCard ? showAdjusted : oldAdjusted;
                TryQueueFleet(
                    lines, showOriginal, showAdjusted, label, colorIndex, higherIsBetter,
                    showEvenIfSmall: fullVisible, showOld: showOld, oldAdjusted: baseline,
                    hasOldBaseline: hasOldBaseline, finalIsDelta: showOld);
            }

            // --- Offense / Health / Energy (full gain = authored; they sum) ---
            // Short labels stay inside the name column on an 8-up gear card.
            // The hover tooltip still spells the full stat name.
            Queue(fullOriginal.firePower, fullAdjusted.firePower, original.firePower, adjusted.firePower, "Fire", 0, displacedAdjusted.firePower);
            Queue(fullOriginal.bulletSpeed, fullAdjusted.bulletSpeed, original.bulletSpeed, adjusted.bulletSpeed, "B.Speed", 1, displacedAdjusted.bulletSpeed);
            Queue(fullOriginal.bulletRange, fullAdjusted.bulletRange, original.bulletRange, adjusted.bulletRange, "Range", 1, displacedAdjusted.bulletRange);
            Queue(fullOriginal.fireRate, fullAdjusted.fireRate, original.fireRate, adjusted.fireRate, "Rate", 1, displacedAdjusted.fireRate);
            Queue(fullOriginal.rammingPower, fullAdjusted.rammingPower, original.rammingPower, adjusted.rammingPower, "Ram", 0, displacedAdjusted.rammingPower);
            Queue(fullOriginal.healthCap, fullAdjusted.healthCap, original.healthCap, adjusted.healthCap, "HP", 2, displacedAdjusted.healthCap);
            Queue(fullOriginal.healthRegen, fullAdjusted.healthRegen, original.healthRegen, adjusted.healthRegen, "HP/s", 3, displacedAdjusted.healthRegen);
            Queue(fullOriginal.energyCap, fullAdjusted.energyCap, original.energyCap, adjusted.energyCap, "Energy", 4, displacedAdjusted.energyCap);
            Queue(fullOriginal.energyRegen, fullAdjusted.energyRegen, original.energyRegen, adjusted.energyRegen, "EN/s", 5, displacedAdjusted.energyRegen);

            // --- Movement: catalog number, moon bonus, then hull gain when it differs ---
            // EACH already prints the purchase gain, so it does not add a hull child row.
            if (!each && isPropulsion)
            {
                TryGetPropulsionCumulativeGain(
                    family, entry, shipLevel, out float cumulativeMove, out float cumulativeAccel);

                TryQueueFleetOrHull(lines, original.moveSpeed, adjusted.moveSpeed, cumulativeMove, "Move", 6, showOld, displacedAdjusted.moveSpeed, hasOldBaseline, oldPrimaryIsThisCard);
                TryQueueFleetOrHull(lines, original.accelerationCap, adjusted.accelerationCap, cumulativeAccel, "Accel", 6, showOld, displacedAdjusted.accelerationCap, hasOldBaseline, oldPrimaryIsThisCard);
            }
            else
            {
                Queue(fullOriginal.moveSpeed, fullAdjusted.moveSpeed, original.moveSpeed, adjusted.moveSpeed, "Move", 6, displacedAdjusted.moveSpeed);
                Queue(fullOriginal.accelerationCap, fullAdjusted.accelerationCap, original.accelerationCap, adjusted.accelerationCap, "Accel", 6, displacedAdjusted.accelerationCap);
            }

            // Same extra-part rule as Move / Accel: the hull keeps its Turn Base.
            // Buying this piece adds only its Turn PerExtra × ship level.
            // The row's VAL / FINAL are the part's turn number; the hull child row is that PerExtra gain.
            if (!each)
            {
                float turnAdjusted = GetAddedGearTurnGain(entry, shipLevel, family, applyFamilyFleet: true);
                TryQueueFleetOrHull(lines, original.turnSpeed, adjusted.turnSpeed, turnAdjusted, "Turn", 7, showOld, displacedAdjusted.turnSpeed, hasOldBaseline, oldPrimaryIsThisCard);
            }
            else
            {
                Queue(fullOriginal.turnSpeed, fullAdjusted.turnSpeed, original.turnSpeed, adjusted.turnSpeed, "Turn", 7, displacedAdjusted.turnSpeed);
            }

            // [TITAN-ORBIT] Absolute OVERDRIVE energy/sec on engines (not a multiplier of ExtraSpeedPercent).
            // More drain is worse. The percent stays in this row's energy colour — it is not recolored.
            if (isEngine)
                Queue(
                    fullOriginal.extraSpeedEnergyDrain,
                    fullAdjusted.extraSpeedEnergyDrain,
                    original.extraSpeedEnergyDrain,
                    adjusted.extraSpeedEnergyDrain,
                    "OD/s",
                    4,
                    displacedAdjusted.extraSpeedEnergyDrain,
                    higherIsBetter: false);

            if (isEngine && (fullAdjusted.extraSpeedPercent > 0.0001f || adjusted.extraSpeedPercent > 0.0001f))
            {
                float pctOriginal = original.extraSpeedPercent * 100f;
                float pctAdjusted = adjusted.extraSpeedPercent * 100f;
                TryQueuePercent(
                    lines, pctOriginal, pctAdjusted, "OD", 6,
                    showOld, displacedAdjusted.extraSpeedPercent * 100f,
                    hasOldBaseline, oldPrimaryIsThisCard);
            }

            // --- Capacity ---
            Queue(fullOriginal.maxGems, fullAdjusted.maxGems, original.maxGems, adjusted.maxGems, "Gems", 8, displacedAdjusted.maxGems);
            Queue(fullOriginal.tractorBeamDistance, fullAdjusted.tractorBeamDistance, original.tractorBeamDistance, adjusted.tractorBeamDistance, "Tract", 8, displacedAdjusted.tractorBeamDistance);
            Queue(fullOriginal.tractorBeamPower, fullAdjusted.tractorBeamPower, original.tractorBeamPower, adjusted.tractorBeamPower, "T.Pwr", 8, displacedAdjusted.tractorBeamPower);
            Queue(fullOriginal.maxPeople, fullAdjusted.maxPeople, original.maxPeople, adjusted.maxPeople, "Troops", 9, displacedAdjusted.maxPeople);

            if (lines.Count == 0)
                return richText ? "<color=#888888>—</color>" : "No stat bonus";

            // --- Compile visible lines ---
            // maxLines is a card-height budget (14 on Gear, 8 on short tooltips).
            // The column header is extra so it does not eat a stat row.
            int budget = Mathf.Max(1, maxLines);
            if (lines.Count > budget)
                lines.RemoveRange(budget, lines.Count - budget);

            string header = role == ShipComponentStackAggregation.GearCardRole.Each
                ? "EACH"
                : role == ShipComponentStackAggregation.GearCardRole.Primary
                    ? "BEST"
                    : "STAT";
            bool showOldColumn = role == ShipComponentStackAggregation.GearCardRole.Primary;
            return FormatGearStatGrid(lines, richText, header, showOldColumn);
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

        /// <summary>
        /// One Gear-card stat row. Banner lines (bullet type) span the card.
        /// Numeric rows fill VAL / BONUS / FINAL. A dim row is the hull contribution
        /// sitting under its parent stat.
        /// </summary>
        struct GearStatRow
        {
            public string Label;
            public string Old;
            public string Value;
            public string Bonus;
            public string Final;
            public int ColorIndex;
            public bool Banner;
            public string BannerText;
            /// <summary>Hull child row — quieter than the parent stat, FINAL stays bright.</summary>
            public bool Dim;
        }

        /// <summary>Empty numeric cell. Keeps the column occupied so the eye can scan down.</summary>
        const string GearStatEmptyCell = "—";

        /// <summary>
        /// Adds <c>Type  Fireballs</c> (offense tint) when this part is a weapon
        /// and the VFX bank has a category name for it. Non-weapons skip this line.
        /// The line sits above the column header — it is a name, not a VAL / BONUS / FINAL triple.
        /// </summary>
        static void TryQueueWeaponBulletType(
            List<GearStatRow> lines,
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
            lines.Add(new GearStatRow
            {
                Banner = true,
                BannerText = "Type  " + typeName,
                ColorIndex = 1,
            });
        }

        /// <summary>
        /// One grid row. VAL is the part before this moon's fleet roll, BONUS is that
        /// roll's percent (or — when the moon does not change it), FINAL is the part after the roll.
        /// <paramref name="higherIsBetter"/> stays so drain callers are explicit; the percent
        /// uses the row colour either way.
        /// </summary>
        static void TryQueueFleet(
            List<GearStatRow> lines,
            float original,
            float adjusted,
            string label,
            int colorIndex,
            bool higherIsBetter = true,
            bool showEvenIfSmall = false,
            bool showOld = false,
            float oldAdjusted = 0f,
            bool hasOldBaseline = false,
            bool finalIsDelta = false)
        {
            // Drain growing is worse, but the card keeps one colour per stat so columns stay scannable.
            _ = higherIsBetter;
            if (!showEvenIfSmall && Mathf.Abs(original) < 0.05f && Mathf.Abs(adjusted) < 0.05f)
                return;

            float shownValue = Mathf.Abs(original) >= 0.05f ? original : adjusted;
            float shownFinal = Mathf.Abs(adjusted) >= 0.05f ? adjusted : original;
            lines.Add(new GearStatRow
            {
                Label = label,
                Old = FormatOldCell(showOld, hasOldBaseline, oldAdjusted),
                Value = FormatSignedStat(shownValue),
                Bonus = TryFormatFleetPercent(original, adjusted, out string percent) ? percent : GearStatEmptyCell,
                Final = finalIsDelta
                    ? FormatDeltaCell(shownFinal, hasOldBaseline, oldAdjusted)
                    : FormatSignedStat(shownFinal),
                ColorIndex = colorIndex,
            });
        }

        /// <summary>
        /// OLD cell. Null when this card has no old column. A real number when a
        /// primary exists, including zero and including a match with this card.
        /// An em dash when this type has no owned primary yet.
        /// </summary>
        static string FormatOldCell(bool showOld, bool hasBaseline, float oldAdjusted, bool percent = false)
        {
            if (!showOld)
                return null;
            if (!hasBaseline)
                return GearStatEmptyCell;
            string text = FormatSignedStat(oldAdjusted);
            return percent ? text + "%" : text;
        }

        /// <summary>
        /// Post-bonus total minus the old primary. With no owned primary, the
        /// whole post-bonus total is the gain.
        /// </summary>
        static string FormatDeltaCell(float shownFinal, bool hasBaseline, float oldAdjusted, bool percent = false)
        {
            float delta = hasBaseline ? shownFinal - oldAdjusted : shownFinal;
            string text = FormatSignedStat(delta);
            return percent ? text + "%" : text;
        }

        /// <summary>
        /// Percent stat (overdrive speed). The % sign lives on VAL and FINAL because the
        /// stat itself is a percent. BONUS is still the moon's extra percent on top.
        /// </summary>
        static void TryQueuePercent(
            List<GearStatRow> lines,
            float originalPercent,
            float adjustedPercent,
            string label,
            int colorIndex,
            bool showOld = false,
            float oldAdjustedPercent = 0f,
            bool hasOldBaseline = false,
            bool oldPrimaryIsThisCard = false)
        {
            if (Mathf.Abs(originalPercent) < 0.05f && Mathf.Abs(adjustedPercent) < 0.05f)
                return;

            float shownValue = Mathf.Abs(originalPercent) >= 0.05f ? originalPercent : adjustedPercent;
            float shownFinal = Mathf.Abs(adjustedPercent) >= 0.05f ? adjustedPercent : originalPercent;
            float baseline = oldPrimaryIsThisCard ? shownFinal : oldAdjustedPercent;
            lines.Add(new GearStatRow
            {
                Label = label,
                Old = FormatOldCell(showOld, hasOldBaseline, baseline, percent: true),
                Value = FormatSignedStat(shownValue) + "%",
                Bonus = TryFormatFleetPercent(originalPercent, adjustedPercent, out string percent)
                    ? percent
                    : GearStatEmptyCell,
                Final = showOld
                    ? FormatDeltaCell(shownFinal, hasOldBaseline, baseline, percent: true)
                    : FormatSignedStat(shownFinal) + "%",
                ColorIndex = colorIndex,
            });
        }

        /// <summary>
        /// Same columns as <see cref="TryQueueFleet"/>. When hull aggregation changes the
        /// gain (extra engines do not add a second Base, turn gear is PerExtra only),
        /// a dim child row puts that ship contribution in FINAL.
        /// </summary>
        static void TryQueueFleetOrHull(
            List<GearStatRow> lines,
            float original,
            float adjusted,
            float hullGain,
            string label,
            int colorIndex,
            bool showOld = false,
            float oldAdjusted = 0f,
            bool hasOldBaseline = false,
            bool oldPrimaryIsThisCard = false)
        {
            bool hasPart = Mathf.Abs(original) >= 0.05f || Mathf.Abs(adjusted) >= 0.05f;
            bool hasHull = Mathf.Abs(hullGain) >= 0.05f;
            // Compare the hull figure to the post-bonus part number (the row's FINAL).
            float compare = Mathf.Abs(adjusted) >= 0.05f ? adjusted : original;
            bool hullDiffers = hasHull && Mathf.Abs(hullGain - compare) >= 0.05f;

            if (!hasPart && !hasHull)
                return;

            // No catalog number — the only figure is what the ship gains.
            if (!hasPart)
            {
                lines.Add(new GearStatRow
                {
                    Label = label,
                    Old = FormatOldCell(showOld, hasOldBaseline, oldPrimaryIsThisCard ? hullGain : oldAdjusted),
                    Value = GearStatEmptyCell,
                    Bonus = "hull",
                    Final = showOld
                        ? FormatDeltaCell(hullGain, hasOldBaseline, oldPrimaryIsThisCard ? hullGain : oldAdjusted)
                        : FormatSignedStat(hullGain),
                    ColorIndex = colorIndex,
                });
                return;
            }

            float baseline = oldPrimaryIsThisCard ? adjusted : oldAdjusted;
            TryQueueFleet(
                lines, original, adjusted, label, colorIndex,
                showOld: showOld, oldAdjusted: baseline, hasOldBaseline: hasOldBaseline,
                finalIsDelta: showOld);
            if (!hullDiffers)
                return;

            // Child row: same colour, quieter, FINAL is the amount added to the ship.
            lines.Add(new GearStatRow
            {
                Label = "hull",
                Old = showOld ? GearStatEmptyCell : null,
                Value = GearStatEmptyCell,
                Bonus = GearStatEmptyCell,
                Final = FormatSignedStat(hullGain),
                ColorIndex = colorIndex,
                Dim = true,
            });
        }

        /// <summary>
        /// Moon fleet percent, e.g. <c>+20%</c> or <c>-5%</c>.
        /// False when the roll does not move the number (1×, or a zero base).
        /// </summary>
        static bool TryFormatFleetPercent(float original, float adjusted, out string percent)
        {
            percent = null;
            if (Mathf.Abs(original) < 0.0001f)
                return false;
            if (Mathf.Abs(adjusted - original) < 0.05f)
                return false;

            float signed = ((adjusted / original) - 1f) * 100f;
            if (Mathf.Abs(signed) <= 0.05f)
                return false;

            string body = Mathf.Abs(signed).ToString("0.#", CultureInfo.InvariantCulture);
            percent = signed > 0f ? "+" + body + "%" : "-" + body + "%";
            return true;
        }

        /// <summary>
        /// <c> → +11.5 +15%</c> when the fleet roll moves the number.
        /// Used by the extra-level hover and the short plain tooltip, not the gear grid.
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
            if (!TryFormatFleetPercent(original, adjusted, out string percent))
                return false;

            _ = higherIsBetter;
            if (richText)
                arrow = " → +" + FormatStatValue(adjusted) + " " + percent;
            else
                arrow = " → +" + FormatStatValue(adjusted) + " (" + percent + ")";

            return true;
        }

        /// <summary>
        /// Paints queued rows as a column grid.
        /// Rich text uses TextMeshPro position stops so VAL / BONUS / FINAL stay aligned
        /// when the card width changes. Plain text pads the same columns for the legacy tooltip.
        /// </summary>
        static string FormatGearStatGrid(
            List<GearStatRow> lines,
            bool richText,
            string statHeader = "STAT",
            bool showOldColumn = false)
        {
            if (lines == null || lines.Count == 0)
                return richText ? "<color=#888888>—</color>" : "No stat bonus";

            bool anyNumeric = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].Banner)
                {
                    anyNumeric = true;
                    break;
                }
            }

            // Stops are a fraction of the description width, so an 8-up card and a
            // wider window keep the same columns. The stat name sits at the left edge.
            // OLD sits in front of VAL only when this primary would replace another piece.
            string oldPos = "22%";
            string valuePos = showOldColumn ? "40%" : "36%";
            string bonusPos = showOldColumn ? "60%" : "58%";
            string finalPos = "80%";

            var sb = new StringBuilder(384);

            // --- Banner lines (bullet type) sit above the header ---
            for (int i = 0; i < lines.Count; i++)
            {
                GearStatRow row = lines[i];
                if (!row.Banner)
                    continue;
                AppendGearGridLineStart(sb, richText, row.ColorIndex, dim: false);
                sb.Append(row.BannerText ?? string.Empty);
                AppendGearGridLineEnd(sb, richText);
            }

            if (!anyNumeric)
                return sb.ToString();

            // --- Column header ---
            // [TITAN-ORBIT] Ice caption, slightly smaller than the numbers, so the stats stay the loud part.
            if (sb.Length > 0)
                sb.Append('\n');
            if (richText)
            {
                sb.Append("<size=85%><color=#9EC7F2>");
                sb.Append(string.IsNullOrEmpty(statHeader) ? "STAT" : statHeader);
                if (showOldColumn)
                    sb.Append("<pos=").Append(oldPos).Append(">OLD");
                sb.Append("<pos=").Append(valuePos).Append(">VAL");
                sb.Append("<pos=").Append(bonusPos).Append(">BONUS");
                sb.Append("<pos=").Append(finalPos).Append(showOldColumn ? ">DLTA" : ">FINAL");
                sb.Append("</color></size>");
            }
            else
            {
                sb.Append(FitGearCell(string.IsNullOrEmpty(statHeader) ? "STAT" : statHeader, 8, right: false));
                if (showOldColumn)
                    sb.Append(FitGearCell("OLD", 6, right: true));
                sb.Append(FitGearCell("VAL", 7, right: true));
                sb.Append(FitGearCell("BONUS", 7, right: true));
                sb.Append(FitGearCell(showOldColumn ? "DLTA" : "FINAL", 7, right: true));
            }

            // --- One row per stat ---
            for (int i = 0; i < lines.Count; i++)
            {
                GearStatRow row = lines[i];
                if (row.Banner)
                    continue;

                AppendGearGridLineStart(sb, richText, row.ColorIndex, row.Dim);
                if (richText)
                {
                    sb.Append(row.Label ?? string.Empty);
                    if (showOldColumn)
                        sb.Append("<pos=").Append(oldPos).Append('>').Append(row.Old ?? GearStatEmptyCell);
                    sb.Append("<pos=").Append(valuePos).Append('>').Append(row.Value ?? GearStatEmptyCell);
                    sb.Append("<pos=").Append(bonusPos).Append('>').Append(row.Bonus ?? GearStatEmptyCell);
                    // FINAL is the number the player came to compare — bold, full strength
                    // even on the quieter hull child row.
                    sb.Append("<pos=").Append(finalPos).Append("><alpha=#FF><b>");
                    sb.Append(row.Final ?? GearStatEmptyCell);
                    sb.Append("</b>");
                }
                else
                {
                    sb.Append(FitGearCell(row.Label, 8, right: false));
                    if (showOldColumn)
                        sb.Append(FitGearCell(row.Old ?? GearStatEmptyCell, 6, right: true));
                    sb.Append(FitGearCell(row.Value, 7, right: true));
                    sb.Append(FitGearCell(row.Bonus, 7, right: true));
                    sb.Append(FitGearCell(row.Final, 7, right: true));
                }

                AppendGearGridLineEnd(sb, richText);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Opens one grid line. Rich lines wrap the row in its category colour.
        /// A newline is inserted when this is not the first line in <paramref name="sb"/>.
        /// </summary>
        static void AppendGearGridLineStart(StringBuilder sb, bool richText, int colorIndex, bool dim)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            if (!richText)
                return;

            Color c = ResolveAbilityLineColor(colorIndex);
            sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>');
            if (dim)
                sb.Append("<alpha=#99>");
        }

        /// <summary>Closes the category colour tag opened by <see cref="AppendGearGridLineStart"/>.</summary>
        static void AppendGearGridLineEnd(StringBuilder sb, bool richText)
        {
            if (richText)
                sb.Append("</color>");
        }

        /// <summary>
        /// Pads a plain-text cell to <paramref name="width"/> characters.
        /// Longer labels are cut so a wide name cannot shove the next column over.
        /// </summary>
        static string FitGearCell(string text, int width, bool right)
        {
            if (string.IsNullOrEmpty(text))
                text = string.Empty;
            if (text.Length > width)
                text = text.Substring(0, width);
            return right ? text.PadLeft(width) : text.PadRight(width);
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
        /// Gear-grid number with a sign, e.g. <c>+6</c> or <c>-2</c>.
        /// Near-zero values still print <c>+0</c> — callers skip those rows before this runs.
        /// </summary>
        static string FormatSignedStat(float value)
        {
            if (value < -0.05f)
                return "-" + FormatStatValue(Mathf.Abs(value));
            return "+" + FormatStatValue(value);
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