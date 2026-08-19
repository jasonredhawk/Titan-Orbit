using System.Globalization;
using System.Text;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Player-facing copy and number formatting for the ten power-bar stats.
    /// Hover tips explain what a slot is (Fire Power = sustained DPS, Gem Cap = cargo
    /// hold, …) and append a small RANK 1 line naming the catalog leader.
    /// <para>
    /// Presentation-only — no ECS reads. Paired with <see cref="ShipPowerBarStatTooltip"/>
    /// (Orbit Menu bars) and <see cref="ShipAbilityStatBreakdown"/> (in-flight chips).
    /// </para>
    /// </summary>
    public static class ShipPowerBarStatCopy
    {
        /// <summary>Reused for RANK 1 / body builds so hover does not allocate a fresh builder.</summary>
        static readonly StringBuilder s_Sb = new StringBuilder(512);

        /// <summary>Muted steel for the RANK 1 caption so it stays secondary to the stat details.</summary>
        const string HexRank = "5B7A94";

        /// <summary>Body line under RANK 1 (hull name + value).</summary>
        const string HexRankBody = "8AA0B8";

        /// <summary>Mint when the hovered hull is the catalog winner.</summary>
        const string HexThisHull = "7DFFB2";

        /// <summary>
        /// What each slot means. Index matches
        /// <see cref="ShipAbilityCategoryColors.PowerBreakdownStatFullLabels"/>.
        /// Slot 0 is labeled Fire Power in the HUD but the bar and chips show sustained DPS.
        /// </summary>
        public static readonly string[] Descriptions =
        {
            "Sustained damage per second from every gun — damage per shot times shots per second, then summed. A slow cannon and a rapid gun can tie here even when raw hit damage looks different.",
            "How fast shots travel. Faster bullets reach the target sooner and are harder to dodge. This does not change damage.",
            "Maximum hull hit points. When this reaches zero the ship is destroyed. Extra Level and a larger cockpit raise the ceiling.",
            "Hit points recovered per second while you are below the cap. Slow regen still matters in long fights.",
            "How much energy the ship can store. Weapons spend energy; a bigger battery lets you fire longer before the clip empties.",
            "Energy recovered per second. High regen keeps guns firing after the first burst.",
            "Cruise speed after mass tax from gems, troops, and hull size. Extra Level and engines raise the cap.",
            "How quickly the nose yaws, in degrees per second after mass tax. Higher turn lets you aim and orbit tighter.",
            "Maximum gems this hull can carry. Cockpit and cargo holds add to the total. Upgrade-tree purchase prices use gem cap as the seed.",
            "Maximum people this hull can carry for colony and transport work."
        };

        /// <summary>
        /// TMP suffix painted after the number (same tokens as the in-flight chips).
        /// Slot 0 uses DPS/s so Fire Power is not read as damage-per-hit.
        /// </summary>
        /// <param name="statIndex">Power-bar slot 0–9.</param>
        public static string GetUnitSuffix(int statIndex)
        {
            switch (statIndex)
            {
                case 0: return " DPS/s";
                case 3:
                case 5: return "/s";
                case 7: return "°/s";
                default: return string.Empty;
            }
        }

        /// <summary>Short + full label, e.g. <c>FIRE POWER (FP)</c>.</summary>
        public static string GetTitleLine(int statIndex)
        {
            string full = GetFullLabel(statIndex).ToUpperInvariant();
            string code = GetShortLabel(statIndex);
            return string.IsNullOrEmpty(code) ? full : full + " (" + code + ")";
        }

        /// <summary>ODEMC category for a slot (Offense … Capacity).</summary>
        public static string GetCategoryTitle(int statIndex)
        {
            int pair = statIndex / 2;
            return ShipAbilityCategoryColors.GetPowerBreakdownCategoryTitle(pair).ToUpperInvariant();
        }

        /// <summary>Player-facing paragraph for one slot. Empty when the index is out of range.</summary>
        public static string GetDescription(int statIndex)
        {
            if (statIndex < 0 || statIndex >= Descriptions.Length)
                return string.Empty;
            return Descriptions[statIndex];
        }

        /// <summary>Invariant chip-style number (<c>12.5</c>, <c>88</c>).</summary>
        public static string FormatValue(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Full power-bar hover body: title, this hull vs catalog max, what the stat is,
        /// then a small RANK 1 line. Call on pointer-enter — not every frame.
        /// </summary>
        /// <param name="statIndex">Hovered slot 0–9.</param>
        /// <param name="thisValue">This card's display-stat value (same as the bar fill).</param>
        /// <param name="maxValue">Pool max used as the fill denominator.</param>
        /// <param name="megaPool">True when the bar used MEGA catalog maxes.</param>
        /// <param name="thisChassisId">Optional chassis on this card; when it matches RANK 1 we say "this hull".</param>
        public static string BuildPowerBarTipBody(
            int statIndex,
            float thisValue,
            float maxValue,
            bool megaPool,
            string thisChassisId)
        {
            StringBuilder sb = s_Sb;
            sb.Clear();

            // --- Title ---
            sb.Append("<color=#E0ECF8><b>").Append(GetTitleLine(statIndex)).Append("</b></color>");
            sb.AppendLine();
            sb.Append("<color=#").Append(HexRank).Append('>')
                .Append(GetCategoryTitle(statIndex))
                .Append("</color>");
            sb.AppendLine();
            sb.AppendLine();

            // --- This hull vs catalog max ---
            // [TITAN-ORBIT] Percent matches the colourful fill (this / poolMax).
            float safeMax = Mathf.Max(maxValue, ShipPowerBarStatMaxes.MinDenominator);
            int pct = Mathf.Clamp(Mathf.RoundToInt(100f * thisValue / safeMax), 0, 999);
            string unit = GetUnitSuffix(statIndex);
            sb.Append("<color=#AAEEDD>This card  ")
                .Append(FormatValue(thisValue))
                .Append(unit)
                .Append("</color>");
            sb.AppendLine();
            sb.Append("<color=#").Append(HexRankBody).Append(">Catalog max  ")
                .Append(FormatValue(maxValue))
                .Append(unit)
                .Append("  ·  ")
                .Append(pct)
                .Append("%</color>");
            sb.AppendLine();
            sb.AppendLine();

            // --- What the stat is ---
            string desc = GetDescription(statIndex);
            if (!string.IsNullOrEmpty(desc))
            {
                sb.Append("<color=#D0D8E4>").Append(desc).Append("</color>");
                sb.AppendLine();
            }

            AppendRankOneFooter(sb, statIndex, megaPool, thisChassisId, thisValue);
            return sb.ToString();
        }

        /// <summary>
        /// Small RANK 1 block used by power-bar tips and in-flight ability chips.
        /// Stays last so the stat details stay the main read.
        /// </summary>
        /// <param name="sb">Tip builder already holding the main body.</param>
        /// <param name="statIndex">Slot 0–9.</param>
        /// <param name="megaPool">Which catalog pool to query.</param>
        /// <param name="thisChassisId">Hovered / live hull; matching RANK 1 says "this hull".</param>
        /// <param name="thisValue">Optional; when it meets the pool max we also treat this card as RANK 1.</param>
        public static void AppendRankOneFooter(
            StringBuilder sb,
            int statIndex,
            bool megaPool,
            string thisChassisId,
            float thisValue = -1f)
        {
            if (sb == null)
                return;

            ShipPowerBarStatLeader leader = ShipFamilyPowerBarNorm.GetStatLeader(statIndex, megaPool);
            if (!leader.IsValid)
                return;

            bool thisIsLeader = leader.MatchesChassis(thisChassisId);
            if (!thisIsLeader && thisValue >= 0f)
            {
                // Value tie: this card's fill is already at the catalog ceiling.
                thisIsLeader = thisValue + 0.0001f >= leader.value;
            }

            sb.AppendLine();
            ShipStatTooltipChrome.AppendSectionBanner(sb, "RANK 1", HexRank);

            string unit = GetUnitSuffix(statIndex);
            if (thisIsLeader)
            {
                sb.Append("<color=#").Append(HexThisHull).Append('>')
                    .Append("This hull holds the catalog max  ")
                    .Append(FormatValue(leader.value))
                    .Append(unit)
                    .Append("</color>");
                sb.AppendLine();
                return;
            }

            sb.Append("<color=#").Append(HexRankBody).Append('>')
                .Append(FormatLeaderLine(in leader, unit))
                .Append("</color>");
            sb.AppendLine();
        }

        /// <summary>
        /// Compact identity string that leads with the <b>ship</b>, then its family.
        /// Regular: <c>Thumper · AstroEagle · L3 · 42</c>.
        /// MEGA: <c>Void Reaper · Galactic Leopard · 88.1 DPS/s</c> (no L7 — the pool is MEGA-only).
        /// </summary>
        public static string FormatLeaderLine(in ShipPowerBarStatLeader leader, string unit)
        {
            string family = string.IsNullOrWhiteSpace(leader.familyId) ? string.Empty : leader.familyId.Trim();
            string ship = ResolveShipLabel(in leader);
            bool mega = MegaShipCatalog.IsMegaChassisId(leader.chassisId);

            var line = new StringBuilder(64);
            line.Append(ship);

            // Family / MEGA visual line. Regular hulls skip it when the ship token
            // already contains the family (NightAye16). MEGAs always include the
            // visual family so Craizan / Leopard / Okamoto is visible next to the hull name.
            if (!string.IsNullOrEmpty(family)
                && !string.Equals(family, "MEGA", System.StringComparison.OrdinalIgnoreCase))
            {
                bool alreadyInName = !mega
                    && ship.IndexOf(family, System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!alreadyInName)
                    line.Append(" · ").Append(family);
            }

            if (!mega && leader.treeLevel > 0)
                line.Append(" · L").Append(leader.treeLevel);

            line.Append(" · ").Append(FormatValue(leader.value)).Append(unit ?? string.Empty);
            return line.ToString();
        }

        /// <summary>
        /// Ship token for RANK 1. Hull name first (prefab / authored), then chassis id,
        /// never family-only when a chassis is known.
        /// </summary>
        static string ResolveShipLabel(in ShipPowerBarStatLeader leader)
        {
            if (!string.IsNullOrWhiteSpace(leader.hullName))
                return leader.hullName.Trim();
            if (!string.IsNullOrWhiteSpace(leader.chassisId))
                return leader.chassisId.Trim();
            if (!string.IsNullOrWhiteSpace(leader.familyId))
                return leader.familyId.Trim();
            return "Unknown hull";
        }

        static string GetFullLabel(int statIndex)
        {
            string[] labels = ShipAbilityCategoryColors.PowerBreakdownStatFullLabels;
            if (statIndex < 0 || statIndex >= labels.Length)
                return "Stat";
            return labels[statIndex];
        }

        static string GetShortLabel(int statIndex)
        {
            string[] labels = ShipAbilityCategoryColors.PowerBreakdownStatLabels;
            if (statIndex < 0 || statIndex >= labels.Length)
                return string.Empty;
            return labels[statIndex];
        }
    }
}
