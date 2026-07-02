using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Client-side assignment + eligibility for tractor beam visuals.</summary>
    public static class GemTractorBeamClientLogic
    {
        struct PullCandidate
        {
            public int GemIndex;
            public int WingIndex;
            public float Dist;
            public bool InFlight;
        }

        static int _cacheFrame = -1;
        static readonly Dictionary<int, Dictionary<int, int>> WingByShipAndGem = new Dictionary<int, Dictionary<int, int>>(32);
        static readonly Dictionary<int, HashSet<int>> AssignedGemsByShip = new Dictionary<int, HashSet<int>>(32);
        static readonly List<PullCandidate> CandidateScratch = new List<PullCandidate>(64);

        public static void RebuildAssignmentCache()
        {
            if (Time.frameCount == _cacheFrame)
                return;
            _cacheFrame = Time.frameCount;

            WingByShipAndGem.Clear();
            AssignedGemsByShip.Clear();

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;

            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<ShipOrbitState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Unity.Collections.Allocator.Temp);
            using var shipOrbits = shipQuery.ToComponentDataArray<ShipOrbitState>(Unity.Collections.Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            using var gemQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GemKinematics>());
            using var gems = gemQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var gemStates = gemQuery.ToComponentDataArray<GemState>(Unity.Collections.Allocator.Temp);
            using var gemTransforms = gemQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);
            using var gemKinematics = gemQuery.ToComponentDataArray<GemKinematics>(Unity.Collections.Allocator.Temp);

            for (int si = 0; si < ships.Length; si++)
            {
                if (!IsShipEligibleForBeam(shipStates[si]))
                    continue;

                var wings = em.HasBuffer<ShipWingTractorBeamElement>(ships[si])
                    ? em.GetBuffer<ShipWingTractorBeamElement>(ships[si])
                    : default;

                BuildForShip(
                    ships[si].Index,
                    shipStates[si],
                    shipOrbits[si].InOrbitRing,
                    shipTransforms[si],
                    wings,
                    gems,
                    gemStates,
                    gemTransforms,
                    gemKinematics,
                    mapW,
                    mapH);
            }
        }

        static void BuildForShip(
            int shipIndex,
            in ShipState shipState,
            bool inOrbit,
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            Unity.Collections.NativeArray<Entity> gems,
            Unity.Collections.NativeArray<GemState> gemStates,
            Unity.Collections.NativeArray<LocalTransform> gemTransforms,
            Unity.Collections.NativeArray<GemKinematics> gemKinematics,
            float mapW,
            float mapH)
        {
            CandidateScratch.Clear();
            int shipLevel = math.max(1, shipState.ShipLevel);

            if (wings.IsCreated && wings.Length > 0)
            {
                for (int wi = 0; wi < wings.Length; wi++)
                {
                    var wing = wings[wi];
                    ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);

                    for (int gi = 0; gi < gems.Length; gi++)
                    {
                        if (!IsGemEligibleForBeam(gemStates[gi]))
                            continue;

                        float3 gemPos = gemTransforms[gi].Position;
                        float dist = GemTractorBeamMath.ToroidalDistance(gemPos, wingPos, mapW, mapH);
                        if (dist > searchRadius)
                            continue;

                        bool inFlight = GemTractorBeamDeployTracker.IsPullPhysicsActive(shipIndex, gems[gi].Index) &&
                                        GetTowardShipSpeed(gemPos, gemKinematics[gi].Velocity, shipTransform.Position, mapW, mapH) >=
                                        GemTractorBeamMath.ActivePullTowardSpeedThreshold * 0.5f;

                        CandidateScratch.Add(new PullCandidate
                        {
                            GemIndex = gems[gi].Index,
                            WingIndex = wi,
                            Dist = dist,
                            InFlight = inFlight,
                        });
                    }
                }
            }
            else
            {
                GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out float searchRadius, out _);
                float3 origin = shipTransform.Position;
                int closestGemIndex = -1;
                float closestDist = float.MaxValue;

                for (int gi = 0; gi < gems.Length; gi++)
                {
                    if (!IsGemEligibleForBeam(gemStates[gi]))
                        continue;

                    float3 gemPos = gemTransforms[gi].Position;
                    float dist = GemTractorBeamMath.ToroidalDistance(gemPos, origin, mapW, mapH);
                    if (dist > searchRadius)
                        continue;

                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestGemIndex = gems[gi].Index;
                    }
                }

                if (closestGemIndex >= 0)
                {
                    if (!WingByShipAndGem.TryGetValue(shipIndex, out var gemToWing))
                    {
                        gemToWing = new Dictionary<int, int>(4);
                        WingByShipAndGem[shipIndex] = gemToWing;
                    }

                    gemToWing[closestGemIndex] = 0;
                    AssignedGemsByShip[shipIndex] = new HashSet<int> { closestGemIndex };
                }

                return;
            }

            if (CandidateScratch.Count == 0)
                return;

            int wingCount = wings.Length;
            var assignedGemIds = new HashSet<int>();
            var wingHasGem = new bool[wingCount];
            var gemToWingMap = new Dictionary<int, int>(wingCount);

            CandidateScratch.Sort((a, b) =>
            {
                if (a.InFlight != b.InFlight)
                    return a.InFlight ? -1 : 1;
                if (a.WingIndex != b.WingIndex)
                    return a.WingIndex.CompareTo(b.WingIndex);
                return a.Dist.CompareTo(b.Dist);
            });

            for (int i = 0; i < CandidateScratch.Count; i++)
            {
                if (!CandidateScratch[i].InFlight)
                    break;

                var c = CandidateScratch[i];
                if (assignedGemIds.Contains(c.GemIndex) || wingHasGem[c.WingIndex])
                    continue;

                assignedGemIds.Add(c.GemIndex);
                wingHasGem[c.WingIndex] = true;
                gemToWingMap[c.GemIndex] = c.WingIndex;
            }

            CandidateScratch.Sort((a, b) =>
            {
                if (a.WingIndex != b.WingIndex)
                    return a.WingIndex.CompareTo(b.WingIndex);
                return a.Dist.CompareTo(b.Dist);
            });

            for (int i = 0; i < CandidateScratch.Count; i++)
            {
                var c = CandidateScratch[i];
                if (wingHasGem[c.WingIndex] || assignedGemIds.Contains(c.GemIndex))
                    continue;

                assignedGemIds.Add(c.GemIndex);
                wingHasGem[c.WingIndex] = true;
                gemToWingMap[c.GemIndex] = c.WingIndex;
            }

            if (gemToWingMap.Count > 0)
            {
                WingByShipAndGem[shipIndex] = gemToWingMap;
                AssignedGemsByShip[shipIndex] = assignedGemIds;
            }
        }

        public static bool CanShipMagneticallyPull(int shipIndex, int gemIndex)
        {
            RebuildAssignmentCache();
            return AssignedGemsByShip.TryGetValue(shipIndex, out var gems) && gems.Contains(gemIndex);
        }

        public static bool IsWithinMagneticPullRange(
            EntityManager em,
            Entity shipEntity,
            in ShipState shipState,
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            Entity gemEntity,
            in LocalTransform gemTransform,
            float mapW,
            float mapH)
        {
            if (!CanShipMagneticallyPull(shipEntity.Index, gemEntity.Index))
                return false;

            if (!WingByShipAndGem.TryGetValue(shipEntity.Index, out var gemToWing) ||
                !gemToWing.TryGetValue(gemEntity.Index, out int wingIndex))
                return false;

            int shipLevel = math.max(1, shipState.ShipLevel);
            bool inOrbit = TryGetInOrbit(em, shipEntity);

            if (wings.IsCreated && wingIndex >= 0 && wingIndex < wings.Length)
            {
                var wing = wings[wingIndex];
                ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);
                return GemTractorBeamMath.IsWithinReach(gemTransform.Position, wingPos, searchRadius, mapW, mapH);
            }

            GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out float fallbackRadius, out _);
            return GemTractorBeamMath.IsWithinReach(gemTransform.Position, shipTransform.Position, fallbackRadius, mapW, mapH);
        }

        public static bool IsWithinCandidateRange(
            EntityManager em,
            Entity shipEntity,
            in ShipState shipState,
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            Entity gemEntity,
            in LocalTransform gemTransform,
            float mapW,
            float mapH)
        {
            if (!IsShipEligibleForBeam(shipState) || !IsGemEligibleForBeam(em.GetComponentData<GemState>(gemEntity)))
                return false;

            int shipLevel = math.max(1, shipState.ShipLevel);
            bool inOrbit = TryGetInOrbit(em, shipEntity);

            if (wings.IsCreated && wings.Length > 0)
            {
                for (int wi = 0; wi < wings.Length; wi++)
                {
                    var wing = wings[wi];
                    ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);
                    if (GemTractorBeamMath.IsWithinReach(gemTransform.Position, wingPos, searchRadius, mapW, mapH))
                        return true;
                }

                return false;
            }

            GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out float fallbackRadius, out _);
            return GemTractorBeamMath.IsWithinReach(gemTransform.Position, shipTransform.Position, fallbackRadius, mapW, mapH);
        }

        public static float3 GetDeployBeamOrigin(
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            in LocalTransform gemTransform,
            int shipLevel,
            bool inOrbit,
            float mapW,
            float mapH)
        {
            if (wings.IsCreated && wings.Length > 0)
            {
                float bestDist = float.MaxValue;
                float3 bestOrigin = shipTransform.Position;
                for (int wi = 0; wi < wings.Length; wi++)
                {
                    var wing = wings[wi];
                    ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);
                    float dist = GemTractorBeamMath.ToroidalDistance(gemTransform.Position, wingPos, mapW, mapH);
                    if (dist <= searchRadius && dist < bestDist)
                    {
                        bestDist = dist;
                        bestOrigin = wingPos;
                    }
                }

                return bestOrigin;
            }

            return shipTransform.Position;
        }

        public static float3 ResolveBeamOrigin(
            Entity shipEntity,
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            Entity gemEntity)
        {
            if (WingByShipAndGem.TryGetValue(shipEntity.Index, out var gemToWing) &&
                gemToWing.TryGetValue(gemEntity.Index, out int wingIndex) &&
                wings.IsCreated && wingIndex >= 0 && wingIndex < wings.Length)
            {
                return ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wingIndex]);
            }

            return shipTransform.Position;
        }

        public static bool IsEligibleForBeamVisual(
            EntityManager em,
            Entity shipEntity,
            in ShipState shipState,
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            Entity gemEntity,
            in LocalTransform gemTransform,
            float mapW,
            float mapH)
        {
            if (!CanShipMagneticallyPull(shipEntity.Index, gemEntity.Index))
                return false;

            return IsWithinMagneticPullRange(em, shipEntity, shipState, shipTransform, wings, gemEntity, gemTransform, mapW, mapH);
        }

        public static bool IsShipEligibleForBeam(in ShipState ship)
        {
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            if (ship.CurrentGems >= ship.GemCapacity)
                return false;
            return true;
        }

        public static bool IsGemEligibleForBeam(in GemState gem) =>
            gem.Value > 0.001f && gem.DepositTeam == TeamId.None;

        static float GetTowardShipSpeed(float3 gemPos, float3 velocity, float3 shipPos, float mapW, float mapH)
        {
            float3 toShip = GemTractorBeamMath.ToroidalDirection(gemPos, shipPos, mapW, mapH);
            velocity.y = 0f;
            return math.dot(velocity, toShip);
        }

        static bool TryGetInOrbit(EntityManager em, Entity shipEntity) =>
            em.HasComponent<ShipOrbitState>(shipEntity) &&
            em.GetComponentData<ShipOrbitState>(shipEntity).InOrbitRing;

        public static void Clear()
        {
            WingByShipAndGem.Clear();
            AssignedGemsByShip.Clear();
            _cacheFrame = -1;
        }
    }
}
