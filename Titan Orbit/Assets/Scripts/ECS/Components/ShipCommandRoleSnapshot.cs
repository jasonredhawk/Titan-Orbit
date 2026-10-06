using TitanOrbit.Core;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-world, local-only snapshot of each team's living top killer / miner / transporter.
    /// Not ghosted — server and client each rebuild from ghosted <see cref="ShipMatchStats"/>
    /// so predicted gun damage and authoritative gems stay on the same winners without
    /// extra replicated fields.
    /// <para>
    /// Written once per sim tick by <c>ShipCommandRoleRefreshSystem</c> (OrderFirst in
    /// SimulationSystemGroup). Combat, mining, people transport, and asteroid destroy
    /// read this instead of scanning every ship again.
    /// </para>
    /// Dead hulls never hold a title (same as nameplate badges). A zero score never wins.
    /// </summary>
    public struct ShipCommandRoleSnapshot : IComponentData
    {
        public int KillerA;
        public int KillerB;
        public int KillerC;
        public int KillerD;
        public int KillerE;

        public int MinerA;
        public int MinerB;
        public int MinerC;
        public int MinerD;
        public int MinerE;

        public int TransporterA;
        public int TransporterB;
        public int TransporterC;
        public int TransporterD;
        public int TransporterE;

        /// <summary>True when <paramref name="networkId"/> is that team's living top killer.</summary>
        public bool IsKiller(TeamId team, int networkId) =>
            networkId > 0 && GetKiller(team) == networkId;

        /// <summary>True when <paramref name="networkId"/> is that team's living top miner.</summary>
        public bool IsMiner(TeamId team, int networkId) =>
            networkId > 0 && GetMiner(team) == networkId;

        /// <summary>True when <paramref name="networkId"/> is that team's living top troop mover.</summary>
        public bool IsTransporter(TeamId team, int networkId) =>
            networkId > 0 && GetTransporter(team) == networkId;

        /// <summary>
        /// True when this owner holds at least one living title on <paramref name="team"/>.
        /// Same rule as the Command Deck and the Team Commander pill.
        /// </summary>
        public bool HoldsCommandSeat(TeamId team, int networkId) =>
            TeamCommanderRules.HoldsCommandSeat(
                IsKiller(team, networkId),
                IsMiner(team, networkId),
                IsTransporter(team, networkId));

        /// <summary>Winner NetworkId for kills, or 0.</summary>
        public int GetKiller(TeamId team) => Read(team, KillerA, KillerB, KillerC, KillerD, KillerE);

        /// <summary>Winner NetworkId for deposited gems, or 0.</summary>
        public int GetMiner(TeamId team) => Read(team, MinerA, MinerB, MinerC, MinerD, MinerE);

        /// <summary>Winner NetworkId for delivered troops, or 0.</summary>
        public int GetTransporter(TeamId team) =>
            Read(team, TransporterA, TransporterB, TransporterC, TransporterD, TransporterE);

        /// <summary>Writes one team's three winners. Team None is ignored.</summary>
        public void Set(TeamId team, int killerId, int minerId, int transporterId)
        {
            switch (team)
            {
                case TeamId.TeamA:
                    KillerA = killerId;
                    MinerA = minerId;
                    TransporterA = transporterId;
                    break;
                case TeamId.TeamB:
                    KillerB = killerId;
                    MinerB = minerId;
                    TransporterB = transporterId;
                    break;
                case TeamId.TeamC:
                    KillerC = killerId;
                    MinerC = minerId;
                    TransporterC = transporterId;
                    break;
                case TeamId.TeamD:
                    KillerD = killerId;
                    MinerD = minerId;
                    TransporterD = transporterId;
                    break;
                case TeamId.TeamE:
                    KillerE = killerId;
                    MinerE = minerId;
                    TransporterE = transporterId;
                    break;
            }
        }

        /// <summary>Clears every slot before a rebuild so a departed team cannot keep a stale title.</summary>
        public void Clear()
        {
            KillerA = KillerB = KillerC = KillerD = KillerE = 0;
            MinerA = MinerB = MinerC = MinerD = MinerE = 0;
            TransporterA = TransporterB = TransporterC = TransporterD = TransporterE = 0;
        }

        static int Read(TeamId team, int a, int b, int c, int d, int e)
        {
            switch (team)
            {
                case TeamId.TeamA: return a;
                case TeamId.TeamB: return b;
                case TeamId.TeamC: return c;
                case TeamId.TeamD: return d;
                case TeamId.TeamE: return e;
                default: return 0;
            }
        }
    }
}
