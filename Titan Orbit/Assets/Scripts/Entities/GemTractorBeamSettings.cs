using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>Shared reach and pull strength for gem tractor beams (server physics + client Shapes visuals).</summary>
    public static class GemTractorBeamSettings
    {
        /// <summary>Legacy reference: wing v1 maxGems (8) maps to this reach in normal space.</summary>
        public const float SearchRadiusNormal = 3f;
        public const float SearchRadiusOrbit = 4.5f;
        /// <summary>Legacy reference: wing v1 maxGems (8) maps to this pull speed in normal space.</summary>
        public const float AttractionSpeedNormal = 10f;
        public const float AttractionSpeedOrbit = 16f;
        /// <summary>Scales authored tractor power into slower in-game pull speeds.</summary>
        public const float GameplayPullSpeedScale = 0.38f;

        public const float MinGameplayPullSpeed = 0.75f;
        public const float MaxGameplayPullSpeed = 5.5f;

        /// <summary>MaxGems → search radius (m). Wing1 with maxGems=8 → 3m in normal space.</summary>
        public const float MaxGemsToSearchRadius = SearchRadiusNormal / 8f;
        /// <summary>MaxGems → pull speed (m/s). Wing1 with maxGems=8 → 10 m/s in normal space.</summary>
        public const float MaxGemsToAttractionSpeed = AttractionSpeedNormal / 8f;

        /// <summary>Min speed toward ship (m/s) before a gem counts as actively tractor-pulled.</summary>
        public const float ActivePullTowardSpeedThreshold = 0.22f;

        private static int pullSetCacheFrame = -1;
        private static float pullSetCachePhysicsFixedTime = -1f;
        private static readonly Dictionary<int, MagneticPullState> pullStateByShipInstanceId = new Dictionary<int, MagneticPullState>(32);
        private static readonly List<GemPullCandidate> pullCandidateScratch = new List<GemPullCandidate>(64);

        private sealed class MagneticPullState
        {
            public readonly HashSet<int> gemIds = new HashSet<int>();
            public readonly Dictionary<int, int> gemIdToWingIndex = new Dictionary<int, int>();
        }

        private struct GemPullCandidate
        {
            public Gem gem;
            public int wingIndex;
            public float dist;
            public bool inFlight;
        }

        /// <summary>Converts wing Max Gems Capacity (at current ship level) into tractor reach and pull strength.</summary>
        public static void GetTractorBeamFromMaxGems(float effectiveMaxGems, bool inOrbitZone, out float searchRadius, out float attractionSpeed)
        {
            float gems = Mathf.Max(0f, effectiveMaxGems);
            searchRadius = gems * MaxGemsToSearchRadius;
            attractionSpeed = gems * MaxGemsToAttractionSpeed;

            ApplyOrbitTractorMultipliers(inOrbitZone, ref searchRadius, ref attractionSpeed);
            searchRadius = Mathf.Max(0.5f, searchRadius);
            attractionSpeed = ScaleToGameplayPullSpeed(attractionSpeed);
        }

        public static float ScaleToGameplayPullSpeed(float authoredPullSpeed)
        {
            float speed = Mathf.Max(0f, authoredPullSpeed) * GameplayPullSpeedScale;
            return Mathf.Clamp(speed, MinGameplayPullSpeed, MaxGameplayPullSpeed);
        }

        public static void ApplyOrbitTractorMultipliers(bool inOrbitZone, ref float searchRadius, ref float attractionSpeed)
        {
            if (!inOrbitZone)
                return;
            searchRadius *= SearchRadiusOrbit / SearchRadiusNormal;
            attractionSpeed *= AttractionSpeedOrbit / AttractionSpeedNormal;
        }

        /// <summary>Resolves authored tractor stats, falling back to maxGems conversion when distance/power are unset.</summary>
        public static void GetTractorBeamFromStats(
            float tractorBeamDistance,
            float tractorBeamDistancePerLevel,
            float tractorBeamPower,
            float tractorBeamPowerPerLevel,
            float maxGems,
            float maxGemsPerLevel,
            int shipLevel,
            bool inOrbitZone,
            out float searchRadius,
            out float attractionSpeed)
        {
            int perLvl = Mathf.Max(0, shipLevel - 1);
            searchRadius = tractorBeamDistance + tractorBeamDistancePerLevel * perLvl;
            attractionSpeed = tractorBeamPower + tractorBeamPowerPerLevel * perLvl;

            if (searchRadius <= 0f && attractionSpeed <= 0f)
            {
                float effectiveMaxGems = Mathf.Max(0f, maxGems + maxGemsPerLevel * perLvl);
                GetTractorBeamFromMaxGems(effectiveMaxGems, inOrbitZone, out searchRadius, out attractionSpeed);
                return;
            }

            ApplyOrbitTractorMultipliers(inOrbitZone, ref searchRadius, ref attractionSpeed);
            searchRadius = Mathf.Max(0.5f, searchRadius);
            attractionSpeed = ScaleToGameplayPullSpeed(attractionSpeed);
        }

        /// <summary>Constant linear pull speed (m/s) after the deploy animation completes.</summary>
        public static float GetGameplayPullSpeed(Starship ship, Gem gem) =>
            GetAttractionSpeedForGem(ship, gem);

        public static bool ShouldApplyGemPullPhysics(Starship ship, Gem gem)
        {
            if (!CanShipMagneticallyPull(ship, gem))
                return false;
            if (!IsWithinMagneticPullRange(ship, gem))
                return false;
            return GemTractorBeamDeployTracker.IsPullPhysicsActive(ship, gem);
        }

        public static void GetAttractionParams(bool inOrbitZone, out float searchRadius, out float attractionSpeed)
        {
            GetTractorBeamFromMaxGems(8f, inOrbitZone, out searchRadius, out attractionSpeed);
        }

        public static bool IsWithinReach(Vector3 gemPos, Vector3 beamOrigin, bool inOrbitZone, float searchRadius)
        {
            return ToroidalMap.ToroidalDistance(gemPos, beamOrigin) <= searchRadius;
        }

        public static bool IsWithinMagneticPullRange(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return false;

            if (!CanShipMagneticallyPull(ship, gem))
                return false;

            var wings = ship.WingTractorBeams;
            if (wings == null || wings.Count == 0)
            {
                GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out float searchRadius, out _);
                return IsWithinReach(GetGemPosition(gem), GetShipPosition(ship), ship.IsInOrbit, searchRadius);
            }

            int wingIndex = GetAssignedWingIndex(ship, gem);
            if (wingIndex < 0)
                return false;

            return IsGemWithinAssignedWingRange(ship, gem, wingIndex);
        }

        /// <summary>Server: rebuild pull-set budgets every physics step so range cut-off tracks ship movement.</summary>
        public static void BeginPhysicsPullUpdate()
        {
            if (pullSetCachePhysicsFixedTime == Time.fixedTime)
                return;
            pullSetCachePhysicsFixedTime = Time.fixedTime;
            pullStateByShipInstanceId.Clear();
            pullSetCacheFrame = -1;
        }

        /// <summary>
        /// True when this gem is assigned to one of the ship's wing tractor beams (one gem per wing max).
        /// </summary>
        public static bool CanShipMagneticallyPull(Starship ship, Gem gem)
        {
            if (!PassesBasicMagneticPullEligibility(ship, gem))
                return false;

            return GetMagneticPullState(ship).gemIds.Contains(gem.GetInstanceID());
        }

        /// <summary>Pull speed for this gem from its assigned wing's Max Gems stats.</summary>
        public static float GetAttractionSpeedForGem(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return AttractionSpeedNormal;

            int wingIndex = GetAssignedWingIndex(ship, gem);
            var wings = ship.WingTractorBeams;
            if (wingIndex >= 0 && wings != null && wingIndex < wings.Count)
            {
                wings[wingIndex].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out _, out float speed);
                return speed;
            }

            GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out _, out float fallback);
            return fallback;
        }

        /// <summary>World origin for the beam line (wing transform when assigned).</summary>
        public static Vector3 GetBeamOrigin(Starship ship, Gem gem)
        {
            if (ship == null)
                return Vector3.zero;

            int wingIndex = GetAssignedWingIndex(ship, gem);
            var wings = ship.WingTractorBeams;
            if (wingIndex >= 0 && wings != null && wingIndex < wings.Count && wings[wingIndex].wingTransform != null)
                return wings[wingIndex].GetWorldPosition();

            return GetShipPosition(ship);
        }

        public static int GetAssignedWingIndex(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return -1;

            if (GetMagneticPullState(ship).gemIdToWingIndex.TryGetValue(gem.GetInstanceID(), out int wingIndex))
                return wingIndex;
            return -1;
        }

        /// <summary>True when deploy finished and the gem is moving toward the ship at pull speed.</summary>
        public static bool IsActivelyBeingPulledToward(Starship ship, Gem gem)
        {
            if (!ShouldApplyGemPullPhysics(ship, gem))
                return false;

            return GetTowardShipSpeed(ship, gem) >= ActivePullTowardSpeedThreshold * 0.5f;
        }

        public static bool ShouldShowTractorBeam(Starship ship, Gem gem) =>
            IsActivelyBeingPulledToward(ship, gem);

        /// <summary>
        /// Looser visual-only gate: show while the gem is in the ship's pull budget and range,
        /// without requiring a noisy per-frame speed threshold (avoids beam pop/flicker).
        /// </summary>
        public static bool IsEligibleForBeamVisual(Starship ship, Gem gem)
        {
            if (!CanShipMagneticallyPull(ship, gem))
                return false;
            return IsWithinMagneticPullRange(ship, gem);
        }

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
            return gem.IsCollectibleByShip(ship);
        }

        private static MagneticPullState GetMagneticPullState(Starship ship)
        {
            if (pullSetCacheFrame != Time.frameCount)
            {
                pullSetCacheFrame = Time.frameCount;
                pullStateByShipInstanceId.Clear();
            }

            int shipId = ship.GetInstanceID();
            if (pullStateByShipInstanceId.TryGetValue(shipId, out MagneticPullState cached))
                return cached;

            MagneticPullState built = BuildMagneticPullState(ship);
            pullStateByShipInstanceId[shipId] = built;
            return built;
        }

        private static bool IsGemWithinAssignedWingRange(Starship ship, Gem gem, int wingIndex)
        {
            var wings = ship.WingTractorBeams;
            if (wings == null || wingIndex < 0 || wingIndex >= wings.Count)
                return false;

            wings[wingIndex].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
            Vector3 gemPos = GetGemPosition(gem);
            Vector3 wingPos = wings[wingIndex].GetWorldPosition();
            return IsWithinReach(gemPos, wingPos, ship.IsInOrbit, searchRadius);
        }

        /// <summary>
        /// Assigns at most one gem per wing tractor beam. Wing count limits simultaneous pulls.
        /// </summary>
        private static MagneticPullState BuildMagneticPullState(Starship ship)
        {
            var state = new MagneticPullState();
            if (!IsShipEligibleForMagneticPull(ship))
                return state;

            var wings = ship.WingTractorBeams;
            int wingCount = wings != null ? wings.Count : 0;
            if (wingCount <= 0)
            {
                BuildFallbackSingleBeamPullSet(ship, state);
                return state;
            }

            var gems = Gem.AllGems;
            if (gems == null || gems.Count == 0)
                return state;

            pullCandidateScratch.Clear();
            for (int wi = 0; wi < wingCount; wi++)
            {
                if (wings[wi].wingTransform == null)
                    continue;

                wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
                Vector3 wingPos = wings[wi].GetWorldPosition();

                for (int gi = 0; gi < gems.Count; gi++)
                {
                    Gem gem = gems[gi];
                    if (!PassesBasicMagneticPullEligibility(ship, gem))
                        continue;

                    Vector3 gemPos = GetGemPosition(gem);
                    float dist = ToroidalMap.ToroidalDistance(gemPos, wingPos);
                    if (dist > searchRadius)
                        continue;

                    bool inFlight = GemTractorBeamDeployTracker.IsPullPhysicsActive(ship, gem) &&
                                    GetTowardShipSpeed(ship, gem) >= ActivePullTowardSpeedThreshold * 0.5f;
                    pullCandidateScratch.Add(new GemPullCandidate
                    {
                        gem = gem,
                        wingIndex = wi,
                        dist = dist,
                        inFlight = inFlight
                    });
                }
            }

            if (pullCandidateScratch.Count == 0)
                return state;

            var assignedGemIds = new HashSet<int>();
            var wingHasGem = new bool[wingCount];

            // Keep in-flight gems on their wing (closest first per wing).
            pullCandidateScratch.Sort((a, b) =>
            {
                if (a.inFlight != b.inFlight)
                    return a.inFlight ? -1 : 1;
                if (a.wingIndex != b.wingIndex)
                    return a.wingIndex.CompareTo(b.wingIndex);
                return a.dist.CompareTo(b.dist);
            });

            for (int i = 0; i < pullCandidateScratch.Count; i++)
            {
                if (!pullCandidateScratch[i].inFlight)
                    break;

                GemPullCandidate c = pullCandidateScratch[i];
                int gemId = c.gem.GetInstanceID();
                if (assignedGemIds.Contains(gemId) || wingHasGem[c.wingIndex])
                    continue;

                assignedGemIds.Add(gemId);
                wingHasGem[c.wingIndex] = true;
                state.gemIds.Add(gemId);
                state.gemIdToWingIndex[gemId] = c.wingIndex;
            }

            pullCandidateScratch.Sort((a, b) =>
            {
                if (a.wingIndex != b.wingIndex)
                    return a.wingIndex.CompareTo(b.wingIndex);
                return a.dist.CompareTo(b.dist);
            });

            for (int i = 0; i < pullCandidateScratch.Count; i++)
            {
                GemPullCandidate c = pullCandidateScratch[i];
                if (wingHasGem[c.wingIndex])
                    continue;

                int gemId = c.gem.GetInstanceID();
                if (assignedGemIds.Contains(gemId))
                    continue;

                assignedGemIds.Add(gemId);
                wingHasGem[c.wingIndex] = true;
                state.gemIds.Add(gemId);
                state.gemIdToWingIndex[gemId] = c.wingIndex;
            }

            return state;
        }

        /// <summary>Ships without wing components: one beam at ship center using default wing-tier stats.</summary>
        private static void BuildFallbackSingleBeamPullSet(Starship ship, MagneticPullState state)
        {
            GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out float searchRadius, out _);
            Vector3 origin = GetShipPosition(ship);

            var gems = Gem.AllGems;
            if (gems == null || gems.Count == 0)
                return;

            pullCandidateScratch.Clear();
            for (int i = 0; i < gems.Count; i++)
            {
                Gem gem = gems[i];
                if (!PassesBasicMagneticPullEligibility(ship, gem))
                    continue;

                Vector3 gemPos = GetGemPosition(gem);
                float dist = ToroidalMap.ToroidalDistance(gemPos, origin);
                if (dist > searchRadius)
                    continue;

                bool inFlight = GemTractorBeamDeployTracker.IsPullPhysicsActive(ship, gem) &&
                                GetTowardShipSpeed(ship, gem) >= ActivePullTowardSpeedThreshold * 0.5f;
                pullCandidateScratch.Add(new GemPullCandidate { gem = gem, wingIndex = 0, dist = dist, inFlight = inFlight });
            }

            if (pullCandidateScratch.Count == 0)
                return;

            pullCandidateScratch.Sort((a, b) =>
            {
                if (a.inFlight != b.inFlight)
                    return a.inFlight ? -1 : 1;
                return a.dist.CompareTo(b.dist);
            });

            Gem chosen = pullCandidateScratch[0].gem;
            int gemId = chosen.GetInstanceID();
            state.gemIds.Add(gemId);
            state.gemIdToWingIndex[gemId] = 0;
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
