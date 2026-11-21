using System.Globalization;
using System.Text;
using TitanOrbit.Data;
using TitanOrbit.Game;
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
        /// Full power-bar hover body: title, this card vs catalog max, what the stat is,
        /// then a small RANK 1 line. Call on pointer-enter — not every frame.
        /// </summary>
        /// <param name="statIndex">Hovered slot 0–9.</param>
        /// <param name="thisValue">This card's display-stat value (same as the bar fill).</param>
        /// <param name="maxValue">Pool max used as the fill denominator.</param>
        /// <param name="pool">Regular ships, Titans, or gear components.</param>
        /// <param name="thisChassisId">Hull id or component leader key. Matching RANK 1 says “this hull” or “this part”.</param>
        /// <param name="componentShipLevel">Gear only. Extra Level of the component ceiling.</param>
        /// <param name="partCategory">Gear only. Same-profile bucket so RANK 1 stays inside engines, wings, weapons, and so on.</param>
        public static string BuildPowerBarTipBody(
            int statIndex,
            float thisValue,
            float maxValue,
            ShipPowerBarComparisonPool pool,
            string thisChassisId,
            int componentShipLevel = 1,
            string partCategory = null)
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

            AppendRankOneFooter(sb, statIndex, pool, thisChassisId, thisValue, componentShipLevel, partCategory);
            return sb.ToString();
        }

        /// <summary>
        /// Small RANK 1 block used by power-bar tips and in-flight ability chips.
        /// Stays last so the stat details stay the main read.
        /// Regular chips and Titan chips keep their own catalog. This overload
        /// picks the pool from <paramref name="megaPool"/>.
        /// </summary>
        public static void AppendRankOneFooter(
            StringBuilder sb,
            int statIndex,
            bool megaPool,
            string thisChassisId,
            float thisValue = -1f)
        {
            AppendRankOneFooter(
                sb,
                statIndex,
                megaPool ? ShipPowerBarComparisonPool.Titans : ShipPowerBarComparisonPool.RegularShips,
                thisChassisId,
                thisValue);
        }

        /// <summary>
        /// Small RANK 1 block for one comparison pool. Gear uses
        /// <see cref="ShipPowerBarComparisonPool.Components"/> and names a part.
        /// </summary>
        /// <param name="sb">Tip builder already holding the main body.</param>
        /// <param name="statIndex">Slot 0–9.</param>
        /// <param name="pool">Which catalog to query.</param>
        /// <param name="thisChassisId">Hovered hull or part. Matching RANK 1 says this card holds the max.</param>
        /// <param name="thisValue">Optional; when it meets the pool max we also treat this card as RANK 1.</param>
        /// <param name="componentShipLevel">Gear only. Extra Level of the component ceiling.</param>
        /// <param name="partCategory">Gear only. Same-profile bucket (Engine, Wing, …). Null mixes every part type.</param>
        public static void AppendRankOneFooter(
            StringBuilder sb,
            int statIndex,
            ShipPowerBarComparisonPool pool,
            string thisChassisId,
            float thisValue = -1f,
            int componentShipLevel = 1,
            string partCategory = null)
        {
            if (sb == null)
                return;

            if (pool == ShipPowerBarComparisonPool.Unset)
                pool = ShipPowerBarComparisonPool.RegularShips;

            ShipPowerBarStatLeader leader = ShipFamilyPowerBarNorm.GetStatLeader(
                statIndex, pool, componentShipLevel, partCategory);
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
                // Gear says "part". Ship tree says "hull". Same mint so the winner still pops.
                string holder = pool == ShipPowerBarComparisonPool.Components
                    ? "This part holds the catalog max  "
                    : "This hull holds the catalog max  ";
                sb.Append("<color=#").Append(HexThisHull).Append('>')
                    .Append(holder)
                    .Append(FormatValue(leader.value))
                    .Append(unit)
                    .Append("</color>");
                if (pool == ShipPowerBarComparisonPool.Components)
                {
                    float bonus = PlanetBonusForLeaderKey(thisChassisId, statIndex);
                    if (bonus <= 0.0001f)
                        bonus = leader.planetBonusMul;
                    // Planet name reuses the winner mint. The roll is its own tag.
                    AppendGearPlanetCallout(sb, thisChassisId, bonus, HexThisHull);
                }
                sb.AppendLine();
                return;
            }

            // The line paints its own colour tags so the planet roll can be
            // mint or red without swallowing the ship name or the stat number.
            sb.Append(FormatLeaderLine(in leader, unit, statIndex, HexRankBody));
            sb.AppendLine();
        }

        /// <summary>
        /// Compact identity string that leads with the <b>ship</b>, then its family.
        /// Regular: <c>Thumper · AstroEagle · L3 · 42</c>.
        /// Titan: <c>Void Reaper · Galactic Leopard · 88.1 DPS/s</c> (no L7 — the pool is Titan-only).
        /// Gear adds the one world whose part won this lane, then that roll:
        /// <c>Twin Engines · Hyper Falcon · Aetheris · +40% · 88</c>.
        /// <paramref name="bodyHex"/> colours the ship name and the stat number.
        /// The roll in the middle is mint or red and is not wrapped in that hex.
        /// Pass null for a plain string with no colour tags on the name.
        /// </summary>
        public static string FormatLeaderLine(
            in ShipPowerBarStatLeader leader,
            string unit,
            int statIndex = -1,
            string bodyHex = null)
        {
            string family = string.IsNullOrWhiteSpace(leader.familyId) ? string.Empty : leader.familyId.Trim();
            string ship = ResolveShipLabel(in leader);
            bool mega = MegaShipCatalog.IsMegaChassisId(leader.chassisId);

            var line = new StringBuilder(96);
            bool tintBody = !string.IsNullOrEmpty(bodyHex);
            if (tintBody)
                line.Append("<color=#").Append(bodyHex).Append('>');
            line.Append(ship);

            // Family / Titan visual line. Regular hulls skip it when the ship token
            // already contains the family (NightAye16). Titans always include the
            // visual family so Craizan / Leopard / Okamoto is visible next to the hull name.
            if (!string.IsNullOrEmpty(family)
                && !string.Equals(family, "MEGA", System.StringComparison.OrdinalIgnoreCase)
                && !string.Equals(family, MegaShipCatalog.DisplayClassShort, System.StringComparison.OrdinalIgnoreCase))
            {
                bool alreadyInName = !mega
                    && ship.IndexOf(family, System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!alreadyInName)
                    line.Append(" · ").Append(family);
            }

            if (!mega && leader.treeLevel > 0)
                line.Append(" · L").Append(leader.treeLevel);

            bool hasPlanetRoll = leader.planetBonusMul > 0.0001f;
            // Close the name colour before the roll so a green or red percent
            // cannot replace the ship name. The stat number opens that colour again.
            if (hasPlanetRoll && tintBody)
                line.Append("</color>");

            // Gear only. Same middot as the family, then the planet roll on this lane.
            if (hasPlanetRoll)
            {
                float bonus = statIndex >= 0
                    ? PlanetBonusForLeaderKey(leader.chassisId, statIndex)
                    : 0f;
                if (bonus <= 0.0001f)
                    bonus = leader.planetBonusMul;
                AppendGearPlanetCallout(line, leader.chassisId, bonus, bodyHex);
            }

            if (hasPlanetRoll && tintBody)
                line.Append("<color=#").Append(bodyHex).Append('>');
            line.Append(" · ").Append(FormatValue(leader.value)).Append(unit ?? string.Empty);
            if (tintBody)
                line.Append("</color>");
            return line.ToString();
        }

        /// <summary>
        /// The winning part's planet, then that world's roll on the hovered lane.
        /// One name only — other worlds that share the family are the same roll
        /// and stay off the line. Sits in the same middot list as the ship family.
        /// </summary>
        static void AppendGearPlanetCallout(StringBuilder line, string leaderKey, float bonusMul, string bodyHex)
        {
            if (line == null)
                return;

            // Planet name uses the same hue as the ship title. Each tag is opened
            // and closed here so it does not sit inside another colour tag.
            string planets = PlanetNamesForLeaderKey(leaderKey);
            if (!string.IsNullOrEmpty(planets))
            {
                if (!string.IsNullOrEmpty(bodyHex))
                    line.Append("<color=#").Append(bodyHex).Append('>');
                line.Append(" · ").Append(planets);
                if (!string.IsNullOrEmpty(bodyHex))
                    line.Append("</color>");
            }

            if (bonusMul > 0.0001f)
            {
                string callout = FamilyStatHudCopy.FormatMultiplierCallout(bonusMul);
                string tinted = FamilyStatHudCopy.TintSignedBonus(callout);
                if (tinted != callout)
                {
                    // +40% / -12% only. The name on either side stays bodyHex.
                    line.Append(" · ").Append(tinted);
                }
                else if (!string.IsNullOrEmpty(bodyHex))
                {
                    line.Append("<color=#").Append(bodyHex).Append("> · ")
                        .Append(callout).Append("</color>");
                }
                else
                {
                    line.Append(" · ").Append(callout);
                }
            }
        }

        /// <summary>
        /// One world name for the family in a gear leader key (<c>AstroEagle|Engine_1</c>).
        /// That key is the part that already won the lane (component × planet bonus).
        /// Empty for hull ids and before planets exist.
        /// </summary>
        static string PlanetNamesForLeaderKey(string leaderKey)
        {
            if (!TrySplitComponentLeaderKey(leaderKey, out string familyId))
                return string.Empty;

            int index = IndexOfFamily(familyId);
            if (index < 0)
                return string.Empty;
            return EcsGameBridge.FormatTopPlanetNameForFamilyConfigIndex(index);
        }

        /// <summary>
        /// This family's planet roll on one power-bar lane. 0 when the key is a hull.
        /// </summary>
        static float PlanetBonusForLeaderKey(string leaderKey, int statIndex)
        {
            if (!TrySplitComponentLeaderKey(leaderKey, out string familyId))
                return 0f;

            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            ShipFamilyDefinition family = config != null
                ? config.GetFamilyDefinitionByFamilyId(familyId)
                : null;
            if (family == null)
                return 0f;
            return family.ResolveSpecialBonuses().MultiplierForPowerBarStat(statIndex);
        }

        static bool TrySplitComponentLeaderKey(string leaderKey, out string familyId)
        {
            familyId = string.Empty;
            if (string.IsNullOrEmpty(leaderKey))
                return false;

            int bar = leaderKey.IndexOf('|');
            if (bar <= 0)
                return false;

            familyId = leaderKey.Substring(0, bar);
            return !string.IsNullOrEmpty(familyId);
        }

        static int IndexOfFamily(string familyId)
        {
            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            if (config?.families == null || string.IsNullOrEmpty(familyId))
                return -1;

            for (int i = 0; i < config.families.Count; i++)
            {
                string id = config.families[i]?.shipFamilyDefinition?.familyId;
                if (string.Equals(id, familyId, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
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
