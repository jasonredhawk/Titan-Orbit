using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
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
    /// [HYBRID] Client tractor-beam bookkeeping. This is a <b>presentation</b> of server locks,
    /// not a second assignment simulation.
    /// <para>
    /// Contract:
    /// 1. The server (<see cref="GemTractorBeamSystem"/>) is the only place that decides which
    ///    wing owns which gem. It writes ghosted <see cref="GemMotionState"/> lock fields.
    /// 2. This class copies those locks into a per-frame cache so Shapes / fade / deploy can draw.
    /// 3. We never invent a wing↔gem pair from local range tests. That was the "broken gem"
    ///    bug: the crystal the player saw was not the collectable server gem (or the ship was
    ///    ineligible on the server), yet the client still drew a latch.
    /// </para>
    /// Gems come from <see cref="GemClientEntityRegistry"/> / hybrid proxies — never a full gem
    /// <c>ToEntityArray</c> (join-crash invariant). Pair keys use <see cref="GhostInstance.ghostId"/>
    /// so Entity.Index reuse cannot keep a beam after the old gem despawns.
    /// </summary>
    public static class GemTractorBeamClientLogic
    {
        /// <summary>
        /// One Instantiated gem ghost plus the interpolated pose the player can scoop.
        /// Position is always ghost <c>LocalTransform</c> — the same sample pickup uses.
        /// </summary>
        public struct GemProxySnapshot
        {
            /// <summary>Client-world gem entity (Index + Version).</summary>
            public Entity Entity;

            /// <summary>
            /// [NETCODE] <see cref="GhostInstance.ghostId"/> — session-unique, same id the server
            /// assigned. 0 means the snapshot is not a live replicated gem (skip it).
            /// </summary>
            public int GhostId;

            /// <summary>Ghosted value / size / self-pickup stamps.</summary>
            public GemState State;

            /// <summary>Interpolated logical pose (unbounded XZ). Not a coasted GO pose.</summary>
            public LocalTransform Transform;

            /// <summary>Ghosted velocity (unused for lock decisions; kept for callers).</summary>
            public GemKinematics Kinematics;

            /// <summary>Ghosted tractor lock. TractorShipId 0 = unlocked.</summary>
            public GemMotionState Motion;
        }

        /// <summary>Unity frame that last built <see cref="PairsByShip"/>.</summary>
        static int _cacheFrame = -1;

        /// <summary>ship entity.Index → primary wing per gem ghostId (this frame only).</summary>
        static readonly Dictionary<int, Dictionary<int, int>> PrimaryWingByShipAndGem =
            new Dictionary<int, Dictionary<int, int>>(32);

        /// <summary>ship entity.Index → lock pairs this frame (one primary pair per locked gem).</summary>
        static readonly Dictionary<int, List<GemTractorBeamAssignment.Pair>> PairsByShip =
            new Dictionary<int, List<GemTractorBeamAssignment.Pair>>(32);

        /// <summary>ship entity.Index → gem ghostIds locked to that ship this frame.</summary>
        static readonly Dictionary<int, HashSet<int>> AssignedGemsByShip =
            new Dictionary<int, HashSet<int>>(32);

        static readonly List<Entity> ProxyEntityScratch = new List<Entity>(256);
        static readonly List<GemProxySnapshot> GemProxyScratch = new List<GemProxySnapshot>(64);

        /// <summary>
        /// Rebuilds the lock cache once per Unity frame from ghosted <see cref="GemMotionState"/>.
        /// Called from visibility / deploy / the beam drawer.
        /// </summary>
        public static void RebuildAssignmentCache()
        {
            // --- Frame cache ---
            // [STANDARD] Drawing, fade, and deploy all ask for pairs; build once per frame.
            if (Time.frameCount == _cacheFrame)
                return;
            _cacheFrame = Time.frameCount;

            PrimaryWingByShipAndGem.Clear();
            PairsByShip.Clear();
            AssignedGemsByShip.Clear();

            // [TITAN-ORBIT] Skip while ShouldSkipShipEntityQueries (Settling / GhostSpawnBacklog /
            // post–TeamChoice hold). Ship ToEntityArray during that window is Crash!!!.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!ToroidalDisplay.ResolveMapSize(em, out float mapW, out float mapH))
                return;

            CollectGemProxies(em, GemProxyScratch);
            if (GemProxyScratch.Count == 0)
                return;

            // --- Ships that may own a lock (tiny query, still join-gated above) ---
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Unity.Collections.Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);
            using var shipOwners = shipQuery.ToComponentDataArray<GhostOwner>(Unity.Collections.Allocator.Temp);

            // NetworkId → ship slot so each locked gem finds its owner in O(ships), not O(gems×ships).
            var shipSlotByNetworkId = new Dictionary<int, int>(ships.Length);
            for (int si = 0; si < ships.Length; si++)
            {
                int networkId = shipOwners[si].NetworkId;
                if (networkId == 0)
                    continue;
                shipSlotByNetworkId[networkId] = si;
            }

            for (int gi = 0; gi < GemProxyScratch.Count; gi++)
            {
                var gem = GemProxyScratch[gi];

                // --- Server lock only ---
                // [NETCODE] TractorShipId is the ship's GhostOwner.NetworkId. 0 = unlocked.
                // A beam without this field is a client-invented latch — we never draw those.
                int lockShipId = gem.Motion.TractorShipId;
                if (lockShipId == 0)
                    continue;
                if (!shipSlotByNetworkId.TryGetValue(lockShipId, out int si))
                    continue;
                if (!IsShipEligibleForBeam(shipStates[si]))
                    continue;

                var wings = em.HasBuffer<ShipWingTractorBeamElement>(ships[si])
                    ? em.GetBuffer<ShipWingTractorBeamElement>(ships[si])
                    : default;

                // Scoop zone: the gem is being consumed (or sitting in the absorb sphere).
                // Hide the latch so we do not point at empty hull space after pickup.
                if (IsInsideCargoAbsorbZone(
                        shipTransforms[si], wings, gem.Transform, gem.State, mapW, mapH))
                    continue;

                int shipIndex = ships[si].Index;
                int gemId = gem.GhostId;
                int wingIndex = gem.Motion.TractorWingIndex;

                if (!PairsByShip.TryGetValue(shipIndex, out var pairs))
                {
                    pairs = new List<GemTractorBeamAssignment.Pair>(4);
                    PairsByShip[shipIndex] = pairs;
                }

                pairs.Add(new GemTractorBeamAssignment.Pair
                {
                    WingIndex = wingIndex,
                    GemKey = gemId,
                    IsPrimary = true,
                });

                if (!PrimaryWingByShipAndGem.TryGetValue(shipIndex, out var primaryMap))
                {
                    primaryMap = new Dictionary<int, int>(4);
                    PrimaryWingByShipAndGem[shipIndex] = primaryMap;
                }

                primaryMap[gemId] = wingIndex;

                if (!AssignedGemsByShip.TryGetValue(shipIndex, out var assigned))
                {
                    assigned = new HashSet<int>();
                    AssignedGemsByShip[shipIndex] = assigned;
                }

                assigned.Add(gemId);
            }
        }

        /// <summary>
        /// Fills <paramref name="dst"/> with Instantiated, replicated gem ghosts that still have
        /// a visible crystal. Prefers <see cref="GemClientEntityRegistry"/>; also merges hybrid
        /// proxy dictionary entities. Per-entity checks only — never a full gem <c>ToEntityArray</c>.
        /// </summary>
        public static void CollectGemProxies(EntityManager em, List<GemProxySnapshot> dst)
        {
            dst.Clear();
            var seen = new HashSet<int>();

            // Drop consumed / despawned ghosts before we snapshot.
            GemClientEntityRegistry.PruneMissing(em);

            GemClientEntityRegistry.CopyLive(ProxyEntityScratch);
            AppendGemSnapshots(em, ProxyEntityScratch, dst, seen);

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null)
            {
                visualizer.CopyLiveProxyEntities(ProxyEntityScratch);
                AppendGemSnapshots(em, ProxyEntityScratch, dst, seen);
            }
        }

        /// <summary>
        /// Presented logical pose from <see cref="GemClientMotionApplier"/> (interpolated LT
        /// plus velocity lead to server-now). Same space as ECS pickup.
        /// </summary>
        static bool TryGetPresentedLogicalPosition(Entity gemEntity, out float3 logicalPos)
        {
            logicalPos = default;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null || !visualizer.TryGetProxy(gemEntity, out GameObject proxy) || proxy == null)
                return false;
            if (!proxy.activeInHierarchy)
                return false;

            var motion = proxy.GetComponent<GemClientMotionApplier>();
            return motion != null && motion.TryGetLogicalPosition(out logicalPos);
        }

        /// <summary>
        /// Appends live replicated gems from a candidate list (deduped by ghostId).
        /// Pose prefers the presented (server-now) crystal when the motion applier is bound.
        /// </summary>
        static void AppendGemSnapshots(
            EntityManager em,
            List<Entity> candidates,
            List<GemProxySnapshot> dst,
            HashSet<int> seen)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity entity = candidates[i];
                if (!em.Exists(entity))
                    continue;
                if (!em.HasComponent<GemTag>(entity) ||
                    !em.HasComponent<GemState>(entity) ||
                    !em.HasComponent<LocalTransform>(entity) ||
                    !em.HasComponent<GhostInstance>(entity))
                    continue;

                // [NETCODE] ghostId 0 is a prefab leftover or an unregistered spawn — not scoopable.
                int ghostId = em.GetComponentData<GhostInstance>(entity).ghostId;
                if (ghostId == 0 || seen.Contains(ghostId))
                    continue;

                var state = em.GetComponentData<GemState>(entity);
                if (!IsGemEligibleForBeam(state))
                    continue;

                // [TITAN-ORBIT] No beam / lock cache entry without a live crystal.
                // Pickup returns the GO to GemVisualPool immediately; a lingering ghost
                // without this mesh is a line to nothing (the second reported bug).
                if (!HasVisibleGemCrystal(entity))
                    continue;

                seen.Add(ghostId);

                var kinematics = em.HasComponent<GemKinematics>(entity)
                    ? em.GetComponentData<GemKinematics>(entity)
                    : default;
                var motion = em.HasComponent<GemMotionState>(entity)
                    ? em.GetComponentData<GemMotionState>(entity)
                    : default;

                // --- Pose: estimated server-now when the motion applier has posed this crystal ---
                // [TITAN-ORBIT] That lead matches GemPickupSystem's server-now gem. Ghost
                // LocalTransform alone is the interpolated past and misses fly-over scoop.
                var transform = em.GetComponentData<LocalTransform>(entity);
                if (TryGetPresentedLogicalPosition(entity, out var presentedPos))
                    transform.Position = presentedPos;

                dst.Add(new GemProxySnapshot
                {
                    Entity = entity,
                    GhostId = ghostId,
                    State = state,
                    Transform = transform,
                    Kinematics = kinematics,
                    Motion = motion,
                });
            }
        }

        /// <summary>
        /// True when this ship's ghost lock list contains <paramref name="gemGhostId"/>.
        /// </summary>
        public static bool CanShipMagneticallyPull(int shipIndex, int gemGhostId)
        {
            RebuildAssignmentCache();
            return AssignedGemsByShip.TryGetValue(shipIndex, out var gems) && gems.Contains(gemGhostId);
        }

        /// <summary>
        /// True when the server has locked this gem to this ship and the crystal is still visible
        /// and not already inside the cargo scoop. Range is <b>not</b> re-tested here — the lock
        /// is the range decision.
        /// </summary>
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
            if (!TryGetGemGhostId(em, gemEntity, out int gemGhostId))
                return false;
            if (!CanShipMagneticallyPull(shipEntity.Index, gemGhostId))
                return false;
            if (!HasVisibleGemCrystal(gemEntity))
                return false;
            if (em.HasComponent<GemState>(gemEntity) &&
                IsInsideCargoAbsorbZone(
                    shipTransform, wings, gemTransform, em.GetComponentData<GemState>(gemEntity), mapW, mapH))
                return false;

            return true;
        }

        /// <summary>Logical world origin for a specific wing buffer index.</summary>
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
        /// Primary wing for this ship→gem after <see cref="RebuildAssignmentCache"/>
        /// (ghost <c>TractorWingIndex</c>).
        /// </summary>
        public static bool TryGetAssignedWingIndex(int shipIndex, int gemGhostId, out int wingIndex)
        {
            RebuildAssignmentCache();
            wingIndex = -1;
            return PrimaryWingByShipAndGem.TryGetValue(shipIndex, out var gemToWing) &&
                   gemToWing.TryGetValue(gemGhostId, out wingIndex);
        }

        /// <summary>
        /// Server-lock pairs for a ship this frame (one primary pair per locked gem).
        /// </summary>
        public static bool TryGetShipBeamPairs(int shipIndex, out List<GemTractorBeamAssignment.Pair> pairs)
        {
            RebuildAssignmentCache();
            return PairsByShip.TryGetValue(shipIndex, out pairs) && pairs != null && pairs.Count > 0;
        }

        /// <summary>
        /// Resolves a ship ghost by <see cref="GhostOwner.NetworkId"/>.
        /// Caller must already gate <c>ClientJoinSettleCache.ShouldSkipShipEntityQueries</c> —
        /// this method uses a ship <c>ToEntityArray</c>.
        /// </summary>
        public static bool TryFindShipEntityByNetworkId(EntityManager em, int networkId, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (networkId == 0)
                return false;

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

        /// <summary>
        /// True when this ship has a server lock on this gem and the crystal is still a valid draw.
        /// </summary>
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
            return IsWithinMagneticPullRange(
                em, shipEntity, shipState, shipTransform, wings, gemEntity, gemTransform, mapW, mapH);
        }

        /// <summary>
        /// Client copy of server tractor eligibility: dead / team-select / full cargo cannot latch.
        /// 0 HP is allowed — same as <c>GemTractorBeamSystem.IsShipEligibleForPull</c>.
        /// </summary>
        public static bool IsShipEligibleForBeam(in ShipState ship)
        {
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            if (ship.CurrentGems >= ship.GemCapacity - 0.001f)
                return false;
            return true;
        }

        /// <summary>
        /// True when the hybrid gem crystal is actually on-screen as a mesh.
        /// A live proxy GO is not enough: end-of-life shrink sets scale to 0 while
        /// <c>TractorShipId</c> stays set, which used to draw beams at empty space.
        /// Pickup also returns the GO to <see cref="GemVisualPool"/> immediately.
        /// </summary>
        public static bool HasVisibleGemCrystal(Entity gemEntity)
        {
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null || !visualizer.TryGetProxy(gemEntity, out GameObject proxy) || proxy == null)
                return false;
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world != null && world.IsCreated &&
                world.EntityManager.Exists(gemEntity) &&
                world.EntityManager.HasComponent<GemState>(gemEntity) &&
                world.EntityManager.GetComponentData<GemState>(gemEntity).IsConsumed)
                return false;
            if (!proxy.activeInHierarchy)
                return false;
            // [TITAN-ORBIT] Lifetime shrink (and a pooled empty shell) leave the root active.
            if (proxy.transform.lossyScale.x < 0.08f)
                return false;
            var renderer = proxy.GetComponentInChildren<Renderer>();
            if (renderer == null || !renderer.enabled)
                return false;
            if (renderer.bounds.size.sqrMagnitude < 0.0025f)
                return false;
            return true;
        }

        /// <summary>
        /// True when the gem center is inside this ship's cargo absorb sphere(s) — the same
        /// wing-tip / hull test as <c>GemPickupSystem</c>.
        /// [TITAN-ORBIT] Tractor VFX must stop here: the gem is being scooped. A 15% slack
        /// covers interpolation so the beam does not linger a frame after consume.
        /// </summary>
        public static bool IsInsideCargoAbsorbZone(
            in LocalTransform shipTransform,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            in LocalTransform gemTransform,
            in GemState gemState,
            float mapW,
            float mapH)
        {
            var settings = TractorBeamSettingsCache.ResolveOrDefault();
            float3 gemPos = gemTransform.Position;
            const float slack = 1.15f;

            bool hasWings = wings.IsCreated && wings.Length > 0;
            if (hasWings)
            {
                float collectRadius = GemCollectMath.ResolveWingCollectRadius(
                    settings, gemState.Value, gemState.Size) * slack;
                for (int wi = 0; wi < wings.Length; wi++)
                {
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wi]);
                    if (GemTractorBeamMath.ToroidalDistance(gemPos, wingPos, mapW, mapH) <= collectRadius)
                        return true;
                }

                if (!settings.AlsoUseHullPickupWithWings)
                    return false;
            }

            float hullRange = GemCollectMath.ResolveHullCollectRadius(
                settings, gemState.Value, gemState.Size, shipTransform.Scale) * slack;
            return GemTractorBeamMath.ToroidalDistance(gemPos, shipTransform.Position, mapW, mapH) <=
                   hullRange;
        }

        /// <summary>
        /// Gem has value and is not mid-deposit. Colour / <c>IsBonusGem</c> is ignored — yellow
        /// extra-yield gems beam like red. Self-pickup is enforced on the server lock
        /// (blocked gems never get <c>TractorShipId</c>) — we do not second-guess it here.
        /// </summary>
        public static bool IsGemEligibleForBeam(in GemState gem) =>
            !gem.IsConsumed && gem.Value > 0.001f && gem.DepositTeam == TeamId.None;

        /// <summary>
        /// [NETCODE] Session-unique gem id from <see cref="GhostInstance"/>. False when the
        /// entity is missing, not a ghost, or still has the prefab's ghostId 0.
        /// </summary>
        public static bool TryGetGemGhostId(EntityManager em, Entity gemEntity, out int ghostId)
        {
            ghostId = 0;
            if (!em.Exists(gemEntity) || !em.HasComponent<GhostInstance>(gemEntity))
                return false;
            ghostId = em.GetComponentData<GhostInstance>(gemEntity).ghostId;
            return ghostId != 0;
        }

        /// <summary>Clears lock caches (leave session / domain reload).</summary>
        public static void Clear()
        {
            PrimaryWingByShipAndGem.Clear();
            PairsByShip.Clear();
            AssignedGemsByShip.Clear();
            GemProxyScratch.Clear();
            ProxyEntityScratch.Clear();
            _cacheFrame = -1;
        }
    }
}
