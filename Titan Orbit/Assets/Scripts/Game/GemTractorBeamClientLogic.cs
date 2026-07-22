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
    /// <summary>
    /// [HYBRID] Client-side wing-to-gem assignment and eligibility for tractor beam <em>visuals</em> only.
    /// Uses the same <see cref="GemTractorBeamAssignment"/> rules as server
    /// <c>GemTractorBeamSystem</c> (nearest wing owns each gem; one gem per wing). Rebuilds a
    /// per-frame cache keyed by ship/gem entity indices. Authoritative pull stays on the server.
    /// [TITAN-ORBIT] Gems come from <see cref="EcsWorldVisualizer"/> hybrid proxies (managed dictionary),
    /// never a full gem <c>ToEntityArray</c> — required under session-long TransformQuarantine
    /// (same pattern as minimap). Ships stay a tiny query.
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
        static readonly Dictionary<int, Dictionary<int, int>> WingByShipAndGem = new Dictionary<int, Dictionary<int, int>>(32);
        static readonly Dictionary<int, HashSet<int>> AssignedGemsByShip = new Dictionary<int, HashSet<int>>(32);
        static readonly List<GemTractorBeamAssignment.Candidate> CandidateScratch =
            new List<GemTractorBeamAssignment.Candidate>(64);
        static readonly List<GemTractorBeamAssignment.Candidate> FilteredScratch =
            new List<GemTractorBeamAssignment.Candidate>(64);
        static readonly List<GemTractorBeamAssignment.Pair> PairScratch =
            new List<GemTractorBeamAssignment.Pair>(16);
        static readonly Dictionary<int, float> NearestDistScratch = new Dictionary<int, float>(32);
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

            WingByShipAndGem.Clear();
            AssignedGemsByShip.Clear();

            // [TITAN-ORBIT] Skip while Settling OR GhostSpawnBacklog.
            // TransformQuarantine stays ON all session on Windows and must NOT suppress beams
            // (gems come from hybrid proxies — no full gem gather). But ship ToEntityArray during
            // post–Join Team Instantiates (Settling OFF, GhostSpawnBacklog ON) → Crash!!!
            // See titan-orbit-windows-join-crash.mdc #11 / 2026-07-19 TeamChoiceResult.
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;

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
                    GemProxyScratch,
                    mapW,
                    mapH);
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

            // --- Same exclusive matching as server GemTractorBeamSystem ---
            int wingCount = wings.Length;
            GemTractorBeamAssignment.AssignOneGemPerWing(
                CandidateScratch,
                wingCount,
                PairScratch,
                NearestDistScratch,
                FilteredScratch);

            if (PairScratch.Count == 0)
                return;

            var gemToWingMap = new Dictionary<int, int>(PairScratch.Count);
            var assignedGemIds = new HashSet<int>(PairScratch.Count);
            for (int i = 0; i < PairScratch.Count; i++)
            {
                var pair = PairScratch[i];
                gemToWingMap[pair.GemId] = pair.WingIndex;
                assignedGemIds.Add(pair.GemId);
            }

            WingByShipAndGem[shipIndex] = gemToWingMap;
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
            if (TryGetAssignedWingIndex(shipEntity.Index, gemEntity.Index, out int wingIndex) &&
                wings.IsCreated && wingIndex >= 0 && wingIndex < wings.Length)
            {
                return ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wingIndex]);
            }

            return shipTransform.Position;
        }

        /// <summary>
        /// Returns the wing buffer index assigned to this ship→gem pair after
        /// <see cref="RebuildAssignmentCache"/> (used by beam VFX to pick the live wing tip).
        /// </summary>
        public static bool TryGetAssignedWingIndex(int shipIndex, int gemIndex, out int wingIndex)
        {
            RebuildAssignmentCache();
            wingIndex = -1;
            return WingByShipAndGem.TryGetValue(shipIndex, out var gemToWing) &&
                   gemToWing.TryGetValue(gemIndex, out wingIndex);
        }

        /// <summary>
        /// [HYBRID] Client presentation pull: when a beam is assigned to <paramref name="gemEntity"/>,
        /// returns the same wing-directed velocity the server applies so gems glide while the beam
        /// is visible (gem ghosts can lag; GO motion must not wait on sparse Velocity snapshots).
        /// </summary>
        /// <returns>True when this gem should be client-pulled toward a wing this frame.</returns>
        public static bool TryGetClientPullVelocity(Entity gemEntity, float3 gemLogicalPos, out float3 pullVelocity)
        {
            pullVelocity = float3.zero;
            RebuildAssignmentCache();

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;

            // --- Find which ship (if any) has this gem assigned ---
            foreach (var kv in WingByShipAndGem)
            {
                if (!kv.Value.TryGetValue(gemEntity.Index, out int wingIndex))
                    continue;

                int shipIndex = kv.Key;
                // [TITAN-ORBIT] Hold client pull until deploy finishes (extend → widen → pull),
                // matching GemTractorBeamSystem so the gem does not slide during the line shot.
                if (!GemTractorBeamDeployTracker.IsPullPhysicsActive(shipIndex, gemEntity.Index))
                    continue;

                if (!TryFindShipEntityByIndex(em, shipIndex, out Entity shipEntity))
                    continue;
                if (!em.HasComponent<ShipState>(shipEntity) || !em.HasComponent<LocalTransform>(shipEntity))
                    continue;

                var shipState = em.GetComponentData<ShipState>(shipEntity);
                if (!IsShipEligibleForBeam(shipState))
                    continue;

                var shipTransform = em.GetComponentData<LocalTransform>(shipEntity);
                var wings = em.HasBuffer<ShipWingTractorBeamElement>(shipEntity)
                    ? em.GetBuffer<ShipWingTractorBeamElement>(shipEntity)
                    : default;

                int shipLevel = math.max(1, shipState.ShipLevel);
                bool inOrbit = TryGetInOrbit(em, shipEntity);

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

                float gemValue = em.HasComponent<GemState>(gemEntity)
                    ? em.GetComponentData<GemState>(gemEntity).Value
                    : 1f;
                float gemSize = em.HasComponent<GemState>(gemEntity)
                    ? em.GetComponentData<GemState>(gemEntity).Size
                    : 0f;

                float pullSpeed = GemTractorBeamMath.ResolvePullSpeedFromWing(wingAttraction, gemValue, gemSize);
                float3 toWing = GemTractorBeamMath.ToroidalDirection(gemLogicalPos, pullTarget, mapW, mapH);
                if (math.lengthsq(toWing) < 0.0001f)
                    return false;

                pullVelocity = toWing * pullSpeed;
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
            WingByShipAndGem.Clear();
            AssignedGemsByShip.Clear();
            GemProxyScratch.Clear();
            ProxyEntityScratch.Clear();
            _cacheFrame = -1;
        }
    }
}
