namespace TitanOrbit.Core
{
    /// <summary>
    /// Authoritative team assignment stored on ship and planet ghosts. Replicated via NetCode
    /// [GhostField] on ECS components. UI uses <see cref="TeamIdExtensions.ToColor"/> for team colors,
    /// and <see cref="TeamIdExtensions.ToDisplayName"/> / <see cref="TeamIdExtensions.ToLetter"/> for
    /// Join Game team cards and other short labels.
    /// None = unassigned (team pick screen); TeamA–TeamE are playable factions.
    /// </summary>
    public enum TeamId : byte
    {
        None = 0,
        TeamA = 1,
        TeamB = 2,
        TeamC = 3,
        TeamD = 4,
        TeamE = 5
    }

    /// <summary>
    /// Display and bitmask helpers for <see cref="TeamId"/> in UI, combat filters, and lobby cards.
    /// </summary>
    public static class TeamIdExtensions
    {
        /// <summary>
        /// Returns the canonical team color for minimap, ship trails, and orbit UI panels.
        /// </summary>
        public static UnityEngine.Color ToColor(this TeamId team)
        {
            // --- Team palette lookup ---
            // [TITAN-ORBIT] Fixed RGB values — minimap, trails, and orbit UI all share this mapping.
            switch (team)
            {
                case TeamId.TeamA: return new UnityEngine.Color(0.9f, 0.25f, 0.25f);
                case TeamId.TeamB: return new UnityEngine.Color(0.25f, 0.4f, 0.9f);
                case TeamId.TeamC: return new UnityEngine.Color(0.2f, 0.7f, 0.28f);
                case TeamId.TeamD: return new UnityEngine.Color(0.95f, 0.55f, 0.12f);
                case TeamId.TeamE: return new UnityEngine.Color(0.65f, 0.25f, 0.85f);
                default: return UnityEngine.Color.white; // [STANDARD] None / unknown → neutral white.
            }
        }

        /// <summary>
        /// Bit index for team mask queries (TeamA = bit 0, …). None returns 0.
        /// </summary>
        public static int ToMaskBit(this TeamId team)
        {
            // --- Bitmask for friendly-fire / minimap filters ---
            // [STANDARD] TeamA=bit0, TeamB=bit1, …; None yields 0 (no team bit set).
            return team == TeamId.None ? 0 : 1 << ((int)team - 1);
        }

        /// <summary>
        /// Single letter for compact UI (A–E). None returns "?".
        /// Used by Join Game team cards and other short labels.
        /// </summary>
        public static string ToLetter(this TeamId team)
        {
            // --- Compact team letter ---
            // [TITAN-ORBIT] TeamA → "A"; matches TeamAPanel / roster naming without the "Team " prefix.
            if (team == TeamId.None)
                return "?";
            return ((char)('A' + (int)team - 1)).ToString();
        }

        /// <summary>
        /// Player-facing label for team pick and lobby cards ("Team A" … "Team E").
        /// </summary>
        public static string ToDisplayName(this TeamId team)
        {
            // --- Display name ---
            if (team == TeamId.None)
                return "None";
            return "Team " + team.ToLetter();
        }

        /// <summary>
        /// Spoken color name for the hold-S comms matrix ("Red", "Purple", …).
        /// Empty when <paramref name="team"/> is <see cref="TeamId.None"/>.
        /// </summary>
        /// <param name="team">Playable faction. None has no color word.</param>
        /// <returns>Catalog label used by <c>ShipCommsKeywordCatalog</c>.</returns>
        public static string ToColorName(this TeamId team)
        {
            // --- Comms / HUD color word ---
            // [TITAN-ORBIT] Same five names players see on hulls: A red, B blue, C green,
            // D orange, E purple. Comms chips and team tiles share this spelling so
            // "Attack Purple Base" matches the purple faction, not a letter.
            switch (team)
            {
                case TeamId.TeamA: return "Red";
                case TeamId.TeamB: return "Blue";
                case TeamId.TeamC: return "Green";
                case TeamId.TeamD: return "Orange";
                case TeamId.TeamE: return "Purple";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Inverse of <see cref="ToColorName"/> — "purple" / "PURPLE" → TeamE.
        /// Used by the comms panel to tint the five color keyword tiles.
        /// </summary>
        /// <param name="label">Chip text from the keyword catalog.</param>
        /// <param name="team">Resolved faction when true.</param>
        /// <returns>True when <paramref name="label"/> is one of the five team colors.</returns>
        public static bool TryParseColorName(string label, out TeamId team)
        {
            team = TeamId.None;
            if (string.IsNullOrWhiteSpace(label))
                return false;

            // [STANDARD] OrdinalIgnoreCase — players tap "Purple"; the catalog stores "Purple".
            if (string.Equals(label, "Red", System.StringComparison.OrdinalIgnoreCase))
            {
                team = TeamId.TeamA;
                return true;
            }

            if (string.Equals(label, "Blue", System.StringComparison.OrdinalIgnoreCase))
            {
                team = TeamId.TeamB;
                return true;
            }

            if (string.Equals(label, "Green", System.StringComparison.OrdinalIgnoreCase))
            {
                team = TeamId.TeamC;
                return true;
            }

            if (string.Equals(label, "Orange", System.StringComparison.OrdinalIgnoreCase))
            {
                team = TeamId.TeamD;
                return true;
            }

            if (string.Equals(label, "Purple", System.StringComparison.OrdinalIgnoreCase))
            {
                team = TeamId.TeamE;
                return true;
            }

            return false;
        }
    }
}
