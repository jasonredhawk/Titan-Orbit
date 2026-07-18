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
    }
}
