using System.Globalization;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Shared Orbit Menu copy for FAMILY STATS (non-identity <see cref="ShipFamilySpecialBonuses"/>)
    /// and family display names. Presentation-only — no ECS writes.
    /// Hide the family-stat block when <see cref="ShipFamilySpecialBonuses.IsIdentity"/>.
    /// Tree cards use <see cref="FormatFamilyDisplayName"/> (title case); header rails use
    /// <see cref="FormatFamilyCaption"/> (uppercase).
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
        /// Title-case family label for upgrade-tree cards (AstroEagle → Astro Eagle).
        /// Unlike <see cref="FormatFamilyCaption"/> this is not uppercased — it sits under
        /// the hull name as a quieter second line, just above the buy chip.
        /// </summary>
        /// <param name="family">Store-planet or chassis family. Null / blank id → empty.</param>
        /// <returns>Spaced words, or empty when there is nothing to show.</returns>
        public static string FormatFamilyDisplayName(ShipFamilyDefinition family)
        {
            if (family == null || string.IsNullOrWhiteSpace(family.familyId))
                return string.Empty;
            string split = Core.DisplayNameFormatting.SplitCamelCase(family.familyId.Trim());
            return string.IsNullOrWhiteSpace(split) ? string.Empty : split;
        }

        /// <summary>
        /// Family line for one upgrade-tree slot. Regular chassis use
        /// <paramref name="fallbackFamily"/> (the docked planet's ladder). MEGA hulls use the
        /// catalog visual line so CraizanStar reads as Craizan Star — same camel-split as
        /// CosmicShark → Cosmic Shark.
        /// </summary>
        /// <param name="chassisId">Ladder or MEGA chassis token. May be null.</param>
        /// <param name="fallbackFamily">Planet / ship family when the slot is not a MEGA.</param>
        public static string FormatFamilyDisplayNameForChassis(string chassisId, ShipFamilyDefinition fallbackFamily)
        {
            // --- MEGA visual line ---
            // [TITAN-ORBIT] MEGA ids are MEGA_007, not Family_Index. The gameplay family on
            // the planet header is not this hull's art line — Craizan / Leopard / Okamoto is.
            if (MegaShipCatalog.IsMegaChassisId(chassisId))
            {
                MegaShipCatalog mega = MegaShipCatalog.Load();
                if (mega != null
                    && mega.TryGetEntryByChassisId(chassisId, out MegaShipCatalogEntry entry)
                    && entry != null)
                {
                    string visual = Core.DisplayNameFormatting.SplitCamelCase(entry.visualFamily.ToString());
                    if (!string.IsNullOrWhiteSpace(visual))
                        return visual;
                }
            }

            return FormatFamilyDisplayName(fallbackFamily);
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
            AppendIfNotOne(sb, "TROOPS", bonuses.maxPeopleMul);
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
