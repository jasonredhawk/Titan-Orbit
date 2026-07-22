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
    /// [HYBRID] Client-side deploy animation timing for tractor beams: extend line, then widen at gem,
    /// before <see cref="IsPullPhysicsActive"/> reports the server pull phase. Pairs with
    /// <see cref="GemTractorBeamMath"/> durations. Only tracks gems already assigned by
    /// <see cref="GemTractorBeamAssignment"/> (nearest wing owns the gem). Cosmetic only —
    /// does not affect gem velocity.
    /// [TITAN-ORBIT] Under TransformQuarantine, gems come from hybrid proxies via
    /// <see cref="GemTractorBeamClientLogic.CollectGemProxies"/> — never a full gem ToEntityArray.
    /// </summary>
    public static class GemTractorBeamDeployTracker
    {
        /// <summary>Per ship-gem pair: when lock started and how long extend phase lasts.</summary>
        struct DeployState
        {
            public float LockStartTime;
            public float ExtendDuration;
        }

        static readonly Dictionary<long, DeployState> StateByPair = new Dictionary<long, DeployState>(128);
        static readonly List<GemTractorBeamClientLogic.GemProxySnapshot> GemScratch =
            new List<GemTractorBeamClientLogic.GemProxySnapshot>(64);
        static int _lastUpdateFrame = -1;

        public const float ExtendLineThickness = 0.065f;

        public static void LateUpdateTick()
        {
            // --- Per-frame refresh ---
            if (Time.frameCount == _lastUpdateFrame)
                return;
            _lastUpdateFrame = Time.frameCount;

            // [TITAN-ORBIT] Settling OR GhostSpawnBacklog. Quarantine stays ON all session — beams
            // must still deploy after settle. Ship ToEntityArray during post–Join Team Instantiates
            // (Settling OFF + GhostSpawnBacklog ON) → Crash!!! (2026-07-19 TeamChoiceResult).
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

            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Unity.Collections.Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            // Quarantine-safe: hybrid gem proxies only.
            GemTractorBeamClientLogic.CollectGemProxies(em, GemScratch);

            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;

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
                    // Only the assigned nearest wing starts deploy — idle beams must not share a gem.
                    if (!GemTractorBeamClientLogic.IsEligibleForBeamVisual(
                            em, ships[si], shipStates[si], shipTransforms[si], wings,
                            gem.Entity, gem.Transform, mapW, mapH))
                        continue;

                    long key = PairKey(ships[si].Index, gem.Entity.Index);
                    active.Add(key);
                    if (StateByPair.ContainsKey(key))
                        continue;

                    // Deploy origin = assigned wing (same as beam draw), not "any closest in range."
                    float3 origin = GemTractorBeamClientLogic.ResolveBeamOrigin(
                        ships[si], shipTransforms[si], wings, gem.Entity);
                    float dist = GemTractorBeamMath.ToroidalDistance(gem.Transform.Position, origin, mapW, mapH);
                    StateByPair[key] = new DeployState
                    {
                        LockStartTime = now,
                        ExtendDuration = GemTractorBeamMath.ComputeExtendDuration(dist),
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

        public static float GetExtensionProgress(int shipIndex, int gemIndex)
        {
            // --- Compute value ---
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemIndex), out DeployState state))
                return 0f;

            float extendDuration = state.ExtendDuration;
            if (extendDuration <= 0.0001f)
                return 1f;

            float elapsed = Mathf.Max(0f, Time.time - state.LockStartTime);
            return Mathf.Clamp01(elapsed / extendDuration);
        }

        public static float GetWidthExpandProgress(int shipIndex, int gemIndex)
        {
            // --- Compute value ---
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemIndex), out DeployState state))
                return 0f;

            float elapsed = Mathf.Max(0f, Time.time - state.LockStartTime);
            if (elapsed <= state.ExtendDuration)
                return 0f;

            return Mathf.Clamp01((elapsed - state.ExtendDuration) / GemTractorBeamMath.WidthExpandDuration);
        }

        public static bool IsInDeployAnimation(int shipIndex, int gemIndex) =>
            StateByPair.ContainsKey(PairKey(shipIndex, gemIndex)) && !IsPullPhysicsActive(shipIndex, gemIndex);

        public static bool IsPullPhysicsActive(int shipIndex, int gemIndex)
        {
            // --- IsPullPhysicsActive ---
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemIndex), out DeployState state))
                return false;

            float elapsed = Mathf.Max(0f, Time.time - state.LockStartTime);
            float total = state.ExtendDuration + GemTractorBeamMath.WidthExpandDuration;
            return elapsed >= total - 0.0001f;
        }

        public static void Clear()
        {
            StateByPair.Clear();
            GemScratch.Clear();
            _lastUpdateFrame = -1;
        }

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
