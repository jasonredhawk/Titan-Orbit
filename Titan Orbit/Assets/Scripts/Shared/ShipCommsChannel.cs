namespace TitanOrbit.Core
{
    /// <summary>
    /// Hold-S comms audience. The compose panel stores this as a byte on the RPC
    /// (<c>ShipCommsCommand.TeamOnly</c> kept that field name for wire compatibility).
    /// <para>
    /// All = every client. Team = teammates. Commander = teammates plus gold command
    /// chrome, and it unlocks the COMMANDER keyword row. Only the top three scorers
    /// on a team may use Commander — the server re-checks rank so a client cannot
    /// spoof the channel.
    /// </para>
    /// </summary>
    public enum ShipCommsChannel : byte
    {
        /// <summary>Every connected client hears the sentence.</summary>
        All = 0,

        /// <summary>Only connections whose living or dead hull shares the speaker's team.</summary>
        Team = 1,

        /// <summary>
        /// Team delivery with command chrome. Unlocks commander keywords
        /// (Everyone, Escort, Form Up, …). Top-three players only.
        /// </summary>
        Commander = 2,
    }

    /// <summary>
    /// Shared commander rules for the comms matrix, leaderboard Command Deck, and
    /// the server RPC check. One place so "top three" cannot drift between HUD and authority.
    /// </summary>
    public static class TeamCommanderRules
    {
        /// <summary>
        /// How many players on a team are commanders (highest combined match score).
        /// Rank 1, 2, and 3 — a two-player team still has two commanders.
        /// </summary>
        public const int Slots = 3;

        /// <summary>Kill points — same weight as <c>ShipMatchScoreLogic</c> / the old NGO ScoreSystem.</summary>
        public const int PointsPerKill = 100;

        /// <summary>Deposited-gem points.</summary>
        public const int PointsPerGem = 2;

        /// <summary>Delivered-troop points.</summary>
        public const int PointsPerPerson = 5;

        /// <summary>
        /// Gold used by the CMDR pill, commander keyword tiles, world-chip frames,
        /// and the leaderboard Command Deck. Space-command brass, not team amber.
        /// </summary>
        public static readonly UnityEngine.Color Gold = new UnityEngine.Color(0.95f, 0.78f, 0.32f, 1f);

        /// <summary>
        /// Combined match score used to pick commanders and sort the leaderboard.
        /// kill=100, deposited gem=2, delivered person=5.
        /// </summary>
        public static int CombinedScore(int kills, int gemsDeposited, int peopleDelivered)
        {
            return kills * PointsPerKill
                   + gemsDeposited * PointsPerGem
                   + peopleDelivered * PointsPerPerson;
        }

        /// <summary>
        /// True when <paramref name="rank"/> is 1-based and inside the command deck.
        /// Rank 0 / missing is not a commander.
        /// </summary>
        /// <param name="rank">1 = highest score on that team.</param>
        public static bool IsCommanderRank(int rank)
        {
            return rank >= 1 && rank <= Slots;
        }

        /// <summary>
        /// Clamps a raw RPC byte into a known channel. Unknown values become All so
        /// a future client cannot invent a fourth audience on an older server.
        /// </summary>
        /// <param name="raw">Byte from <c>ShipCommsCommand.TeamOnly</c>.</param>
        public static ShipCommsChannel Sanitize(byte raw)
        {
            if (raw == (byte)ShipCommsChannel.Team)
                return ShipCommsChannel.Team;
            if (raw == (byte)ShipCommsChannel.Commander)
                return ShipCommsChannel.Commander;
            return ShipCommsChannel.All;
        }

        /// <summary>
        /// True when the server should deliver to teammates only (Team or Commander).
        /// All is the only match-wide broadcast.
        /// </summary>
        public static bool IsTeamScoped(ShipCommsChannel channel)
        {
            return channel != ShipCommsChannel.All;
        }
    }
}
