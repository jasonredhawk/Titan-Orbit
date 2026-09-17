namespace TitanOrbit.Core
{
    /// <summary>
    /// Hold-S comms audience. The compose panel stores this as a byte on the RPC
    /// (<c>ShipCommsCommand.TeamOnly</c> kept that field name for wire compatibility).
    /// <para>
    /// All = every client. Team = teammates. Commander = teammates plus gold command
    /// chrome, and it unlocks the COMMANDER keyword row. Only players who currently
    /// hold an earned category title (top killer, top miner, or top troop mover)
    /// may use Commander — the server re-checks those titles so a client cannot
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
        /// (Everyone, Escort, Form Up, …). Earned category titles only.
        /// </summary>
        Commander = 2,
    }

    /// <summary>
    /// Shared commander rules for the comms matrix, leaderboard Command Deck, and
    /// the server RPC check. One place so "earned command seat" cannot drift between
    /// HUD and authority.
    /// A Command Deck seat is the same person as a
    /// <see cref="TeamCommandRoleRules"/> title: most kills, most gems deposited,
    /// or most troops delivered on that team. Zero scores never win, so a fresh
    /// spawn is never a commander even if they sit first on an empty leaderboard.
    /// </summary>
    public static class TeamCommanderRules
    {
        /// <summary>
        /// How many command seats a team can fill — one per earned category
        /// (killer / miner / troops). A team can have 0–3 commanders; one player
        /// can hold more than one title.
        /// </summary>
        public const int Slots = 3;

        /// <summary>
        /// True when this player holds at least one earned category title.
        /// [TITAN-ORBIT] Command is earned (a kill, a gem deposit, or a troop drop
        /// that currently leads the team) — not leaderboard place. A zero-score
        /// hull can still be rank 1 on an empty team; that is not a command seat.
        /// </summary>
        /// <param name="isKiller">True when this NetworkId is the team's living top killer.</param>
        /// <param name="isMiner">True when this NetworkId is the team's living top miner.</param>
        /// <param name="isTransporter">True when this NetworkId is the team's living top troop mover.</param>
        public static bool HoldsCommandSeat(bool isKiller, bool isMiner, bool isTransporter)
        {
            return isKiller || isMiner || isTransporter;
        }

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
        /// Combined match score used to sort the leaderboard (not to grant command).
        /// kill=100, deposited gem=2, delivered person=5.
        /// </summary>
        public static int CombinedScore(int kills, int gemsDeposited, int peopleDelivered)
        {
            return kills * PointsPerKill
                   + gemsDeposited * PointsPerGem
                   + peopleDelivered * PointsPerPerson;
        }

        /// <summary>
        /// True when this 1-based team score rank is 1–3. Used for path-stroke
        /// thickness only — it is <b>not</b> command authority. Call
        /// <see cref="HoldsCommandSeat"/> for the Command Deck / CMDR pill.
        /// </summary>
        /// <param name="rank">1 = highest combined score on that team.</param>
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
