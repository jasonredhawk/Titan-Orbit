using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Ship-local troop-escort hit tests. Parked orbs use
    /// <see cref="PeopleTransportMath.EvaluateEscortSwarmPose"/> — no escort ghosts.
    /// In-flight seats are the load-hop entity, not this scan.
    /// </summary>
    public static class PeopleTransportEscortHitScan
    {
        /// <summary>
        /// Swept segment vs this ship's parked escort spheres. Friendly / own orbs are skipped.
        /// </summary>
        public static bool TryKeepNearestEscortHitOnShip(
            EntityManager em,
            Entity shipEntity,
            in BulletElement bullet,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            double timeSeconds,
            ref float bestT,
            ref float3 bestHit,
            out byte seatId,
            out float healthAfter)
        {
            seatId = 0;
            healthAfter = -1f;
            if (!em.HasBuffer<PeopleEscortVitalElement>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<LocalTransform>(shipEntity) ||
                !em.HasComponent<GhostOwner>(shipEntity))
                return false;

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            // Parked orbs are hidden while this hull is fully moon-docked — no hitbox.
            if (ShipMoonDockState.IsFullyLandedOnMoon(em, shipEntity))
                return false;
            if (ship.Team == (TeamId)bullet.OwnerTeam)
                return false;
            if (bullet.OwnerNetworkId > 0 &&
                em.GetComponentData<GhostOwner>(shipEntity).NetworkId == bullet.OwnerNetworkId)
                return false;

            var xf = em.GetComponentData<LocalTransform>(shipEntity);
            int netId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            float3 vel = float3.zero;
            float3 heading = float3.zero;
            if (em.HasComponent<ShipKinematics>(shipEntity))
            {
                var kin = em.GetComponentData<ShipKinematics>(shipEntity);
                vel = kin.Velocity;
                heading = kin.FormationHeading;
            }

            var buf = em.GetBuffer<PeopleEscortVitalElement>(shipEntity);
            bool hit = false;
            float pad = math.clamp(bullet.ScaleMultiplier * 0.18f, 0f, 0.85f);
            for (int i = 0; i < buf.Length; i++)
            {
                var vital = buf[i];
                if (vital.InFlight != 0 || vital.Amount <= 0 || vital.Health <= 0)
                    continue;

                float3 center = PeopleTransportEscortLogic.EvaluateParkedPose(
                    em, shipEntity, in xf, in vital, netId, vel, timeSeconds, mapW, mapH, heading);
                float radius = PeopleTransportMath.GetEscortHitRadius(vital.Amount) + pad;
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, center, radius, mapW, mapH, out float3 contact))
                    continue;

                float t = BulletCollision.GetSegmentHitParameter(from, to, contact);
                if (t > bestT)
                    continue;

                bestT = t;
                bestHit = contact;
                seatId = vital.SeatId;
                healthAfter = vital.Health;
                hit = true;
            }

            return hit;
        }

        /// <summary>
        /// Closest parked enemy escort on this ship for turret lead-aim.
        /// </summary>
        public static bool TryFindNearestEscortOnShip(
            EntityManager em,
            Entity shipEntity,
            float3 from,
            float maxDistSq,
            float mapW,
            float mapH,
            double timeSeconds,
            out float3 pos,
            out float3 vel)
        {
            pos = default;
            vel = float3.zero;
            if (!em.HasBuffer<PeopleEscortVitalElement>(shipEntity) ||
                !em.HasComponent<LocalTransform>(shipEntity) ||
                !em.HasComponent<GhostOwner>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity))
                return false;

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            if (ShipMoonDockState.IsFullyLandedOnMoon(em, shipEntity))
                return false;

            var xf = em.GetComponentData<LocalTransform>(shipEntity);
            int netId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            float3 shipVel = float3.zero;
            float3 heading = float3.zero;
            if (em.HasComponent<ShipKinematics>(shipEntity))
            {
                var kin = em.GetComponentData<ShipKinematics>(shipEntity);
                shipVel = kin.Velocity;
                heading = kin.FormationHeading;
            }

            var buf = em.GetBuffer<PeopleEscortVitalElement>(shipEntity);
            float best = maxDistSq;
            bool found = false;
            for (int i = 0; i < buf.Length; i++)
            {
                var vital = buf[i];
                if (vital.InFlight != 0 || vital.Amount <= 0)
                    continue;

                float3 center = PeopleTransportEscortLogic.EvaluateParkedPose(
                    em, shipEntity, in xf, in vital, netId, shipVel, timeSeconds, mapW, mapH, heading);
                float3 off = ToroidalMapEcs.ShortestOffsetXZ(from, center, mapW, mapH);
                float distSq = math.lengthsq(new float3(off.x, 0f, off.z));
                if (distSq >= best)
                    continue;

                best = distSq;
                pos = center;
                vel = shipVel;
                found = true;
            }

            return found;
        }
    }
}
