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
        /// <summary>Global reach multiplier applied to component stats (0.35 = 65% shorter reach).</summary>
        public const float SearchRadiusScale = 0.35f;
        private const float MinSearchRadius = 0.5f * SearchRadiusScale;
        /// <summary>Legacy reference: wing v1 maxGems (8) maps to this pull speed in normal space.</summary>
        public const float AttractionSpeedNormal = 10f;
        public const float AttractionSpeedOrbit = 16f;
        /// <summary>Scales authored tractor power into slower in-game pull speeds.</summary>
        public const float GameplayPullSpeedScale = 0.38f;

        public const float MinGameplayPullSpeed = 0.75f;
        public const float MaxGameplayPullSpeed = 5.5f;

        /// <summary>Pull speed multiplier at min gem value (small gems pull in faster).</summary>
        public const float PullSpeedMultiplierAtMinGemValue = 1.35f;
        /// <summary>Pull speed multiplier at max gem value (large gems pull in slower).</summary>
        public const float PullSpeedMultiplierAtMaxGemValue = 0.05f;

        /// <summary>Cap on combined pull speed when multiple wings target the same gem.</summary>
        public const float MaxStackedGameplayPullSpeed = 18f;

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
        private static readonly List<Gem> reachableGemScratch = new List<Gem>(32);
        /// <summary>Guards reentrant GetMagneticPullState while BuildMagneticPullState is running.</summary>
        private static readonly HashSet<int> buildingPullStateForShipIds = new HashSet<int>();
        /// <summary>Per-ship wing → gem locks until collected or out of wing reach.</summary>
        private static readonly Dictionary<int, Dictionary<int, int>> stickyGemIdByShipAndWing =
            new Dictionary<int, Dictionary<int, int>>(32);
        private static readonly List<int> stickyWingRemovalScratch = new List<int>(8);

        private sealed class MagneticPullState
        {
            public readonly HashSet<int> gemIds = new HashSet<int>();
            public readonly Dictionary<int, List<int>> gemIdToWingIndices = new Dictionary<int, List<int>>();
            public readonly Dictionary<int, int> wingIndexToGemId = new Dictionary<int, int>();
        }

        private struct GemPullCandidate
        {
            public Gem gem;
            public int wingIndex;
            public float dist;
        }

        /// <summary>Converts wing Max Gems Capacity (at current ship level) into tractor reach and pull strength.</summary>
        public static void GetTractorBeamFromMaxGems(float effectiveMaxGems, bool inOrbitZone, out float searchRadius, out float attractionSpeed)
        {
            float gems = Mathf.Max(0f, effectiveMaxGems);
            searchRadius = gems * MaxGemsToSearchRadius;
            attractionSpeed = gems * MaxGemsToAttractionSpeed;

            ApplyOrbitTractorMultipliers(inOrbitZone, ref searchRadius, ref attractionSpeed);
            searchRadius = FinalizeSearchRadius(searchRadius);
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
            searchRadius = FinalizeSearchRadius(searchRadius);
            attractionSpeed = ScaleToGameplayPullSpeed(attractionSpeed);
        }

        private static float FinalizeSearchRadius(float searchRadius) =>
            Mathf.Max(MinSearchRadius, searchRadius * SearchRadiusScale);

        /// <summary>Constant linear pull speed (m/s) after the deploy animation completes.</summary>
        public static float GetGameplayPullSpeed(Starship ship, Gem gem) =>
            GetAttractionSpeedForGem(ship, gem);

        public static bool ShouldApplyGemPullPhysics(Starship ship, Gem gem)
        {
            if (!PassesBasicMagneticPullEligibility(ship, gem))
                return false;
            if (!HasTractorInvolvement(ship, gem))
                return false;
            if (!IsWithinMagneticPullRange(ship, gem))
                return false;
            return GemTractorBeamDeployTracker.IsPullPhysicsActive(ship, gem);
        }

        /// <summary>Beam lock, wing assignment, or active deploy toward this gem.</summary>
        public static bool HasTractorInvolvement(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return false;

            if (GetMagneticPullState(ship).gemIds.Contains(gem.GetInstanceID()))
                return true;

            return GemTractorBeamDeployTracker.HasActiveLock(ship, gem);
        }

        public static void GetAttractionParams(bool inOrbitZone, out float searchRadius, out float attractionSpeed)
        {
            GetTractorBeamFromMaxGems(8f, inOrbitZone, out searchRadius, out attractionSpeed);
        }

        public static bool IsWithinReach(Vector3 gemPos, Vector3 beamOrigin, bool inOrbitZone, float searchRadius)
        {
            return ToroidalMap.ToroidalDistance(gemPos, beamOrigin) <= searchRadius;
        }

        /// <summary>Range check for deploy seeding / EnsureDeployState — does not require pull-set assignment.</summary>
        public static bool IsWithinCandidateMagneticPullRange(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return false;

            var wings = ship.WingTractorBeams;
            Vector3 gemPos = GetGemPosition(gem);
            if (wings == null || wings.Count == 0)
            {
                GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out float searchRadius, out _);
                return IsWithinReach(gemPos, GetShipPosition(ship), ship.IsInOrbit, searchRadius);
            }

            for (int wi = 0; wi < wings.Count; wi++)
            {
                if (wings[wi].wingTransform == null)
                    continue;
                wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
                Vector3 wingPos = wings[wi].GetWorldPosition();
                if (IsWithinReach(gemPos, wingPos, ship.IsInOrbit, searchRadius))
                    return true;
            }

            return false;
        }

        /// <summary>True when the gem is still within tractor reach of its locking or assigned wing (never ship-center for winged ships).</summary>
        public static bool IsWithinMagneticPullRange(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return false;

            if (!PassesBasicMagneticPullEligibility(ship, gem))
                return false;

            var wings = ship.WingTractorBeams;
            if (wings == null || wings.Count == 0)
            {
                GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out float searchRadius, out _);
                return IsWithinReach(GetGemPosition(gem), GetShipPosition(ship), ship.IsInOrbit, searchRadius);
            }

            int rangeWing = GetTractorRangeWingIndex(ship, gem);
            if (rangeWing >= 0)
                return IsGemWithinWingTractorRange(ship, gem, rangeWing);

            return false;
        }

        /// <summary>Server: rebuild pull-set budgets every physics step so range cut-off tracks ship movement.</summary>
        public static void BeginPhysicsPullUpdate()
        {
            GemTractorBeamDeployTracker.LateUpdateTick();

            if (pullSetCachePhysicsFixedTime == Time.fixedTime)
                return;
            pullSetCachePhysicsFixedTime = Time.fixedTime;
            pullStateByShipInstanceId.Clear();
            pullSetCacheFrame = -1;
            PruneStaleStickyWingLocks();
        }

        /// <summary>
        /// True when this gem is assigned to a wing tractor and/or has an active beam lock on a wing.
        /// </summary>
        public static bool CanShipMagneticallyPull(Starship ship, Gem gem)
        {
            if (!PassesBasicMagneticPullEligibility(ship, gem))
                return false;

            if (GetMagneticPullState(ship).gemIds.Contains(gem.GetInstanceID()))
                return true;

            if (!GemTractorBeamDeployTracker.HasActiveLock(ship, gem))
                return false;

            int lockWing = GetTractorRangeWingIndex(ship, gem);
            return lockWing >= 0 && IsGemWithinWingTractorRange(ship, gem, lockWing);
        }

        /// <summary>Pull speed for this gem from all assigned wings, scaled by gem size (small = faster).</summary>
        public static float GetAttractionSpeedForGem(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return AttractionSpeedNormal;

            var wings = ship.WingTractorBeams;
            var assignedWings = GetAssignedWingIndices(ship, gem);
            if (assignedWings.Count == 0)
            {
                GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out _, out float fallbackSpeed);
                return ApplyGemSizePullSpeedScale(fallbackSpeed, gem);
            }

            float combinedSpeed = 0f;
            for (int i = 0; i < assignedWings.Count; i++)
            {
                int wingIndex = assignedWings[i];
                if (wings != null && wingIndex >= 0 && wingIndex < wings.Count)
                {
                    wings[wingIndex].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out _, out float wingSpeed);
                    combinedSpeed += wingSpeed;
                }
                else
                {
                    GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out _, out float fallbackSpeed);
                    combinedSpeed += fallbackSpeed;
                }
            }

            return ApplyGemSizePullSpeedScale(
                Mathf.Min(combinedSpeed, MaxStackedGameplayPullSpeed),
                gem);
        }

        /// <summary>Scales wing pull speed by gem value (visual size): smaller gems accelerate faster, larger gems slower.</summary>
        public static float ApplyGemSizePullSpeedScale(float basePullSpeed, Gem gem)
        {
            if (gem == null || basePullSpeed <= 0f)
                return basePullSpeed;

            float sizeT = gem.GetValueSizeT();
            float sizeMul = Mathf.Lerp(PullSpeedMultiplierAtMinGemValue, PullSpeedMultiplierAtMaxGemValue, sizeT);
            float minScaled = MinGameplayPullSpeed * PullSpeedMultiplierAtMaxGemValue;
            return Mathf.Clamp(basePullSpeed * sizeMul, minScaled, MaxGameplayPullSpeed);
        }

        /// <summary>World origin for the beam line (locking wing, assigned wing, or closest in-range wing).</summary>
        public static Vector3 GetBeamOrigin(Starship ship, Gem gem) => GetPullTargetPosition(ship, gem);

        /// <summary>World position gems should move toward for this ship.</summary>
        public static Vector3 GetPullTargetPosition(Starship ship, Gem gem)
        {
            if (ship == null)
                return Vector3.zero;

            return GetWingBeamOrigin(ship, GetPullWingIndex(ship, gem));
        }

        /// <summary>Wing used for reach checks: deploy lock, then assigned, then closest in-range.</summary>
        public static int GetTractorRangeWingIndex(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return -1;

            if (GemTractorBeamDeployTracker.TryGetLockingWingIndex(ship, gem, out int lockWing) && lockWing >= 0)
                return lockWing;

            int assignedClosest = GetClosestAssignedWingIndex(ship, gem);
            if (assignedClosest >= 0)
                return assignedClosest;

            return GetClosestInRangeWingIndex(ship, gem);
        }

        /// <summary>Wing index gems should move toward: deploy lock, assigned wing, else closest in-range wing.</summary>
        public static int GetPullWingIndex(Starship ship, Gem gem) => GetTractorRangeWingIndex(ship, gem);

        /// <summary>Normalized XZ direction from the gem toward the closest relevant wing (never blended toward ship center).</summary>
        public static bool TryGetPullTowardDirection(Starship ship, Gem gem, out Vector3 direction)
        {
            direction = Vector3.zero;
            if (ship == null || gem == null)
                return false;

            Vector3 gemPos = GetGemPosition(gem);
            int wingIndex = GetPullWingIndex(ship, gem);
            if (wingIndex >= 0)
            {
                Vector3 toWing = ToroidalMap.ToroidalDirection(gemPos, GetWingBeamOrigin(ship, wingIndex));
                toWing.y = 0f;
                if (toWing.sqrMagnitude > 0.0001f)
                {
                    direction = toWing.normalized;
                    return true;
                }
            }

            var wings = ship.WingTractorBeams;
            if (wings != null && wings.Count > 0)
                return false;

            Vector3 toShip = ToroidalMap.ToroidalDirection(gemPos, GetShipPosition(ship));
            toShip.y = 0f;
            if (toShip.sqrMagnitude < 0.0001f)
                return false;

            direction = toShip.normalized;
            return true;
        }

        public static Vector3 GetWingBeamOrigin(Starship ship, int wingIndex)
        {
            if (ship == null)
                return Vector3.zero;

            var wings = ship.WingTractorBeams;
            if (wingIndex >= 0 && wings != null && wingIndex < wings.Count && wings[wingIndex].wingTransform != null)
                return wings[wingIndex].GetWorldPosition();

            return GetShipPosition(ship);
        }

        /// <summary>First assigned wing (legacy / deploy timing). Prefer <see cref="GetAssignedWingIndices"/>.</summary>
        public static int GetAssignedWingIndex(Starship ship, Gem gem) => GetPrimaryAssignedWingIndex(ship, gem);

        public static int GetPrimaryAssignedWingIndex(Starship ship, Gem gem) => GetClosestAssignedWingIndex(ship, gem);

        public static int GetClosestAssignedWingIndex(Starship ship, Gem gem)
        {
            var assigned = GetAssignedWingIndices(ship, gem);
            if (assigned.Count == 0)
                return -1;

            var wings = ship.WingTractorBeams;
            if (wings == null || wings.Count == 0)
                return assigned[0];

            Vector3 gemPos = GetGemPosition(gem);
            int bestWing = assigned[0];
            float bestDist = float.MaxValue;
            for (int i = 0; i < assigned.Count; i++)
            {
                int wingIndex = assigned[i];
                if (wingIndex < 0 || wingIndex >= wings.Count || wings[wingIndex].wingTransform == null)
                    continue;

                float dist = ToroidalMap.ToroidalDistance(gemPos, wings[wingIndex].GetWorldPosition());
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestWing = wingIndex;
                }
            }

            return bestWing;
        }

        public static IReadOnlyList<int> GetAssignedWingIndices(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return System.Array.Empty<int>();

            if (GetMagneticPullState(ship).gemIdToWingIndices.TryGetValue(gem.GetInstanceID(), out List<int> wingIndices) &&
                wingIndices != null && wingIndices.Count > 0)
            {
                return wingIndices;
            }

            return System.Array.Empty<int>();
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

        public static bool PassesBasicMagneticPullEligibility(Starship ship, Gem gem)
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

            if (buildingPullStateForShipIds.Contains(shipId))
                return new MagneticPullState();

            buildingPullStateForShipIds.Add(shipId);
            try
            {
                MagneticPullState built = BuildMagneticPullState(ship);
                pullStateByShipInstanceId[shipId] = built;
                return built;
            }
            finally
            {
                buildingPullStateForShipIds.Remove(shipId);
            }
        }

        public static bool IsGemWithinWingTractorRange(Starship ship, Gem gem, int wingIndex)
        {
            var wings = ship.WingTractorBeams;
            if (wings == null || wingIndex < 0 || wingIndex >= wings.Count)
                return false;

            wings[wingIndex].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
            Vector3 gemPos = GetGemPosition(gem);
            Vector3 wingPos = wings[wingIndex].GetWorldPosition();
            return IsWithinReach(gemPos, wingPos, ship.IsInOrbit, searchRadius);
        }

        private static bool IsGemWithinAssignedWingRange(Starship ship, Gem gem, int wingIndex) =>
            IsGemWithinWingTractorRange(ship, gem, wingIndex);

        /// <summary>
        /// Assigns each wing to at most one gem. Free wings always claim distinct gems first; when fewer
        /// gems than active tractor beams are in range, remaining wings stack on those gems and pull speed combines.
        /// </summary>
        private static MagneticPullState BuildMagneticPullState(Starship ship)
        {
            var state = new MagneticPullState();
            if (!IsShipEligibleForMagneticPull(ship))
            {
                ClearStickyWingLocksForShip(ship.GetInstanceID());
                return state;
            }

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

                    pullCandidateScratch.Add(new GemPullCandidate
                    {
                        gem = gem,
                        wingIndex = wi,
                        dist = dist
                    });
                }
            }

            var wingAssigned = new bool[wingCount];
            ApplyStickyWingLocks(ship, wings, wingCount, state, wingAssigned);
            ApplyDeployWingLocks(ship, state, wingAssigned);

            if (pullCandidateScratch.Count == 0 && state.gemIds.Count == 0)
                return state;

            CollectReachableGems(ship, wings, wingCount, reachableGemScratch);
            int activeWingCount = CountActiveWings(wings, wingCount);
            int freeWings = CountFreeWings(wingAssigned);

            if (reachableGemScratch.Count > 0 && freeWings > 0)
            {
                // Always give each free wing its own gem first; only stack leftovers when gems < beams.
                AssignIndependentOneGemPerWing(ship, state, wingAssigned);
                freeWings = CountFreeWings(wingAssigned);
                if (freeWings > 0 && reachableGemScratch.Count < activeWingCount)
                    AssignSplitWingsEvenly(ship, wings, wingCount, wingAssigned, state, reachableGemScratch, freeWings);
            }

            return state;
        }

        private static int CountActiveWings(IReadOnlyList<WingTractorBeamSlot> wings, int wingCount)
        {
            int active = 0;
            if (wings == null)
                return 0;

            for (int wi = 0; wi < wingCount; wi++)
            {
                if (wings[wi].wingTransform != null)
                    active++;
            }

            return active;
        }

        private static void AssignIndependentOneGemPerWing(Starship ship, MagneticPullState state, bool[] wingAssigned)
        {
            var assignedGemIds = new HashSet<int>(state.gemIds);
            pullCandidateScratch.Sort((a, b) =>
            {
                if (a.wingIndex != b.wingIndex)
                    return a.wingIndex.CompareTo(b.wingIndex);
                return a.dist.CompareTo(b.dist);
            });

            for (int i = 0; i < pullCandidateScratch.Count; i++)
            {
                GemPullCandidate c = pullCandidateScratch[i];
                if (wingAssigned[c.wingIndex])
                    continue;

                int gemId = c.gem.GetInstanceID();
                if (assignedGemIds.Contains(gemId))
                    continue;

                AssignWingToGem(ship, state, c.wingIndex, c.gem);
                wingAssigned[c.wingIndex] = true;
                assignedGemIds.Add(gemId);
            }
        }

        /// <summary>Honor active tractor beam deploy locks so locked gems enter the pull set on the locking wing.</summary>
        private static void ApplyDeployWingLocks(Starship ship, MagneticPullState state, bool[] wingAssigned)
        {
            var gems = Gem.AllGems;
            if (gems == null)
                return;

            for (int gi = 0; gi < gems.Count; gi++)
            {
                Gem gem = gems[gi];
                if (gem == null || !PassesBasicMagneticPullEligibility(ship, gem))
                    continue;
                if (!GemTractorBeamDeployTracker.TryGetLockingWingIndex(ship, gem, out int wingIndex))
                    continue;
                if (wingIndex < 0 || wingIndex >= wingAssigned.Length || wingAssigned[wingIndex])
                    continue;
                if (!IsGemWithinWingTractorRange(ship, gem, wingIndex))
                    continue;

                AssignWingToGem(ship, state, wingIndex, gem);
                wingAssigned[wingIndex] = true;
            }
        }

        /// <summary>Restore wing locks from prior frames; release when gem is gone or out of wing reach.</summary>
        private static void ApplyStickyWingLocks(
            Starship ship,
            IReadOnlyList<WingTractorBeamSlot> wings,
            int wingCount,
            MagneticPullState state,
            bool[] wingAssigned)
        {
            int shipId = ship.GetInstanceID();
            if (!stickyGemIdByShipAndWing.TryGetValue(shipId, out Dictionary<int, int> sticky))
                return;

            stickyWingRemovalScratch.Clear();
            foreach (var kv in sticky)
            {
                int wingIndex = kv.Key;
                int gemInstanceId = kv.Value;

                if (wingIndex < 0 || wingIndex >= wingCount || wings[wingIndex].wingTransform == null)
                {
                    stickyWingRemovalScratch.Add(wingIndex);
                    continue;
                }

                Gem gem = TryFindGemByInstanceId(gemInstanceId);
                if (!IsStickyWingLockStillValid(ship, gem, wingIndex))
                {
                    stickyWingRemovalScratch.Add(wingIndex);
                    continue;
                }

                if (wingAssigned[wingIndex])
                    continue;

                // Sticky entry already exists — only update pull state (not sticky) to avoid modifying the dictionary during enumeration.
                ApplyWingToPullState(state, wingIndex, gem);
                wingAssigned[wingIndex] = true;
            }

            for (int i = 0; i < stickyWingRemovalScratch.Count; i++)
                sticky.Remove(stickyWingRemovalScratch[i]);
        }

        private static bool IsStickyWingLockStillValid(Starship ship, Gem gem, int wingIndex)
        {
            if (gem == null)
                return false;
            if (!PassesBasicMagneticPullEligibility(ship, gem))
                return false;
            return IsGemWithinAssignedWingRange(ship, gem, wingIndex);
        }

        private static Gem TryFindGemByInstanceId(int gemInstanceId)
        {
            var gems = Gem.AllGems;
            if (gems == null)
                return null;

            for (int i = 0; i < gems.Count; i++)
            {
                Gem gem = gems[i];
                if (gem != null && gem.GetInstanceID() == gemInstanceId)
                    return gem;
            }

            return null;
        }

        private static void PruneStaleStickyWingLocks()
        {
            if (stickyGemIdByShipAndWing.Count == 0)
                return;

            var ships = Starship.AllStarships;
            if (ships == null || ships.Count == 0)
            {
                stickyGemIdByShipAndWing.Clear();
                return;
            }

            stickyWingRemovalScratch.Clear();
            foreach (int shipId in stickyGemIdByShipAndWing.Keys)
            {
                bool found = false;
                for (int i = 0; i < ships.Count && !found; i++)
                {
                    Starship ship = ships[i];
                    if (ship != null && ship.IsSpawned && ship.GetInstanceID() == shipId)
                        found = true;
                }

                if (!found)
                    stickyWingRemovalScratch.Add(shipId);
            }

            for (int i = 0; i < stickyWingRemovalScratch.Count; i++)
                stickyGemIdByShipAndWing.Remove(stickyWingRemovalScratch[i]);
        }

        private static void ClearStickyWingLocksForShip(int shipInstanceId)
        {
            stickyGemIdByShipAndWing.Remove(shipInstanceId);
        }

        private static void CollectReachableGems(
            Starship ship,
            IReadOnlyList<WingTractorBeamSlot> wings,
            int wingCount,
            List<Gem> reachableGems)
        {
            reachableGems.Clear();
            var seenGemIds = new HashSet<int>();

            var gems = Gem.AllGems;
            if (gems == null)
                return;

            for (int gi = 0; gi < gems.Count; gi++)
            {
                Gem gem = gems[gi];
                if (gem == null || !PassesBasicMagneticPullEligibility(ship, gem))
                    continue;

                int gemId = gem.GetInstanceID();
                if (!seenGemIds.Add(gemId))
                    continue;

                if (!IsGemReachableByAnyWing(ship, wings, wingCount, gem))
                    continue;

                reachableGems.Add(gem);
            }

            reachableGems.Sort((a, b) =>
            {
                int sizeCmp = b.GetValueSizeT().CompareTo(a.GetValueSizeT());
                if (sizeCmp != 0)
                    return sizeCmp;
                return CompareGemPullPriority(ship, a, b);
            });
        }

        private static int CompareGemPullPriority(Starship ship, Gem a, Gem b)
        {
            float distA = GetClosestFreeWingDistance(ship, a);
            float distB = GetClosestFreeWingDistance(ship, b);
            return distA.CompareTo(distB);
        }

        private static float GetClosestFreeWingDistance(Starship ship, Gem gem)
        {
            var wings = ship.WingTractorBeams;
            if (wings == null || wings.Count == 0)
                return float.MaxValue;

            Vector3 gemPos = GetGemPosition(gem);
            float best = float.MaxValue;
            for (int wi = 0; wi < wings.Count; wi++)
            {
                if (wings[wi].wingTransform == null)
                    continue;

                wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
                Vector3 wingPos = wings[wi].GetWorldPosition();
                float dist = ToroidalMap.ToroidalDistance(gemPos, wingPos);
                if (dist <= searchRadius)
                    best = Mathf.Min(best, dist);
            }

            return best;
        }

        private static bool IsGemReachableByAnyWing(
            Starship ship,
            IReadOnlyList<WingTractorBeamSlot> wings,
            int wingCount,
            Gem gem)
        {
            if (wings == null || gem == null)
                return false;

            Vector3 gemPos = GetGemPosition(gem);
            for (int wi = 0; wi < wingCount; wi++)
            {
                if (wings[wi].wingTransform == null)
                    continue;

                wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
                Vector3 wingPos = wings[wi].GetWorldPosition();
                if (ToroidalMap.ToroidalDistance(gemPos, wingPos) <= searchRadius)
                    return true;
            }

            return false;
        }


        private static int CountFreeWings(bool[] wingAssigned)
        {
            int free = 0;
            for (int i = 0; i < wingAssigned.Length; i++)
            {
                if (!wingAssigned[i])
                    free++;
            }

            return free;
        }

        private static void AssignSplitWingsEvenly(
            Starship ship,
            IReadOnlyList<WingTractorBeamSlot> wings,
            int wingCount,
            bool[] wingAssigned,
            MagneticPullState state,
            List<Gem> gems,
            int freeWingsToAssign)
        {
            int gemCount = gems.Count;
            if (gemCount <= 0 || freeWingsToAssign <= 0)
                return;

            int alreadyAssigned = 0;
            for (int gi = 0; gi < gemCount; gi++)
            {
                int gemId = gems[gi].GetInstanceID();
                if (state.gemIdToWingIndices.TryGetValue(gemId, out List<int> existing))
                    alreadyAssigned += existing.Count;
            }

            int budget = freeWingsToAssign + alreadyAssigned;
            int basePerGem = budget / gemCount;
            int remainder = budget % gemCount;

            for (int gi = 0; gi < gemCount; gi++)
            {
                Gem gem = gems[gi];
                int gemId = gem.GetInstanceID();
                int wingsOnGem = state.gemIdToWingIndices.TryGetValue(gemId, out List<int> existing)
                    ? existing.Count
                    : 0;

                int targetTotal = Mathf.Max(1, basePerGem + (remainder > 0 ? 1 : 0));
                if (remainder > 0)
                    remainder--;

                int toAssign = targetTotal - wingsOnGem;
                for (int n = 0; n < toAssign; n++)
                {
                    int wingIndex = FindClosestFreeWingForGem(ship, wings, wingCount, wingAssigned, gem);
                    if (wingIndex < 0)
                        return;

                    AssignWingToGem(ship, state, wingIndex, gem);
                    wingAssigned[wingIndex] = true;
                }
            }
        }

        /// <summary>Closest wing whose tractor reach currently contains the gem.</summary>
        public static int GetClosestInRangeWingIndex(Starship ship, Gem gem)
        {
            var wings = ship.WingTractorBeams;
            if (wings == null || gem == null)
                return -1;

            Vector3 gemPos = GetGemPosition(gem);
            int bestWing = -1;
            float bestDist = float.MaxValue;
            for (int wi = 0; wi < wings.Count; wi++)
            {
                if (wings[wi].wingTransform == null)
                    continue;

                wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
                Vector3 wingPos = wings[wi].GetWorldPosition();
                float dist = ToroidalMap.ToroidalDistance(gemPos, wingPos);
                if (dist > searchRadius || dist >= bestDist)
                    continue;

                bestDist = dist;
                bestWing = wi;
            }

            return bestWing;
        }

        private static int FindClosestFreeWingForGem(
            Starship ship,
            IReadOnlyList<WingTractorBeamSlot> wings,
            int wingCount,
            bool[] wingAssigned,
            Gem gem)
        {
            if (wings == null || gem == null)
                return -1;

            Vector3 gemPos = GetGemPosition(gem);
            int bestWing = -1;
            float bestDist = float.MaxValue;

            for (int wi = 0; wi < wingCount; wi++)
            {
                if (wingAssigned[wi] || wings[wi].wingTransform == null)
                    continue;

                wings[wi].GetTractorParams(ship.ShipLevel, ship.IsInOrbit, out float searchRadius, out _);
                Vector3 wingPos = wings[wi].GetWorldPosition();
                float dist = ToroidalMap.ToroidalDistance(gemPos, wingPos);
                if (dist > searchRadius || dist >= bestDist)
                    continue;

                bestDist = dist;
                bestWing = wi;
            }

            return bestWing;
        }

        private static void AssignWingToGem(Starship ship, MagneticPullState state, int wingIndex, Gem gem)
        {
            if (gem == null || ship == null)
                return;

            ApplyWingToPullState(state, wingIndex, gem);
            RecordStickyWingLock(ship, wingIndex, gem.GetInstanceID());
        }

        private static void ApplyWingToPullState(MagneticPullState state, int wingIndex, Gem gem)
        {
            int gemId = gem.GetInstanceID();
            state.gemIds.Add(gemId);
            state.wingIndexToGemId[wingIndex] = gemId;

            if (!state.gemIdToWingIndices.TryGetValue(gemId, out List<int> wingIndices))
            {
                wingIndices = new List<int>(4);
                state.gemIdToWingIndices[gemId] = wingIndices;
            }

            if (!wingIndices.Contains(wingIndex))
                wingIndices.Add(wingIndex);
        }

        private static void RecordStickyWingLock(Starship ship, int wingIndex, int gemInstanceId)
        {
            int shipId = ship.GetInstanceID();
            if (!stickyGemIdByShipAndWing.TryGetValue(shipId, out Dictionary<int, int> sticky))
            {
                sticky = new Dictionary<int, int>(8);
                stickyGemIdByShipAndWing[shipId] = sticky;
            }

            sticky[wingIndex] = gemInstanceId;
        }

        /// <summary>Ships without wing components: one beam at ship center using default wing-tier stats.</summary>
        private static void BuildFallbackSingleBeamPullSet(Starship ship, MagneticPullState state)
        {
            GetTractorBeamFromMaxGems(8f, ship.IsInOrbit, out float searchRadius, out _);
            Vector3 origin = GetShipPosition(ship);

            int shipId = ship.GetInstanceID();
            if (stickyGemIdByShipAndWing.TryGetValue(shipId, out Dictionary<int, int> sticky) &&
                sticky.TryGetValue(0, out int lockedGemId))
            {
                Gem lockedGem = TryFindGemByInstanceId(lockedGemId);
                if (lockedGem != null &&
                    PassesBasicMagneticPullEligibility(ship, lockedGem) &&
                    IsWithinReach(GetGemPosition(lockedGem), origin, ship.IsInOrbit, searchRadius))
                {
                    AssignWingToGem(ship, state, 0, lockedGem);
                    return;
                }

                sticky.Remove(0);
            }

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

                pullCandidateScratch.Add(new GemPullCandidate { gem = gem, wingIndex = 0, dist = dist });
            }

            if (pullCandidateScratch.Count == 0)
                return;

            pullCandidateScratch.Sort((a, b) => a.dist.CompareTo(b.dist));

            Gem chosen = pullCandidateScratch[0].gem;
            AssignWingToGem(ship, state, 0, chosen);
        }

        private static float GetTowardShipSpeed(Starship ship, Gem gem)
        {
            float towardSpeed = GemTractorBeamMotionTracker.GetTowardShipSpeed(ship, gem);

            if (gem.IsServer)
            {
                var gemRb = gem.GetComponent<Rigidbody>();
                if (gemRb != null && !gemRb.isKinematic &&
                    TryGetPullTowardDirection(ship, gem, out Vector3 pullDir))
                {
                    Vector3 vel = gemRb.linearVelocity;
                    vel.y = 0f;
                    towardSpeed = Mathf.Max(towardSpeed, Vector3.Dot(vel, pullDir));
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
