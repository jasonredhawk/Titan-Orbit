using System.Collections.Generic;
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

        private static int pullSetCacheFrame = -1;
        private static readonly Dictionary<int, HashSet<int>> pullSetByShipInstanceId = new Dictionary<int, HashSet<int>>(32);
        private static readonly List<GemPullCandidate> pullCandidateScratch = new List<GemPullCandidate>(64);

        private struct GemPullCandidate
        {
            public Gem gem;
            public float dist;
            public bool inFlight;
        }

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

        /// <summary>
        /// True when this gem is in the ship's magnetic pull budget (only enough gems to fill remaining capacity are selected).
        /// </summary>
        public static bool CanShipMagneticallyPull(Starship ship, Gem gem)
        {
            if (!PassesBasicMagneticPullEligibility(ship, gem))
                return false;

            return GetMagneticPullSet(ship).Contains(gem.GetInstanceID());
        }

        /// <summary>True when the ship is pulling and the gem is visibly moving toward it.</summary>
        public static bool IsActivelyBeingPulledToward(Starship ship, Gem gem)
        {
            if (!CanShipMagneticallyPull(ship, gem))
                return false;

            return GetTowardShipSpeed(ship, gem) >= ActivePullTowardSpeedThreshold;
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

        private static bool IsShipEligibleForMagneticPull(Starship ship)
        {
            if (ship == null || !ship.IsSpawned || ship.IsDead)
                return false;
            if (ship.IsGemCollectionSuppressed || ship.GemMoonDocked)
                return false;
            if (ship.CurrentGems >= ship.GemCapacity)
                return false;
            return true;
        }

        private static bool PassesBasicMagneticPullEligibility(Starship ship, Gem gem)
        {
            if (!IsShipEligibleForMagneticPull(ship))
                return false;
            if (gem == null || !gem.IsSpawned || gem.IsInPool || gem.IsDepositGem || gem.Value <= 0f)
                return false;
            if (!gem.IsCollectibleByShip(ship))
                return false;

            Vector3 shipPos = GetShipPosition(ship);
            Vector3 gemPos = GetGemPosition(gem);
            return IsWithinReach(gemPos, shipPos, ship.IsInOrbit);
        }

        private static HashSet<int> GetMagneticPullSet(Starship ship)
        {
            if (pullSetCacheFrame != Time.frameCount)
            {
                pullSetCacheFrame = Time.frameCount;
                pullSetByShipInstanceId.Clear();
            }

            int shipId = ship.GetInstanceID();
            if (pullSetByShipInstanceId.TryGetValue(shipId, out HashSet<int> cached))
                return cached;

            HashSet<int> built = BuildMagneticPullSet(ship);
            pullSetByShipInstanceId[shipId] = built;
            return built;
        }

        /// <summary>
        /// Picks the minimum nearby gems (by value) needed to fill remaining capacity: in-flight pulls first, then closest idle gems.
        /// </summary>
        private static HashSet<int> BuildMagneticPullSet(Starship ship)
        {
            var set = new HashSet<int>();
            if (!IsShipEligibleForMagneticPull(ship))
                return set;

            float capacityLeft = Mathf.Max(0f, ship.GemCapacity - ship.CurrentGems);
            if (capacityLeft <= 0f)
                return set;

            Vector3 shipPos = GetShipPosition(ship);
            bool inOrbit = ship.IsInOrbit;
            GetAttractionParams(inOrbit, out float searchRadius, out _);

            var gems = Gem.AllGems;
            if (gems == null || gems.Count == 0)
                return set;

            pullCandidateScratch.Clear();
            for (int i = 0; i < gems.Count; i++)
            {
                Gem gem = gems[i];
                if (!PassesBasicMagneticPullEligibility(ship, gem))
                    continue;

                Vector3 gemPos = GetGemPosition(gem);
                float dist = ToroidalMap.ToroidalDistance(gemPos, shipPos);
                if (dist > searchRadius)
                    continue;

                bool inFlight = GetTowardShipSpeed(ship, gem) >= ActivePullTowardSpeedThreshold;
                pullCandidateScratch.Add(new GemPullCandidate { gem = gem, dist = dist, inFlight = inFlight });
            }

            if (pullCandidateScratch.Count == 0)
                return set;

            float reserved = 0f;

            // Keep gems already moving toward the ship (closest first) up to the remaining capacity budget.
            pullCandidateScratch.Sort((a, b) =>
            {
                if (a.inFlight != b.inFlight)
                    return a.inFlight ? -1 : 1;
                return a.dist.CompareTo(b.dist);
            });

            for (int i = 0; i < pullCandidateScratch.Count; i++)
            {
                if (!pullCandidateScratch[i].inFlight)
                    break;
                if (reserved >= capacityLeft)
                    break;

                Gem gem = pullCandidateScratch[i].gem;
                set.Add(gem.GetInstanceID());
                reserved += gem.Value;
            }

            // Pull additional closest gems until the budget is met.
            pullCandidateScratch.Sort((a, b) => a.dist.CompareTo(b.dist));
            for (int i = 0; i < pullCandidateScratch.Count; i++)
            {
                if (reserved >= capacityLeft)
                    break;

                Gem gem = pullCandidateScratch[i].gem;
                int gemId = gem.GetInstanceID();
                if (set.Contains(gemId))
                    continue;

                set.Add(gemId);
                reserved += gem.Value;
            }

            return set;
        }

        private static float GetTowardShipSpeed(Starship ship, Gem gem)
        {
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

            return towardSpeed;
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
