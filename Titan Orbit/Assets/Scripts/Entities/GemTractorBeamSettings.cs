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
            public bool inFlight;
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

        /// <summary>True when an assigned gem is still inside its wing (or fallback) reach.</summary>
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

            var assignedWings = GetAssignedWingIndices(ship, gem);
            if (assignedWings.Count == 0)
                return false;

            for (int i = 0; i < assignedWings.Count; i++)
            {
                if (IsGemWithinAssignedWingRange(ship, gem, assignedWings[i]))
                    return true;
            }

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
        }

        /// <summary>
        /// True when this gem is assigned to one or more of the ship's wing tractor beams.
        /// </summary>
        public static bool CanShipMagneticallyPull(Starship ship, Gem gem)
        {
            if (!PassesBasicMagneticPullEligibility(ship, gem))
                return false;

            return GetMagneticPullState(ship).gemIds.Contains(gem.GetInstanceID());
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

        /// <summary>World origin for the beam line (closest assigned wing, or ship center).</summary>
        public static Vector3 GetBeamOrigin(Starship ship, Gem gem)
        {
            if (ship == null)
                return Vector3.zero;

            int wingIndex = GetPrimaryAssignedWingIndex(ship, gem);
            return GetWingBeamOrigin(ship, wingIndex);
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

        public static int GetPrimaryAssignedWingIndex(Starship ship, Gem gem)
        {
            var assigned = GetAssignedWingIndices(ship, gem);
            return assigned.Count > 0 ? assigned[0] : -1;
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
        /// Assigns each wing to at most one gem. When fewer gems than wings are in range, extra wings
        /// coordinate on the same gem(s) and pull speed stacks; otherwise each wing pulls a distinct gem.
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

                    bool inFlight = GemTractorBeamDeployTracker.TryIsPullPhysicsActive(ship, gem) &&
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

            var wingAssigned = new bool[wingCount];

            // Keep in-flight gems on their wing.
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
                if (wingAssigned[c.wingIndex])
                    continue;

                AssignWingToGem(state, c.wingIndex, c.gem);
                wingAssigned[c.wingIndex] = true;
            }

            CollectReachableGems(ship, wings, wingCount, reachableGemScratch);
            int activeWingCount = CountActiveWings(wings, wingCount);
            int freeWings = CountFreeWings(wingAssigned);

            if (reachableGemScratch.Count > 0 && freeWings > 0)
            {
                if (reachableGemScratch.Count < activeWingCount)
                    AssignSplitWingsEvenly(ship, wings, wingCount, wingAssigned, state, reachableGemScratch, freeWings);
                else
                    AssignIndependentOneGemPerWing(state, wingAssigned);
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

        private static void AssignIndependentOneGemPerWing(MagneticPullState state, bool[] wingAssigned)
        {
            var assignedGemIds = new HashSet<int>();
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

                AssignWingToGem(state, c.wingIndex, c.gem);
                wingAssigned[c.wingIndex] = true;
                assignedGemIds.Add(gemId);
            }
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

                    AssignWingToGem(state, wingIndex, gem);
                    wingAssigned[wingIndex] = true;
                }
            }
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

        private static void AssignWingToGem(MagneticPullState state, int wingIndex, Gem gem)
        {
            if (gem == null)
                return;

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

                bool inFlight = GemTractorBeamDeployTracker.TryIsPullPhysicsActive(ship, gem) &&
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
            AssignWingToGem(state, 0, chosen);
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
