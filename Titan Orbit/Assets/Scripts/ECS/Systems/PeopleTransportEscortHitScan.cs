using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One parked troop-transport sphere for this server tick.
    /// Pose is the closed-form seat on the owning hull — no escort ghost.
    /// </summary>
    public struct EscortHitTarget
    {
        /// <summary>Ship that owns the <see cref="PeopleEscortVitalElement"/> row.</summary>
        public Entity ShipEntity;

        /// <summary>World center from <see cref="PeopleTransportMath.EvaluateEscortSwarmPose"/>.</summary>
        public float3 Position;

        /// <summary>Unpadded sphere radius (<see cref="PeopleTransportMath.GetEscortHitRadius"/>).</summary>
        public float Radius;

        /// <summary>Owner team — friendly bullets pass through.</summary>
        public byte Team;

        /// <summary>GhostOwner.NetworkId — own bullets never hit own seats.</summary>
        public int OwnerNetworkId;

        /// <summary>Seat id in the vital buffer (0 is a real seat). Radius 0 means destroyed this tick.</summary>
        public byte SeatId;
    }

    /// <summary>
    /// Ship-local troop-escort hit tests. Parked orbs use
    /// <see cref="PeopleTransportMath.EvaluateEscortSwarmPose"/> — no escort ghosts.
    /// In-flight seats are the load-hop entity, not this scan.
    /// </summary>
    public static class PeopleTransportEscortHitScan
    {
        /// <summary>
        /// Fills <paramref name="targetsOut"/> with every living parked seat this tick.
        /// Same pose the escort meshes draw (<see cref="PeopleTransportEscortLogic.EvaluateParkedPose"/>).
        /// Seats sit 1.1–2.5× outside the hull, so they are not covered by the ship hash cell.
        /// </summary>
        public static void RebuildSeatedTargets(
            EntityManager em,
            NativeArray<Entity> ships,
            double timeSeconds,
            float mapW,
            float mapH,
            List<EscortHitTarget> targetsOut)
        {
            targetsOut.Clear();
            if (!ships.IsCreated || ships.Length == 0)
                return;

            for (int s = 0; s < ships.Length; s++)
            {
                Entity ship = ships[s];
                if (!em.HasBuffer<PeopleEscortVitalElement>(ship) ||
                    !em.HasComponent<ShipState>(ship) ||
                    !em.HasComponent<LocalTransform>(ship) ||
                    !em.HasComponent<GhostOwner>(ship))
                    continue;

                var shipState = em.GetComponentData<ShipState>(ship);
                if (shipState.IsDead || shipState.AwaitingTeamSelection)
                    continue;
                // Parked orbs are hidden while this hull is fully moon-docked — no hitbox.
                if (ShipMoonDockState.IsFullyLandedOnMoon(em, ship))
                    continue;

                var xf = em.GetComponentData<LocalTransform>(ship);
                int netId = em.GetComponentData<GhostOwner>(ship).NetworkId;
                float3 heading = float3.zero;
                if (em.HasComponent<ShipKinematics>(ship))
                    heading = em.GetComponentData<ShipKinematics>(ship).FormationHeading;

                var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
                byte team = (byte)shipState.Team;
                for (int i = 0; i < buf.Length; i++)
                {
                    var vital = buf[i];
                    if (vital.InFlight != 0 || vital.Amount <= 0 || vital.Health <= 0)
                        continue;

                    float3 center = PeopleTransportEscortLogic.EvaluateParkedPose(
                        em, ship, in xf, in vital, netId, timeSeconds, mapW, mapH, heading);
                    targetsOut.Add(new EscortHitTarget
                    {
                        ShipEntity = ship,
                        Position = center,
                        Radius = PeopleTransportMath.GetEscortHitRadius(vital.Amount),
                        Team = team,
                        OwnerNetworkId = netId,
                        SeatId = vital.SeatId,
                    });
                }
            }
        }

        /// <summary>
        /// Swept segment vs every parked seat built this tick. Friendly / own orbs are skipped.
        /// Returns true when a nearer contact than <paramref name="bestT"/> is found.
        /// </summary>
        public static bool TryKeepNearestSeatedHit(
            in BulletElement bullet,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            List<EscortHitTarget> targets,
            ref float bestT,
            ref float3 bestHit,
            out int targetIndex)
        {
            targetIndex = -1;
            if (targets == null || targets.Count == 0)
                return false;

            bool improved = false;
            float pad = math.clamp(bullet.ScaleMultiplier * 0.18f, 0f, 0.85f);
            for (int i = 0; i < targets.Count; i++)
            {
                EscortHitTarget t = targets[i];
                if (t.Radius <= 0f)
                    continue;
                if (t.Team == bullet.OwnerTeam)
                    continue;
                if (bullet.OwnerNetworkId > 0 && t.OwnerNetworkId == bullet.OwnerNetworkId)
                    continue;

                float radius = t.Radius + pad;
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, t.Position, radius, mapW, mapH, out float3 contact))
                    continue;

                float candT = BulletCollision.GetSegmentHitParameter(from, to, contact);
                if (candT < 0f || candT > 1f)
                    continue;
                if (candT >= bestT)
                    continue;

                bestT = candT;
                bestHit = contact;
                targetIndex = i;
                improved = true;
            }

            return improved;
        }

        /// <summary>
        /// Drops a seat from this tick's list after it was destroyed, so a later bullet
        /// does not stop on the empty slot.
        /// </summary>
        public static void DisableSeatedTarget(List<EscortHitTarget> targets, Entity ship, byte seatId)
        {
            if (targets == null)
                return;

            for (int i = 0; i < targets.Count; i++)
            {
                EscortHitTarget t = targets[i];
                if (t.ShipEntity != ship || t.SeatId != seatId)
                    continue;
                t.Radius = 0f;
                targets[i] = t;
            }
        }

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
            float3 heading = float3.zero;
            if (em.HasComponent<ShipKinematics>(shipEntity))
                heading = em.GetComponentData<ShipKinematics>(shipEntity).FormationHeading;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(shipEntity);
            bool hit = false;
            float pad = math.clamp(bullet.ScaleMultiplier * 0.18f, 0f, 0.85f);
            for (int i = 0; i < buf.Length; i++)
            {
                var vital = buf[i];
                if (vital.InFlight != 0 || vital.Amount <= 0 || vital.Health <= 0)
                    continue;

                float3 center = PeopleTransportEscortLogic.EvaluateParkedPose(
                    em, shipEntity, in xf, in vital, netId, timeSeconds, mapW, mapH, heading);
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
    }
}
