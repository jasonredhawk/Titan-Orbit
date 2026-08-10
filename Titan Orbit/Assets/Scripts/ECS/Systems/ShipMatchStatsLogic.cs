using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Thin server helper that increments <see cref="ShipMatchStats"/> on a known
    /// ship entity, and stamps <see cref="ShipCombatAttribution"/> for kill credit.
    /// <para>
    /// Server-only callers (<c>WorldSystemFilterFlags.ServerSimulation</c> systems) — never run from
    /// client presentation. No ship gathers live here (callers already hold the ship entity or a
    /// NetworkId→Entity map) so Windows join-crash gate scanners stay clean.
    /// </para>
    /// </summary>
    public static class ShipMatchStatsLogic
    {
        /// <summary>
        /// Adds deltas to <paramref name="shipEntity"/>'s <see cref="ShipMatchStats"/>.
        /// No-op when the entity is missing stats or all deltas are zero.
        /// </summary>
        /// <param name="em">Server EntityManager.</param>
        /// <param name="shipEntity">Ship that earned the credit.</param>
        /// <param name="kills">Kill count to add (usually 0 or 1).</param>
        /// <param name="gemsDeposited">Gems deposited to add (integer floor already applied by caller).</param>
        /// <param name="peopleDelivered">People delivered to add.</param>
        /// <returns>True when at least one field changed.</returns>
        public static bool TryAddOnShip(
            EntityManager em,
            Entity shipEntity,
            int kills,
            int gemsDeposited,
            int peopleDelivered)
        {
            // --- Early outs ---
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
                return false;
            if (kills == 0 && gemsDeposited == 0 && peopleDelivered == 0)
                return false;
            if (!em.HasComponent<ShipMatchStats>(shipEntity))
                return false;

            // --- Apply deltas ---
            var stats = em.GetComponentData<ShipMatchStats>(shipEntity);
            if (kills != 0)
                stats.Kills += kills;
            if (gemsDeposited != 0)
                stats.GemsDeposited += gemsDeposited;
            if (peopleDelivered != 0)
                stats.PeopleDelivered += peopleDelivered;
            em.SetComponentData(shipEntity, stats);
            return true;
        }

        /// <summary>
        /// Records who last damaged a ship for later kill credit. Call after a real damage apply.
        /// </summary>
        /// <param name="em">Server EntityManager.</param>
        /// <param name="victimShip">Ship that took damage.</param>
        /// <param name="damagerNetworkId">Attacker GhostOwner / bullet OwnerNetworkId.</param>
        /// <param name="serverElapsed">Server ElapsedTime for the stamp.</param>
        public static void SetLastDamager(
            EntityManager em,
            Entity victimShip,
            int damagerNetworkId,
            float serverElapsed)
        {
            // --- Guards ---
            if (victimShip == Entity.Null || damagerNetworkId <= 0 || !em.Exists(victimShip))
                return;

            // [TITAN-ORBIT] Ensure component exists on older prefabs (server-only, not ghosted).
            if (!em.HasComponent<ShipCombatAttribution>(victimShip))
                em.AddComponentData(victimShip, new ShipCombatAttribution());

            em.SetComponentData(victimShip, new ShipCombatAttribution
            {
                LastDamagerNetworkId = damagerNetworkId,
                LastDamageServerTime = serverElapsed,
            });
        }
    }
}
