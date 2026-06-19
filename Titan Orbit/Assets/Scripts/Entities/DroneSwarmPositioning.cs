using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>Ship-relative drone formation: rear escort cluster, side orbit shields, and intercept wall.</summary>
    public static class DroneSwarmPositioning
    {
        public const float DroneHitSphereRadius = 0.42f;

        public struct ShieldAssignment
        {
            public int enemyInstanceId;
            public int indexOnEnemy;
            public int countOnEnemy;
        }

        public static void GetShipBasis(Starship ship, out Vector3 shipPos, out Vector3 forward, out Vector3 right)
        {
            shipPos = ship != null ? ship.transform.position : Vector3.zero;
            shipPos.y = DroneSwarmLogic.FixedY;
            forward = ship != null ? ship.transform.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude < 0.01f) right = Vector3.right;
            right.Normalize();
        }

        public static float PerDroneBuzzPhase(ulong shipNetworkId, int slotIndex, StoreItemType type) =>
            DroneSwarmLogic.DeterministicBasePhaseRad(shipNetworkId, slotIndex, type);

        /// <summary>Shared fighter / mining / shield buzz wobble.</summary>
        public static Vector3 ComputeBuzzOffset(
            Vector3 axisA,
            Vector3 axisB,
            int slotIndex,
            float clusterOrdinal,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            float t = (float)timeSeconds;
            float buzz = buzzPhase + slotIndex * 0.37f;
            float wobble = buzz + clusterOrdinal * 0.61f + t * buzzSpeed * 0.45f;
            return axisA * (Mathf.Sin(t * buzzSpeed + buzz) * buzzAmplitude)
                + axisB * (Mathf.Cos(t * buzzSpeed * 1.17f + buzz * 1.3f) * buzzAmplitude * 0.55f)
                + axisA * (Mathf.Sin(wobble) * buzzAmplitude * 0.45f)
                + axisB * (Mathf.Cos(wobble * 1.13f) * buzzAmplitude * 0.35f);
        }

        public struct OrbitSlotTarget
        {
            public float angleDeg;
            public float radius;
            public Vector3 buzz;
        }

        public static Vector3 PolarSlotToWorld(Vector3 shipPos, Vector3 forward, Vector3 right, float angleDeg, float radius)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            Vector3 world = shipPos + forward * (Mathf.Cos(rad) * radius) + right * (Mathf.Sin(rad) * radius);
            world.y = DroneSwarmLogic.FixedY;
            return world;
        }

        public static void WorldOffsetToPolar(Vector3 forward, Vector3 right, Vector3 offset, out float angleDeg, out float radius)
        {
            offset.y = 0f;
            radius = offset.magnitude;
            angleDeg = radius > 0.001f
                ? Mathf.Atan2(Vector3.Dot(offset, right), Vector3.Dot(offset, forward)) * Mathf.Rad2Deg
                : 0f;
        }

        /// <summary>World XZ angle (degrees) and radius from ship center.</summary>
        public static void WorldOffsetToWorldPolar(Vector3 offset, out float worldAngleDeg, out float radius)
        {
            offset.y = 0f;
            radius = offset.magnitude;
            worldAngleDeg = radius > 0.001f
                ? Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg
                : 0f;
        }

        public static Vector3 WorldPolarToWorld(Vector3 shipPos, float worldAngleDeg, float radius)
        {
            float rad = worldAngleDeg * Mathf.Deg2Rad;
            Vector3 world = shipPos + new Vector3(Mathf.Sin(rad) * radius, 0f, Mathf.Cos(rad) * radius);
            world.y = DroneSwarmLogic.FixedY;
            return world;
        }

        public static float ShipLocalSlotToWorldAngleDeg(Vector3 forward, Vector3 right, float localAngleDeg, float radius)
        {
            float rad = localAngleDeg * Mathf.Deg2Rad;
            Vector3 offset = forward * (Mathf.Cos(rad) * radius) + right * (Mathf.Sin(rad) * radius);
            WorldOffsetToWorldPolar(offset, out float worldAngleDeg, out _);
            return worldAngleDeg;
        }

        public static OrbitSlotTarget ComputeRearEscortOrbitSlot(
            Starship ownerShip,
            int slotIndex,
            int clusterOrdinal,
            int clusterCount,
            float behindDistance,
            float lateralSpread,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            GetShipBasis(ownerShip, out _, out Vector3 forward, out Vector3 right);
            Vector3 behind = -forward;

            float center = (clusterCount - 1) * 0.5f;
            float lateral = (clusterOrdinal - center) * lateralSpread;
            float angleDeg = Mathf.Atan2(lateral, behindDistance) * Mathf.Rad2Deg + 180f;
            float radius = Mathf.Sqrt(behindDistance * behindDistance + lateral * lateral);
            Vector3 buzz = ComputeBuzzOffset(right, behind, slotIndex, clusterOrdinal, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            return new OrbitSlotTarget { angleDeg = angleDeg, radius = radius, buzz = buzz };
        }

        public static OrbitSlotTarget ComputeShieldSideOrbitSlot(
            Starship ownerShip,
            int slotIndex,
            int sideOrdinal,
            int sideCount,
            float orbitRadius,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            GetShipBasis(ownerShip, out _, out Vector3 forward, out Vector3 right);
            int sideSign = (sideOrdinal % 2 == 0) ? 1 : -1;
            if (sideCount <= 1)
                sideSign = sideOrdinal == 0 ? 1 : -1;

            float sideCenter = sideSign * Mathf.PI * 0.5f;
            float wobble = buzzPhase + sideOrdinal * 0.85f + (float)timeSeconds * buzzSpeed * 0.45f;
            float sweep = Mathf.PI * 0.32f;
            float angleRad = sideCenter
                + Mathf.Sin(wobble) * sweep * 0.55f
                + Mathf.Cos(wobble * 0.73f + sideOrdinal) * sweep * 0.35f;
            Vector3 radialDir = forward * Mathf.Cos(angleRad) + right * Mathf.Sin(angleRad);
            Vector3 tangent = Vector3.Cross(Vector3.up, radialDir);
            if (tangent.sqrMagnitude < 0.0001f) tangent = forward;
            tangent.Normalize();

            Vector3 buzz = ComputeBuzzOffset(tangent, radialDir, slotIndex, sideOrdinal, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            return new OrbitSlotTarget { angleDeg = angleRad * Mathf.Rad2Deg, radius = orbitRadius, buzz = buzz };
        }

        public static OrbitSlotTarget ComputeShieldBlockOrbitSlot(
            Starship ownerShip,
            Starship enemyShip,
            int slotIndex,
            int indexOnEnemy,
            int countOnEnemy,
            float blockDistanceFromShip,
            float formationSpacing,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            if (ownerShip == null || enemyShip == null)
                return new OrbitSlotTarget { angleDeg = 0f, radius = blockDistanceFromShip, buzz = Vector3.zero };

            GetShipBasis(ownerShip, out Vector3 shipPos, out Vector3 forward, out Vector3 right);
            Vector3 enemyPos = enemyShip.transform.position;
            enemyPos.y = DroneSwarmLogic.FixedY;

            Vector3 toEnemy = ToroidalMap.WrapPosition(enemyPos - shipPos);
            toEnemy.y = 0f;
            float dist = toEnemy.magnitude;
            if (dist < 0.01f)
                return ComputeShieldSideOrbitSlot(ownerShip, slotIndex, indexOnEnemy, countOnEnemy, blockDistanceFromShip, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);

            Vector3 lineDir = toEnemy / dist;
            Vector3 perp = Vector3.Cross(Vector3.up, lineDir);
            if (perp.sqrMagnitude < 0.01f) perp = right;
            perp.Normalize();

            float lateral = (indexOnEnemy - (countOnEnemy - 1) * 0.5f) * formationSpacing;
            float along = Mathf.Min(blockDistanceFromShip, dist * 0.42f);
            Vector3 baseOffset = lineDir * along + perp * lateral;
            WorldOffsetToPolar(forward, right, baseOffset, out float angleDeg, out float radius);
            Vector3 buzz = ComputeBuzzOffset(perp, lineDir, slotIndex, indexOnEnemy, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            return new OrbitSlotTarget { angleDeg = angleDeg, radius = radius, buzz = buzz };
        }

        /// <summary>Fighter + mining share the same rear cluster behind the ship with buzz and small orbit wobble.</summary>
        public static Vector3 ComputeSharedRearEscortWorldPosition(
            Starship ownerShip,
            int slotIndex,
            int clusterOrdinal,
            int clusterCount,
            float behindDistance,
            float lateralSpread,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            GetShipBasis(ownerShip, out Vector3 shipPos, out Vector3 forward, out Vector3 right);
            Vector3 behind = -forward;

            float center = (clusterCount - 1) * 0.5f;
            float lateral = (clusterOrdinal - center) * lateralSpread;
            Vector3 buzz = ComputeBuzzOffset(right, behind, slotIndex, clusterOrdinal, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);

            Vector3 world = shipPos + behind * behindDistance + right * lateral + buzz;
            world.y = DroneSwarmLogic.FixedY;
            return world;
        }

        /// <summary>Shield idle: orbit ship center on port/starboard arcs with the same buzz as rear escorts.</summary>
        public static Vector3 ComputeShieldSideOrbitWorldPosition(
            Starship ownerShip,
            int slotIndex,
            int sideOrdinal,
            int sideCount,
            float orbitRadius,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            GetShipBasis(ownerShip, out Vector3 shipPos, out Vector3 forward, out Vector3 right);
            int sideSign = (sideOrdinal % 2 == 0) ? 1 : -1;
            if (sideCount <= 1)
                sideSign = sideOrdinal == 0 ? 1 : -1;

            float sideCenter = sideSign * Mathf.PI * 0.5f;
            float wobble = buzzPhase + sideOrdinal * 0.85f + (float)timeSeconds * buzzSpeed * 0.45f;
            float sweep = Mathf.PI * 0.32f;
            float angle = sideCenter
                + Mathf.Sin(wobble) * sweep * 0.55f
                + Mathf.Cos(wobble * 0.73f + sideOrdinal) * sweep * 0.35f;

            Vector3 radial = forward * (Mathf.Cos(angle) * orbitRadius) + right * (Mathf.Sin(angle) * orbitRadius);
            Vector3 radialDir = radial.sqrMagnitude > 0.0001f ? radial.normalized : right * sideSign;
            Vector3 tangent = Vector3.Cross(Vector3.up, radialDir);
            if (tangent.sqrMagnitude < 0.0001f) tangent = forward;
            tangent.Normalize();

            Vector3 buzz = ComputeBuzzOffset(tangent, radialDir, slotIndex, sideOrdinal, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            Vector3 world = shipPos + radial + buzz;
            world.y = DroneSwarmLogic.FixedY;
            return world;
        }

        /// <summary>Shield active: just outside hull toward enemy, with buzz and lateral wall spacing.</summary>
        public static Vector3 ComputeShieldBlockWorldPosition(
            Starship ownerShip,
            Starship enemyShip,
            int slotIndex,
            int indexOnEnemy,
            int countOnEnemy,
            float blockDistanceFromShip,
            float formationSpacing,
            float buzzAmplitude,
            float buzzSpeed,
            double timeSeconds,
            float buzzPhase)
        {
            if (ownerShip == null || enemyShip == null)
                return ownerShip != null ? ownerShip.transform.position : Vector3.zero;

            GetShipBasis(ownerShip, out Vector3 shipPos, out _, out Vector3 right);
            Vector3 enemyPos = enemyShip.transform.position;
            enemyPos.y = DroneSwarmLogic.FixedY;

            Vector3 toEnemy = ToroidalMap.WrapPosition(enemyPos - shipPos);
            toEnemy.y = 0f;
            float dist = toEnemy.magnitude;
            if (dist < 0.01f)
                return ComputeShieldSideOrbitWorldPosition(ownerShip, slotIndex, indexOnEnemy, countOnEnemy, blockDistanceFromShip, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);

            Vector3 lineDir = toEnemy / dist;
            Vector3 perp = Vector3.Cross(Vector3.up, lineDir);
            if (perp.sqrMagnitude < 0.01f) perp = right;
            perp.Normalize();

            float lateral = (indexOnEnemy - (countOnEnemy - 1) * 0.5f) * formationSpacing;
            float along = Mathf.Min(blockDistanceFromShip, dist * 0.42f);
            Vector3 buzz = ComputeBuzzOffset(perp, lineDir, slotIndex, indexOnEnemy, buzzAmplitude, buzzSpeed, timeSeconds, buzzPhase);
            Vector3 world = shipPos + lineDir * along + perp * lateral + buzz;
            world.y = DroneSwarmLogic.FixedY;
            return world;
        }

        /// <summary>
        /// Tilt a flat shield plate so its rest normal points at the threat.
        /// </summary>
        public static Quaternion ComputeShieldFaceEnemyRotation(Vector3 droneWorldPos, Vector3 enemyWorldPos, Vector3 flatFaceRestNormal)
        {
            Vector3 toEnemy = ToroidalMap.WrapPosition(enemyWorldPos - droneWorldPos);
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude < 0.0001f)
                return Quaternion.identity;
            Vector3 rest = flatFaceRestNormal.sqrMagnitude > 0.0001f ? flatFaceRestNormal.normalized : Vector3.up;
            return Quaternion.FromToRotation(rest, toEnemy.normalized);
        }

        /// <summary>Idle shield: flat face points outward from the ship center.</summary>
        public static Quaternion ComputeShieldFaceOutwardRotation(Vector3 shipWorldPos, Vector3 droneWorldPos, Vector3 flatFaceRestNormal)
        {
            Vector3 outward = droneWorldPos - shipWorldPos;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.0001f)
                return Quaternion.identity;
            Vector3 rest = flatFaceRestNormal.sqrMagnitude > 0.0001f ? flatFaceRestNormal.normalized : Vector3.up;
            return Quaternion.FromToRotation(rest, outward.normalized);
        }

        public static List<Starship> CollectEnemyShipsInRange(Starship ownerShip, float range)
        {
            var result = new List<Starship>(8);
            if (ownerShip == null) return result;
            DroneTargetCache.RefreshIfNeeded();
            Vector3 ownerPos = ownerShip.transform.position;
            ownerPos.y = DroneSwarmLogic.FixedY;
            TeamManager.Team myTeam = ownerShip.ShipTeam;
            float rangeSq = range * range;
            foreach (var ship in DroneTargetCache.Ships)
            {
                if (ship == null || ship.IsDead || ship == ownerShip || ship.ShipTeam == myTeam) continue;
                if (ToroidalMap.WrapPosition(ship.transform.position - ownerPos).sqrMagnitude <= rangeSq)
                    result.Add(ship);
            }
            return result;
        }

        /// <summary>Assign each shield drone to an in-range enemy (round-robin). Fills per-slot assignment map keyed by equipment slot index.</summary>
        public static void BuildShieldAssignments(
            IReadOnlyList<EquippedEquipmentEntry> equipment,
            IReadOnlyList<int> shieldSlotIndices,
            Starship ownerShip,
            float engageRangeFromShip,
            Dictionary<int, ShieldAssignment> assignmentsOut)
        {
            assignmentsOut.Clear();
            if (shieldSlotIndices == null || shieldSlotIndices.Count == 0 || ownerShip == null) return;

            List<Starship> enemies = CollectEnemyShipsInRange(ownerShip, engageRangeFromShip);
            if (enemies.Count == 0) return;
            enemies.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));

            var countPerEnemy = new Dictionary<int, int>();
            for (int i = 0; i < shieldSlotIndices.Count; i++)
            {
                int slot = shieldSlotIndices[i];
                Starship enemy = enemies[i % enemies.Count];
                int enemyId = enemy.GetInstanceID();
                if (!countPerEnemy.ContainsKey(enemyId))
                    countPerEnemy[enemyId] = 0;
                int indexOnEnemy = countPerEnemy[enemyId];
                countPerEnemy[enemyId] = indexOnEnemy + 1;
                assignmentsOut[slot] = new ShieldAssignment
                {
                    enemyInstanceId = enemyId,
                    indexOnEnemy = indexOnEnemy,
                    countOnEnemy = 0
                };
            }

            foreach (var kv in assignmentsOut)
            {
                ShieldAssignment a = kv.Value;
                if (countPerEnemy.TryGetValue(a.enemyInstanceId, out int total))
                {
                    a.countOnEnemy = total;
                    assignmentsOut[kv.Key] = a;
                }
            }
        }

        public static Starship FindShipByInstanceId(int instanceId)
        {
            foreach (var ship in Starship.AllStarships)
            {
                if (ship != null && ship.GetInstanceID() == instanceId)
                    return ship;
            }
            return null;
        }

        /// <summary>Closest point on segment to a sphere center; returns true if within combined radius.</summary>
        public static bool SegmentIntersectsSphere(Vector3 segFrom, Vector3 segTo, Vector3 center, float radius, out Vector3 closestPoint)
        {
            center.y = DroneSwarmLogic.FixedY;
            segFrom.y = DroneSwarmLogic.FixedY;
            segTo.y = DroneSwarmLogic.FixedY;
            Vector3 ab = segTo - segFrom;
            float abLenSq = ab.sqrMagnitude;
            float t = abLenSq < 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(center - segFrom, ab) / abLenSq);
            closestPoint = segFrom + ab * t;
            closestPoint.y = DroneSwarmLogic.FixedY;
            return (closestPoint - center).sqrMagnitude <= radius * radius;
        }
    }
}
