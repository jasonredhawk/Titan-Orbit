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
    /// [HYBRID] Client-side deploy animation timing for tractor beams: extend line, then widen at gem,
    /// before pull reports active. Prefers ghosted <see cref="GemMotionState"/> lock tick + extend
    /// duration (same clock as server). Falls back to local assignment timing only for beam VFX
    /// when the gem has no lock yet. Does not write gem velocity.
    /// [TITAN-ORBIT] Under TransformQuarantine, gems come from hybrid proxies via
    /// <see cref="GemTractorBeamClientLogic.CollectGemProxies"/> — never a full gem ToEntityArray.
    /// </summary>
    public static class GemTractorBeamDeployTracker
    {
        /// <summary>Per ship-gem pair: deploy clock (local fallback when ghost lock missing).</summary>
        struct DeployState
        {
            public float LockStartTime;
            public float ExtendDuration;
            /// <summary>True when LockStartTime is ServerTick seconds (ghost lock), not Time.time.</summary>
            public bool FromGhostLock;
            public uint LockTick;
        }

        static readonly Dictionary<long, DeployState> StateByPair = new Dictionary<long, DeployState>(128);
        static readonly List<GemTractorBeamClientLogic.GemProxySnapshot> GemScratch =
            new List<GemTractorBeamClientLogic.GemProxySnapshot>(64);
        static int _lastUpdateFrame = -1;

        public const float ExtendLineThickness = 0.065f;

        /// <summary>
        /// Refreshes deploy pairs: ghost locks first, then local assignment for VFX-only beams.
        /// </summary>
        public static void LateUpdateTick()
        {
            if (Time.frameCount == _lastUpdateFrame)
                return;
            _lastUpdateFrame = Time.frameCount;

            // [TITAN-ORBIT] Settling OR GhostSpawnBacklog — ship queries unsafe during Instantiates.
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
            {
                StateByPair.Clear();
                return;
            }

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
            {
                StateByPair.Clear();
                return;
            }

            var em = world.EntityManager;
            float now = Time.time;
            var active = new HashSet<long>(StateByPair.Count);

            // Quarantine-safe: hybrid gem proxies only.
            GemTractorBeamClientLogic.CollectGemProxies(em, GemScratch);

            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;

            // --- Ghost locks (authoritative deploy clock) ---
            for (int gi = 0; gi < GemScratch.Count; gi++)
            {
                var gem = GemScratch[gi];
                if (!em.Exists(gem.Entity) || !em.HasComponent<GemMotionState>(gem.Entity))
                    continue;

                var motion = em.GetComponentData<GemMotionState>(gem.Entity);
                if (motion.TractorShipId == 0 || motion.TractorLockTick == 0)
                    continue;

                if (!GemTractorBeamClientLogic.TryFindShipEntityByNetworkId(
                        em, motion.TractorShipId, out Entity shipEntity))
                    continue;

                long key = PairKey(shipEntity.Index, gem.Entity.Index);
                active.Add(key);
                StateByPair[key] = new DeployState
                {
                    LockTick = motion.TractorLockTick,
                    ExtendDuration = motion.TractorExtendDuration > 0.0001f
                        ? motion.TractorExtendDuration
                        : GemTractorBeamMath.MinExtendDuration,
                    FromGhostLock = true,
                    LockStartTime = 0f,
                };
            }

            // --- Local fallback for beam VFX when ghost lock has not replicated yet ---
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Unity.Collections.Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int si = 0; si < ships.Length; si++)
            {
                if (!GemTractorBeamClientLogic.IsShipEligibleForBeam(shipStates[si]))
                    continue;

                var wings = em.HasBuffer<ShipWingTractorBeamElement>(ships[si])
                    ? em.GetBuffer<ShipWingTractorBeamElement>(ships[si])
                    : default;

                for (int gi = 0; gi < GemScratch.Count; gi++)
                {
                    var gem = GemScratch[gi];
                    long key = PairKey(ships[si].Index, gem.Entity.Index);
                    if (active.Contains(key))
                        continue;

                    if (!GemTractorBeamClientLogic.IsEligibleForBeamVisual(
                            em, ships[si], shipStates[si], shipTransforms[si], wings,
                            gem.Entity, gem.Transform, mapW, mapH))
                        continue;

                    active.Add(key);
                    if (StateByPair.ContainsKey(key))
                        continue;

                    float3 origin = GemTractorBeamClientLogic.ResolveBeamOrigin(
                        ships[si], shipTransforms[si], wings, gem.Entity);
                    float dist = GemTractorBeamMath.ToroidalDistance(gem.Transform.Position, origin, mapW, mapH);
                    StateByPair[key] = new DeployState
                    {
                        LockStartTime = now,
                        ExtendDuration = GemTractorBeamMath.ComputeExtendDuration(dist),
                        FromGhostLock = false,
                        LockTick = 0,
                    };
                }
            }

            if (StateByPair.Count > active.Count)
            {
                var stale = new List<long>(8);
                foreach (var kv in StateByPair)
                {
                    if (!active.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                    StateByPair.Remove(stale[i]);
            }
        }

        /// <summary>
        /// True when ghost lock says deploy (extend + widen) is finished — shared ServerTick clock.
        /// </summary>
        public static bool IsPullPhysicsActiveFromGhostLock(in GemMotionState motion)
        {
            if (motion.TractorLockTick == 0)
                return false;

            // PhaseTractor means server already applied pull — treat as active immediately.
            if (motion.Phase == GemMotionState.PhaseTractor)
                return true;

            if (!TryGetServerElapsedSeconds(out double nowSec))
                return false;

            int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            double lockSec = motion.TractorLockTick / (double)hz;
            float extend = motion.TractorExtendDuration > 0.0001f
                ? motion.TractorExtendDuration
                : GemTractorBeamMath.MinExtendDuration;
            float elapsed = (float)math.max(0d, nowSec - lockSec);
            float total = extend + GemTractorBeamMath.WidthExpandDuration;
            return elapsed >= total - 0.0001f;
        }

        public static float GetExtensionProgress(int shipIndex, int gemIndex)
        {
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemIndex), out DeployState state))
                return 0f;

            float extendDuration = state.ExtendDuration;
            if (extendDuration <= 0.0001f)
                return 1f;

            float elapsed = GetElapsed(state);
            return Mathf.Clamp01(elapsed / extendDuration);
        }

        public static float GetWidthExpandProgress(int shipIndex, int gemIndex)
        {
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemIndex), out DeployState state))
                return 0f;

            float elapsed = GetElapsed(state);
            if (elapsed <= state.ExtendDuration)
                return 0f;

            return Mathf.Clamp01((elapsed - state.ExtendDuration) / GemTractorBeamMath.WidthExpandDuration);
        }

        public static bool IsInDeployAnimation(int shipIndex, int gemIndex) =>
            StateByPair.ContainsKey(PairKey(shipIndex, gemIndex)) && !IsPullPhysicsActive(shipIndex, gemIndex);

        public static bool IsPullPhysicsActive(int shipIndex, int gemIndex)
        {
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemIndex), out DeployState state))
                return false;

            float elapsed = GetElapsed(state);
            float total = state.ExtendDuration + GemTractorBeamMath.WidthExpandDuration;
            return elapsed >= total - 0.0001f;
        }

        public static void Clear()
        {
            StateByPair.Clear();
            GemScratch.Clear();
            _lastUpdateFrame = -1;
        }

        static float GetElapsed(in DeployState state)
        {
            if (state.FromGhostLock && state.LockTick != 0 && TryGetServerElapsedSeconds(out double nowSec))
            {
                int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
                double lockSec = state.LockTick / (double)hz;
                return (float)math.max(0d, nowSec - lockSec);
            }

            return Mathf.Max(0f, Time.time - state.LockStartTime);
        }

        /// <summary>ServerTick seconds from the client visualization world NetworkTime singleton.</summary>
        static bool TryGetServerElapsedSeconds(out double elapsed)
        {
            elapsed = 0d;
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            if (q.IsEmptyIgnoreFilter)
                return false;

            var networkTime = q.GetSingleton<NetworkTime>();
            int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            using var rateQ = em.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            if (!rateQ.IsEmptyIgnoreFilter)
                hz = math.max(1, rateQ.GetSingleton<ClientServerTickRate>().SimulationTickRate);

            elapsed = PlanetGemMoonOrbitClock.ToElapsedSeconds(networkTime, hz, includeTickFraction: true);
            return elapsed > 0d || (networkTime.ServerTick.IsValid);
        }

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
