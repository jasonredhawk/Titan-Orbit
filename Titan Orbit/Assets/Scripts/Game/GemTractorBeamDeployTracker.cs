using System.Collections.Generic;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client deploy animation timing for tractor beams: extend line, then widen at the
    /// gem. Driven only by ghosted <see cref="GemMotionState"/> lock tick + extend duration
    /// (the same clock the server stamped). There is no local "VFX-only" lock — that fallback
    /// drew beams to gems the server had not claimed (broken / uncollectable crystals).
    /// </summary>
    public static class GemTractorBeamDeployTracker
    {
        /// <summary>Per ship–gem pair: deploy clock from the ghost lock.</summary>
        struct DeployState
        {
            public float ExtendDuration;
            public uint LockTick;
        }

        static readonly Dictionary<long, DeployState> StateByPair = new Dictionary<long, DeployState>(128);
        static readonly List<GemTractorBeamClientLogic.GemProxySnapshot> GemScratch =
            new List<GemTractorBeamClientLogic.GemProxySnapshot>(64);
        static int _lastUpdateFrame = -1;

        public const float ExtendLineThickness = 0.065f;

        /// <summary>
        /// Refreshes deploy pairs from ghost locks only. Keys are ship entity.Index + gem ghostId.
        /// </summary>
        public static void LateUpdateTick()
        {
            if (Time.frameCount == _lastUpdateFrame)
                return;
            _lastUpdateFrame = Time.frameCount;

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
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
            var active = new HashSet<long>(StateByPair.Count);

            GemTractorBeamClientLogic.CollectGemProxies(em, GemScratch);

            // --- Ghost locks (authoritative deploy clock) ---
            // [NETCODE] TractorLockTick + TractorExtendDuration were stamped on the server
            // when the wing claimed the gem. No lock → no deploy state → no beam animation.
            for (int gi = 0; gi < GemScratch.Count; gi++)
            {
                var gem = GemScratch[gi];
                if (gem.GhostId == 0)
                    continue;
                if (gem.Motion.TractorShipId == 0 || gem.Motion.TractorLockTick == 0)
                    continue;

                if (!GemTractorBeamClientLogic.TryFindShipEntityByNetworkId(
                        em, gem.Motion.TractorShipId, out Entity shipEntity))
                    continue;

                long key = PairKey(shipEntity.Index, gem.GhostId);
                active.Add(key);
                StateByPair[key] = new DeployState
                {
                    LockTick = gem.Motion.TractorLockTick,
                    ExtendDuration = gem.Motion.TractorExtendDuration > 0.0001f
                        ? gem.Motion.TractorExtendDuration
                        : GemTractorBeamMath.MinExtendDuration,
                };
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
        /// PhaseTractor means the server already applied pull, so treat as active immediately.
        /// </summary>
        public static bool IsPullPhysicsActiveFromGhostLock(in GemMotionState motion)
        {
            if (motion.TractorLockTick == 0)
                return false;

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

        /// <summary>0–1 extend-line progress for this ship→gem ghost lock, or 0 if unlocked.</summary>
        public static float GetExtensionProgress(int shipIndex, int gemGhostId)
        {
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemGhostId), out DeployState state))
                return 0f;

            float extendDuration = state.ExtendDuration;
            if (extendDuration <= 0.0001f)
                return 1f;

            return Mathf.Clamp01(GetElapsed(state) / extendDuration);
        }

        /// <summary>0–1 cone-widen progress after the extend line finishes, or 0 if unlocked.</summary>
        public static float GetWidthExpandProgress(int shipIndex, int gemGhostId)
        {
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemGhostId), out DeployState state))
                return 0f;

            float elapsed = GetElapsed(state);
            if (elapsed <= state.ExtendDuration)
                return 0f;

            return Mathf.Clamp01((elapsed - state.ExtendDuration) / GemTractorBeamMath.WidthExpandDuration);
        }

        /// <summary>True while the extend/widen animation is still playing for this lock.</summary>
        public static bool IsInDeployAnimation(int shipIndex, int gemGhostId) =>
            StateByPair.ContainsKey(PairKey(shipIndex, gemGhostId)) &&
            !IsPullPhysicsActive(shipIndex, gemGhostId);

        /// <summary>True when extend + widen have finished for this ghost lock.</summary>
        public static bool IsPullPhysicsActive(int shipIndex, int gemGhostId)
        {
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemGhostId), out DeployState state))
                return false;

            float elapsed = GetElapsed(state);
            float total = state.ExtendDuration + GemTractorBeamMath.WidthExpandDuration;
            return elapsed >= total - 0.0001f;
        }

        /// <summary>Drops all deploy clocks (leave session / domain reload).</summary>
        public static void Clear()
        {
            StateByPair.Clear();
            GemScratch.Clear();
            _lastUpdateFrame = -1;
        }

        static float GetElapsed(in DeployState state)
        {
            if (state.LockTick != 0 && TryGetServerElapsedSeconds(out double nowSec))
            {
                int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
                double lockSec = state.LockTick / (double)hz;
                return (float)math.max(0d, nowSec - lockSec);
            }

            return 0f;
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
            return elapsed > 0d || networkTime.ServerTick.IsValid;
        }

        static long PairKey(int shipIndex, int gemGhostId) => ((long)shipIndex << 32) | (uint)gemGhostId;
    }
}
