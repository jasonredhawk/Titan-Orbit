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
    /// <summary>Client-side deploy timing for tractor beam extend / widen before pull.</summary>
    public static class GemTractorBeamDeployTracker
    {
        struct DeployState
        {
            public float LockStartTime;
            public float ExtendDuration;
        }

        static readonly Dictionary<long, DeployState> StateByPair = new Dictionary<long, DeployState>(128);
        static int _lastUpdateFrame = -1;

        public const float ExtendLineThickness = 0.065f;

        public static void LateUpdateTick()
        {
            if (Time.frameCount == _lastUpdateFrame)
                return;
            _lastUpdateFrame = Time.frameCount;

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
                ComponentType.ReadOnly<ShipOrbitState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Unity.Collections.Allocator.Temp);
            using var shipOrbits = shipQuery.ToComponentDataArray<ShipOrbitState>(Unity.Collections.Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            using var gemQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var gems = gemQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var gemStates = gemQuery.ToComponentDataArray<GemState>(Unity.Collections.Allocator.Temp);
            using var gemTransforms = gemQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;

            for (int si = 0; si < ships.Length; si++)
            {
                if (!GemTractorBeamClientLogic.IsShipEligibleForBeam(shipStates[si]))
                    continue;

                var wings = em.HasBuffer<ShipWingTractorBeamElement>(ships[si])
                    ? em.GetBuffer<ShipWingTractorBeamElement>(ships[si])
                    : default;

                for (int gi = 0; gi < gems.Length; gi++)
                {
                    if (!GemTractorBeamClientLogic.IsGemEligibleForBeam(gemStates[gi]))
                        continue;

                    if (!GemTractorBeamClientLogic.IsWithinCandidateRange(
                            em, ships[si], shipStates[si], shipTransforms[si], wings,
                            gems[gi], gemTransforms[gi], mapW, mapH))
                        continue;

                    long key = PairKey(ships[si].Index, gems[gi].Index);
                    active.Add(key);
                    if (StateByPair.ContainsKey(key))
                        continue;

                    float3 origin = GemTractorBeamClientLogic.GetDeployBeamOrigin(
                        shipTransforms[si], wings, gemTransforms[gi],
                        math.max(1, shipStates[si].ShipLevel), shipOrbits[si].InOrbitRing, mapW, mapH);
                    float dist = GemTractorBeamMath.ToroidalDistance(gemTransforms[gi].Position, origin, mapW, mapH);
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
            if (!StateByPair.TryGetValue(PairKey(shipIndex, gemIndex), out DeployState state))
                return false;

            float elapsed = Mathf.Max(0f, Time.time - state.LockStartTime);
            float total = state.ExtendDuration + GemTractorBeamMath.WidthExpandDuration;
            return elapsed >= total - 0.0001f;
        }

        public static void Clear()
        {
            StateByPair.Clear();
            _lastUpdateFrame = -1;
        }

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
