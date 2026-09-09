using System.Collections.Generic;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One derived escort hit sphere for the current server tick.
    /// Built from ship pose + packed <see cref="ShipState.CurrentPeople"/> or live landing slots —
    /// no escort ghosts.
    /// </summary>
    public struct PeopleEscortHitTarget
    {
        /// <summary>Owning ship (people cargo + escort buffer live here).</summary>
        public Entity ShipEntity;

        /// <summary>Slot index in <see cref="PeopleEscortSlot"/> (or packed follow index).</summary>
        public int SlotIndex;

        /// <summary>Planar world center.</summary>
        public float3 Position;

        /// <summary>Planar velocity for turret lead (zero while snapped to formation).</summary>
        public float3 Velocity;

        /// <summary>Hit-sphere radius matching visual scale.</summary>
        public float Radius;

        /// <summary>Owner team — friendly bullets pass through.</summary>
        public byte Team;

        /// <summary>GhostOwner.NetworkId — own bullets never hit own escorts.</summary>
        public int OwnerNetworkId;
    }

    /// <summary>
    /// Builds deterministic escort hit spheres each server tick for
    /// <see cref="BulletSimulationSystem"/> nearest-hit scans.
    /// </summary>
    public static class PeopleTransportEscortHitScan
    {
        /// <summary>
        /// Clears and fills <paramref name="targetsOut"/> from every living ship with troop cargo.
        /// Uses live <see cref="PeopleEscortSlot"/> poses (hover follow and landing).
        /// </summary>
        public static void RebuildTargets(
            EntityManager em,
            NativeArray<Entity> ships,
            float mapW,
            float mapH,
            List<PeopleEscortHitTarget> targetsOut)
        {
            targetsOut.Clear();
            if (ships.Length == 0)
                return;

            for (int s = 0; s < ships.Length; s++)
            {
                Entity ship = ships[s];
                if (!em.HasComponent<ShipState>(ship) || !em.HasComponent<LocalTransform>(ship))
                    continue;
                if (!em.HasComponent<GhostOwner>(ship))
                    continue;

                var shipState = em.GetComponentData<ShipState>(ship);
                if (shipState.IsDead || shipState.AwaitingTeamSelection || shipState.CurrentPeople <= 0)
                    continue;
                if (PlanetaryDefenseTurretControlLogic.IsControllingTurret(em, ship))
                    continue;

                var transform = em.GetComponentData<LocalTransform>(ship);
                var ghost = em.GetComponentData<GhostOwner>(ship);
                byte team = (byte)shipState.Team;
                int ownerNetId = ghost.NetworkId;

                if (em.HasBuffer<PeopleEscortSlot>(ship))
                {
                    var slots = em.GetBuffer<PeopleEscortSlot>(ship);
                    if (slots.Length > 0)
                    {
                        for (int i = 0; i < slots.Length; i++)
                        {
                            var slot = slots[i];
                            if (slot.Amount <= 0.01f)
                                continue;
                            float3 pos = slot.Position;
                            pos.y = 0f;
                            float3 vel = slot.Velocity;
                            vel.y = 0f;
                            targetsOut.Add(new PeopleEscortHitTarget
                            {
                                ShipEntity = ship,
                                SlotIndex = i,
                                Position = pos,
                                Velocity = vel,
                                Radius = PeopleTransportMath.GetEscortHitRadius(slot.Amount),
                                Team = team,
                                OwnerNetworkId = ownerNetId,
                            });
                        }

                        continue;
                    }
                }

                AppendPackedFollowTargets(
                    em, targetsOut, ship, in shipState, in transform, team, ownerNetId, mapW, mapH);
            }
        }

        /// <summary>
        /// Swept segment vs escort spheres. Friendly / own capsules are skipped.
        /// Returns true when a nearer contact than <paramref name="bestT"/> is found.
        /// </summary>
        public static bool TryKeepNearestEscortHit(
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            List<PeopleEscortHitTarget> targets,
            ref float bestT,
            ref float3 bestHit,
            out int targetIndex)
        {
            targetIndex = -1;
            if (targets == null || targets.Count == 0)
                return false;

            bool improved = false;
            for (int i = 0; i < targets.Count; i++)
            {
                PeopleEscortHitTarget t = targets[i];
                if (t.Team == b.OwnerTeam)
                    continue;
                if (b.OwnerNetworkId > 0 && t.OwnerNetworkId == b.OwnerNetworkId)
                    continue;

                float radius = math.max(PeopleTransportMath.TransportRadius, t.Radius);
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, t.Position, radius, mapW, mapH, out float3 hit))
                    continue;

                float3 delta = to - from;
                float lenSq = math.lengthsq(delta);
                float candT = lenSq > 1e-8f
                    ? math.dot(hit - from, delta) / lenSq
                    : 0f;
                if (candT < 0f || candT > 1f)
                    continue;
                if (candT >= bestT)
                    continue;

                bestT = candT;
                bestHit = hit;
                targetIndex = i;
                improved = true;
            }

            return improved;
        }

        static void AppendPackedFollowTargets(
            EntityManager em,
            List<PeopleEscortHitTarget> targetsOut,
            Entity ship,
            in ShipState shipState,
            in LocalTransform transform,
            byte team,
            int ownerNetId,
            float mapW,
            float mapH)
        {
            int people = shipState.CurrentPeople;
            int level = math.max(1, shipState.ShipLevel);
            int count = PeopleTransportMath.GetEscortSlotCount(people, level);
            if (count <= 0)
                return;

            PeopleTransportEscortLogic.ResolveEscortShipExtents(
                em, ship, in transform, out float extX, out float extZ);
            for (int i = 0; i < count; i++)
            {
                int amount = PeopleTransportMath.GetEscortSlotAmount(people, level, i);
                if (amount <= 0)
                    continue;
                float3 pos = PeopleTransportMath.EvaluateEscortSlotPose(
                    transform.Position, transform.Rotation, extX, extZ, i, count, amount, ownerNetId, mapW, mapH);
                targetsOut.Add(new PeopleEscortHitTarget
                {
                    ShipEntity = ship,
                    SlotIndex = i,
                    Position = pos,
                    Velocity = float3.zero,
                    Radius = PeopleTransportMath.GetEscortHitRadius(amount),
                    Team = team,
                    OwnerNetworkId = ownerNetId,
                });
            }
        }
    }
}
