using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Client-side fade/hold for tractor-beam visuals so assignment jitter does not pop beams on/off.</summary>
    public static class GemTractorBeamVisibilityTracker
    {
        static readonly Dictionary<long, float> VisibilityByPair = new Dictionary<long, float>(128);
        static int _lastUpdateFrame = -1;

        const float FadeInPerSecond = 5f;
        const float FadeOutPerSecond = 1.8f;

        public static void LateUpdateTick()
        {
            if (Time.frameCount == _lastUpdateFrame)
                return;
            _lastUpdateFrame = Time.frameCount;

            GemTractorBeamDeployTracker.LateUpdateTick();
            GemTractorBeamClientLogic.RebuildAssignmentCache();

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
            {
                VisibilityByPair.Clear();
                return;
            }

            var em = world.EntityManager;
            float dt = Time.deltaTime;
            var touched = new HashSet<long>(VisibilityByPair.Count);
            float mapW = Generation.ToroidalMapEcs.MapWidth;
            float mapH = Generation.ToroidalMapEcs.MapHeight;

            using var shipQuery = em.CreateEntityQuery(
                typeof(ECS.ShipTag),
                typeof(ECS.ShipState),
                typeof(Unity.Transforms.LocalTransform));
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ECS.ShipState>(Unity.Collections.Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<Unity.Transforms.LocalTransform>(Unity.Collections.Allocator.Temp);

            using var gemQuery = em.CreateEntityQuery(
                typeof(ECS.GemTag),
                typeof(ECS.GemState),
                typeof(Unity.Transforms.LocalTransform));
            using var gems = gemQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var gemTransforms = gemQuery.ToComponentDataArray<Unity.Transforms.LocalTransform>(Unity.Collections.Allocator.Temp);

            for (int si = 0; si < ships.Length; si++)
            {
                if (!GemTractorBeamClientLogic.IsShipEligibleForBeam(shipStates[si]))
                    continue;

                var wings = em.HasBuffer<ECS.ShipWingTractorBeamElement>(ships[si])
                    ? em.GetBuffer<ECS.ShipWingTractorBeamElement>(ships[si])
                    : default;

                for (int gi = 0; gi < gems.Length; gi++)
                {
                    long key = PairKey(ships[si].Index, gems[gi].Index);
                    touched.Add(key);

                    bool wantsVisible = GemTractorBeamClientLogic.IsEligibleForBeamVisual(
                        em, ships[si], shipStates[si], shipTransforms[si], wings,
                        gems[gi], gemTransforms[gi], mapW, mapH);

                    VisibilityByPair.TryGetValue(key, out float visibility);

                    if (wantsVisible && GemTractorBeamDeployTracker.IsInDeployAnimation(ships[si].Index, gems[gi].Index))
                        visibility = 1f;
                    else
                    {
                        float fadeSpeed = wantsVisible ? FadeInPerSecond : FadeOutPerSecond;
                        visibility = Mathf.MoveTowards(visibility, wantsVisible ? 1f : 0f, fadeSpeed * dt);
                    }

                    if (visibility > 0.001f)
                        VisibilityByPair[key] = visibility;
                    else
                        VisibilityByPair.Remove(key);
                }
            }

            if (VisibilityByPair.Count > touched.Count)
            {
                var stale = new List<long>(8);
                foreach (var kv in VisibilityByPair)
                {
                    if (!touched.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    long key = stale[i];
                    float visibility = VisibilityByPair[key];
                    visibility = Mathf.MoveTowards(visibility, 0f, FadeOutPerSecond * dt);
                    if (visibility > 0.001f)
                        VisibilityByPair[key] = visibility;
                    else
                        VisibilityByPair.Remove(key);
                }
            }
        }

        public static float GetVisibility(int shipIndex, int gemIndex) =>
            VisibilityByPair.TryGetValue(PairKey(shipIndex, gemIndex), out float visibility) ? visibility : 0f;

        public static void Clear()
        {
            VisibilityByPair.Clear();
            _lastUpdateFrame = -1;
            GemTractorBeamDeployTracker.Clear();
            GemTractorBeamClientLogic.Clear();
        }

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
