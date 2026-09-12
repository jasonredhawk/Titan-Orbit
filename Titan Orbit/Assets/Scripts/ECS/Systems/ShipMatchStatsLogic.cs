using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

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
            StampHit(em, victimShip, damagerNetworkId, serverElapsed, float2.zero, 0f);
        }

        /// <summary>
        /// Records last damager and the cosmetic death-impulse of this hit.
        /// Damager 0 is allowed when only the impulse is known (asteroid).
        /// </summary>
        public static void SetLastDamager(
            EntityManager em,
            Entity victimShip,
            int damagerNetworkId,
            float serverElapsed,
            float2 impulseXZ,
            float impulsePower)
        {
            StampHit(em, victimShip, damagerNetworkId, serverElapsed, impulseXZ, impulsePower);
        }

        /// <summary>Stamps kill-impulse only (environment hits with no player damager).</summary>
        public static void SetLastImpulse(
            EntityManager em,
            Entity victimShip,
            float2 impulseXZ,
            float impulsePower,
            float serverElapsed)
        {
            StampHit(em, victimShip, 0, serverElapsed, impulseXZ, impulsePower);
        }

        static void StampHit(
            EntityManager em,
            Entity victimShip,
            int damagerNetworkId,
            float serverElapsed,
            float2 impulseXZ,
            float impulsePower)
        {
            if (victimShip == Entity.Null || !em.Exists(victimShip))
                return;

            bool hasImpulse = math.lengthsq(impulseXZ) > 1e-8f || impulsePower > 0.0001f;
            if (damagerNetworkId <= 0 && !hasImpulse)
                return;

            if (!em.HasComponent<ShipCombatAttribution>(victimShip))
                em.AddComponentData(victimShip, new ShipCombatAttribution());

            var cur = em.GetComponentData<ShipCombatAttribution>(victimShip);
            if (damagerNetworkId > 0)
            {
                cur.LastDamagerNetworkId = damagerNetworkId;
                cur.LastDamageServerTime = serverElapsed;
            }

            if (hasImpulse)
            {
                cur.LastImpulseXZ = math.lengthsq(impulseXZ) > 1e-8f
                    ? math.normalizesafe(impulseXZ)
                    : cur.LastImpulseXZ;
                cur.LastImpulsePower = math.max(0f, impulsePower);
                if (serverElapsed > 0f)
                    cur.LastDamageServerTime = serverElapsed;
            }

            em.SetComponentData(victimShip, cur);
        }

        /// <summary>
        /// Credits one kill to the last damager. No-op when already credited this life,
        /// same team, self, or unknown attacker.
        /// </summary>
        public static void TryCreditKillFromAttribution(EntityManager em, Entity victim, TeamId victimTeam)
        {
            if (victim == Entity.Null || !em.Exists(victim) || !em.HasComponent<ShipCombatAttribution>(victim))
                return;

            var attr = em.GetComponentData<ShipCombatAttribution>(victim);
            if (attr.KillCredited != 0)
                return;

            int killerNetworkId = attr.LastDamagerNetworkId;
            if (killerNetworkId <= 0)
                return;

            int victimNetworkId = 0;
            if (em.HasComponent<GhostOwner>(victim))
                victimNetworkId = em.GetComponentData<GhostOwner>(victim).NetworkId;
            if (victimNetworkId > 0 && victimNetworkId == killerNetworkId)
                return;

            if (!TryFindShipByNetworkId(em, killerNetworkId, out Entity killerShip, out TeamId killerTeam))
                return;
            if (victimTeam == TeamId.None || killerTeam == TeamId.None || killerTeam == victimTeam)
                return;

            if (TryAddOnShip(em, killerShip, kills: 1, gemsDeposited: 0, peopleDelivered: 0))
            {
                attr.KillCredited = 1;
                em.SetComponentData(victim, attr);
            }
        }

        static bool TryFindShipByNetworkId(
            EntityManager em,
            int networkId,
            out Entity shipEntity,
            out TeamId team)
        {
            shipEntity = Entity.Null;
            team = TeamId.None;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(ShipState));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                shipEntity = entities[i];
                team = states[i].Team;
                return true;
            }

            return false;
        }
    }
}
