using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only gem tractor beam: assigns gems in wing search radii to ship wings, runs deploy
    /// timing (beam extend + width expand), then sets gem velocity toward wing pull targets.
    /// Writes ghosted <see cref="GemMotionState"/> lock fields so clients present the same pull
    /// without inventing wing assignment. Runs <b>before</b> <see cref="GemMotionSystem"/> so
    /// velocity and pose integrate in the same tick.
    /// Matching uses <see cref="GemTractorBeamAssignment"/> + <see cref="TractorBeamSettings"/>:
    /// sticky primary locks (assists re-target when PrimaryStickyOnly), primary fill so many gems
    /// keep many wings busy, and spare-wing assists capped by MaxCooperatingBeams. Stacked pull
    /// uses diminishing assists (<see cref="GemTractorBeamMath.StackedBeamPullScale"/>): primary
    /// 100%, each extra AssistPullScale (default 25%). Range/power multipliers apply via
    /// <see cref="ShipWingTractorBeamPose.GetTractorParams"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MiningSystem))]
    [UpdateBefore(typeof(GemMotionSystem))]
    [UpdateBefore(typeof(GemPickupSystem))]
    public partial class GemTractorBeamSystem : SystemBase
    {
        /// <summary>Per ship–gem pair: when lock started and how long beam extension takes.</summary>
        struct DeployState
        {
            public double LockStartTime;
            public float ExtendDuration;
            public uint LockTick;
        }

        // [STANDARD] Managed lists — not Burst-ready; acceptable for moderate gem counts.
        readonly Dictionary<long, DeployState> _deployByPair = new Dictionary<long, DeployState>(128);
        readonly List<GemTractorBeamAssignment.Candidate> _candidateScratch =
            new List<GemTractorBeamAssignment.Candidate>(64);
        readonly List<GemTractorBeamAssignment.Candidate> _filteredScratch =
            new List<GemTractorBeamAssignment.Candidate>(64);
        readonly List<GemTractorBeamAssignment.Pair> _pairScratch =
            new List<GemTractorBeamAssignment.Pair>(16);
        readonly Dictionary<int, int> _gemBeamCountScratch = new Dictionary<int, int>(32);
        /// <summary>Gem entity.Index → Entity for the current ship assignment pass.</summary>
        readonly Dictionary<int, Entity> _gemEntityByIndex = new Dictionary<int, Entity>(32);
        /// <summary>Gems locked or pulled this frame — others clear tractor ghost fields.</summary>
        readonly HashSet<int> _gemsLockedThisFrame = new HashSet<int>();
        /// <summary>
        /// Sticky wing→gem locks per ship entity index. Survives ship rotation until the gem
        /// leaves that wing's search radius. When TractorBeamSettings.PrimaryStickyOnly is on,
        /// only primary pairs are stored here (assists re-match every tick).
        /// </summary>
        readonly Dictionary<int, Dictionary<int, int>> _stickyLocksByShip =
            new Dictionary<int, Dictionary<int, int>>(16);
        /// <summary>Scratch: gemId → primary wing for ghost TractorWingIndex.</summary>
        readonly Dictionary<int, int> _primaryWingByGem = new Dictionary<int, int>(16);
        /// <summary>Scratch: gemId → all wing indices pulling it this tick (primary + assists).</summary>
        readonly Dictionary<int, List<int>> _wingsByGem = new Dictionary<int, List<int>>(16);

        /// <summary>Reused across ticks — avoid per-tick HashSet alloc (was a multi-ms GC hitch).</summary>
        readonly HashSet<long> _activePairsScratch = new HashSet<long>();

        /// <summary>Reused deploy-start set inside <see cref="BuildAssignment"/>.</summary>
        readonly HashSet<int> _deployStartedScratch = new HashSet<int>();

        /// <summary>Assigns gems, writes lock state, applies pull velocity before motion integrate.</summary>
        protected override void OnUpdate()
        {
            // --- Map period for seam reach ---
            // [TITAN-ORBIT] Prefer MapStateSingleton (same as MiningSystem / ShipPhysicsDrive).
            // Missing size → skip this tick (never invent 1000 — wrap-tile lock fails with wrong period).
            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                preferredW = mapState.MapWidth;
                preferredH = mapState.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;
            if (ToroidalMapEcs.IsValidMapSize(preferredW, preferredH))
                ToroidalMapEcs.SetMapSize(mapW, mapH);
            double now = SystemAPI.Time.ElapsedTime;
            uint serverTick = 0;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime) &&
                networkTime.ServerTick.IsValid)
            {
                serverTick = networkTime.ServerTick.TickIndexForValidTick;
            }

            _activePairsScratch.Clear();
            var activePairs = _activePairsScratch;
            _gemsLockedThisFrame.Clear();

            // --- Same timeline as GemState.SpawnServerTime / self-pickup block stamps ---
            float nowServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                EntityManager, now);

            // --- Per ship: assign gems to wings, write locks, apply pull velocity ---
            foreach (var (shipTransform, shipState, shipOrbit, moonDock, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>, RefRO<ShipOrbitState>, RefRO<ShipMoonDockState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!IsShipEligibleForPull(shipState.ValueRO, moonDock.ValueRO))
                {
                    _stickyLocksByShip.Remove(shipEntity.Index);
                    continue;
                }

                int shipNetworkId = 0;
                if (EntityManager.HasComponent<GhostOwner>(shipEntity))
                    shipNetworkId = EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;
                if (shipNetworkId == 0)
                {
                    _stickyLocksByShip.Remove(shipEntity.Index);
                    continue;
                }

                // [TITAN-ORBIT] Orbit ring widens tractor search radius via GemTractorBeamMath.
                bool inOrbit = shipOrbit.ValueRO.InOrbitRing;
                int shipLevel = math.max(1, shipState.ValueRO.ShipLevel);
                var wings = EntityManager.GetBuffer<ShipWingTractorBeamElement>(shipEntity);
                BuildAssignment(
                    shipEntity,
                    shipNetworkId,
                    nowServerTime,
                    shipTransform.ValueRO,
                    inOrbit,
                    shipLevel,
                    wings,
                    mapW,
                    mapH,
                    now,
                    serverTick,
                    activePairs);

                ApplyLockAndPull(
                    shipEntity,
                    shipNetworkId,
                    nowServerTime,
                    shipTransform.ValueRO,
                    inOrbit,
                    shipLevel,
                    wings,
                    mapW,
                    mapH,
                    now,
                    serverTick);
            }

            // --- Clear ghost lock on gems no longer assigned to any ship ---
            ClearStaleTractorLocks();

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
        /// Writes <see cref="GemMotionState"/> lock fields for assigned gems; after deploy completes,
        /// builds pull velocity from every wing on that gem (primary + spare assists).
        /// Primary contributes full wing pull; each assist adds AssistPullScale (settings,
        /// default 25%) of its own strength so three equal beams ≈ 150% rather than 300%.
        /// <para>
        /// [TITAN-ORBIT] <see cref="GemMotionState.PhaseTractor"/> is applied only after a non-zero
        /// pull velocity replaces <see cref="GemKinematics"/> — otherwise Coast keeps linear damping
        /// so damage-spill gems do not fly forever.
        /// </para>
        /// </summary>
        void ApplyLockAndPull(
            Entity shipEntity,
            int shipNetworkId,
            float nowServerTime,
            in LocalTransform shipTransform,
            bool inOrbit,
            int shipLevel,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            float mapW,
            float mapH,
            double now,
            uint serverTick)
        {
            if (_pairScratch.Count == 0)
                return;

            // --- Group pairs by gem (one ghost lock, possibly many wing forces) ---
            _primaryWingByGem.Clear();
            _wingsByGem.Clear();

            for (int i = 0; i < _pairScratch.Count; i++)
            {
                var pair = _pairScratch[i];
                if (!_wingsByGem.TryGetValue(pair.GemId, out var wingList))
                {
                    wingList = new List<int>(4);
                    _wingsByGem[pair.GemId] = wingList;
                }

                wingList.Add(pair.WingIndex);
                if (pair.IsPrimary || !_primaryWingByGem.ContainsKey(pair.GemId))
                    _primaryWingByGem[pair.GemId] = pair.WingIndex;
            }

            foreach (var (gemState, gemTransform, gemKinematics, gemEntity) in SystemAPI
                         .Query<RefRO<GemState>, RefRO<LocalTransform>, RefRW<GemKinematics>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                // Re-check self-pickup block (BuildAssignment already filtered; keep Apply safe).
                if (!PassesGemEligibility(gemState.ValueRO, shipNetworkId, nowServerTime))
                    continue;
                if (!_wingsByGem.TryGetValue(gemEntity.Index, out var wingList) || wingList.Count == 0)
                    continue;

                long pairKey = PairKey(shipEntity.Index, gemEntity.Index);
                if (!_deployByPair.TryGetValue(pairKey, out DeployState deploy))
                    continue;

                int primaryWing = _primaryWingByGem.TryGetValue(gemEntity.Index, out int pw) ? pw : wingList[0];
                _gemsLockedThisFrame.Add(gemEntity.Index);

                bool pullActive = IsPullPhysicsActive(deploy, now);

                // --- Ghost lock fields (beam VFX) — always while assigned ---
                // [TITAN-ORBIT] PhaseTractor is set ONLY after we overwrite GemKinematics with a
                // real pull velocity. Setting Tractor phase first then failing the pull left
                // damage-spill burst speed intact while GemMotionSystem skipped damping → gems
                // flew forever with "no friction."
                if (EntityManager.HasComponent<GemMotionState>(gemEntity))
                {
                    var motion = EntityManager.GetComponentData<GemMotionState>(gemEntity);
                    motion.TractorShipId = shipNetworkId;
                    motion.TractorWingIndex = (byte)math.clamp(primaryWing, 0, 255);
                    motion.TractorLockTick = deploy.LockTick != 0 ? deploy.LockTick : serverTick;
                    motion.TractorExtendDuration = deploy.ExtendDuration;

                    // Deploy / no pull yet: Coast so linear damping still bleeds burst speed.
                    if (!pullActive)
                    {
                        if (motion.Phase == GemMotionState.PhaseTractor)
                            motion.Phase = GemMotionState.PhaseCoast;
                        EntityManager.SetComponentData(gemEntity, motion);
                        continue;
                    }

                    // Defer PhaseTractor write until pull velocity is applied (below).
                    EntityManager.SetComponentData(gemEntity, motion);
                }
                else if (!pullActive)
                {
                    continue;
                }

                // --- Stacked pull (diminishing assists) ---
                // Spare assists only exist when AssignWings phase-3 ran (more beams than free gems)
                // and MaxCooperatingBeams > 1. Primary = 100%; each assist = AssistPullScale.
                // Direction still aims at each wing tip so multi-beam gems drift toward the cluster.
                float assistScale = TractorBeamSettingsCache.ResolveOrDefault().AssistPullScale;
                float3 gemPos = gemTransform.ValueRO.Position;
                float3 velocity = float3.zero;
                for (int wi = 0; wi < wingList.Count; wi++)
                {
                    int wingIndex = wingList[wi];
                    float pullSpeed = ResolveWingPullSpeed(
                        wingIndex, wings, shipLevel, inOrbit,
                        gemState.ValueRO.Value, gemState.ValueRO.Size);
                    // Primary lock gets full strength; assists are the "additional" beams.
                    bool isPrimary = wingIndex == primaryWing;
                    float stackScale = GemTractorBeamMath.StackedBeamPullScale(isPrimary, assistScale);
                    float3 pullTarget = ResolvePullTarget(shipTransform, wings, wingIndex);
                    float3 toWing = GemTractorBeamMath.ToroidalDirection(gemPos, pullTarget, mapW, mapH);
                    if (math.lengthsq(toWing) < 0.0001f)
                        continue;
                    velocity += toWing * (pullSpeed * stackScale);
                }

                // --- No usable pull this tick: stay Coast so burst / leftover speed can damp ---
                if (math.lengthsq(velocity) < 0.0001f)
                {
                    if (EntityManager.HasComponent<GemMotionState>(gemEntity))
                    {
                        var motion = EntityManager.GetComponentData<GemMotionState>(gemEntity);
                        if (motion.Phase == GemMotionState.PhaseTractor)
                        {
                            motion.Phase = GemMotionState.PhaseCoast;
                            EntityManager.SetComponentData(gemEntity, motion);
                        }
                    }

                    continue;
                }

                // --- Active pull: replace kinematics (kills damage-spill burst) + Tractor phase ---
                var kin = gemKinematics.ValueRO;
                kin.Velocity = velocity;
                gemKinematics.ValueRW = kin;

                if (EntityManager.HasComponent<GemMotionState>(gemEntity))
                {
                    var motion = EntityManager.GetComponentData<GemMotionState>(gemEntity);
                    motion.Phase = GemMotionState.PhaseTractor;
                    EntityManager.SetComponentData(gemEntity, motion);
                }
            }
        }

        /// <summary>
        /// Clears tractor ghost fields on gems that were not locked this frame so clients stop pulling.
        /// Restores Coast so <see cref="GemMotionSystem"/> linear damping applies again.
        /// </summary>
        void ClearStaleTractorLocks()
        {
            foreach (var (motion, entity) in SystemAPI
                         .Query<RefRW<GemMotionState>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                if (_gemsLockedThisFrame.Contains(entity.Index))
                    continue;

                var m = motion.ValueRO;
                if (m.TractorShipId == 0 && m.Phase != GemMotionState.PhaseTractor)
                    continue;

                // --- Unlock → Coast (never leave PhaseTractor without a ship id) ---
                m.TractorShipId = 0;
                m.TractorWingIndex = 0;
                m.TractorLockTick = 0;
                m.TractorExtendDuration = 0f;
                if (m.Phase == GemMotionState.PhaseTractor)
                    m.Phase = GemMotionState.PhaseCoast;
                motion.ValueRW = m;
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
                // --- No wing buffer: legacy max-gems tier + designer power multiplier ---
                GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out _, out wingAttraction);
                TractorBeamSettingsCache.ApplyPower(ref wingAttraction);
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
        /// Scans gems within each wing's search radius, then runs sticky/primary/assist matching.
        /// Fills <see cref="_pairScratch"/> for <see cref="ApplyLockAndPull"/>.
        /// </summary>
        void BuildAssignment(
            Entity shipEntity,
            int shipNetworkId,
            float nowServerTime,
            in LocalTransform shipTransform,
            bool inOrbit,
            int shipLevel,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            float mapW,
            float mapH,
            double now,
            uint serverTick,
            HashSet<long> activePairs)
        {
            _candidateScratch.Clear();
            _pairScratch.Clear();
            _gemEntityByIndex.Clear();

            int shipIndex = shipEntity.Index;
            int wingCount = wings.Length;
            if (wingCount <= 0)
            {
                _stickyLocksByShip.Remove(shipIndex);
                BuildFallbackAssignment(
                    shipEntity, shipNetworkId, nowServerTime, shipTransform, inOrbit, mapW, mapH, now,
                    serverTick, activePairs);
                return;
            }

            if (!_stickyLocksByShip.TryGetValue(shipIndex, out var stickyLocks))
            {
                stickyLocks = new Dictionary<int, int>(wingCount);
                _stickyLocksByShip[shipIndex] = stickyLocks;
            }

            // --- Collect in-range wing↔gem samples ---
            for (int wi = 0; wi < wingCount; wi++)
            {
                var wing = wings[wi];
                ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);

                foreach (var (gemState, gemTransform, gemEntity) in SystemAPI
                             .Query<RefRO<GemState>, RefRO<LocalTransform>>()
                             .WithAll<GemTag>()
                             .WithEntityAccess())
                {
                    if (!PassesGemEligibility(gemState.ValueRO, shipNetworkId, nowServerTime))
                        continue;

                    float3 gemPos = gemTransform.ValueRO.Position;
                    float dist = GemTractorBeamMath.ToroidalDistance(gemPos, wingPos, mapW, mapH);
                    if (dist > searchRadius)
                        continue;

                    _gemEntityByIndex[gemEntity.Index] = gemEntity;
                    _candidateScratch.Add(new GemTractorBeamAssignment.Candidate
                    {
                        WingIndex = wi,
                        GemId = gemEntity.Index,
                        Dist = dist,
                    });
                }
            }

            if (_candidateScratch.Count == 0)
            {
                stickyLocks.Clear();
                return;
            }

            // --- Sticky + primary fill + spare assists (shared with client VFX) ---
            // [TITAN-ORBIT] Tunables from TractorBeamSettings: primary-only sticky + cooperate cap.
            var beamSettings = TractorBeamSettingsCache.ResolveOrDefault();
            GemTractorBeamAssignment.AssignWings(
                _candidateScratch,
                wingCount,
                stickyLocks,
                _pairScratch,
                _filteredScratch,
                _gemBeamCountScratch,
                beamSettings.PrimaryStickyOnly,
                beamSettings.MaxCooperatingBeams);

            _deployStartedScratch.Clear();
            var deployStarted = _deployStartedScratch;
            for (int i = 0; i < _pairScratch.Count; i++)
            {
                var pair = _pairScratch[i];
                if (!_gemEntityByIndex.TryGetValue(pair.GemId, out Entity gemEntity))
                    continue;

                // One deploy clock per ship–gem (shared by primary + assists on that gem).
                if (!deployStarted.Add(pair.GemId))
                    continue;

                long pairKey = PairKey(shipIndex, pair.GemId);
                activePairs.Add(pairKey);
                float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[pair.WingIndex]);
                float3 gemPos = EntityManager.GetComponentData<LocalTransform>(gemEntity).Position;
                EnsureDeployState(pairKey, wingPos, gemPos, mapW, mapH, now, serverTick);
            }
        }

        /// <summary>Ships without wing buffers pull the single closest gem to hull center.</summary>
        void BuildFallbackAssignment(
            Entity shipEntity,
            int shipNetworkId,
            float nowServerTime,
            in LocalTransform shipTransform,
            bool inOrbit,
            float mapW,
            float mapH,
            double now,
            uint serverTick,
            HashSet<long> activePairs)
        {
            // --- Hull-center fallback reach (legacy ships without wing buffers) ---
            GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out float searchRadius, out _);
            TractorBeamSettingsCache.ApplyReach(ref searchRadius);
            float3 origin = shipTransform.Position;

            Entity closest = Entity.Null;
            float closestDist = float.MaxValue;

            foreach (var (gemState, gemTransform, gemEntity) in SystemAPI
                         .Query<RefRO<GemState>, RefRO<LocalTransform>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                if (!PassesGemEligibility(gemState.ValueRO, shipNetworkId, nowServerTime))
                    continue;

                float3 gemPos = gemTransform.ValueRO.Position;
                float dist = GemTractorBeamMath.ToroidalDistance(gemPos, origin, mapW, mapH);
                if (dist > searchRadius)
                    continue;

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = gemEntity;
                }
            }

            if (closest == Entity.Null)
                return;

            _pairScratch.Add(new GemTractorBeamAssignment.Pair
            {
                WingIndex = 0,
                GemId = closest.Index,
                IsPrimary = true,
            });

            long pairKey = PairKey(shipEntity.Index, closest.Index);
            activePairs.Add(pairKey);
            EnsureDeployState(
                pairKey, origin,
                EntityManager.GetComponentData<LocalTransform>(closest).Position,
                mapW, mapH, now, serverTick);
        }

        /// <summary>Starts deploy timer for a new ship–gem pair (beam extend duration from distance).</summary>
        void EnsureDeployState(
            long pairKey,
            float3 origin,
            float3 gemPos,
            float mapW,
            float mapH,
            double now,
            uint serverTick)
        {
            if (_deployByPair.ContainsKey(pairKey))
                return;

            float dist = GemTractorBeamMath.ToroidalDistance(gemPos, origin, mapW, mapH);
            _deployByPair[pairKey] = new DeployState
            {
                LockStartTime = now,
                ExtendDuration = GemTractorBeamMath.ComputeExtendDuration(dist),
                LockTick = serverTick,
            };
        }

        /// <summary>True after extend + width-expand phases complete — gem may be pulled.</summary>
        static bool IsPullPhysicsActive(in DeployState state, double now)
        {
            double elapsed = now - state.LockStartTime;
            double total = state.ExtendDuration + GemTractorBeamMath.WidthExpandDuration;
            return elapsed >= total - 0.0001;
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

        /// <summary>
        /// Loose gem with value, not already depositing, and not in the source ship's
        /// damage-spill self-pickup penalty window.
        /// </summary>
        static bool PassesGemEligibility(in GemState gem, int shipNetworkId, float nowServerTime) =>
            gem.Value > 0.001f &&
            gem.DepositTeam == TeamId.None &&
            !GemSelfPickupBlock.IsBlockedForShip(gem, shipNetworkId, nowServerTime);

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
