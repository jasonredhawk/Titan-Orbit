namespace TitanOrbit.Core
{
    /// <summary>
    /// Hold-S comms audience. The compose panel stores this as a byte on the RPC
    /// (<c>ShipCommsCommand.TeamOnly</c> kept that field name for wire compatibility).
    /// <para>
    /// The compose card only offers All and Team. Commander is not a third tab —
    /// it is a send-time upgrade: teammates plus gold command chrome. Earning a
    /// category title (top killer, top miner, or top troop mover) unlocks the
    /// COMMANDER keyword row on its own, and the Team pill relabels to
    /// Team Commander. The server re-checks those titles so a client cannot
    /// spoof command words or gold chrome.
    /// </para>
    /// </summary>
    public enum ShipCommsChannel : byte
    {
        /// <summary>Every connected client hears the sentence.</summary>
        All = 0,

        /// <summary>Only connections whose living or dead hull shares the speaker's team.</summary>
        Team = 1,

        /// <summary>
        /// Team delivery with gold command chrome. Not a compose-tab choice —
        /// applied when a seated commander sends command-deck words on Team
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
        /// Gold used by the Team Commander pill, commander keyword tiles,
        /// world-chip frames, and the leaderboard Command Deck. Space-command
        /// brass, not team amber.
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
        /// <see cref="HoldsCommandSeat"/> for the Command Deck / Team Commander pill.
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
        /// Compose-card choice: All or Team. Older PlayerPrefs / RPCs that stored
        /// Commander collapse to Team — the gold channel is applied at send time
        /// by <see cref="ResolveDeliveryChannel"/>, not by a third pill.
        /// </summary>
        /// <param name="raw">Byte from PlayerPrefs or a remembered compose toggle.</param>
        public static ShipCommsChannel SanitizeComposeChoice(byte raw)
        {
            ShipCommsChannel channel = Sanitize(raw);
            return channel == ShipCommsChannel.Commander
                ? ShipCommsChannel.Team
                : channel;
        }

        /// <summary>
        /// Channel the server should actually deliver. Commander words and gold
        /// chrome require an earned seat. A seated commander on Team who used
        /// command-deck words is upgraded to Commander so teammates see brass
        /// frames. All stays All even with those words — the whole match hears
        /// them, without command chrome. A Commander request from a non-commander
        /// collapses to Team so a spoofed byte cannot paint gold.
        /// </summary>
        /// <param name="requested">Audience byte from the compose card / RPC.</param>
        /// <param name="isCommander">True when the speaker holds a living category title.</param>
        /// <param name="usesCommanderWords">True when the sentence includes Everyone / Escort / Form Up / …</param>
        public static ShipCommsChannel ResolveDeliveryChannel(
            ShipCommsChannel requested,
            bool isCommander,
            bool usesCommanderWords)
        {
            ShipCommsChannel channel = Sanitize((byte)requested);

            // --- No seat ---
            // Gold chrome and command-deck words are title-gated. A leftover
            // Commander request from an older client becomes ordinary Team.
            if (!isCommander)
                return channel == ShipCommsChannel.Commander ? ShipCommsChannel.Team : channel;

            // --- Team Commander ---
            // [TITAN-ORBIT] The compose card has no CMDR tab. Team + command
            // words is how a seated commander paints gold world chips.
            if (channel == ShipCommsChannel.Team && usesCommanderWords)
                return ShipCommsChannel.Commander;

            return channel;
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
