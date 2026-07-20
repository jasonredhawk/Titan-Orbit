using System.Collections.Generic;
using TitanOrbit.ECS;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client-side fade in/out for tractor beam visuals so wing reassignment does not pop beams.
    /// Orchestrates <see cref="GemTractorBeamDeployTracker"/> and <see cref="GemTractorBeamClientLogic"/>
    /// each LateUpdate. Visibility 0–1 multiplied into Shapes alpha in <see cref="GemTractorBeamVisual"/>.
    /// [TITAN-ORBIT] Gems from hybrid proxies under TransformQuarantine — never full gem ToEntityArray.
    /// </summary>
    public static class GemTractorBeamVisibilityTracker
    {
        /// <summary>0 = hidden, 1 = fully visible — keyed by packed shipIndex|gemIndex.</summary>
        static readonly Dictionary<long, float> VisibilityByPair = new Dictionary<long, float>(128);
        static readonly List<GemTractorBeamClientLogic.GemProxySnapshot> GemScratch =
            new List<GemTractorBeamClientLogic.GemProxySnapshot>(64);
        static int _lastUpdateFrame = -1;

        const float FadeInPerSecond = 5f;
        const float FadeOutPerSecond = 1.8f;

        public static void LateUpdateTick()
        {
            // --- Per-frame refresh ---
            if (Time.frameCount == _lastUpdateFrame)
                return;
            _lastUpdateFrame = Time.frameCount;

            // [TITAN-ORBIT] Settling OR GhostSpawnBacklog — quarantine must not hide beams for the
            // whole session, but ship ToEntityArray during post–Join Team Instantiates Crash!!!
            // (Settling stays OFF after JoinSettleCompleted; backlog covers the ship Instantiates window).
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
            {
                VisibilityByPair.Clear();
                return;
            }

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

            GemTractorBeamClientLogic.CollectGemProxies(em, GemScratch);

            for (int si = 0; si < ships.Length; si++)
            {
                if (!GemTractorBeamClientLogic.IsShipEligibleForBeam(shipStates[si]))
                    continue;

                var wings = em.HasBuffer<ECS.ShipWingTractorBeamElement>(ships[si])
                    ? em.GetBuffer<ECS.ShipWingTractorBeamElement>(ships[si])
                    : default;

                for (int gi = 0; gi < GemScratch.Count; gi++)
                {
                    var gem = GemScratch[gi];
                    long key = PairKey(ships[si].Index, gem.Entity.Index);
                    touched.Add(key);

                    bool wantsVisible = GemTractorBeamClientLogic.IsEligibleForBeamVisual(
                        em, ships[si], shipStates[si], shipTransforms[si], wings,
                        gem.Entity, gem.Transform, mapW, mapH);

                    VisibilityByPair.TryGetValue(key, out float visibility);

                    if (wantsVisible && GemTractorBeamDeployTracker.IsInDeployAnimation(ships[si].Index, gem.Entity.Index))
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
            // --- Clear state ---
            VisibilityByPair.Clear();
            GemScratch.Clear();
            _lastUpdateFrame = -1;
            GemTractorBeamDeployTracker.Clear();
            GemTractorBeamClientLogic.Clear();
        }

        static long PairKey(int shipIndex, int gemIndex) => ((long)shipIndex << 32) | (uint)gemIndex;
    }
}
