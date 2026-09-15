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

        /// <summary>
        /// Equipment slot index for RemainingCharges HP.
        /// <c>-1</c> after this tick destroyed that drone (<c>RemoveAt</c> freed the gear row).
        /// </summary>
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
    /// Killing a drone <c>RemoveAt</c>s its <see cref="EquippedEquipmentElement"/> so the
    /// ship's LOADOUT / gear slot is empty again.
    /// </summary>
    public static class DroneSwarmHitScan
    {
        /// <summary>Idle rear poses used so each shield picks its own closest enemy.</summary>
        static readonly List<Vector3> s_ShieldIdlePos = new List<Vector3>(8);

        /// <summary>Vector3 copy of enemy poses for shared assignment math (Entities has no float3 dict API).</summary>
        static readonly Dictionary<int, Vector3> s_EnemyPosVec = new Dictionary<int, Vector3>(16);

        /// <summary>
        /// Clears and fills <paramref name="targetsOut"/> with every living drone pose this tick.
        /// Shield block walls use the same sorted-enemy assignment as client visuals
        /// (enemy hulls plus enemy planetary-defense pads).
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
            Dictionary<int, DroneSwarmPositioning.ShieldAssignment> shieldAssignments,
            List<PlanetaryDefenseHitTarget> defenseTargets = null)
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

                // [TITAN-ORBIT] 0-HP drones from before this kill path still occupy the
                // equipment buffer (a filled LOADOUT row). Strip them here so a leftover
                // wreck cannot block a store buy while we rebuild this tick's spheres.
                CompactDestroyedDroneSlots(em, ship);

                var shipState = em.GetComponentData<ShipState>(ship);
                if (shipState.IsDead || shipState.AwaitingTeamSelection)
                    continue;
                // [TITAN-ORBIT] No shield spheres while the owner is piloting a defense pad.
                if (PlanetaryDefenseTurretControlLogic.IsControllingTurret(em, ship))
                    continue;

                var buf = em.GetBuffer<EquippedEquipmentElement>(ship);
                rearSlotsScratch.Clear();
                shieldSlotsScratch.Clear();
                bool anyDrone = false;
                for (int i = 0; i < buf.Length; i++)
                {
                    var e = buf[i];
                    var type = (StoreItemType)e.ItemType;
                    if (!StoreItemData.IsDrone(type) || e.RemainingCharges <= 0)
                        continue;
                    anyDrone = true;
                    if (type == StoreItemType.FighterDrone || type == StoreItemType.MiningDrone)
                        rearSlotsScratch.Add(i);
                    else if (type == StoreItemType.ShieldDrone)
                        shieldSlotsScratch.Add(i);
                }

                if (!anyDrone)
                    continue;

                var transform = em.GetComponentData<LocalTransform>(ship);
                var ghost = em.GetComponentData<GhostOwner>(ship);
                Vector3 shipPos = (Vector3)transform.Position;
                Quaternion shipRot = (Quaternion)transform.Rotation;
                DroneSwarmPositioning.GetShipBasis(shipPos, shipRot, out shipPos, out Vector3 forward, out Vector3 right);
                float hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(transform.Scale);
                float escortRadius = DroneSwarmPositioning.GetDroneOrbitRadiusFromHull(hullRadius);
                float orbitRadius = DroneSwarmPositioning.GetShieldOrbitRadiusFromHull(hullRadius);
                float coverEx = 0f, coverEz = 0f, coverCx = 0f, coverCz = 0f;
                if (em.HasComponent<ShipHullColliderState>(ship))
                {
                    var hull = em.GetComponentData<ShipHullColliderState>(ship);
                    coverEx = hull.AppliedCoveringExtentX;
                    coverEz = hull.AppliedCoveringExtentZ;
                    coverCx = hull.AppliedCoveringCenterX;
                    coverCz = hull.AppliedCoveringCenterZ;
                }
                int ownerNetId = ghost.NetworkId;
                byte team = (byte)shipState.Team;
                int rearCount = math.max(1, rearSlotsScratch.Count);
                int shieldCount = math.max(1, shieldSlotsScratch.Count);

                for (int i = 0; i < buf.Length; i++)
                {
                    var e = buf[i];
                    var type = (StoreItemType)e.ItemType;
                    if (!StoreItemData.IsDrone(type) || e.RemainingCharges <= 0)
                        continue;

                    int rearOrd = IndexOf(rearSlotsScratch, i);
                    int shieldOrd = IndexOf(shieldSlotsScratch, i);
                    bool isShield = type == StoreItemType.ShieldDrone;
                    var ctx = new DroneSwarmPositioning.SlotEvaluationContext
                    {
                        ShipPos = shipPos,
                        Forward = forward,
                        Right = right,
                        OrbitRadius = isShield ? orbitRadius : escortRadius,
                        TimeSeconds = timeSeconds,
                        ShipNetworkId = ownerNetId,
                        MapW = mapW,
                        MapH = mapH,
                        RearOrdinal = rearOrd,
                        RearCount = rearCount,
                        ShieldOrdinal = shieldOrd,
                        ShieldCount = shieldCount,
                        HasShieldTarget = false,
                    };
                    DroneSwarmPositioning.ApplyCoveringHullShape(
                        ref ctx, transform.Scale, coverEx, coverEz, coverCx, coverCz);
                    Vector3 idle = DroneSwarmPositioning.EvaluateSlotPose(type, i, in ctx).WorldPosition;
                    idle.y = DroneSwarmLogic.FixedY;
                    Vector3 offset = DroneSwarmFormationRuntime.Get(
                        DroneSwarmLogic.FormationAnchorKey(ownerNetId, i)).Offset;
                    Vector3 world = idle + offset;
                    world.y = DroneSwarmLogic.FixedY;
                    int droneLevel = math.max(1, e.ItemLevel > 0
                        ? e.ItemLevel
                        : StoreItemData.DroneReferenceMaxLevel);
                    targetsOut.Add(new DroneHitTarget
                    {
                        ShipEntity = ship,
                        SlotIndex = i,
                        Position = new float3(world.x, DroneSwarmLogic.FixedY, world.z),
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
                if (t.SlotIndex < 0)
                    continue;
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
        /// Applies bullet (or splash) damage to one drone's ghosted HP
        /// (<see cref="EquippedEquipmentElement.RemainingCharges"/>).
        /// Called from <see cref="BulletSimulationSystem"/> after a swept sphere hit, and from
        /// <see cref="BulletBankHitEffects"/> for blast falloff.
        /// When HP reaches 0 the equipment row is removed so the ship's LOADOUT / gear slot
        /// is free for a new store buy — same <c>RemoveAt</c> as a player discard.
        /// </summary>
        /// <param name="em">Server EntityManager (authoritative equipment buffer).</param>
        /// <param name="ship">Owning ship entity — the buffer lives here, not on a drone ghost.</param>
        /// <param name="slotIndex">Equipment index captured when hit spheres were built this tick.</param>
        /// <param name="damage">Raw incoming damage before shield-absorb cards.</param>
        /// <param name="liveTargets">
        /// This tick's derived hit spheres. Required when the slot is destroyed: later bullets
        /// and splash still hold the old index, and <c>RemoveAt</c> shifts later rows down.
        /// Pass <c>null</c> only when no cached target list exists.
        /// </param>
        public static void ApplyDamageToDroneSlot(
            EntityManager em,
            Entity ship,
            int slotIndex,
            float damage,
            List<DroneHitTarget> liveTargets = null)
        {
            if (!em.HasBuffer<EquippedEquipmentElement>(ship))
                return;
            var buf = em.GetBuffer<EquippedEquipmentElement>(ship);
            if (slotIndex < 0 || slotIndex >= buf.Length)
                return;

            var e = buf[slotIndex];
            if (!StoreItemData.IsDrone((StoreItemType)e.ItemType) || e.RemainingCharges <= 0)
                return;

            // --- Shield absorb cards ---
            // [TITAN-ORBIT] Shield drones can be tougher via ShieldDroneAbsorbMul (divide incoming).
            if ((StoreItemType)e.ItemType == StoreItemType.ShieldDrone)
            {
                float absorb = CardEffectQuery.GetMul(em, ship, CardEffectKind.ShieldDroneAbsorbMul);
                if (absorb > 1.0001f)
                    damage /= absorb;
            }

            // --- Apply HP ---
            // Charges are ints on the ghost; ceil so a fractional splash still chips 1 HP.
            int dmg = math.max(1, (int)math.ceil(damage));
            e.RemainingCharges = math.max(0, e.RemainingCharges - dmg);

            if (e.RemainingCharges > 0)
            {
                buf[slotIndex] = e;
                return;
            }

            // --- Destroyed: free the gear slot ---
            // [TITAN-ORBIT] Leaving ItemType with 0 HP used to skip combat/visuals but still
            // counted as a filled LOADOUT row (HasEmptyLoadoutSlot uses buffer.Length).
            // RemoveAt matches MoonOrbitStoreSystem discard so the player can rebuy.
            buf.RemoveAt(slotIndex);
            InvalidateAndShiftLiveTargets(liveTargets, ship, slotIndex);
        }

        /// <summary>
        /// Strips leftover 0-HP drone rows from one ship's equipment buffer.
        /// Walks high-to-low so each <c>RemoveAt</c> does not skip a neighbor.
        /// Safe to call every tick — the buffer is only a handful of loadout rows.
        /// </summary>
        /// <returns>How many drone rows were removed.</returns>
        public static int CompactDestroyedDroneSlots(EntityManager em, Entity ship)
        {
            if (!em.HasBuffer<EquippedEquipmentElement>(ship))
                return 0;

            var buf = em.GetBuffer<EquippedEquipmentElement>(ship);
            int removed = 0;
            for (int i = buf.Length - 1; i >= 0; i--)
            {
                var e = buf[i];
                if (!StoreItemData.IsDrone((StoreItemType)e.ItemType) || e.RemainingCharges > 0)
                    continue;
                buf.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        /// <summary>
        /// After <c>RemoveAt(removedSlot)</c>, this tick's cached spheres still point at the
        /// old indices. Invalidate the wreck and shift later slots on the same ship down by one
        /// so a second bullet or splash cannot damage the rocket/mine that slid into that row.
        /// </summary>
        static void InvalidateAndShiftLiveTargets(
            List<DroneHitTarget> liveTargets,
            Entity ship,
            int removedSlot)
        {
            if (liveTargets == null || liveTargets.Count == 0)
                return;

            for (int i = 0; i < liveTargets.Count; i++)
            {
                DroneHitTarget t = liveTargets[i];
                if (t.ShipEntity != ship)
                    continue;

                if (t.SlotIndex == removedSlot)
                {
                    // SlotIndex < 0 → TryKeepNearestDroneHit and splash skip this sphere.
                    t.SlotIndex = -1;
                    liveTargets[i] = t;
                    continue;
                }

                if (t.SlotIndex > removedSlot)
                {
                    t.SlotIndex--;
                    liveTargets[i] = t;
                }
            }
        }

        static void CollectEnemiesInRange(
            EntityManager em,
            NativeArray<Entity> ships,
            List<PlanetaryDefenseHitTarget> defenseTargets,
            Vector3 ownerPos,
            TeamId ownerTeam,
            int ownerNetworkId,
            float shipRange,
            float turretRange,
            float mapW,
            float mapH,
            List<int> enemyNetIdsOut,
            Dictionary<int, float3> enemyPosOut)
        {
            float shipRangeSq = shipRange * shipRange;
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
                if (dist * dist > shipRangeSq)
                    continue;

                enemyNetIdsOut.Add(ghost.NetworkId);
                enemyPosOut[ghost.NetworkId] = pos;
            }

            if (defenseTargets == null || defenseTargets.Count == 0)
                return;

            float turretRangeSq = turretRange * turretRange;
            byte ownerTeamByte = (byte)ownerTeam;
            for (int i = 0; i < defenseTargets.Count; i++)
            {
                var pad = defenseTargets[i];
                if (pad.Team == (byte)TeamId.None || pad.Team == ownerTeamByte)
                    continue;

                int padId = DroneSwarmLogic.MakeDefensePadEnemyId(pad.PlanetId, pad.SlotIndex);
                if (padId == 0)
                    continue;

                float3 pos = pad.Position;
                pos.y = 0f;
                float dist = DroneSwarmLogic.ToroidalDistanceXZ(
                    ownerPos.x, ownerPos.z, pos.x, pos.z, mapW, mapH);
                if (dist * dist > turretRangeSq)
                    continue;

                enemyNetIdsOut.Add(padId);
                enemyPosOut[padId] = pos;
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
