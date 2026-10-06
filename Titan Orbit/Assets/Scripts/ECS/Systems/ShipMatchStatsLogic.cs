using TitanOrbit.Data;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

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
            float serverElapsed,
            Entity sourceEntity = default,
            byte sourceKind = 0,
            int sourceGhostId = 0,
            float2 sourcePosXZ = default,
            bool hasSourcePos = false)
        {
            StampHit(
                em, victimShip, damagerNetworkId, serverElapsed, float2.zero, 0f,
                sourceEntity, sourceKind, sourceGhostId, sourcePosXZ, hasSourcePos);
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
            float impulsePower,
            Entity sourceEntity = default,
            byte sourceKind = 0,
            int sourceGhostId = 0,
            float2 sourcePosXZ = default,
            bool hasSourcePos = false)
        {
            StampHit(
                em, victimShip, damagerNetworkId, serverElapsed, impulseXZ, impulsePower,
                sourceEntity, sourceKind, sourceGhostId, sourcePosXZ, hasSourcePos);
        }

        /// <summary>Stamps kill-impulse only (environment hits with no player damager).</summary>
        public static void SetLastImpulse(
            EntityManager em,
            Entity victimShip,
            float2 impulseXZ,
            float impulsePower,
            float serverElapsed)
        {
            StampHit(
                em, victimShip, 0, serverElapsed, impulseXZ, impulsePower,
                Entity.Null, 0, 0, float2.zero, false);
        }

        static void StampHit(
            EntityManager em,
            Entity victimShip,
            int damagerNetworkId,
            float serverElapsed,
            float2 impulseXZ,
            float impulsePower,
            Entity sourceEntity,
            byte sourceKind,
            int sourceGhostId,
            float2 sourcePosXZ,
            bool hasSourcePos)
        {
            if (victimShip == Entity.Null || !em.Exists(victimShip))
                return;

            bool hasImpulse = math.lengthsq(impulseXZ) > 1e-8f || impulsePower > 0.0001f;
            bool hasSource = sourceEntity != Entity.Null && em.Exists(sourceEntity);
            if (damagerNetworkId <= 0 && !hasImpulse && !hasSource && sourceKind == 0 && !hasSourcePos)
                return;

            if (!em.HasComponent<ShipCombatAttribution>(victimShip))
                em.AddComponentData(victimShip, new ShipCombatAttribution());

            var cur = em.GetComponentData<ShipCombatAttribution>(victimShip);
            if (damagerNetworkId > 0)
            {
                cur.LastDamagerNetworkId = damagerNetworkId;
                cur.LastDamageServerTime = serverElapsed;
                if (cur.LastSourceKind == 0)
                    cur.LastSourceKind = (byte)DeathVfxSourceKind.Ship;
            }

            if (hasSource)
                StampSourceBody(em, sourceEntity, ref cur);

            if (sourceKind != 0)
                cur.LastSourceKind = sourceKind;
            if (sourceGhostId != 0)
                cur.LastSourceGhostId = sourceGhostId;
            if (hasSourcePos)
            {
                cur.LastSourcePosXZ = sourcePosXZ;
                cur.LastSourceHasPos = 1;
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

        static void StampSourceBody(EntityManager em, Entity source, ref ShipCombatAttribution cur)
        {
            if (em.HasComponent<GhostInstance>(source))
                cur.LastSourceGhostId = em.GetComponentData<GhostInstance>(source).ghostId;

            if (em.HasComponent<GhostOwner>(source))
            {
                int net = em.GetComponentData<GhostOwner>(source).NetworkId;
                if (net > 0)
                    cur.LastDamagerNetworkId = net;
            }

            if (em.HasComponent<LocalTransform>(source))
            {
                float3 p = em.GetComponentData<LocalTransform>(source).Position;
                cur.LastSourcePosXZ = new float2(p.x, p.z);
                cur.LastSourceHasPos = 1;
            }

            if (em.HasComponent<AsteroidTag>(source))
                cur.LastSourceKind = (byte)DeathVfxSourceKind.Asteroid;
            else if (em.HasComponent<PlanetTag>(source) || em.HasComponent<PlanetState>(source))
                cur.LastSourceKind = (byte)DeathVfxSourceKind.Turret;
            else if (em.HasComponent<ShipTag>(source))
                cur.LastSourceKind = (byte)DeathVfxSourceKind.Ship;
            else if (cur.LastSourceKind == 0)
                cur.LastSourceKind = (byte)DeathVfxSourceKind.Ship;
        }

        /// <summary>
        /// Death-camera kind for a damaging bullet: homing rocket, planetary-defense pad,
        /// or the owning ship.
        /// </summary>
        public static void ClassifyBulletSource(in BulletElement bullet, out byte kind, out int sourceGhostId)
        {
            sourceGhostId = bullet.SourceGhostId;
            if (bullet.SourceKind != 0)
            {
                kind = bullet.SourceKind;
                return;
            }

            if (bullet.Homing != 0)
            {
                kind = (byte)DeathVfxSourceKind.Missile;
                return;
            }

            if (bullet.DamageFilter == BulletDamageFilter.ShipsAndTransports)
            {
                kind = (byte)DeathVfxSourceKind.Turret;
                return;
            }

            kind = (byte)DeathVfxSourceKind.Ship;
        }
    }
}
