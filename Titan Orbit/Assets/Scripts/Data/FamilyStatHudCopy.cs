using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Shared Orbit Menu copy for ship-family names and
    /// <see cref="ShipFamilySpecialBonuses"/> readouts.
    /// Presentation-only — no ECS writes.
    /// <para>
    /// Tree cards use <see cref="FormatFamilyDisplayName"/> (title case).
    /// Header rails use <see cref="FormatFamilyCaption"/> (uppercase).
    /// The upgrade-tree lineage matrix and the sidebar FAMILY BONUSES block
    /// both walk <see cref="CollectBonusRows"/> so every authored multiplier
    /// can be listed, including 1× baseline fields (shown as <c>1.00×</c>).
    /// Hull and Energy are separate groups so defense and power are not mixed.
    /// The same plate also lists bullet-type damage via <see cref="CollectBankDamageRows"/>.
    /// </para>
    /// </summary>
    public static class FamilyStatHudCopy
    {
        /// <summary>
        /// HUD grouping for one family bonus. Matches the five power-bar
        /// categories (Movement / Offense / Defense / Energy / Capacity) plus a
        /// BANK row for planet bullet-type damage. Camera height stays in Hold —
        /// it is presentation zoom, not a cargo stat, but it is still a lineage
        /// identity lever and should sit with the other non-combat extras.
        /// </summary>
        public enum BonusCategory
        {
            Mobility = 0,
            Combat = 1,
            Hull = 2,
            /// <summary>Energy cap / regen — split from Hull so defense and power are not one mixed strip.</summary>
            Energy = 3,
            Hold = 4,
            /// <summary>Planet / hull bullet-bank damage (FP + vs-target muls).</summary>
            Ordnance = 5
        }

        /// <summary>
        /// One family-bonus cell for the Orbit Menu lineage matrix.
        /// Built from a <see cref="ShipFamilySpecialBonuses"/> field.
        /// </summary>
        public struct BonusRow
        {
            /// <summary>Short telemetry tag (MOVE, RAM, CAM).</summary>
            public string ShortLabel;

            /// <summary>Full player-facing name (MOVE SPEED).</summary>
            public string FullLabel;

            /// <summary>Resolved multiplier (0 / unset becomes 1).</summary>
            public float Multiplier;

            /// <summary>
            /// Signed percent the player should read. Drain uses inverted
            /// polarity so a 0.8× drain is −20% (a boost).
            /// </summary>
            public float SignedPercent;

            /// <summary>True when the multiplier is approximately 1×.</summary>
            public bool IsIdentity;

            /// <summary>True when a lower multiplier helps the player (overdrive drain).</summary>
            public bool LowerIsBetter;

            /// <summary>
            /// True when the signed percent is a player-facing gain.
            /// Camera height is never a boost or a penalty — zoom only.
            /// </summary>
            public bool IsBoost;

            /// <summary>True when the signed percent is a player-facing loss.</summary>
            public bool IsPenalty;

            /// <summary>True for cameraHeightMul — neither good nor bad.</summary>
            public bool IsNeutral;

            /// <summary>Matrix row this cell belongs to.</summary>
            public BonusCategory Category;
        }

        /// <summary>TMP hex for a gain (ice green).</summary>
        public const string HexBoost = "59FA9E";

        /// <summary>TMP hex for a trade-off (amber).</summary>
        public const string HexPenalty = "F2B852";

        /// <summary>TMP hex for camera / zoom (ice blue).</summary>
        public const string HexNeutral = "7EC8FF";

        /// <summary>TMP hex for a 1× baseline value.</summary>
        public const string HexMuted = "5B7A94";

        /// <summary>Stock 1× copy shown on identity / near-identity cells.</summary>
        public const string NeutralBaseText = "1.00×";

        /// <summary>Ordered category captions for the lineage matrix.</summary>
        public static readonly string[] CategoryCaptions =
        {
            "MOVE",
            "COMBAT",
            "HULL",
            "ENERGY",
            "HOLD",
            "BANK"
        };

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
        /// Used by editor card-deck windows — the Orbit Menu lists every field instead.
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
            AppendIfNotOne(sb, "CAM", bonuses.cameraHeightMul);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Sidebar / hover list of every ≠1 bonus, one line each, full name + signed percent.
        /// Empty when the family is identity (no special modifiers).
        /// </summary>
        /// <param name="bonuses">Family special-bonus struct from the definition asset.</param>
        /// <returns>TMP rich text, or empty.</returns>
        public static string FormatListedBonusesRichText(in ShipFamilySpecialBonuses bonuses)
        {
            // --- Collect ≠1 rows ---
            s_ScratchRows.Clear();
            CollectBonusRows(bonuses, s_ScratchRows, includeIdentity: false);
            if (s_ScratchRows.Count == 0)
                return string.Empty;

            var sb = new StringBuilder(s_ScratchRows.Count * 40);
            for (int i = 0; i < s_ScratchRows.Count; i++)
            {
                BonusRow row = s_ScratchRows[i];
                if (sb.Length > 0)
                    sb.AppendLine();

                // Full name stays muted; the signed percent carries the colour.
                sb.Append("<color=#").Append(HexNeutral).Append('>')
                    .Append(row.FullLabel)
                    .Append("</color>  <color=#")
                    .Append(HexForRow(row))
                    .Append('>')
                    .Append(FormatSignedPercent(row))
                    .Append("</color>");
            }

            return sb.ToString();
        }

        /// <summary>True when the family should show a FAMILY BONUSES rail.</summary>
        public static bool HasVisibleFamilyStats(ShipFamilyDefinition family) =>
            family != null && !family.specialBonuses.IsIdentity;

        /// <summary>
        /// Writes every family-bonus field into <paramref name="dest"/> in matrix order.
        /// Zero / unset authored muls become 1× (same as <see cref="ShipFamilySpecialBonuses.Apply"/>).
        /// </summary>
        /// <param name="bonuses">Family multipliers from the definition asset.</param>
        /// <param name="dest">Caller-owned list. Cleared then filled.</param>
        /// <param name="includeIdentity">
        /// When true, 1× baseline fields stay in the list as <c>1.00×</c> stock bases.
        /// When false, only ≠1 trade-offs remain (sidebar compact list).
        /// </param>
        public static void CollectBonusRows(
            in ShipFamilySpecialBonuses bonuses,
            List<BonusRow> dest,
            bool includeIdentity)
        {
            if (dest == null)
                return;

            dest.Clear();

            // --- Mobility ---
            // ExtraSpeedPercent / ExtraSpeedEnergyDrain scale engine OVERDRIVE, not cruise speed.
            TryAdd(dest, "MOVE", "MOVE SPEED", bonuses.moveSpeedMul, BonusCategory.Mobility,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "ACCEL", "ACCELERATION", bonuses.accelerationMul, BonusCategory.Mobility,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "TURN", "TURN SPEED", bonuses.turnSpeedMul, BonusCategory.Mobility,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "OD%", "OVERDRIVE", bonuses.extraSpeedPercentMul, BonusCategory.Mobility,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "DRAIN", "OD DRAIN", bonuses.extraSpeedEnergyDrainMul, BonusCategory.Mobility,
                lowerIsBetter: true, isNeutral: false, includeIdentity);

            // --- Combat ---
            TryAdd(dest, "FP", "FIRE POWER", bonuses.firePowerMul, BonusCategory.Combat,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "RATE", "FIRE RATE", bonuses.fireRateMul, BonusCategory.Combat,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "BSPD", "BULLET SPEED", bonuses.bulletSpeedMul, BonusCategory.Combat,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "RANGE", "BULLET RANGE", bonuses.bulletRangeMul, BonusCategory.Combat,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "RAM", "RAMMING", bonuses.rammingMul, BonusCategory.Combat,
                lowerIsBetter: false, isNeutral: false, includeIdentity);

            // --- Hull (defense only) ---
            // Energy used to share this strip; it is its own power-bar category
            // so the matrix keeps HP / regen next to each other, not mixed with EN.
            TryAdd(dest, "HP", "HEALTH CAP", bonuses.healthCapMul, BonusCategory.Hull,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "H.REG", "HEALTH REGEN", bonuses.healthRegenMul, BonusCategory.Hull,
                lowerIsBetter: false, isNeutral: false, includeIdentity);

            // --- Energy ---
            TryAdd(dest, "EN", "ENERGY CAP", bonuses.energyCapMul, BonusCategory.Energy,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "E.REG", "ENERGY REGEN", bonuses.energyRegenMul, BonusCategory.Energy,
                lowerIsBetter: false, isNeutral: false, includeIdentity);

            // --- Hold (cargo + tractor + camera zoom) ---
            // [TITAN-ORBIT] cameraHeightMul is presentation-only (CameraFollowEcs). It does
            // not change sim stats — >1 zooms out, <1 zooms in.
            TryAdd(dest, "GEMS", "GEM CAP", bonuses.maxGemsMul, BonusCategory.Hold,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "TROOPS", "TROOP CAP", bonuses.maxPeopleMul, BonusCategory.Hold,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "TRACT", "TRACTOR RANGE", bonuses.tractorDistanceMul, BonusCategory.Hold,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "T.PWR", "TRACTOR POWER", bonuses.tractorPowerMul, BonusCategory.Hold,
                lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "CAM", "CAM HEIGHT", bonuses.cameraHeightMul, BonusCategory.Hold,
                lowerIsBetter: false, isNeutral: true, includeIdentity);
        }

        /// <summary>
        /// Bullet-type damage board for the lineage matrix. Bank fire-power plus
        /// vs-asteroid / ship / moon / gem multipliers at the current Extra Level.
        /// <see cref="BulletBankProfile.GetDamageMultiplier"/> already stacks
        /// Everything-target rows onto each class.
        /// </summary>
        /// <param name="profile">Resolved bank profile. Null skips the row.</param>
        /// <param name="extras">Fire Power Extra Levels (hull level only in the store).</param>
        /// <param name="dest">Caller-owned list. Appended — not cleared.</param>
        /// <param name="includeIdentity">When true, 1× targets stay as <c>1.00×</c> stock bases.</param>
        public static void CollectBankDamageRows(
            BulletBankProfile profile,
            int extras,
            List<BonusRow> dest,
            bool includeIdentity)
        {
            if (dest == null || profile == null)
                return;

            int extraLevels = Mathf.Max(0, extras);
            float fp = profile.statModifiers.firePowerMultiplier;

            // --- General shot damage ---
            // firePowerMultiplier is the bank's "this type hits harder / softer" lever.
            TryAdd(dest, "DMG", "BANK DAMAGE", fp, BonusCategory.Ordnance,
                lowerIsBetter: false, isNeutral: false, includeIdentity);

            // --- Per-target combat bonuses ---
            // Magnitude 1.2 at Extra 0 = +20% vs that class. Extra Levels add
            // magnitudePerExtra before we convert to a signed percent.
            TryAdd(dest, "AST", "VS ASTEROIDS",
                profile.GetDamageMultiplier(BulletBankDamageTarget.Asteroid, extraLevels),
                BonusCategory.Ordnance, lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "SHIP", "VS SHIPS",
                profile.GetDamageMultiplier(BulletBankDamageTarget.ShipOrDrone, extraLevels),
                BonusCategory.Ordnance, lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "MOON", "VS MOONS",
                profile.GetDamageMultiplier(BulletBankDamageTarget.GemMoon, extraLevels),
                BonusCategory.Ordnance, lowerIsBetter: false, isNeutral: false, includeIdentity);
            TryAdd(dest, "GEM", "VS GEMS",
                profile.GetDamageMultiplier(BulletBankDamageTarget.Gem, extraLevels),
                BonusCategory.Ordnance, lowerIsBetter: false, isNeutral: false, includeIdentity);
        }

        /// <summary>
        /// Sidebar lines for ≠1 bank damage (BANK DAMAGE +20%, VS ASTEROIDS +12%).
        /// Empty when the profile is missing or every mul is 1×.
        /// </summary>
        public static string FormatListedBankDamageRichText(BulletBankProfile profile, int extras)
        {
            s_ScratchBankRows.Clear();
            CollectBankDamageRows(profile, extras, s_ScratchBankRows, includeIdentity: false);
            if (s_ScratchBankRows.Count == 0)
                return string.Empty;

            var sb = new StringBuilder(s_ScratchBankRows.Count * 40);
            for (int i = 0; i < s_ScratchBankRows.Count; i++)
            {
                BonusRow row = s_ScratchBankRows[i];
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append("<color=#").Append(HexNeutral).Append('>')
                    .Append(row.FullLabel)
                    .Append("</color>  <color=#")
                    .Append(HexForRow(row))
                    .Append('>')
                    .Append(FormatSignedPercent(row))
                    .Append("</color>");
            }

            return sb.ToString();
        }

        /// <summary>Caption for a matrix category group (MOVE, COMBAT, HULL, ENERGY, HOLD, BANK).</summary>
        public static string GetCategoryCaption(BonusCategory category)
        {
            int i = (int)category;
            if (i < 0 || i >= CategoryCaptions.Length)
                return string.Empty;
            return CategoryCaptions[i];
        }

        /// <summary>
        /// Player-facing value for one cell.
        /// Neutral 1× fields show the stock base (<c>1.00×</c>) so the board is
        /// a full lineage card, not only the live trade-offs. Active fields use
        /// a signed percent (<c>+12%</c> / <c>−25%</c>).
        /// </summary>
        public static string FormatSignedPercent(in BonusRow row)
        {
            if (row.IsIdentity)
                return NeutralBaseText;

            float abs = Mathf.Abs(row.SignedPercent);
            string body = abs.ToString("0.#", CultureInfo.InvariantCulture);
            if (row.SignedPercent > 0.05f)
                return "+" + body + "%";
            if (row.SignedPercent < -0.05f)
                return "−" + body + "%";
            return NeutralBaseText;
        }

        /// <summary>TMP hex for a cell value (boost / penalty / zoom / baseline).</summary>
        public static string HexForRow(in BonusRow row)
        {
            if (row.IsIdentity)
                return HexMuted;
            if (row.IsNeutral)
                return HexNeutral;
            if (row.IsBoost)
                return HexBoost;
            if (row.IsPenalty)
                return HexPenalty;
            return HexNeutral;
        }

        static readonly List<BonusRow> s_ScratchRows = new List<BonusRow>(20);
        static readonly List<BonusRow> s_ScratchBankRows = new List<BonusRow>(8);

        static void TryAdd(
            List<BonusRow> dest,
            string shortLabel,
            string fullLabel,
            float authoredMul,
            BonusCategory category,
            bool lowerIsBetter,
            bool isNeutral,
            bool includeIdentity)
        {
            float mul = authoredMul > 0.0001f ? authoredMul : 1f;
            bool identity = Mathf.Abs(mul - 1f) < 0.0001f;
            if (identity && !includeIdentity)
                return;

            // Drain: 0.8× means 20% less energy cost — a boost. Flip the sign so
            // the HUD can colour it green without special-casing every caller.
            float rawPercent = (mul - 1f) * 100f;
            float signed = lowerIsBetter ? -rawPercent : rawPercent;

            dest.Add(new BonusRow
            {
                ShortLabel = shortLabel,
                FullLabel = fullLabel,
                Multiplier = mul,
                SignedPercent = signed,
                IsIdentity = identity,
                LowerIsBetter = lowerIsBetter,
                IsNeutral = isNeutral && !identity,
                IsBoost = !identity && !isNeutral && signed > 0.05f,
                IsPenalty = !identity && !isNeutral && signed < -0.05f,
                Category = category
            });
        }

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
