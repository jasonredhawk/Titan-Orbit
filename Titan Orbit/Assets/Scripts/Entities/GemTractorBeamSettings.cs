using UnityEngine;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>Shared reach and pull strength for gem tractor beams (server physics + client Shapes visuals).</summary>
    public static class GemTractorBeamSettings
    {
        public const float SearchRadiusNormal = 6.5f;
        public const float SearchRadiusOrbit = 10f;
        public const float AttractionSpeedNormal = 10f;
        public const float AttractionSpeedOrbit = 16f;
        public const float AttractionAccelerationFactor = 4f;

        /// <summary>Min speed toward ship (m/s) before a gem counts as actively tractor-pulled.</summary>
        public const float ActivePullTowardSpeedThreshold = 0.22f;

        public static void GetAttractionParams(bool inOrbitZone, out float searchRadius, out float attractionSpeed)
        {
            searchRadius = inOrbitZone ? SearchRadiusOrbit : SearchRadiusNormal;
            attractionSpeed = inOrbitZone ? AttractionSpeedOrbit : AttractionSpeedNormal;
        }

        public static bool IsWithinReach(Vector3 gemPos, Vector3 shipPos, bool inOrbitZone)
        {
            GetAttractionParams(inOrbitZone, out float searchRadius, out _);
            return ToroidalMap.ToroidalDistance(gemPos, shipPos) <= searchRadius;
        }

        public static bool CanShipMagneticallyPull(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return false;
            if (!ship.IsSpawned || ship.IsDead)
                return false;
            if (ship.IsGemCollectionSuppressed || ship.GemMoonDocked)
                return false;
            if (ship.CurrentGems >= ship.GemCapacity)
                return false;
            if (!gem.IsSpawned || gem.IsInPool || gem.IsDepositGem || gem.Value <= 0f)
                return false;
            if (!gem.IsCollectibleByShip(ship))
                return false;

            Vector3 shipPos = GetShipPosition(ship);
            Vector3 gemPos = GetGemPosition(gem);
            return IsWithinReach(gemPos, shipPos, ship.IsInOrbit);
        }

        /// <summary>True when the ship is pulling and the gem is visibly moving toward it.</summary>
        public static bool IsActivelyBeingPulledToward(Starship ship, Gem gem)
        {
            if (!CanShipMagneticallyPull(ship, gem))
                return false;

            float towardSpeed = GemTractorBeamMotionTracker.GetTowardShipSpeed(ship, gem);

            if (gem.IsServer)
            {
                var gemRb = gem.GetComponent<Rigidbody>();
                if (gemRb != null && !gemRb.isKinematic)
                {
                    Vector3 shipPos = GetShipPosition(ship);
                    Vector3 gemPos = GetGemPosition(gem);
                    Vector3 toShip = ToroidalMap.ToroidalDirection(gemPos, shipPos);
                    toShip.y = 0f;
                    if (toShip.sqrMagnitude > 0.0001f)
                    {
                        Vector3 vel = gemRb.linearVelocity;
                        vel.y = 0f;
                        towardSpeed = Mathf.Max(towardSpeed, Vector3.Dot(vel, toShip.normalized));
                    }
                }
            }

            return towardSpeed >= ActivePullTowardSpeedThreshold;
        }

        public static bool ShouldShowTractorBeam(Starship ship, Gem gem) =>
            IsActivelyBeingPulledToward(ship, gem);

        public static bool IsPulledByAnyShip(Gem gem)
        {
            if (gem == null)
                return false;
            foreach (var ship in Starship.AllStarships)
            {
                if (ship != null && IsActivelyBeingPulledToward(ship, gem))
                    return true;
            }
            return false;
        }

        private static Vector3 GetShipPosition(Starship ship)
        {
            var shipRb = ship.GetComponent<Rigidbody>();
            Vector3 pos = shipRb != null ? shipRb.position : ship.transform.position;
            pos.y = 0f;
            return pos;
        }

        private static Vector3 GetGemPosition(Gem gem)
        {
            var gemRb = gem.GetComponent<Rigidbody>();
            Vector3 pos = gemRb != null ? gemRb.position : gem.transform.position;
            pos.y = 0f;
            return pos;
        }
    }
}
