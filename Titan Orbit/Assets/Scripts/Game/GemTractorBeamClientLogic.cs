using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client-side wing-to-gem assignment for tractor beam <em>visuals</em>.
    /// Mirrors server <see cref="GemTractorBeamAssignment"/> (sticky locks, primary fill, spare
    /// assists). Authoritative pull velocity still comes from ghosted <see cref="GemMotionState"/>.
    /// [TITAN-ORBIT] Gems come from hybrid proxies — never a full gem <c>ToEntityArray</c>.
    /// </summary>
    public static class GemTractorBeamClientLogic
    {
        /// <summary>Per-gem snapshot collected from hybrid proxies (quarantine-safe).</summary>
        public struct GemProxySnapshot
        {
            public Entity Entity;
            public GemState State;
            public LocalTransform Transform;
            public GemKinematics Kinematics;
        }

        static int _cacheFrame = -1;
        /// <summary>ship → gem → primary wing (for ghost-aligned tip / range checks).</summary>
        static readonly Dictionary<int, Dictionary<int, int>> PrimaryWingByShipAndGem =
            new Dictionary<int, Dictionary<int, int>>(32);
        /// <summary>ship → all wing↔gem pairs this frame (primary + assists) for multi-beam draw.</summary>
        static readonly Dictionary<int, List<GemTractorBeamAssignment.Pair>> PairsByShip =
            new Dictionary<int, List<GemTractorBeamAssignment.Pair>>(32);
        static readonly Dictionary<int, HashSet<int>> AssignedGemsByShip = new Dictionary<int, HashSet<int>>(32);
        /// <summary>Sticky wing→gem locks per ship — survives rotation until out of that wing's range.</summary>
        static readonly Dictionary<int, Dictionary<int, int>> StickyLocksByShip =
            new Dictionary<int, Dictionary<int, int>>(32);
        static readonly List<GemTractorBeamAssignment.Candidate> CandidateScratch =
            new List<GemTractorBeamAssignment.Candidate>(64);
        static readonly List<GemTractorBeamAssignment.Candidate> FilteredScratch =
            new List<GemTractorBeamAssignment.Candidate>(64);
        static readonly List<GemTractorBeamAssignment.Pair> PairScratch =
            new List<GemTractorBeamAssignment.Pair>(16);
        static readonly Dictionary<int, int> GemBeamCountScratch = new Dictionary<int, int>(32);
        static readonly List<Entity> ProxyEntityScratch = new List<Entity>(256);
        static readonly List<GemProxySnapshot> GemProxyScratch = new List<GemProxySnapshot>(64);

        /// <summary>
        /// Rebuilds wing→gem assignment once per Unity frame. Called from visibility tracker and beam drawer.
        /// </summary>
        public static void RebuildAssignmentCache()
        {
            // --- Rebuild cache ---
            // [STANDARD] Frame cache — assignment is O(ships×gems×wings); avoid rebuilding per beam draw.
            if (Time.frameCount == _cacheFrame)
                return;
            _cacheFrame = Time.frameCount;

            PrimaryWingByShipAndGem.Clear();
            PairsByShip.Clear();
            AssignedGemsByShip.Clear();
            // StickyLocksByShip intentionally persists across frames (unlocked only when out of range).

            // [TITAN-ORBIT] Skip while ShouldSkipShipEntityQueries (Settling / GhostSpawnBacklog /
            // post–TeamChoice hold). TransformQuarantine stays ON all session and must NOT suppress
            // beams (gems from hybrid proxies). Hand-rolled Settling||GhostSpawnBacklog missed the
            // TeamChoice hold → ship ToEntityArray Crash!!! (Player.log 2026-07-23).
            // See titan-orbit-teamchoice-crash-hardstop.mdc.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            // --- Rolled map period (never invent 1000) ---
            // [TITAN-ORBIT] Wrong period → wrap-tile assignment fails while main-tile still works.
            if (!ToroidalDisplay.ResolveMapSize(em, out float mapW, out float mapH))
                return;

            // --- Ships: tiny query, but still unsafe during GhostSpawnBacklog Instantiates ---
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<ShipOrbitState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Unity.Collections.Allocator.Temp);
            using var shipOrbits = shipQuery.ToComponentDataArray<ShipOrbitState>(Unity.Collections.Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            // --- Gems: hybrid proxy registry only (never ToEntityArray all gems) ---
            CollectGemProxies(em, GemProxyScratch);
            if (GemProxyScratch.Count == 0)
                return;

            var liveShipIndices = new HashSet<int>(ships.Length);
            for (int si = 0; si < ships.Length; si++)
            {
                liveShipIndices.Add(ships[si].Index);
                if (!IsShipEligibleForBeam(shipStates[si]))
                {
                    StickyLocksByShip.Remove(ships[si].Index);
                    continue;
                }

                var wings = em.HasBuffer<ShipWingTractorBeamElement>(ships[si])
                    ? em.GetBuffer<ShipWingTractorBeamElement>(ships[si])
                    : default;

                BuildForShip(
                    ships[si].Index,
                    shipStates[si],
                    shipOrbits[si].InOrbitRing,
                    shipTransforms[si],
                    wings,
                    GemProxyScratch,
                    mapW,
                    mapH);
            }

            // Drop sticky state for ships that left the world (entity index reuse safety).
            if (StickyLocksByShip.Count > liveShipIndices.Count)
            {
                var stale = new List<int>(4);
                foreach (var kv in StickyLocksByShip)
                {
                    if (!liveShipIndices.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                    StickyLocksByShip.Remove(stale[i]);
            }

        }

        /// <summary>
        /// Fills <paramref name="dst"/> with Instantiated gem entities for tractor VFX.
        /// Prefers <see cref="GemClientEntityRegistry"/> (Instantiates hook) so beams work before
        /// the GO proxy finishes; also merges hybrid proxy dictionary entities.
        /// Per-entity HasComponent only — never a full gem <c>ToEntityArray</c>.
        /// </summary>
        public static void CollectGemProxies(EntityManager em, List<GemProxySnapshot> dst)
        {
            dst.Clear();
            var seen = new HashSet<int>();

            // --- Path A: Instantiates registry (available as soon as GhostSpawn Instantiates) ---
            GemClientEntityRegistry.CopyLive(ProxyEntityScratch);
            AppendGemSnapshots(em, ProxyEntityScratch, dst, seen);

            // --- Path B: hybrid GO proxies (covers gems that somehow skipped the registry) ---
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null)
            {
                visualizer.CopyLiveProxyEntities(ProxyEntityScratch);
                AppendGemSnapshots(em, ProxyEntityScratch, dst, seen);
            }
        }

        /// <summary>Appends eligible gem snapshots from a candidate entity list (deduped by index).</summary>
        static void AppendGemSnapshots(
            EntityManager em,
            List<Entity> candidates,
            List<GemProxySnapshot> dst,
            HashSet<int> seen)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity entity = candidates[i];
                if (!em.Exists(entity) || seen.Contains(entity.Index))
                    continue;
                // Per-entity checks — not GatherEntitiesWithoutFilter over GemTag.
                if (!em.HasComponent<GemTag>(entity) ||
                    !em.HasComponent<GemState>(entity) ||
                    !em.HasComponent<LocalTransform>(entity))
                    continue;

                var state = em.GetComponentData<GemState>(entity);
                if (!IsGemEligibleForBeam(state))
                    continue;

                seen.Add(entity.Index);
                var kinematics = em.HasComponent<GemKinematics>(entity)
                    ? em.GetComponentData<GemKinematics>(entity)
                    : default;

                dst.Add(new GemProxySnapshot
                {
                    Entity = entity,
                    State = state,
                    Transform = em.GetComponentData<LocalTransform>(entity),
                    Kinematics = kinematics,
                });
            }
        }

        static void BuildForShip(
            int shipIndex,
            in ShipState shipState,
            bool inOrbit,
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            List<GemProxySnapshot> gems,
            float mapW,
            float mapH)
        {
            CandidateScratch.Clear();
            int shipLevel = math.max(1, shipState.ShipLevel);

            if (wings.IsCreated && wings.Length > 0)
            {
                // --- Collect in-range wing↔gem samples ---
                for (int wi = 0; wi < wings.Length; wi++)
                {
                    var wing = wings[wi];
                    ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);

                    for (int gi = 0; gi < gems.Count; gi++)
                    {
                        float3 gemPos = gems[gi].Transform.Position;
                        float dist = GemTractorBeamMath.ToroidalDistance(gemPos, wingPos, mapW, mapH);
                        if (dist > searchRadius)
                            continue;

                        CandidateScratch.Add(new GemTractorBeamAssignment.Candidate
                        {
                            GemId = gems[gi].Entity.Index,
                            WingIndex = wi,
                            Dist = dist,
                        });
                    }
                }
            }
            else
            {
                // --- No wings: single hull-center beam (legacy fallback) ---
                StickyLocksByShip.Remove(shipIndex);
                GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out float searchRadius, out _);
                float3 origin = shipTransform.Position;
                int closestGemIndex = -1;
                float closestDist = float.MaxValue;

                for (int gi = 0; gi < gems.Count; gi++)
                {
                    float3 gemPos = gems[gi].Transform.Position;
                    float dist = GemTractorBeamMath.ToroidalDistance(gemPos, origin, mapW, mapH);
                    if (dist > searchRadius)
                        continue;

                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestGemIndex = gems[gi].Entity.Index;
                    }
                }

                if (closestGemIndex >= 0)
                {
                    var pair = new GemTractorBeamAssignment.Pair
                    {
                        WingIndex = 0,
                        GemId = closestGemIndex,
                        IsPrimary = true,
                    };
                    PairsByShip[shipIndex] = new List<GemTractorBeamAssignment.Pair>(1) { pair };
                    PrimaryWingByShipAndGem[shipIndex] = new Dictionary<int, int>(1)
                    {
                        [closestGemIndex] = 0,
                    };
                    AssignedGemsByShip[shipIndex] = new HashSet<int> { closestGemIndex };
                }

                return;
            }

            if (CandidateScratch.Count == 0)
            {
                StickyLocksByShip.Remove(shipIndex);
                return;
            }

            if (!StickyLocksByShip.TryGetValue(shipIndex, out var stickyLocks))
            {
                stickyLocks = new Dictionary<int, int>(wings.Length);
                StickyLocksByShip[shipIndex] = stickyLocks;
            }

            // --- Same sticky / primary / assist matching as server ---
            GemTractorBeamAssignment.AssignWings(
                CandidateScratch,
                wings.Length,
                stickyLocks,
                PairScratch,
                FilteredScratch,
                GemBeamCountScratch);

            if (PairScratch.Count == 0)
                return;

            var pairs = new List<GemTractorBeamAssignment.Pair>(PairScratch.Count);
            var primaryMap = new Dictionary<int, int>(PairScratch.Count);
            var assignedGemIds = new HashSet<int>(PairScratch.Count);
            for (int i = 0; i < PairScratch.Count; i++)
            {
                var pair = PairScratch[i];
                pairs.Add(pair);
                assignedGemIds.Add(pair.GemId);
                if (pair.IsPrimary || !primaryMap.ContainsKey(pair.GemId))
                    primaryMap[pair.GemId] = pair.WingIndex;
            }

            PairsByShip[shipIndex] = pairs;
            PrimaryWingByShipAndGem[shipIndex] = primaryMap;
            AssignedGemsByShip[shipIndex] = assignedGemIds;
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

            int shipLevel = math.max(1, shipState.ShipLevel);
            bool inOrbit = TryGetInOrbit(em, shipEntity);

            // Any locked wing (primary or assist) still in its own radius keeps the gem active.
            if (PairsByShip.TryGetValue(shipEntity.Index, out var pairs) && pairs != null && wings.IsCreated)
            {
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (pairs[i].GemId != gemEntity.Index)
                        continue;
                    int wingIndex = pairs[i].WingIndex;
                    if (wingIndex < 0 || wingIndex >= wings.Length)
                        continue;
                    var wing = wings[wingIndex];
                    ShipWingTractorBeamPose.GetTractorParams(wing, shipLevel, inOrbit, out float searchRadius, out _);
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wing);
                    if (GemTractorBeamMath.IsWithinReach(gemTransform.Position, wingPos, searchRadius, mapW, mapH))
                        return true;
                }

                return false;
            }

            if (!PrimaryWingByShipAndGem.TryGetValue(shipEntity.Index, out var gemToWing) ||
                !gemToWing.TryGetValue(gemEntity.Index, out int primaryWing))
                return false;

            if (wings.IsCreated && primaryWing >= 0 && primaryWing < wings.Length)
            {
                var wing = wings[primaryWing];
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
            if (!IsShipEligibleForBeam(shipState))
                return false;
            if (em.HasComponent<GemState>(gemEntity) && !IsGemEligibleForBeam(em.GetComponentData<GemState>(gemEntity)))
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
            if (TryGetAssignedWingIndex(shipEntity.Index, gemEntity.Index, out int wingIndex))
                return ResolveBeamOriginForWing(shipTransform, wings, wingIndex);

            return shipTransform.Position;
        }

        /// <summary>Logical world origin for a specific wing buffer index (primary or assist).</summary>
        public static float3 ResolveBeamOriginForWing(
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            int wingIndex)
        {
            if (wings.IsCreated && wingIndex >= 0 && wingIndex < wings.Length)
                return ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wingIndex]);
            return shipTransform.Position;
        }

        /// <summary>
        /// Returns the <b>primary</b> wing for this ship→gem after
        /// <see cref="RebuildAssignmentCache"/> (ghost TractorWingIndex / single-tip helpers).
        /// </summary>
        public static bool TryGetAssignedWingIndex(int shipIndex, int gemIndex, out int wingIndex)
        {
            RebuildAssignmentCache();
            wingIndex = -1;
            return PrimaryWingByShipAndGem.TryGetValue(shipIndex, out var gemToWing) &&
                   gemToWing.TryGetValue(gemIndex, out wingIndex);
        }

        /// <summary>
        /// All wing↔gem pairs for a ship this frame (primary + spare assists) for multi-beam draw.
        /// </summary>
        public static bool TryGetShipBeamPairs(int shipIndex, out List<GemTractorBeamAssignment.Pair> pairs)
        {
            RebuildAssignmentCache();
            return PairsByShip.TryGetValue(shipIndex, out pairs) && pairs != null && pairs.Count > 0;
        }

        /// <summary>
        /// [HYBRID] Pull velocity from <b>ghosted</b> <see cref="GemMotionState"/> lock only.
        /// Used by <see cref="GemClientMotionApplier"/> — never invents wing assignment.
        /// </summary>
        /// <param name="gemEntity">Gem ghost (for value/size mass feel).</param>
        /// <param name="motion">Ghost motion/lock sample for this gem.</param>
        /// <param name="gemLogicalPos">Current logical gem pose (usually ghost LocalTransform).</param>
        /// <param name="pullVelocity">World XZ pull velocity toward the locked wing tip.</param>
        /// <returns>True when lock is valid and a pull direction could be resolved.</returns>
        public static bool TryGetPullVelocityFromGhostLock(
            Entity gemEntity,
            in GemMotionState motion,
            float3 gemLogicalPos,
            out float3 pullVelocity)
        {
            pullVelocity = float3.zero;
            if (motion.Phase != GemMotionState.PhaseTractor || motion.TractorShipId == 0)
                return false;

            // Deploy must be complete on the shared ServerTick clock (same numbers as server).
            if (!GemTractorBeamDeployTracker.IsPullPhysicsActiveFromGhostLock(motion))
                return false;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!TryFindShipEntityByNetworkId(em, motion.TractorShipId, out Entity shipEntity))
                return false;
            if (!em.HasComponent<ShipState>(shipEntity) || !em.HasComponent<LocalTransform>(shipEntity))
                return false;

            var shipState = em.GetComponentData<ShipState>(shipEntity);
            if (!IsShipEligibleForBeam(shipState))
                return false;

            var shipTransform = em.GetComponentData<LocalTransform>(shipEntity);
            var wings = em.HasBuffer<ShipWingTractorBeamElement>(shipEntity)
                ? em.GetBuffer<ShipWingTractorBeamElement>(shipEntity)
                : default;

            int shipLevel = math.max(1, shipState.ShipLevel);
            bool inOrbit = TryGetInOrbit(em, shipEntity);
            int wingIndex = motion.TractorWingIndex;

            float wingAttraction;
            float3 pullTarget;
            if (wings.IsCreated && wingIndex >= 0 && wingIndex < wings.Length)
            {
                ShipWingTractorBeamPose.GetTractorParams(
                    wings[wingIndex], shipLevel, inOrbit, out _, out wingAttraction);
                pullTarget = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wingIndex]);
            }
            else
            {
                GemTractorBeamMath.GetTractorBeamFromMaxGems(8f, inOrbit, out _, out wingAttraction);
                pullTarget = shipTransform.Position;
            }

            float gemValue = 1f;
            float gemSize = 0f;
            if (em.Exists(gemEntity) && em.HasComponent<GemState>(gemEntity))
            {
                var gemState = em.GetComponentData<GemState>(gemEntity);
                gemValue = gemState.Value;
                gemSize = gemState.Size;
            }

            float mapW;
            float mapH;
            if (!ToroidalDisplay.ResolveMapSize(em, out mapW, out mapH))
                return false;
            // --- Stack assist wings when local assignment matches this ghost lock ---
            // [TITAN-ORBIT] Mirror server diminishing stack: primary 100%, each assist 25% of its
            // own pull (GemTractorBeamMath.StackedBeamPullScale) so GO motion matches authority.
            float3 velocity = float3.zero;
            int shipIndex = shipEntity.Index;
            RebuildAssignmentCache();
            if (PairsByShip.TryGetValue(shipIndex, out var pairs) && pairs != null)
            {
                bool any = false;
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (pairs[i].GemId != gemEntity.Index)
                        continue;

                    int wi = pairs[i].WingIndex;
                    float attract;
                    float3 target;
                    if (wings.IsCreated && wi >= 0 && wi < wings.Length)
                    {
                        ShipWingTractorBeamPose.GetTractorParams(
                            wings[wi], shipLevel, inOrbit, out _, out attract);
                        target = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wi]);
                    }
                    else
                    {
                        attract = wingAttraction;
                        target = pullTarget;
                    }

                    float speed = GemTractorBeamMath.ResolvePullSpeedFromWing(attract, gemValue, gemSize);
                    // Ghost TractorWingIndex is the authoritative primary (same as server pull math).
                    float stackScale = GemTractorBeamMath.StackedBeamPullScale(wi == wingIndex);
                    float3 toWing = GemTractorBeamMath.ToroidalDirection(gemLogicalPos, target, mapW, mapH);
                    if (math.lengthsq(toWing) < 0.0001f)
                        continue;
                    velocity += toWing * (speed * stackScale);
                    any = true;
                }

                if (any)
                {
                    pullVelocity = velocity;
                    return math.lengthsq(pullVelocity) > 0.0001f;
                }
            }

            // --- Fallback: ghost primary wing only ---
            float pullSpeed = GemTractorBeamMath.ResolvePullSpeedFromWing(wingAttraction, gemValue, gemSize);
            float3 toPrimary = GemTractorBeamMath.ToroidalDirection(gemLogicalPos, pullTarget, mapW, mapH);
            if (math.lengthsq(toPrimary) < 0.0001f)
                return false;

            pullVelocity = toPrimary * pullSpeed;
            return true;
        }

        /// <summary>
        /// [LEGACY] Prefer ghost lock. Do not invent local assignment for GO kinematics.
        /// </summary>
        public static bool TryGetClientPullVelocity(Entity gemEntity, float3 gemLogicalPos, out float3 pullVelocity)
        {
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world != null && world.IsCreated)
            {
                var em = world.EntityManager;
                if (em.Exists(gemEntity) && em.HasComponent<GemMotionState>(gemEntity))
                {
                    var motion = em.GetComponentData<GemMotionState>(gemEntity);
                    if (TryGetPullVelocityFromGhostLock(gemEntity, motion, gemLogicalPos, out pullVelocity))
                        return true;
                }
            }

            pullVelocity = float3.zero;
            return false;
        }

        /// <summary>Resolves a ship ghost by <see cref="GhostOwner.NetworkId"/>.</summary>
        public static bool TryFindShipEntityByNetworkId(EntityManager em, int networkId, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (networkId == 0)
                return false;

            // Tiny ship query — caller must already gate GhostSpawnBacklog.
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Unity.Collections.Allocator.Temp);
            for (int i = 0; i < ships.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                shipEntity = ships[i];
                return true;
            }

            return false;
        }

        /// <summary>Resolves a ship entity from the index used as the assignment-cache key.</summary>
        static bool TryFindShipEntityByIndex(EntityManager em, int shipIndex, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            using var shipQuery = em.CreateEntityQuery(ComponentType.ReadOnly<ShipTag>());
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (int i = 0; i < ships.Length; i++)
            {
                if (ships[i].Index != shipIndex)
                    continue;
                shipEntity = ships[i];
                return true;
            }

            return false;
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
            // --- IsShipEligibleForBeam ---
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            if (ship.CurrentGems >= ship.GemCapacity)
                return false;
            return true;
        }

        public static bool IsGemEligibleForBeam(in GemState gem) =>
            gem.Value > 0.001f && gem.DepositTeam == TeamId.None;

        static bool TryGetInOrbit(EntityManager em, Entity shipEntity) =>
            em.HasComponent<ShipOrbitState>(shipEntity) &&
            em.GetComponentData<ShipOrbitState>(shipEntity).InOrbitRing;

        public static void Clear()
        {
            // --- Clear state ---
            PrimaryWingByShipAndGem.Clear();
            PairsByShip.Clear();
            AssignedGemsByShip.Clear();
            StickyLocksByShip.Clear();
            GemProxyScratch.Clear();
            ProxyEntityScratch.Clear();
            _cacheFrame = -1;
        }
    }
}
