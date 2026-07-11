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
        /// Applies per-level growth to every stat field, including mobility penalty on move/turn at higher levels.
        /// <paramref name="shipLevel"/> 1 = base stats only; level 7 adds six steps of *PerLevel fields.
        /// </summary>
        public static ShipComponentAbilityStats GetEffectiveStatsAtShipLevel(ShipComponentAbilityStats stats, int shipLevel)
        {
            int perLvl = Mathf.Max(0, shipLevel - 1);
            float moveAtLevel = stats.moveSpeed + stats.moveSpeedPerLevel * perLvl;
            float turnAtLevel = stats.turnSpeed + stats.turnSpeedPerLevel * perLvl;

            // --- Linear growth on most stats; move/turn also pass through mobility penalty curve ---
            return new ShipComponentAbilityStats
            {
                firePower = stats.firePower + stats.firePowerPerLevel * perLvl,
                firePowerPerLevel = stats.firePowerPerLevel,
                bulletSpeed = stats.bulletSpeed,
                bulletSpeedPerLevel = stats.bulletSpeedPerLevel,
                fireRate = stats.fireRate + stats.fireRatePerLevel * perLvl,
                fireRatePerLevel = stats.fireRatePerLevel,
                rammingPower = stats.rammingPower + stats.rammingPowerPerLevel * perLvl,
                rammingPowerPerLevel = stats.rammingPowerPerLevel,
                healthCap = stats.healthCap + stats.healthCapPerLevel * perLvl,
                healthCapPerLevel = stats.healthCapPerLevel,
                healthRegen = stats.healthRegen + stats.healthRegenPerLevel * perLvl,
                healthRegenPerLevel = stats.healthRegenPerLevel,
                energyCap = stats.energyCap + stats.energyCapPerLevel * perLvl,
                energyCapPerLevel = stats.energyCapPerLevel,
                energyRegen = stats.energyRegen + stats.energyRegenPerLevel * perLvl,
                energyRegenPerLevel = stats.energyRegenPerLevel,
                moveSpeed = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(moveAtLevel, perLvl),
                moveSpeedPerLevel = stats.moveSpeedPerLevel,
                accelerationCap = stats.accelerationCap + stats.accelerationCapPerLevel * perLvl,
                accelerationCapPerLevel = stats.accelerationCapPerLevel,
                turnSpeed = ShipPropulsionAggregation.ApplyShipLevelMobilityScale(turnAtLevel, perLvl),
                turnSpeedPerLevel = stats.turnSpeedPerLevel,
                maxGems = stats.maxGems + stats.maxGemsPerLevel * perLvl,
                maxGemsPerLevel = stats.maxGemsPerLevel,
                tractorBeamDistance = stats.tractorBeamDistance + stats.tractorBeamDistancePerLevel * perLvl,
                tractorBeamDistancePerLevel = stats.tractorBeamDistancePerLevel,
                tractorBeamPower = stats.tractorBeamPower + stats.tractorBeamPowerPerLevel * perLvl,
                tractorBeamPowerPerLevel = stats.tractorBeamPowerPerLevel,
                maxPeople = stats.maxPeople + stats.maxPeoplePerLevel * perLvl,
                maxPeoplePerLevel = stats.maxPeoplePerLevel
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
            ShipComponentAbilityStats effective = GetEffectiveStatsAtShipLevel(entry.stats, shipLevel);
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
