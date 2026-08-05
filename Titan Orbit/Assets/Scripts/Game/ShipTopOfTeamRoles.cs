using System.Collections.Generic;
using TitanOrbit.Core;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Picks each team's top killer / gem miner / transporter from match-long scores.
    /// Shared by minimap role dots and ship world nameplates so rules stay identical:
    /// zero scores never win; ties → lowest owner NetworkId.
    /// Client presentation only — reads already-synced cargo/score caches, never ECS gathers.
    /// </summary>
    public static class ShipTopOfTeamRoles
    {
        /// <summary>NetworkIds winning each match-stat category for one team (0 = none).</summary>
        public struct Ids
        {
            /// <summary>Owner NetworkId with the most kills on this team (0 = none).</summary>
            public int KillerNetworkId;

            /// <summary>Owner NetworkId with the most gems deposited on this team (0 = none).</summary>
            public int MinerNetworkId;

            /// <summary>Owner NetworkId with the most people delivered on this team (0 = none).</summary>
            public int TransporterNetworkId;
        }

        /// <summary>
        /// One ship row used for recompute. Callers copy from <c>ShipMatchStats</c> /
        /// <c>MinimapBlipAnchor</c> — this type intentionally avoids UI assembly references.
        /// </summary>
        public struct Candidate
        {
            /// <summary>Ship team; <see cref="TeamId.None"/> rows are ignored.</summary>
            public TeamId Team;

            /// <summary>[NETCODE] Owner NetworkId — also the tie-break key.</summary>
            public int OwnerNetworkId;

            /// <summary>Match-long kills.</summary>
            public int Kills;

            /// <summary>Match-long gems deposited.</summary>
            public int GemsDeposited;

            /// <summary>Match-long people delivered.</summary>
            public int PeopleDelivered;

            /// <summary>Dead ships never win a top-of-team role.</summary>
            public bool IsDead;
        }

        /// <summary>Last recompute winners keyed by team.</summary>
        static readonly Dictionary<TeamId, Ids> s_TopsByTeam = new Dictionary<TeamId, Ids>(8);

        /// <summary>
        /// Rebuilds per-team winners from <paramref name="candidates"/>.
        /// Safe to call every frame; replaces the previous snapshot entirely.
        /// </summary>
        /// <param name="candidates">Living/dead ship score rows (null entries skipped).</param>
        public static void Recompute(IReadOnlyList<Candidate> candidates)
        {
            // --- Clear previous winners ---
            s_TopsByTeam.Clear();
            if (candidates == null || candidates.Count == 0)
                return;

            // --- Track best score per role so we can compare without re-scanning ---
            var bestKillScore = new Dictionary<TeamId, int>(8);
            var bestGemScore = new Dictionary<TeamId, int>(8);
            var bestPeopleScore = new Dictionary<TeamId, int>(8);
            var topsByTeam = new Dictionary<TeamId, Ids>(8);

            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate ship = candidates[i];
                if (ship.IsDead || ship.Team == TeamId.None || ship.OwnerNetworkId <= 0)
                    continue;

                if (!topsByTeam.TryGetValue(ship.Team, out var tops))
                    tops = default;

                // --- Top killer (blue) ---
                if (ship.Kills > 0)
                {
                    bestKillScore.TryGetValue(ship.Team, out int curScore);
                    int curId = tops.KillerNetworkId;
                    if (curId == 0 || IsBetterTop(ship.Kills, ship.OwnerNetworkId, curScore, curId))
                    {
                        tops.KillerNetworkId = ship.OwnerNetworkId;
                        bestKillScore[ship.Team] = ship.Kills;
                    }
                }

                // --- Top miner (red) ---
                if (ship.GemsDeposited > 0)
                {
                    bestGemScore.TryGetValue(ship.Team, out int curScore);
                    int curId = tops.MinerNetworkId;
                    if (curId == 0 || IsBetterTop(ship.GemsDeposited, ship.OwnerNetworkId, curScore, curId))
                    {
                        tops.MinerNetworkId = ship.OwnerNetworkId;
                        bestGemScore[ship.Team] = ship.GemsDeposited;
                    }
                }

                // --- Top transporter (yellow) ---
                if (ship.PeopleDelivered > 0)
                {
                    bestPeopleScore.TryGetValue(ship.Team, out int curScore);
                    int curId = tops.TransporterNetworkId;
                    if (curId == 0 || IsBetterTop(ship.PeopleDelivered, ship.OwnerNetworkId, curScore, curId))
                    {
                        tops.TransporterNetworkId = ship.OwnerNetworkId;
                        bestPeopleScore[ship.Team] = ship.PeopleDelivered;
                    }
                }

                topsByTeam[ship.Team] = tops;
            }

            foreach (var kv in topsByTeam)
                s_TopsByTeam[kv.Key] = kv.Value;
        }

        /// <summary>Reads the last recompute winners for <paramref name="team"/>.</summary>
        /// <returns>True when at least one role winner exists for the team.</returns>
        public static bool TryGet(TeamId team, out Ids ids)
        {
            return s_TopsByTeam.TryGetValue(team, out ids);
        }

        /// <summary>True when <paramref name="networkId"/> is their team's top killer.</summary>
        public static bool IsKiller(TeamId team, int networkId)
        {
            return networkId > 0 &&
                   TryGet(team, out var ids) &&
                   ids.KillerNetworkId == networkId;
        }

        /// <summary>True when <paramref name="networkId"/> is their team's top gem miner.</summary>
        public static bool IsMiner(TeamId team, int networkId)
        {
            return networkId > 0 &&
                   TryGet(team, out var ids) &&
                   ids.MinerNetworkId == networkId;
        }

        /// <summary>True when <paramref name="networkId"/> is their team's top transporter.</summary>
        public static bool IsTransporter(TeamId team, int networkId)
        {
            return networkId > 0 &&
                   TryGet(team, out var ids) &&
                   ids.TransporterNetworkId == networkId;
        }

        /// <summary>Higher score wins; equal score → lower NetworkId.</summary>
        static bool IsBetterTop(int candidateScore, int candidateId, int currentScore, int currentId)
        {
            if (candidateScore > currentScore)
                return true;
            if (candidateScore < currentScore)
                return false;
            return candidateId < currentId;
        }
    }
}
