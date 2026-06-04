using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>Deterministic drone orbit and shield-intercept math shared by all peers.</summary>
    public static class DroneSwarmLogic
    {
        public const float DefaultOrbitRadius = 3f;
        public const float DefaultOrbitSpeedDeg = 90f;
        public const float DefaultMoveSpeed = 8f;
        public const float FixedY = 0f;

        public static float DeterministicBasePhaseRad(ulong shipNetworkId, int slotIndex, StoreItemType droneType)
        {
            uint hash = (uint)(shipNetworkId ^ ((ulong)slotIndex * 0x9E3779B9UL) ^ ((ulong)(int)droneType * 0x85EBCA6BUL));
            hash ^= hash >> 16;
            hash *= 0x7FEB352D;
            hash ^= hash >> 15;
            return (hash % 6283) / 1000f;
        }

        public static int CountDroneSlots(System.Collections.Generic.IReadOnlyList<EquippedEquipmentEntry> equipment)
        {
            if (equipment == null) return 0;
            int n = 0;
            for (int i = 0; i < equipment.Count; i++)
            {
                if (StoreItemData.IsDrone(equipment[i].ItemType))
                    n++;
            }
            return n;
        }

        public static int DroneOrdinalAtSlot(System.Collections.Generic.IReadOnlyList<EquippedEquipmentEntry> equipment, int slotIndex)
        {
            if (equipment == null || slotIndex < 0) return 0;
            int ordinal = 0;
            for (int i = 0; i <= slotIndex && i < equipment.Count; i++)
            {
                if (!StoreItemData.IsDrone(equipment[i].ItemType)) continue;
                if (i == slotIndex) return ordinal;
                ordinal++;
            }
            return ordinal;
        }

        /// <summary>World-space ring orbit around ship (does not rotate with ship facing).</summary>
        public static void ComputeOrbitWorldOffset(
            ulong shipNetworkId,
            int slotIndex,
            int droneOrdinal,
            int droneCount,
            StoreItemType droneType,
            double serverTimeSeconds,
            float orbitRadius,
            float orbitSpeedDeg,
            out Vector3 worldOffset)
        {
            float basePhase = DeterministicBasePhaseRad(shipNetworkId, slotIndex, droneType);
            float spread = droneCount > 0 ? droneOrdinal * (Mathf.PI * 2f / droneCount) : 0f;
            float angle = basePhase + spread + orbitSpeedDeg * Mathf.Deg2Rad * (float)serverTimeSeconds;
            worldOffset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * orbitRadius;
        }

        public static Vector3 WorldFirePosition(Vector3 shipWorldPos, Vector3 worldOffset, Vector3 knockbackOffset)
        {
            Vector3 p = shipWorldPos + worldOffset + knockbackOffset;
            p.y = FixedY;
            return p;
        }

        public static bool TryComputeShieldWorldOffset(
            Vector3 shipWorldPos,
            Vector3 currentWorldPos,
            float moveSpeed,
            float interceptSpeedMultiplier,
            float bulletDetectRadius,
            TeamManager.Team ownerTeam,
            out Vector3 worldOffset)
        {
            worldOffset = currentWorldPos - shipWorldPos;
            worldOffset.y = 0f;
            if (!TryFindIncomingBulletTowardShip(shipWorldPos, ownerTeam, bulletDetectRadius, out Vector3 bulletPos, out _))
                return false;

            bulletPos.y = FixedY;
            Vector3 toShip = shipWorldPos - bulletPos;
            toShip.y = 0f;
            if (toShip.sqrMagnitude < 0.01f) return false;

            float distToShip = toShip.magnitude;
            Vector3 bulletDir = toShip / distToShip;
            float interceptDist = Mathf.Max(1.5f, distToShip * 0.4f);
            Vector3 idealPos = bulletPos + bulletDir * (distToShip - interceptDist);
            idealPos.y = FixedY;

            Vector3 myPos = currentWorldPos;
            myPos.y = FixedY;
            Vector3 toIdeal = idealPos - myPos;
            toIdeal.y = 0f;
            if (toIdeal.sqrMagnitude < 0.01f) return false;

            float speed = moveSpeed * interceptSpeedMultiplier;
            float step = Mathf.Min(speed * Time.fixedDeltaTime, toIdeal.magnitude);
            Vector3 next = myPos + toIdeal.normalized * step;
            worldOffset = next - shipWorldPos;
            worldOffset.y = 0f;
            return true;
        }

        public static bool TryFindIncomingBulletTowardShip(
            Vector3 shipWorldPos,
            TeamManager.Team ownerTeam,
            float bulletDetectRadius,
            out Vector3 bulletPos,
            out Vector3 bulletVelocity)
        {
            bulletPos = Vector3.zero;
            bulletVelocity = Vector3.zero;
            DroneTargetCache.RefreshIfNeeded();

            shipWorldPos.y = FixedY;
            int n = DroneTargetCache.BulletSnapshotCount;
            float bestScore = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                ServerBulletSnapshot snap = DroneTargetCache.GetBulletSnapshot(i);
                if (snap.OwnerTeam == ownerTeam) continue;

                Vector3 bp = snap.Position;
                bp.y = FixedY;
                float dist = Vector3.Distance(bp, shipWorldPos);
                if (dist > bulletDetectRadius) continue;

                Vector3 toShip = shipWorldPos - bp;
                toShip.y = 0f;
                if (toShip.sqrMagnitude < 0.01f) continue;
                toShip.Normalize();

                Vector3 vel = snap.Velocity;
                vel.y = 0f;
                if (vel.sqrMagnitude < 0.01f) continue;
                Vector3 velNormalized = vel.normalized;

                float dot = Vector3.Dot(velNormalized, toShip);
                if (dot < 0.5f) continue;

                float score = dist * (1f - dot);
                if (score < bestScore)
                {
                    bestScore = score;
                    bulletPos = bp;
                    bulletVelocity = vel;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>Enemy ship nearest to <paramref name="ownerShip"/> within engage range (toroidal XZ).</summary>
        public static Starship FindNearestEnemyShipNearOwner(Starship ownerShip, float engageRangeFromShip)
        {
            if (ownerShip == null) return null;
            DroneTargetCache.RefreshIfNeeded();
            Vector3 ownerPos = ownerShip.transform.position;
            ownerPos.y = FixedY;
            TeamManager.Team myTeam = ownerShip.ShipTeam;
            Starship nearest = null;
            float nearestSq = engageRangeFromShip * engageRangeFromShip;
            foreach (var ship in DroneTargetCache.Ships)
            {
                if (ship == null || ship.IsDead || ship == ownerShip || ship.ShipTeam == myTeam) continue;
                float sq = ToroidalMap.WrapPosition(ship.transform.position - ownerPos).sqrMagnitude;
                if (sq < nearestSq)
                {
                    nearestSq = sq;
                    nearest = ship;
                }
            }
            return nearest;
        }

        /// <summary>Asteroid nearest to <paramref name="ownerShip"/> within engage range (toroidal XZ).</summary>
        public static Asteroid FindNearestAsteroidNearOwner(Starship ownerShip, float engageRangeFromShip)
        {
            if (ownerShip == null) return null;
            DroneTargetCache.RefreshIfNeeded();
            Vector3 ownerPos = ownerShip.transform.position;
            ownerPos.y = FixedY;
            Asteroid nearest = null;
            float nearestSq = engageRangeFromShip * engageRangeFromShip;
            foreach (var ast in DroneTargetCache.Asteroids)
            {
                if (ast == null || ast.IsDestroyed) continue;
                float sq = ToroidalMap.WrapPosition(ast.transform.position - ownerPos).sqrMagnitude;
                if (sq < nearestSq)
                {
                    nearestSq = sq;
                    nearest = ast;
                }
            }
            return nearest;
        }
    }
}
