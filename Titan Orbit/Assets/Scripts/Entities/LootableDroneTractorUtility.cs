using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>Tractor pull for <see cref="LootableDrone"/> using the same wing reach/speed stats as gems.</summary>
    public static class LootableDroneTractorUtility
    {
        public static bool IsShipEligibleForDronePull(Starship ship)
        {
            if (ship == null || !ship.IsSpawned || ship.IsDead)
                return false;
            if (ship.GemMoonDocked)
                return false;
            if (!ship.HasEmptyEquipmentSlot)
                return false;
            return true;
        }

        public static bool IsWithinPullRange(Starship ship, LootableDrone drone)
        {
            if (ship == null || drone == null)
                return false;
            return IsWithinPullRange(ship, GetDroneWorldPosition(drone));
        }

        public static bool IsWithinPullRange(Starship ship, Vector3 droneWorldPos)
        {
            if (!IsShipEligibleForDronePull(ship))
                return false;

            var wings = ship.WingTractorBeams;
            if (wings == null || wings.Count == 0)
            {
                GemTractorBeamSettings.GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out float searchRadius, out _);
                return GemTractorBeamSettings.IsWithinReach(droneWorldPos, GetShipWorldPosition(ship), ship.IsInOrbit, searchRadius);
            }

            for (int wi = 0; wi < wings.Count; wi++)
            {
                if (wings[wi].wingTransform == null) continue;
                wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
                if (GemTractorBeamSettings.IsWithinReach(droneWorldPos, wings[wi].GetWorldPosition(), ship.IsInOrbit, searchRadius))
                    return true;
            }

            return false;
        }

        public static bool TryGetPullTowardDirection(Starship ship, LootableDrone drone, out Vector3 direction)
        {
            direction = Vector3.zero;
            if (ship == null || drone == null) return false;

            Vector3 dronePos = GetDroneWorldPosition(drone);
            var wings = ship.WingTractorBeams;
            if (wings != null && wings.Count > 0)
            {
                int bestWing = -1;
                float bestDist = float.MaxValue;
                for (int wi = 0; wi < wings.Count; wi++)
                {
                    if (wings[wi].wingTransform == null) continue;
                    wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
                    if (!GemTractorBeamSettings.IsWithinReach(dronePos, wings[wi].GetWorldPosition(), ship.IsInOrbit, searchRadius))
                        continue;
                    float dist = ToroidalMap.ToroidalDistance(dronePos, wings[wi].GetWorldPosition());
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestWing = wi;
                    }
                }

                if (bestWing >= 0)
                {
                    direction = ToroidalMap.ToroidalDirection(dronePos, wings[bestWing].GetWorldPosition());
                    direction.y = 0f;
                    if (direction.sqrMagnitude > 0.0001f)
                    {
                        direction.Normalize();
                        return true;
                    }
                }
            }

            direction = ToroidalMap.ToroidalDirection(dronePos, GetShipWorldPosition(ship));
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return false;
            direction.Normalize();
            return true;
        }

        public static float GetPullSpeed(Starship ship, LootableDrone drone)
        {
            if (ship == null || drone == null) return 0f;

            Vector3 dronePos = GetDroneWorldPosition(drone);
            var wings = ship.WingTractorBeams;
            if (wings != null && wings.Count > 0)
            {
                float bestSpeed = 0f;
                for (int wi = 0; wi < wings.Count; wi++)
                {
                    if (wings[wi].wingTransform == null) continue;
                    wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out float wingSpeed);
                    if (!GemTractorBeamSettings.IsWithinReach(dronePos, wings[wi].GetWorldPosition(), ship.IsInOrbit, searchRadius))
                        continue;
                    if (wingSpeed > bestSpeed)
                        bestSpeed = wingSpeed;
                }
                if (bestSpeed > 0f)
                    return bestSpeed;
            }

            GemTractorBeamSettings.GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out _, out float fallbackSpeed);
            return fallbackSpeed;
        }

        public static bool ShouldApplyPullPhysics(Starship ship, LootableDrone drone)
        {
            if (!IsShipEligibleForDronePull(ship) || drone == null || !drone.IsSpawned || drone.IsDestroyed)
                return false;
            return IsWithinPullRange(ship, drone);
        }

        public static bool IsPulledByAnyShip(LootableDrone drone)
        {
            if (drone == null) return false;
            foreach (var ship in Starship.AllStarships)
            {
                if (ship != null && ShouldApplyPullPhysics(ship, drone))
                    return true;
            }
            return false;
        }

        private static Vector3 GetDroneWorldPosition(LootableDrone drone)
        {
            var droneRb = drone.GetComponent<Rigidbody>();
            Vector3 pos = droneRb != null ? droneRb.position : drone.transform.position;
            pos.y = 0f;
            return pos;
        }

        private static Vector3 GetShipWorldPosition(Starship ship)
        {
            var shipRb = ship.GetComponent<Rigidbody>();
            Vector3 pos = shipRb != null ? shipRb.position : ship.transform.position;
            pos.y = 0f;
            return pos;
        }
    }
}
