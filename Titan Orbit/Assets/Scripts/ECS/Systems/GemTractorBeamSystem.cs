using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only gem tractor beam: assigns gems in wing search radii to ship wings, then
    /// sets gem velocity toward wing pull targets on the same tick the lock starts.
    /// Writes ghosted <see cref="GemMotionState"/> lock fields so clients present the same pull
    /// without inventing wing assignment. Runs <b>before</b> <see cref="GemMotionSystem"/> so
    /// velocity and pose integrate in the same tick.
    /// Matching uses <see cref="GemTractorBeamAssignment"/> + <see cref="TractorBeamSettings"/>:
    /// sticky primary locks (assists re-target when PrimaryStickyOnly), primary fill so many gems
    /// keep many wings busy, and spare-wing assists capped by MaxCooperatingBeams. Stacked pull
    /// uses diminishing assists (<see cref="GemTractorBeamMath.StackedBeamPullScale"/>): primary
    /// 100%, each extra AssistPullScale (default 25%). Range/power multipliers apply via
    /// <see cref="ShipWingTractorBeamPose.GetTractorParams"/>.
    /// Nearby gem scans use <see cref="GemSpatialHash"/> so 100 ships do not walk every gem.
    /// Deploy extend/widen is gameplay timing: lock + beam VFX start immediately, pull velocity
    /// waits until the thin line reaches the gem and the cone has widened (distance-scaled).
    /// Burst gems keep Coast damping during that wait. Clients draw beams from ghost lock
    /// fields only; they must not invent a second wing assignment.
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
        readonly Dictionary<long, int> _gemBeamCountScratch = new Dictionary<long, int>(32);
        /// <summary>Packed gem key → Entity for the current ship assignment pass.</summary>
        readonly Dictionary<long, Entity> _gemEntityByKey = new Dictionary<long, Entity>(32);
        /// <summary>Gems locked or pulled this frame — others clear tractor ghost fields.</summary>
        readonly HashSet<int> _gemsLockedThisFrame = new HashSet<int>();

        /// <summary>Entities locked this tick (for unlock when a ship becomes ineligible).</summary>
        readonly HashSet<Entity> _lockedEntitiesThisFrame = new HashSet<Entity>();

        /// <summary>Entities that had a tractor lock last tick — only these need unlock scans.</summary>
        readonly HashSet<Entity> _lockedEntitiesCarry = new HashSet<Entity>();

        /// <summary>Live gems for the spatial hash (built once per tick).</summary>
        EntityQuery _gemSpatialQuery;
        /// <summary>
        /// Sticky wing→gem locks per ship entity index. Survives ship rotation until the gem
        /// leaves that wing's search radius. When TractorBeamSettings.PrimaryStickyOnly is on,
        /// only primary pairs are stored here (assists re-match every tick).
        /// </summary>
        readonly Dictionary<int, Dictionary<int, long>> _stickyLocksByShip =
            new Dictionary<int, Dictionary<int, long>>(16);
        /// <summary>Scratch: gem key → primary wing for ghost TractorWingIndex.</summary>
        readonly Dictionary<long, int> _primaryWingByGem = new Dictionary<long, int>(16);
        /// <summary>Scratch: gem key → all wing indices pulling it this tick (primary + assists).</summary>
        readonly Dictionary<long, List<int>> _wingsByGem = new Dictionary<long, List<int>>(16);

        /// <summary>Reused across ticks — avoid per-tick HashSet alloc (was a multi-ms GC hitch).</summary>
        readonly HashSet<long> _activePairsScratch = new HashSet<long>();

        /// <summary>Reused deploy-start set inside <see cref="BuildAssignment"/>.</summary>
        readonly HashSet<long> _deployStartedScratch = new HashSet<long>();

        /// <summary>Caches the gem spatial-hash query (all live gems, once per tick).</summary>
        protected override void OnCreate()
        {
            _gemSpatialQuery = GetEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

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
            _lockedEntitiesThisFrame.Clear();

            // --- Same timeline as GemState.SpawnServerTime / self-pickup block stamps ---
            float nowServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                EntityManager, now);
            int simHz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate) &&
                tickRate.SimulationTickRate > 0)
                simHz = tickRate.SimulationTickRate;

            NativeArray<Entity> gemEntities = default;
            NativeArray<LocalTransform> gemTransforms = default;
            GemSpatialHash hash = default;
            NativeList<int> nearby = default;
            NativeHashSet<int> seenScratch = default;
            int gemCount = _gemSpatialQuery.CalculateEntityCount();
            if (gemCount > 0)
            {
                gemEntities = _gemSpatialQuery.ToEntityArray(Allocator.Temp);
                gemTransforms = _gemSpatialQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                hash = GemSpatialHash.Build(gemEntities, gemTransforms, mapW, mapH, Allocator.Temp);
                nearby = new NativeList<int>(32, Allocator.Temp);
                seenScratch = new NativeHashSet<int>(32, Allocator.Temp);
            }

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
                    hash,
                    nearby,
                    seenScratch,
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
                    serverTick,
                    simHz);
            }

            // --- Clear ghost lock on gems no longer assigned to any ship ---
            ClearStaleTractorLocks();

            if (nearby.IsCreated)
                nearby.Dispose();
            if (seenScratch.IsCreated)
                seenScratch.Dispose();
            if (hash.IsCreated)
                hash.Dispose();
            if (gemEntities.IsCreated)
                gemEntities.Dispose();
            if (gemTransforms.IsCreated)
                gemTransforms.Dispose();

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
        /// Writes <see cref="GemMotionState"/> lock fields for assigned gems and applies pull
        /// velocity from every wing on that gem (primary + spare assists) after deploy finishes.
        /// Primary contributes full wing pull; each assist adds AssistPullScale (settings,
        /// default 25%) of its own strength so three equal beams ≈ 150% rather than 300%.
        /// <para>
        /// Lock + beam VFX start immediately. Pull waits until the thin extend line reaches
        /// the gem and the cone has widened (<see cref="GemTractorBeamMath.IsDeployPullReady"/>).
        /// Duration scales with wing→gem distance. During the wait the gem stays
        /// <see cref="GemMotionState.PhaseCoast"/> so burst damping still applies.
        /// <see cref="GemMotionState.PhaseTractor"/> is applied only after a non-zero pull
        /// velocity replaces <see cref="GemKinematics"/>.
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
            uint serverTick,
            int simHz)
        {
            if (_pairScratch.Count == 0)
                return;

            // --- Group pairs by gem (one ghost lock, possibly many wing forces) ---
            _primaryWingByGem.Clear();
            _wingsByGem.Clear();

            for (int i = 0; i < _pairScratch.Count; i++)
            {
                var pair = _pairScratch[i];
                if (!_wingsByGem.TryGetValue(pair.GemKey, out var wingList))
                {
                    wingList = new List<int>(4);
                    _wingsByGem[pair.GemKey] = wingList;
                }

                wingList.Add(pair.WingIndex);
                if (pair.IsPrimary || !_primaryWingByGem.ContainsKey(pair.GemKey))
                    _primaryWingByGem[pair.GemKey] = pair.WingIndex;
            }

            foreach (var kv in _wingsByGem)
            {
                if (!_gemEntityByKey.TryGetValue(kv.Key, out Entity gemEntity))
                    continue;
                if (!EntityManager.Exists(gemEntity) ||
                    !EntityManager.HasComponent<GemState>(gemEntity) ||
                    !EntityManager.HasComponent<LocalTransform>(gemEntity) ||
                    !EntityManager.HasComponent<GemKinematics>(gemEntity))
                    continue;

                var gemState = EntityManager.GetComponentData<GemState>(gemEntity);
                var gemTransform = EntityManager.GetComponentData<LocalTransform>(gemEntity);
                var gemKinematics = EntityManager.GetComponentData<GemKinematics>(gemEntity);
                var wingList = kv.Value;

                // Re-check self-pickup block (BuildAssignment already filtered; keep Apply safe).
                if (!PassesGemEligibility(gemState, shipNetworkId, nowServerTime))
                    continue;
                if (wingList == null || wingList.Count == 0)
                    continue;

                long pairKey = PairKey(shipEntity.Index, gemEntity.Index);
                if (!_deployByPair.TryGetValue(pairKey, out DeployState deploy))
                    continue;

                int primaryWing = _primaryWingByGem.TryGetValue(MakeGemKey(gemEntity), out int pw)
                    ? pw
                    : wingList[0];
                _gemsLockedThisFrame.Add(gemEntity.Index);
                _lockedEntitiesThisFrame.Add(gemEntity);

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
                    EntityManager.SetComponentData(gemEntity, motion);
                }

                // --- Wait for extend + widen before pull ---
                // Lock is live (beam VFX). Coast damping still runs. Pull starts when the
                // thin line has reached the gem and the cone has opened.
                uint lockTick = deploy.LockTick != 0 ? deploy.LockTick : serverTick;
                if (!GemTractorBeamMath.IsDeployPullReady(
                        lockTick, deploy.ExtendDuration, nowServerTime, simHz))
                    continue;

                // --- Stacked pull — after deploy ---
                var beamSettings = TractorBeamSettingsCache.ResolveOrDefault();
                float assistScale = beamSettings.AssistPullScale;
                float3 gemPos = gemTransform.Position;
                float3 velocity = float3.zero;
                float3 primaryTarget = ResolvePullTarget(shipTransform, wings, primaryWing);
                for (int wi = 0; wi < wingList.Count; wi++)
                {
                    int wingIndex = wingList[wi];
                    float pullSpeed = ResolveWingPullSpeed(
                        wingIndex, wings, shipLevel, inOrbit,
                        gemState.Value, gemState.Size);
                    bool isPrimary = wingIndex == primaryWing;
                    float stackScale = GemTractorBeamMath.StackedBeamPullScale(isPrimary, assistScale);
                    float3 pullTarget = ResolvePullTarget(shipTransform, wings, wingIndex);
                    float3 toWing = GemTractorBeamMath.ToroidalDirection(gemPos, pullTarget, mapW, mapH);
                    if (math.lengthsq(toWing) < 0.0001f)
                        continue;
                    velocity += toWing * (pullSpeed * stackScale);
                }

                float distToPrimary = GemTractorBeamMath.ToroidalDistance(
                    gemPos, primaryTarget, mapW, mapH);
                float absorbRadius = wings.Length > 0
                    ? GemCollectMath.ResolveWingCollectRadius(
                        beamSettings, gemState.Value, gemState.Size)
                    : GemCollectMath.ResolveHullCollectRadius(
                        beamSettings, gemState.Value, gemState.Size, shipTransform.Scale);

                if (math.lengthsq(velocity) < 0.0001f && distToPrimary > absorbRadius)
                {
                    float3 dir = GemTractorBeamMath.ToroidalDirection(
                        gemPos, primaryTarget, mapW, mapH);
                    if (math.lengthsq(dir) >= 0.0001f)
                        velocity = dir * GemTractorBeamMath.MinGameplayPullSpeed;
                }

                if (math.lengthsq(velocity) < 0.0001f)
                {
                    // Already on the absorb point — lock stays; pickup consumes this tick.
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

                gemKinematics.Velocity = velocity;
                EntityManager.SetComponentData(gemEntity, gemKinematics);

                if (EntityManager.HasComponent<GemMotionState>(gemEntity))
                {
                    var motion = EntityManager.GetComponentData<GemMotionState>(gemEntity);
                    motion.Phase = GemMotionState.PhaseTractor;
                    EntityManager.SetComponentData(gemEntity, motion);
                }
            }
        }

        /// <summary>
        /// Clears tractor ghost fields on gems that were locked last tick but not this tick
        /// (ineligible ship, out of range, consumed). Restores Coast so damping applies.
        /// Does not scan every gem on the map — only the previous lock set.
        /// </summary>
        void ClearStaleTractorLocks()
        {
            foreach (Entity entity in _lockedEntitiesCarry)
            {
                if (_lockedEntitiesThisFrame.Contains(entity))
                    continue;
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<GemMotionState>(entity))
                    continue;
                // Keep the lock on a scooped crystal until DestroyEntity so relevancy pin
                // still includes it while IsConsumed replicates.
                if (EntityManager.HasComponent<GemState>(entity) &&
                    EntityManager.GetComponentData<GemState>(entity).IsConsumed)
                    continue;

                var m = EntityManager.GetComponentData<GemMotionState>(entity);
                if (m.TractorShipId == 0 && m.Phase != GemMotionState.PhaseTractor)
                    continue;

                // --- Unlock → Coast (never leave PhaseTractor without a ship id) ---
                m.TractorShipId = 0;
                m.TractorWingIndex = 0;
                m.TractorLockTick = 0;
                m.TractorExtendDuration = 0f;
                if (m.Phase == GemMotionState.PhaseTractor)
                    m.Phase = GemMotionState.PhaseCoast;
                EntityManager.SetComponentData(entity, m);
            }

            _lockedEntitiesCarry.Clear();
            foreach (Entity entity in _lockedEntitiesThisFrame)
                _lockedEntitiesCarry.Add(entity);
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
        /// Scans gems within each wing's search radius via the spatial hash, then runs
        /// sticky/primary/assist matching. Fills <see cref="_pairScratch"/> for
        /// <see cref="ApplyLockAndPull"/>.
        /// </summary>
        void BuildAssignment(
            in GemSpatialHash hash,
            NativeList<int> nearby,
            NativeHashSet<int> seenScratch,
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
            _gemEntityByKey.Clear();

            int shipIndex = shipEntity.Index;
            int wingCount = wings.Length;
            if (wingCount <= 0)
            {
                _stickyLocksByShip.Remove(shipIndex);
                BuildFallbackAssignment(
                    hash, nearby, seenScratch,
                    shipEntity, shipNetworkId, nowServerTime, shipTransform, inOrbit, mapW, mapH, now,
                    serverTick, activePairs);
                return;
            }

            if (!_stickyLocksByShip.TryGetValue(shipIndex, out var stickyLocks))
            {
                stickyLocks = new Dictionary<int, long>(wingCount);
                _stickyLocksByShip[shipIndex] = stickyLocks;
            }

            if (!hash.IsCreated)
            {
                stickyLocks.Clear();
                return;
            }

            // --- Collect in-range wing↔gem samples (spatial cells, not every gem on the map) ---
            for (int wi = 0; wi < wingCount; wi++)
            {
                var wing = wings[wi];
                ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);
                hash.GatherNearby(wingPos, searchRadius, nearby, seenScratch);

                for (int n = 0; n < nearby.Length; n++)
                {
                    var entry = hash.Entries[nearby[n]];
                    Entity gemEntity = entry.Entity;
                    if (!EntityManager.Exists(gemEntity) || !EntityManager.HasComponent<GemState>(gemEntity))
                        continue;

                    var gemState = EntityManager.GetComponentData<GemState>(gemEntity);
                    if (!PassesGemEligibility(gemState, shipNetworkId, nowServerTime))
                        continue;

                    float dist = GemTractorBeamMath.ToroidalDistance(entry.Position, wingPos, mapW, mapH);
                    if (dist > searchRadius)
                        continue;

                    long gemKey = MakeGemKey(gemEntity);
                    _gemEntityByKey[gemKey] = gemEntity;
                    _candidateScratch.Add(new GemTractorBeamAssignment.Candidate
                    {
                        WingIndex = wi,
                        GemKey = gemKey,
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
                if (!_gemEntityByKey.TryGetValue(pair.GemKey, out Entity gemEntity))
                    continue;

                // One deploy clock per ship–gem (shared by primary + assists on that gem).
                if (!deployStarted.Add(pair.GemKey))
                    continue;

                long pairKey = PairKey(shipIndex, gemEntity.Index);
                activePairs.Add(pairKey);
                float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[pair.WingIndex]);
                float3 gemPos = EntityManager.GetComponentData<LocalTransform>(gemEntity).Position;
                EnsureDeployState(pairKey, wingPos, gemPos, mapW, mapH, now, serverTick);
            }
        }

        /// <summary>Ships without wing buffers pull the single closest gem to hull center.</summary>
        void BuildFallbackAssignment(
            in GemSpatialHash hash,
            NativeList<int> nearby,
            NativeHashSet<int> seenScratch,
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

            if (hash.IsCreated)
            {
                hash.GatherNearby(origin, searchRadius, nearby, seenScratch);
                for (int n = 0; n < nearby.Length; n++)
                {
                    var entry = hash.Entries[nearby[n]];
                    Entity gemEntity = entry.Entity;
                    if (!EntityManager.Exists(gemEntity) || !EntityManager.HasComponent<GemState>(gemEntity))
                        continue;

                    var gemState = EntityManager.GetComponentData<GemState>(gemEntity);
                    if (!PassesGemEligibility(gemState, shipNetworkId, nowServerTime))
                        continue;

                    float dist = GemTractorBeamMath.ToroidalDistance(entry.Position, origin, mapW, mapH);
                    if (dist > searchRadius)
                        continue;

                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closest = gemEntity;
                    }
                }
            }

            if (closest == Entity.Null)
                return;

            _gemEntityByKey[MakeGemKey(closest)] = closest;
            _pairScratch.Add(new GemTractorBeamAssignment.Pair
            {
                WingIndex = 0,
                GemKey = MakeGemKey(closest),
                IsPrimary = true,
            });

            long pairKey = PairKey(shipEntity.Index, closest.Index);
            activePairs.Add(pairKey);
            EnsureDeployState(
                pairKey, origin,
                EntityManager.GetComponentData<LocalTransform>(closest).Position,
                mapW, mapH, now, serverTick);
        }

        /// <summary>
        /// Starts the deploy clock for a new ship–gem pair. Extend duration scales with
        /// wing→gem distance so far crystals take longer for the thin line to arrive.
        /// Pull waits on this clock (<see cref="GemTractorBeamMath.IsDeployPullReady"/>).
        /// </summary>
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

        /// <summary>
        /// [TITAN-ORBIT] Ship cannot pull when dead, picking team, moon-docking, or at gem
        /// capacity. A living 0-HP hull (cargo still aboard) may still lock — dual-resource
        /// death is hull AND gems empty (see <see cref="ShipDamageLogic"/>).
        /// </summary>
        static bool IsShipEligibleForPull(in ShipState ship, in ShipMoonDockState moonDock)
        {
            // [TITAN-ORBIT] Do not treat 0 HP as dead. Grind/ram often zeroes hull while cargo
            // remains; blocking tractor there left beams on the client and no pull on the server.
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            if (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.01f)
                return false;
            if (ship.CurrentGems >= ship.GemCapacity - 0.001f)
                return false;
            return true;
        }

        /// <summary>
        /// Loose gem with value, not already depositing, and not in the source ship's
        /// damage-spill self-pickup penalty window. Does not read <c>IsBonusGem</c> or ship team —
        /// yellow extra-yield crystals tractor like red, including for enemy ships.
        /// </summary>
        static bool PassesGemEligibility(in GemState gem, int shipNetworkId, float nowServerTime) =>
            !gem.IsConsumed &&
            gem.Value > 0.001f &&
            gem.DepositTeam == TeamId.None &&
            !GemSelfPickupBlock.IsTractorBlockedForShip(gem, shipNetworkId, nowServerTime);

        static long MakeGemKey(Entity e) => ((long)(uint)e.Version << 32) | (uint)e.Index;

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
