using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemMotionSystem))]
    [UpdateBefore(typeof(GemPickupSystem))]
    public partial class GemTractorBeamSystem : SystemBase
    {
        struct DeployState
        {
            public double LockStartTime;
            public float ExtendDuration;
        }

        struct PullCandidate
        {
            public Entity Gem;
            public int WingIndex;
            public float Dist;
            public bool InFlight;
        }

        readonly Dictionary<long, DeployState> _deployByPair = new Dictionary<long, DeployState>(128);
        readonly List<PullCandidate> _candidateScratch = new List<PullCandidate>(64);

        protected override void OnUpdate()
        {
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            double now = SystemAPI.Time.ElapsedTime;

            var activePairs = new HashSet<long>();

            foreach (var (shipTransform, shipState, shipOrbit, moonDock, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>, RefRO<ShipOrbitState>, RefRO<ShipMoonDockState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!IsShipEligibleForPull(shipState.ValueRO, moonDock.ValueRO))
                    continue;

                bool inOrbit = shipOrbit.ValueRO.InOrbitRing;
                int shipLevel = math.max(1, shipState.ValueRO.ShipLevel);
                var wings = EntityManager.GetBuffer<ShipWingTractorBeamElement>(shipEntity);
                using var assignment = BuildAssignment(
                    shipEntity,
                    shipTransform.ValueRO,
                    shipState.ValueRO,
                    inOrbit,
                    shipLevel,
                    wings,
                    mapW,
                    mapH,
                    now,
                    activePairs);

                ApplyPullPhysics(
                    shipEntity,
                    shipTransform.ValueRO,
                    shipState.ValueRO,
                    inOrbit,
                    shipLevel,
                    wings,
                    assignment,
                    mapW,
                    mapH,
                    now);
            }

            if (_deployByPair.Count > activePairs.Count)
            {
                var stale = new List<long>(8);
                foreach (var kv in _deployByPair)
                {
                    if (!activePairs.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                    _deployByPair.Remove(stale[i]);
            }
        }

        void ApplyPullPhysics(
            Entity shipEntity,
            in LocalTransform shipTransform,
            in ShipState shipState,
            bool inOrbit,
            int shipLevel,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            NativeParallelHashMap<int, int> assignment,
            float mapW,
            float mapH,
            double now)
        {
            foreach (var (gemState, gemTransform, gemKinematics, gemEntity) in SystemAPI
                         .Query<RefRO<GemState>, RefRO<LocalTransform>, RefRW<GemKinematics>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                if (!PassesGemEligibility(gemState.ValueRO))
                    continue;
                if (!assignment.ContainsKey(gemEntity.Index))
                    continue;

                long pairKey = PairKey(shipEntity.Index, gemEntity.Index);
                if (!IsPullPhysicsActive(pairKey, now))
                    continue;

                int wingIndex = assignment[gemEntity.Index];
                float pullSpeed = ResolvePullSpeed(wingIndex, wings, shipLevel, inOrbit);

                float3 gemPos = gemTransform.ValueRO.Position;
                float3 pullTarget = ResolvePullTarget(shipTransform, wings, wingIndex);
                float3 toWing = GemTractorBeamMath.ToroidalDirection(gemPos, pullTarget, mapW, mapH);
                if (math.lengthsq(toWing) < 0.0001f)
                    continue;

                gemKinematics.ValueRW = new GemKinematics { Velocity = toWing * pullSpeed };
            }
        }

        static float3 ResolvePullTarget(in LocalTransform shipTransform, DynamicBuffer<ShipWingTractorBeamElement> wings, int wingIndex)
        {
            if (wingIndex >= 0 && wingIndex < wings.Length)
                return ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wingIndex]);
            return shipTransform.Position;
        }

        NativeParallelHashMap<int, int> BuildAssignment(
            Entity shipEntity,
            in LocalTransform shipTransform,
            in ShipState shipState,
            bool inOrbit,
            int shipLevel,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            float mapW,
            float mapH,
            double now,
            HashSet<long> activePairs)
        {
            var assignment = new NativeParallelHashMap<int, int>(8, Allocator.Temp);
            _candidateScratch.Clear();

            int wingCount = wings.Length;
            if (wingCount <= 0)
            {
                BuildFallbackAssignment(shipEntity, shipTransform, inOrbit, mapW, mapH, now, activePairs, ref assignment);
                return assignment;
            }

            for (int wi = 0; wi < wingCount; wi++)
            {
                var wing = wings[wi];
                ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);

                foreach (var (gemState, gemTransform, gemKinematics, gemEntity) in SystemAPI
                             .Query<RefRO<GemState>, RefRO<LocalTransform>, RefRO<GemKinematics>>()
                             .WithAll<GemTag>()
                             .WithEntityAccess())
                {
                    if (!PassesGemEligibility(gemState.ValueRO))
                        continue;

                    float3 gemPos = gemTransform.ValueRO.Position;
                    float dist = GemTractorBeamMath.ToroidalDistance(gemPos, wingPos, mapW, mapH);
                    if (dist > searchRadius)
                        continue;

                    long pairKey = PairKey(shipEntity.Index, gemEntity.Index);
                    activePairs.Add(pairKey);
                    EnsureDeployState(pairKey, wingPos, gemPos, mapW, mapH, now);

                    bool inFlight = IsPullPhysicsActive(pairKey, now) &&
                                    GetTowardShipSpeed(gemPos, gemKinematics.ValueRO.Velocity, shipTransform.Position, mapW, mapH) >=
                                    GemTractorBeamMath.ActivePullTowardSpeedThreshold * 0.5f;

                    _candidateScratch.Add(new PullCandidate
                    {
                        Gem = gemEntity,
                        WingIndex = wi,
                        Dist = dist,
                        InFlight = inFlight,
                    });
                }
            }

            if (_candidateScratch.Count == 0)
                return assignment;

            var assignedGemIds = new HashSet<int>();
            var wingHasGem = new bool[wingCount];

            _candidateScratch.Sort((a, b) =>
            {
                if (a.InFlight != b.InFlight)
                    return a.InFlight ? -1 : 1;
                if (a.WingIndex != b.WingIndex)
                    return a.WingIndex.CompareTo(b.WingIndex);
                return a.Dist.CompareTo(b.Dist);
            });

            for (int i = 0; i < _candidateScratch.Count; i++)
            {
                if (!_candidateScratch[i].InFlight)
                    break;

                var c = _candidateScratch[i];
                if (assignedGemIds.Contains(c.Gem.Index) || wingHasGem[c.WingIndex])
                    continue;

                assignedGemIds.Add(c.Gem.Index);
                wingHasGem[c.WingIndex] = true;
                assignment.TryAdd(c.Gem.Index, c.WingIndex);
            }

            _candidateScratch.Sort((a, b) =>
            {
                if (a.WingIndex != b.WingIndex)
                    return a.WingIndex.CompareTo(b.WingIndex);
                return a.Dist.CompareTo(b.Dist);
            });

            for (int i = 0; i < _candidateScratch.Count; i++)
            {
                var c = _candidateScratch[i];
                if (wingHasGem[c.WingIndex] || assignedGemIds.Contains(c.Gem.Index))
                    continue;

                assignedGemIds.Add(c.Gem.Index);
                wingHasGem[c.WingIndex] = true;
                assignment.TryAdd(c.Gem.Index, c.WingIndex);
            }

            return assignment;
        }

        void BuildFallbackAssignment(
            Entity shipEntity,
            in LocalTransform shipTransform,
            bool inOrbit,
            float mapW,
            float mapH,
            double now,
            HashSet<long> activePairs,
            ref NativeParallelHashMap<int, int> assignment)
        {
            GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out float searchRadius, out _);
            float3 origin = shipTransform.Position;

            Entity closest = Entity.Null;
            float closestDist = float.MaxValue;

            foreach (var (gemState, gemTransform, gemEntity) in SystemAPI
                         .Query<RefRO<GemState>, RefRO<LocalTransform>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                if (!PassesGemEligibility(gemState.ValueRO))
                    continue;

                float3 gemPos = gemTransform.ValueRO.Position;
                float dist = GemTractorBeamMath.ToroidalDistance(gemPos, origin, mapW, mapH);
                if (dist > searchRadius)
                    continue;

                long pairKey = PairKey(shipEntity.Index, gemEntity.Index);
                activePairs.Add(pairKey);
                EnsureDeployState(pairKey, origin, gemPos, mapW, mapH, now);

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = gemEntity;
                }
            }

            if (closest != Entity.Null)
                assignment.TryAdd(closest.Index, 0);
        }

        static float ResolvePullSpeed(
            int wingIndex,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            int shipLevel,
            bool inOrbit)
        {
            if (wingIndex >= 0 && wingIndex < wings.Length)
            {
                ShipWingTractorBeamPose.GetTractorParams(wings[wingIndex], shipLevel, inOrbit, out _, out float speed);
                return speed;
            }

            GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out _, out float fallback);
            return fallback;
        }

        void EnsureDeployState(long pairKey, float3 origin, float3 gemPos, float mapW, float mapH, double now)
        {
            if (_deployByPair.ContainsKey(pairKey))
                return;

            float dist = GemTractorBeamMath.ToroidalDistance(gemPos, origin, mapW, mapH);
            _deployByPair[pairKey] = new DeployState
            {
                LockStartTime = now,
                ExtendDuration = GemTractorBeamMath.ComputeExtendDuration(dist),
            };
        }

        bool IsPullPhysicsActive(long pairKey, double now)
        {
            if (!_deployByPair.TryGetValue(pairKey, out DeployState state))
                return false;

            double elapsed = now - state.LockStartTime;
            double total = state.ExtendDuration + GemTractorBeamMath.WidthExpandDuration;
            return elapsed >= total - 0.0001;
        }

        static float GetTowardShipSpeed(float3 gemPos, float3 velocity, float3 shipPos, float mapW, float mapH)
        {
            float3 toShip = GemTractorBeamMath.ToroidalDirection(gemPos, shipPos, mapW, mapH);
            velocity.y = 0f;
            return math.dot(velocity, toShip);
        }

        static bool IsShipEligibleForPull(in ShipState ship, in ShipMoonDockState moonDock)
        {
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            if (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.01f)
                return false;
            if (ship.CurrentGems >= ship.GemCapacity)
                return false;
            return true;
        }

        static bool PassesGemEligibility(in GemState gem) =>
            gem.Value > 0.001f && gem.DepositTeam == TeamId.None;

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
