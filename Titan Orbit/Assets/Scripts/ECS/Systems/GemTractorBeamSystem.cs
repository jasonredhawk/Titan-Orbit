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
    /// Server-only gem tractor beam. Assigns gems in wing search radii, then commits the
    /// capture on that same tick: cargo is credited, the crystal is destroyed, and one
    /// <see cref="GemTractorLockRpc"/> with <see cref="GemMotionState.PhaseCommitted"/> plus
    /// <see cref="GemConsumedRpc"/> tell clients to play the suck-in. There is no world-space
    /// pull chase, so ship speed cannot drag a locked gem or drop the capture.
    /// Matching uses <see cref="GemTractorBeamAssignment"/> + <see cref="TractorBeamSettings"/>.
    /// Nearby gem scans use <see cref="GemSpatialHash"/> so ships do not walk every gem.
    /// Each wing holds one gem until that beam's extend, cone, and suck-in finish, then
    /// may lock the next crystal. Clients must not invent a second wing assignment.
    /// Flying over a gem that never locked still uses <see cref="GemPickupSystem"/>.
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
        /// <summary>Entities locked this tick (for unlock when a ship becomes ineligible).</summary>
        readonly HashSet<Entity> _lockedEntitiesThisFrame = new HashSet<Entity>();

        /// <summary>Entities that had a tractor lock last tick — only these need unlock scans.</summary>
        readonly HashSet<Entity> _lockedEntitiesCarry = new HashSet<Entity>();

        /// <summary>Live gems for the spatial hash (built once per tick).</summary>
        EntityQuery _gemSpatialQuery;

        /// <summary>Ships that can commit a tractor capture. Snapshotted so cargo writes are safe.</summary>
        EntityQuery _shipQuery;
        /// <summary>
        /// Sticky wing→gem locks per ship entity index. Survives ship rotation until the gem
        /// leaves that wing's search radius. When TractorBeamSettings.PrimaryStickyOnly is on,
        /// only primary pairs are stored here (assists re-match every tick).
        /// </summary>
        readonly Dictionary<int, Dictionary<int, long>> _stickyLocksByShip =
            new Dictionary<int, Dictionary<int, long>>(16);
        /// <summary>
        /// Wing is busy until this server-clock time. One capture per wing; the next gem
        /// waits until the beam animation for the current gem has finished.
        /// </summary>
        readonly Dictionary<int, Dictionary<int, float>> _wingBusyUntilByShip =
            new Dictionary<int, Dictionary<int, float>>(16);
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
            _shipQuery = GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<ShipOrbitState>(),
                ComponentType.ReadOnly<ShipMoonDockState>());
        }

        /// <summary>Assigns gems and commits cargo on the first lock. No pull-velocity chase.</summary>
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

            var rpcEcb = new EntityCommandBuffer(Allocator.Temp);

            _activePairsScratch.Clear();
            var activePairs = _activePairsScratch;
            _lockedEntitiesThisFrame.Clear();

            // --- Same timeline as GemState.SpawnServerTime / self-pickup block stamps ---
            float nowServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                EntityManager, now);

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

            // --- Per ship: assign gems, commit cargo on first lock (snapshot so writes are safe) ---
            if (!_shipQuery.IsEmptyIgnoreFilter)
            {
                var shipEntities = _shipQuery.ToEntityArray(Allocator.Temp);
                for (int s = 0; s < shipEntities.Length; s++)
                {
                    Entity shipEntity = shipEntities[s];
                    var shipState = EntityManager.GetComponentData<ShipState>(shipEntity);
                    var moonDock = EntityManager.GetComponentData<ShipMoonDockState>(shipEntity);
                    if (!IsShipEligibleForPull(shipState, moonDock))
                    {
                        _stickyLocksByShip.Remove(shipEntity.Index);
                        _wingBusyUntilByShip.Remove(shipEntity.Index);
                        continue;
                    }

                    int shipNetworkId = 0;
                    if (EntityManager.HasComponent<GhostOwner>(shipEntity))
                        shipNetworkId = EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;
                    if (shipNetworkId == 0)
                    {
                        _stickyLocksByShip.Remove(shipEntity.Index);
                        _wingBusyUntilByShip.Remove(shipEntity.Index);
                        continue;
                    }

                    // [TITAN-ORBIT] Orbit ring widens tractor search radius via GemTractorBeamMath.
                    var shipTransform = EntityManager.GetComponentData<LocalTransform>(shipEntity);
                    bool inOrbit = EntityManager.GetComponentData<ShipOrbitState>(shipEntity).InOrbitRing;
                    int shipLevel = math.max(1, shipState.ShipLevel);
                    var wings = EntityManager.GetBuffer<ShipWingTractorBeamElement>(shipEntity);
                    BuildAssignment(
                        hash,
                        nearby,
                        seenScratch,
                        shipEntity,
                        shipNetworkId,
                        nowServerTime,
                        shipTransform,
                        inOrbit,
                        shipLevel,
                        wings,
                        mapW,
                        mapH,
                        now,
                        serverTick,
                        activePairs);

                    CommitLockedGems(
                        ref rpcEcb,
                        shipEntity,
                        shipNetworkId,
                        nowServerTime,
                        serverTick);
                }

                shipEntities.Dispose();
            }

            // --- Clear lock on gems no longer assigned to any ship ---
            ClearStaleTractorLocks(ref rpcEcb);

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

            rpcEcb.Playback(EntityManager);
            rpcEcb.Dispose();
        }

        /// <summary>
        /// Credits cargo and destroys every gem assigned to this ship this tick.
        /// The lock is the capture: no pull velocity, no later scoop test.
        /// A second ship later in this tick sees <see cref="GemState.IsConsumed"/> and skips.
        /// Gems that do not fit in the remaining hold are left unlocked.
        /// </summary>
        void CommitLockedGems(
            ref EntityCommandBuffer rpcEcb,
            Entity shipEntity,
            int shipNetworkId,
            float nowServerTime,
            uint serverTick)
        {
            if (_pairScratch.Count == 0)
                return;
            if (!EntityManager.HasComponent<ShipState>(shipEntity))
                return;

            // --- Group pairs by gem (one capture, primary wing is the beam target) ---
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

            var ship = EntityManager.GetComponentData<ShipState>(shipEntity);
            float capacityLeft = ship.GemCapacity - ship.CurrentGems;
            bool cargoChanged = false;

            foreach (var kv in _wingsByGem)
            {
                if (capacityLeft <= 0.001f)
                    break;

                if (!_gemEntityByKey.TryGetValue(kv.Key, out Entity gemEntity))
                    continue;
                if (!EntityManager.Exists(gemEntity) ||
                    !EntityManager.HasComponent<GemState>(gemEntity) ||
                    !EntityManager.HasComponent<LocalTransform>(gemEntity))
                    continue;

                var gemState = EntityManager.GetComponentData<GemState>(gemEntity);
                var wingList = kv.Value;

                // Re-check self-pickup block (BuildAssignment already filtered; keep commit safe).
                if (!PassesGemEligibility(gemState, shipNetworkId, nowServerTime))
                    continue;
                if (wingList == null || wingList.Count == 0)
                    continue;

                long pairKey = PairKey(shipEntity.Index, gemEntity.Index);
                if (!_deployByPair.TryGetValue(pairKey, out DeployState deploy))
                    continue;

                // Whole crystal, even when cargo cannot hold it. Same waste rule as pickup.
                float take = math.min(gemState.Value, capacityLeft);
                if (take <= 0.001f)
                    break;

                int primaryWing = _primaryWingByGem.TryGetValue(MakeGemKey(gemEntity), out int pw)
                    ? pw
                    : wingList[0];

                // One beam per wing until this gem's extend, cone, and suck-in finish.
                float hold = GemTractorBeamMath.ComputeCaptureHoldSeconds(deploy.ExtendDuration);
                float busyUntil = nowServerTime + hold;
                for (int wi = 0; wi < wingList.Count; wi++)
                    MarkWingBusy(shipEntity.Index, wingList[wi], busyUntil);

                ship.CurrentGems = math.min(ship.GemCapacity, ship.CurrentGems + take);
                capacityLeft = ship.GemCapacity - ship.CurrentGems;
                cargoChanged = true;

                // Mark before the next ship in this tick runs assignment.
                gemState.IsConsumed = true;
                EntityManager.SetComponentData(gemEntity, gemState);

                var motion = EntityManager.HasComponent<GemMotionState>(gemEntity)
                    ? EntityManager.GetComponentData<GemMotionState>(gemEntity)
                    : default;
                motion.TractorShipId = shipNetworkId;
                motion.TractorWingIndex = (byte)math.clamp(primaryWing, 0, 255);
                motion.TractorLockTick = deploy.LockTick != 0 ? deploy.LockTick : serverTick;
                motion.TractorExtendDuration = deploy.ExtendDuration;
                motion.Phase = GemMotionState.PhaseCommitted;
                if (EntityManager.HasComponent<GemMotionState>(gemEntity))
                    EntityManager.SetComponentData(gemEntity, motion);

                NotifyTractorLock(ref rpcEcb, gemEntity, motion);
                GemNetNotify.SendConsumed(
                    ref rpcEcb,
                    gemState.SpawnId,
                    EntityManager.GetComponentData<LocalTransform>(gemEntity).Position,
                    shipNetworkId,
                    gemState.Tint,
                    take);
                rpcEcb.DestroyEntity(gemEntity);
            }

            if (cargoChanged)
                EntityManager.SetComponentData(shipEntity, ship);
        }

        /// <summary>
        /// Clears tractor ghost fields on gems that were locked last tick but not this tick
        /// (ineligible ship, out of range, consumed). Restores Coast so damping applies.
        /// Does not scan every gem on the map — only the previous lock set.
        /// </summary>
        void ClearStaleTractorLocks(ref EntityCommandBuffer rpcEcb)
        {
            foreach (Entity entity in _lockedEntitiesCarry)
            {
                if (_lockedEntitiesThisFrame.Contains(entity))
                    continue;
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<GemMotionState>(entity))
                    continue;
                if (EntityManager.HasComponent<GemState>(entity) &&
                    EntityManager.GetComponentData<GemState>(entity).IsConsumed)
                    continue;

                var m = EntityManager.GetComponentData<GemMotionState>(entity);
                // Committed captures are already consumed. Never unlock them.
                if (m.Phase == GemMotionState.PhaseCommitted)
                    continue;
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
                NotifyTractorLock(ref rpcEcb, entity, m);
            }

            _lockedEntitiesCarry.Clear();
            foreach (Entity entity in _lockedEntitiesThisFrame)
                _lockedEntitiesCarry.Add(entity);
        }

        /// <summary>Broadcasts a lock/unlock so clients present the same beam without gem ghosts.</summary>
        void NotifyTractorLock(ref EntityCommandBuffer rpcEcb, Entity gemEntity, in GemMotionState motion)
        {
            if (!EntityManager.HasComponent<GemState>(gemEntity))
                return;

            int spawnId = EntityManager.GetComponentData<GemState>(gemEntity).SpawnId;
            if (spawnId == 0)
                return;

            float3 pos = float3.zero;
            if (EntityManager.HasComponent<LocalTransform>(gemEntity))
                pos = EntityManager.GetComponentData<LocalTransform>(gemEntity).Position;

            GemNetNotify.SendTractorLock(ref rpcEcb, new GemTractorLockRpc
            {
                SpawnId = spawnId,
                TractorShipId = motion.TractorShipId,
                TractorWingIndex = motion.TractorWingIndex,
                TractorLockTick = motion.TractorLockTick,
                TractorExtendDuration = motion.TractorExtendDuration,
                Phase = motion.Phase,
            }, pos);
        }

        /// <summary>
        /// Scans gems within each wing's search radius via the spatial hash, then runs
        /// sticky/primary/assist matching. Fills <see cref="_pairScratch"/> for
        /// <see cref="CommitLockedGems"/>.
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
                // This wing is still playing the previous capture.
                if (IsWingBusy(shipIndex, wi, nowServerTime))
                    continue;

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
            // Hull has one beam. Wait until the previous capture animation finishes.
            if (IsWingBusy(shipEntity.Index, 0, nowServerTime))
                return;

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
        /// wing→gem distance and is the client's suck-in length. Capture does not wait on it.
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
        /// Loose gem with value, not already depositing, and not blocked for this ship
        /// (own damage-spill penalty, or another ship's asteroid-destroy first dibs).
        /// Does not read <c>IsBonusGem</c> or ship team — yellow extra-yield crystals
        /// tractor like red once the window allows this hull.
        /// </summary>
        static bool PassesGemEligibility(in GemState gem, int shipNetworkId, float nowServerTime) =>
            !gem.IsConsumed &&
            gem.Value > 0.001f &&
            gem.DepositTeam == TeamId.None &&
            !GemSelfPickupBlock.IsTractorBlockedForShip(gem, shipNetworkId, nowServerTime);

        bool IsWingBusy(int shipIndex, int wingIndex, float nowServerTime)
        {
            if (!_wingBusyUntilByShip.TryGetValue(shipIndex, out var wings))
                return false;
            if (!wings.TryGetValue(wingIndex, out float until))
                return false;
            if (nowServerTime < until)
                return true;
            wings.Remove(wingIndex);
            if (wings.Count == 0)
                _wingBusyUntilByShip.Remove(shipIndex);
            return false;
        }

        void MarkWingBusy(int shipIndex, int wingIndex, float busyUntil)
        {
            if (!_wingBusyUntilByShip.TryGetValue(shipIndex, out var wings))
            {
                wings = new Dictionary<int, float>(4);
                _wingBusyUntilByShip[shipIndex] = wings;
            }

            if (!wings.TryGetValue(wingIndex, out float prev) || busyUntil > prev)
                wings[wingIndex] = busyUntil;
        }

        static long MakeGemKey(Entity e) => ((long)(uint)e.Version << 32) | (uint)e.Index;

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
