using System.Globalization;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Shared Orbit Menu copy for FAMILY STATS (non-identity <see cref="ShipFamilySpecialBonuses"/>)
    /// and a one-line family display name. Presentation-only — no ECS writes.
    /// Hide the family-stat block when <see cref="ShipFamilySpecialBonuses.IsIdentity"/>.
    /// </summary>
    public static class FamilyStatHudCopy
    {
        /// <summary>Uppercase HUD family name from familyId (AstroEagle → ASTRO EAGLE).</summary>
        public static string FormatFamilyCaption(ShipFamilyDefinition family)
        {
            if (family == null || string.IsNullOrWhiteSpace(family.familyId))
                return "UNKNOWN FAMILY";
            string split = Core.DisplayNameFormatting.SplitCamelCase(family.familyId.Trim());
            return string.IsNullOrWhiteSpace(split) ? "UNKNOWN FAMILY" : split.ToUpperInvariant();
        }

        /// <summary>
        /// Compact rail of only ≠1 family muls, e.g. <c>MOVE ×1.2  GEMS ×1.5</c>.
        /// Empty when the family has no special bonuses.
        /// </summary>
        public static string FormatNonIdentityBonuses(in ShipFamilySpecialBonuses bonuses)
        {
            if (bonuses.IsIdentity)
                return string.Empty;

            var sb = new StringBuilder(96);
            AppendIfNotOne(sb, "MOVE", bonuses.moveSpeedMul);
            AppendIfNotOne(sb, "ACCEL", bonuses.accelerationMul);
            AppendIfNotOne(sb, "TURN", bonuses.turnSpeedMul);
            AppendIfNotOne(sb, "FP", bonuses.firePowerMul);
            AppendIfNotOne(sb, "RATE", bonuses.fireRateMul);
            AppendIfNotOne(sb, "BSPD", bonuses.bulletSpeedMul);
            AppendIfNotOne(sb, "RANGE", bonuses.bulletRangeMul);
            AppendIfNotOne(sb, "RAM", bonuses.rammingMul);
            AppendIfNotOne(sb, "HP", bonuses.healthCapMul);
            AppendIfNotOne(sb, "H.REG", bonuses.healthRegenMul);
            AppendIfNotOne(sb, "EN", bonuses.energyCapMul);
            AppendIfNotOne(sb, "E.REG", bonuses.energyRegenMul);
            AppendIfNotOne(sb, "OD%", bonuses.extraSpeedPercentMul);
            AppendIfNotOne(sb, "OD DRAIN", bonuses.extraSpeedEnergyDrainMul);
            AppendIfNotOne(sb, "GEMS", bonuses.maxGemsMul);
            AppendIfNotOne(sb, "PEOPLE", bonuses.maxPeopleMul);
            AppendIfNotOne(sb, "TRACTOR", bonuses.tractorDistanceMul);
            AppendIfNotOne(sb, "T.PWR", bonuses.tractorPowerMul);
            return sb.ToString().Trim();
        }

        /// <summary>True when the family should show a FAMILY STATS rail.</summary>
        public static bool HasVisibleFamilyStats(ShipFamilyDefinition family) =>
            family != null && !family.specialBonuses.IsIdentity;

        static void AppendIfNotOne(StringBuilder sb, string label, float mul)
        {
            float m = mul > 0.0001f ? mul : 1f;
            if (Mathf.Abs(m - 1f) < 0.0001f)
                return;
            if (sb.Length > 0)
                sb.Append("  ");
            sb.Append(label).Append(" ×").Append(m.ToString("0.##", CultureInfo.InvariantCulture));
        }
    }
}
