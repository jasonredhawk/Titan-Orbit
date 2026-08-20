using TitanOrbit.Core;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Per-player troop deliveries toward capturing a planet (hostile unloads).
    /// Server-only siege ledger — not ghosted. The winner is written to
    /// <see cref="PlanetState.TopContributorNetworkId"/> at capture time.
    /// </summary>
    public struct PlanetPeopleContributionElement : IBufferElementData
    {
        /// <summary>[NETCODE] Ship owner who unloaded these troops.</summary>
        public int NetworkId;

        /// <summary>[TITAN-ORBIT] People delivered during the current siege (hostile drain + capture hit).</summary>
        public int PeopleDelivered;

        /// <summary>[TITAN-ORBIT] Delivering team as byte (<see cref="TeamId"/>).</summary>
        public byte Team;
    }

    /// <summary>
    /// [ECS/DOTS] Server helpers for the per-planet siege contribution buffer.
    /// Increments on hostile unload; on capture picks the capturing team's highest troop total
    /// (tie → lowest NetworkId) and clears the ledger for the next siege.
    /// </summary>
    public static class PlanetPeopleContributionLogic
    {
        /// <summary>
        /// Adds <paramref name="people"/> to this player's siege total on the planet.
        /// Creates the buffer on first use.
        /// </summary>
        public static void Add(
            EntityManager em,
            Entity planetEntity,
            int networkId,
            int people,
            TeamId team)
        {
            if (networkId <= 0 || people <= 0 || team == TeamId.None)
                return;
            if (planetEntity == Entity.Null || !em.Exists(planetEntity))
                return;

            if (!em.HasBuffer<PlanetPeopleContributionElement>(planetEntity))
                em.AddBuffer<PlanetPeopleContributionElement>(planetEntity);

            byte teamByte = (byte)team;
            var buffer = em.GetBuffer<PlanetPeopleContributionElement>(planetEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                var entry = buffer[i];
                if (entry.NetworkId != networkId || entry.Team != teamByte)
                    continue;

                entry.PeopleDelivered += people;
                buffer[i] = entry;
                return;
            }

            buffer.Add(new PlanetPeopleContributionElement
            {
                NetworkId = networkId,
                PeopleDelivered = people,
                Team = teamByte,
            });
        }

        /// <summary>
        /// Picks the capturing team's top troop contributor, then clears the siege ledger.
        /// </summary>
        /// <param name="fallbackNetworkId">Used when the buffer is empty (capturing ship).</param>
        /// <returns>Winning NetworkId, or 0 when none can be resolved.</returns>
        public static int ResolveTopAndClear(
            EntityManager em,
            Entity planetEntity,
            TeamId capturingTeam,
            int fallbackNetworkId)
        {
            int winner = fallbackNetworkId > 0 ? fallbackNetworkId : 0;
            if (planetEntity == Entity.Null ||
                !em.Exists(planetEntity) ||
                !em.HasBuffer<PlanetPeopleContributionElement>(planetEntity))
                return winner;

            byte teamByte = (byte)capturingTeam;
            var buffer = em.GetBuffer<PlanetPeopleContributionElement>(planetEntity);
            int bestScore = 0;
            int bestId = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                var entry = buffer[i];
                if (entry.Team != teamByte || entry.NetworkId <= 0 || entry.PeopleDelivered <= 0)
                    continue;

                if (bestId == 0 ||
                    entry.PeopleDelivered > bestScore ||
                    (entry.PeopleDelivered == bestScore && entry.NetworkId < bestId))
                {
                    bestScore = entry.PeopleDelivered;
                    bestId = entry.NetworkId;
                }
            }

            buffer.Clear();
            return bestId > 0 ? bestId : winner;
        }
    }
}
