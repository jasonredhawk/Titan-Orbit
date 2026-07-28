using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One derived drone hit sphere for the current server tick.
    /// Built once per bullet tick from ship pose + equipment — no drone ghosts.
    /// </summary>
    public struct DroneHitTarget
    {
        /// <summary>Owning ship entity (equipment buffer lives here).</summary>
        public Entity ShipEntity;

        /// <summary>Equipment slot index for RemainingCharges HP.</summary>
        public int SlotIndex;

        /// <summary>Planar world center on FixedY (EvaluateSlotPose).</summary>
        public float3 Position;

        /// <summary>Owner team — friendly bullets pass through.</summary>
        public byte Team;

        /// <summary>GhostOwner.NetworkId — own bullets never hit own drones.</summary>
        public int OwnerNetworkId;

        /// <summary>
        /// Hit-sphere radius multiplier from purchase level
        /// (<see cref="StoreItemData.GetDroneVisualScale"/>). Level 6 ≈ 1.0 (authored radius).
        /// </summary>
        public float HitRadiusScale;
    }

    /// <summary>
    /// Builds deterministic drone hit spheres each server tick for
    /// <see cref="BulletSimulationSystem"/> nearest-hit scans.
    /// Shield (and fighter/mining) bodies use <see cref="DroneSwarmPositioning.EvaluateSlotPose"/>
    /// so intercept matches the buzzing formation without networking drone transforms.
    /// </summary>
    public static class DroneSwarmHitScan
    {
        /// <summary>
        /// Clears and fills <paramref name="targetsOut"/> with every living drone pose this tick.
        /// Shield block walls use the same sorted-enemy assignment as client visuals.
        /// </summary>
        public static void RebuildTargets(
            EntityManager em,
            NativeArray<Entity> ships,
            NativeArray<Entity> allShipsForEnemies,
            double timeSeconds,
            float mapW,
            float mapH,
            List<DroneHitTarget> targetsOut,
            List<int> rearSlotsScratch,
            List<int> shieldSlotsScratch,
            List<int> enemyNetIdsScratch,
            Dictionary<int, float3> enemyPosByNetId,
            Dictionary<int, DroneSwarmPositioning.ShieldAssignment> shieldAssignments)
        {
            targetsOut.Clear();
            if (ships.Length == 0)
                return;

            for (int s = 0; s < ships.Length; s++)
            {
                Entity ship = ships[s];
                if (!em.HasComponent<ShipState>(ship) || !em.HasComponent<LocalTransform>(ship))
                    continue;
                if (!em.HasComponent<GhostOwner>(ship) || !em.HasBuffer<EquippedEquipmentElement>(ship))
                    continue;

                var shipState = em.GetComponentData<ShipState>(ship);
                if (shipState.IsDead || shipState.AwaitingTeamSelection)
                    continue;

                var buf = em.GetBuffer<EquippedEquipmentElement>(ship);
                rearSlotsScratch.Clear();
                shieldSlotsScratch.Clear();
                bool anyShield = false;
                for (int i = 0; i < buf.Length; i++)
                {
                    var e = buf[i];
                    var type = (StoreItemType)e.ItemType;
                    // [TITAN-ORBIT] Only shield drones intercept bullets (store "blocks fire").
                    // Skipping fighter/mining spheres saves EvaluateSlotPose × N every tick.
                    if (type != StoreItemType.ShieldDrone || e.RemainingCharges <= 0)
                        continue;
                    anyShield = true;
                    shieldSlotsScratch.Add(i);
                }

                if (!anyShield)
                    continue;

                var transform = em.GetComponentData<LocalTransform>(ship);
                var ghost = em.GetComponentData<GhostOwner>(ship);
                Vector3 shipPos = (Vector3)transform.Position;
                Quaternion shipRot = (Quaternion)transform.Rotation;
                DroneSwarmPositioning.GetShipBasis(shipPos, shipRot, out shipPos, out Vector3 forward, out Vector3 right);
                float hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(transform.Scale);
                float orbitRadius = DroneSwarmPositioning.GetDroneOrbitRadiusFromHull(hullRadius);
                int ownerNetId = ghost.NetworkId;
                byte team = (byte)shipState.Team;

                // --- Enemy list for shield walls (deterministic by NetworkId sort) ---
                enemyNetIdsScratch.Clear();
                enemyPosByNetId.Clear();
                shieldAssignments.Clear();
                CollectEnemiesInRange(
                    em, allShipsForEnemies, shipPos, (TeamId)team, ownerNetId,
                    DroneSwarmLogic.ShieldEngageRange, mapW, mapH,
                    enemyNetIdsScratch, enemyPosByNetId);
                DroneSwarmPositioning.BuildShieldAssignments(
                    shieldSlotsScratch, enemyNetIdsScratch, shieldAssignments);

                int shieldCount = math.max(1, shieldSlotsScratch.Count);

                for (int sIdx = 0; sIdx < shieldSlotsScratch.Count; sIdx++)
                {
                    int slot = shieldSlotsScratch[sIdx];
                    bool hasShieldTarget = false;
                    Vector3 enemyPos = default;
                    int indexOnEnemy = 0;
                    int countOnEnemy = 1;
                    if (shieldAssignments.TryGetValue(slot, out var assign) &&
                        assign.EnemyNetworkId > 0 &&
                        enemyPosByNetId.TryGetValue(assign.EnemyNetworkId, out float3 ep))
                    {
                        hasShieldTarget = true;
                        enemyPos = new Vector3(ep.x, 0f, ep.z);
                        indexOnEnemy = assign.IndexOnEnemy;
                        countOnEnemy = math.max(1, assign.CountOnEnemy);
                    }

                    var ctx = new DroneSwarmPositioning.SlotEvaluationContext
                    {
                        ShipPos = shipPos,
                        Forward = forward,
                        Right = right,
                        OrbitRadius = orbitRadius,
                        TimeSeconds = timeSeconds,
                        ShipNetworkId = ownerNetId,
                        MapW = mapW,
                        MapH = mapH,
                        ShieldOrdinal = sIdx,
                        ShieldCount = shieldCount,
                        HasShieldTarget = hasShieldTarget,
                        EnemyPos = enemyPos,
                        IndexOnEnemy = indexOnEnemy,
                        CountOnEnemy = countOnEnemy,
                    };
                    var pose = DroneSwarmPositioning.EvaluateSlotPose(
                        StoreItemType.ShieldDrone, slot, in ctx);
                    int droneLevel = math.max(1, buf[slot].ItemLevel > 0
                        ? buf[slot].ItemLevel
                        : StoreItemData.DroneReferenceMaxLevel);
                    targetsOut.Add(new DroneHitTarget
                    {
                        ShipEntity = ship,
                        SlotIndex = slot,
                        Position = new float3(pose.WorldPosition.x, DroneSwarmLogic.FixedY, pose.WorldPosition.z),
                        Team = team,
                        OwnerNetworkId = ownerNetId,
                        HitRadiusScale = StoreItemData.GetDroneVisualScale(droneLevel),
                    });
                }
            }
        }

        /// <summary>
        /// Swept segment vs all drone spheres. Friendly / own bullets are skipped.
        /// Returns true when a nearer contact than <paramref name="bestT"/> is found.
        /// </summary>
        public static bool TryKeepNearestDroneHit(
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            List<DroneHitTarget> targets,
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
                DroneHitTarget t = targets[i];
                // Ally / own drones do not absorb (shields block enemy fire only).
                if (t.Team == b.OwnerTeam)
                    continue;
                if (b.OwnerNetworkId > 0 && t.OwnerNetworkId == b.OwnerNetworkId)
                    continue;

                // --- Level-scaled hit sphere (matches visual size) ---
                float radius = DroneSwarmPositioning.DroneHitSphereRadius
                    * math.max(0.25f, t.HitRadiusScale > 0.01f ? t.HitRadiusScale : 1f);

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

        /// <summary>
        /// Applies bullet damage to a drone slot's RemainingCharges (ghosted HP).
        /// Removes the slot equipment when HP hits 0.
        /// </summary>
        public static void ApplyDamageToDroneSlot(EntityManager em, Entity ship, int slotIndex, float damage)
        {
            if (!em.HasBuffer<EquippedEquipmentElement>(ship))
                return;
            var buf = em.GetBuffer<EquippedEquipmentElement>(ship);
            if (slotIndex < 0 || slotIndex >= buf.Length)
                return;

            var e = buf[slotIndex];
            if (!StoreItemData.IsDrone((StoreItemType)e.ItemType) || e.RemainingCharges <= 0)
                return;

            int dmg = math.max(1, (int)math.ceil(damage));
            e.RemainingCharges = math.max(0, e.RemainingCharges - dmg);
            // Keep ItemType — visuals / combat skip slots with RemainingCharges <= 0.
            buf[slotIndex] = e;
        }

        static void CollectEnemiesInRange(
            EntityManager em,
            NativeArray<Entity> ships,
            Vector3 ownerPos,
            TeamId ownerTeam,
            int ownerNetworkId,
            float range,
            float mapW,
            float mapH,
            List<int> enemyNetIdsOut,
            Dictionary<int, float3> enemyPosOut)
        {
            float rangeSq = range * range;
            for (int i = 0; i < ships.Length; i++)
            {
                Entity e = ships[i];
                if (!em.HasComponent<ShipState>(e) || !em.HasComponent<GhostOwner>(e))
                    continue;
                var st = em.GetComponentData<ShipState>(e);
                if (st.IsDead)
                    continue;
                if (ownerTeam != TeamId.None && st.Team == ownerTeam)
                    continue;
                var ghost = em.GetComponentData<GhostOwner>(e);
                if (ownerNetworkId > 0 && ghost.NetworkId == ownerNetworkId)
                    continue;

                float3 pos = em.GetComponentData<LocalTransform>(e).Position;
                pos.y = 0f;
                float dist = DroneSwarmLogic.ToroidalDistanceXZ(
                    ownerPos.x, ownerPos.z, pos.x, pos.z, mapW, mapH);
                if (dist * dist > rangeSq)
                    continue;

                enemyNetIdsOut.Add(ghost.NetworkId);
                enemyPosOut[ghost.NetworkId] = pos;
            }
        }

        static int IndexOf(List<int> list, int value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value)
                    return i;
            }
            return 0;
        }
    }
}
