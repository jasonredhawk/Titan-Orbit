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
    /// <summary>
    /// Server-only gem tractor beam: assigns gems in wing search radii to ship wings, runs deploy
    /// timing (beam extend + width expand), then sets gem velocity toward wing pull targets.
    /// Reads ShipWingTractorBeamElement buffer (synced from prefab by ShipWingTractorBeamSyncSystem).
    /// [TITAN-ORBIT] One gem per wing (parallel collectors). Pull speed uses each wing's
    /// TractorBeamPower (+ level / orbit), matching original NGO GemTractorBeamSettings — not a
    /// global gem-mass base speed. Runs after GemMotionSystem, before GemPickupSystem.
    /// Ship must have gem capacity and not be dead, team-selecting, or moon-docking.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemMotionSystem))]
    [UpdateBefore(typeof(GemPickupSystem))]
    public partial class GemTractorBeamSystem : SystemBase
    {
        /// <summary>Per ship–gem pair: when lock started and how long beam extension takes.</summary>
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

        // [STANDARD] Managed dictionaries — not Burst-ready; acceptable for moderate gem counts.
        readonly Dictionary<long, DeployState> _deployByPair = new Dictionary<long, DeployState>(128);
        readonly List<PullCandidate> _candidateScratch = new List<PullCandidate>(64);

        protected override void OnUpdate()
        {
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            double now = SystemAPI.Time.ElapsedTime;

            var activePairs = new HashSet<long>();

            // --- Per ship: assign gems to wings, then apply pull velocity ---
            foreach (var (shipTransform, shipState, shipOrbit, moonDock, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>, RefRO<ShipOrbitState>, RefRO<ShipMoonDockState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!IsShipEligibleForPull(shipState.ValueRO, moonDock.ValueRO))
                    continue;

                // [TITAN-ORBIT] Orbit ring widens tractor search radius via GemTractorBeamMath.
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

            // --- Prune deploy state for ship–gem pairs no longer in range ---
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

        /// <summary>
        /// Sets gem velocity toward assigned wing after the deploy VFX window (extend line + width open).
        /// Matches client <see cref="Game.GemTractorBeamDeployTracker"/> so pull starts when the cone is ready.
        /// </summary>
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

                // [TITAN-ORBIT] Wait for extend + width-expand so gameplay matches the VFX beat:
                // line out → mouth opens → then pull.
                long pairKey = PairKey(shipEntity.Index, gemEntity.Index);
                if (!IsPullPhysicsActive(pairKey, now))
                    continue;

                int wingIndex = assignment[gemEntity.Index];
                // [TITAN-ORBIT] Pull speed comes from the assigned wing's TractorBeamPower
                // (original NGO GemTractorBeamSettings), with a mild gem-mass feel factor.
                float pullSpeed = ResolveWingPullSpeed(
                    wingIndex, wings, shipLevel, inOrbit,
                    gemState.ValueRO.Value, gemState.ValueRO.Size);

                float3 gemPos = gemTransform.ValueRO.Position;
                float3 pullTarget = ResolvePullTarget(shipTransform, wings, wingIndex);
                // [TITAN-ORBIT] Toroidal wrap — shortest direction on the flat map.
                float3 toWing = GemTractorBeamMath.ToroidalDirection(gemPos, pullTarget, mapW, mapH);
                if (math.lengthsq(toWing) < 0.0001f)
                    continue;

                // Keep any residual tumble; overwrite linear velocity with tractor pull.
                var kin = gemKinematics.ValueRO;
                kin.Velocity = toWing * pullSpeed;
                gemKinematics.ValueRW = kin;
            }
        }

        /// <summary>
        /// Resolves gameplay pull speed for the wing assigned to this gem.
        /// Falls back to default max-gems tier when the ship has no wing buffer.
        /// </summary>
        static float ResolveWingPullSpeed(
            int wingIndex,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            int shipLevel,
            bool inOrbit,
            float gemValue,
            float gemSize)
        {
            float wingAttraction;
            if (wingIndex >= 0 && wingIndex < wings.Length)
            {
                ShipWingTractorBeamPose.GetTractorParams(
                    wings[wingIndex], shipLevel, inOrbit, out _, out wingAttraction);
            }
            else
            {
                GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out _, out wingAttraction);
            }

            return GemTractorBeamMath.ResolvePullSpeedFromWing(wingAttraction, gemValue, gemSize);
        }

        static float3 ResolvePullTarget(in LocalTransform shipTransform, DynamicBuffer<ShipWingTractorBeamElement> wings, int wingIndex)
        {
            if (wingIndex >= 0 && wingIndex < wings.Length)
                return ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wingIndex]);
            return shipTransform.Position;
        }

        /// <summary>
        /// Scans gems within each wing's search radius, prioritizes in-flight gems, assigns one gem per wing.
        /// Falls back to hull-center pull when no wing buffer exists.
        /// </summary>
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

            // --- Collect candidates per wing ---
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

                    // Gems already moving toward ship keep their wing assignment (sticky pull).
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

            // --- Assign: in-flight gems first, then nearest per wing ---
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

        /// <summary>Ships without wing buffers pull the single closest gem to hull center.</summary>
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

        /// <summary>Starts deploy timer for a new ship–gem pair (beam extend duration from distance).</summary>
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

        /// <summary>True after extend + width-expand phases complete — gem may be pulled.</summary>
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

        /// <summary>
        /// [TITAN-ORBIT] Ship cannot pull when dead, picking team, moon-docking, or at gem capacity.
        /// </summary>
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
