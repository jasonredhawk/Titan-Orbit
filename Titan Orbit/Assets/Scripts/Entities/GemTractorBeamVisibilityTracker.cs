using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Client-side fade/hold for tractor-beam visuals so pull detection jitter does not pop beams on/off.
    /// </summary>
    public static class GemTractorBeamVisibilityTracker
    {
        private static readonly Dictionary<long, float> visibilityByPair = new Dictionary<long, float>(128);
        private static int lastUpdateFrame = -1;

        private const float FadeInPerSecond = 5f;
        private const float FadeOutPerSecond = 1.8f;

        public static void LateUpdateTick()
        {
            if (Time.frameCount == lastUpdateFrame)
                return;
            lastUpdateFrame = Time.frameCount;

            GemTractorBeamDeployTracker.LateUpdateTick();

            var ships = Starship.AllStarships;
            var gems = Gem.AllGems;
            if (ships == null || gems == null)
            {
                visibilityByPair.Clear();
                return;
            }

            float dt = Time.deltaTime;
            var touched = new HashSet<long>(visibilityByPair.Count);

            for (int si = 0; si < ships.Count; si++)
            {
                Starship ship = ships[si];
                if (ship == null || !ship.IsSpawned || ship.IsDead)
                    continue;

                for (int gi = 0; gi < gems.Count; gi++)
                {
                    Gem gem = gems[gi];
                    if (gem == null || !gem.IsSpawned || gem.IsInPool || gem.IsDepositGem || gem.Value <= 0f)
                        continue;

                    long key = PairKey(ship.GetInstanceID(), gem.GetInstanceID());
                    touched.Add(key);

                    bool wantsVisible = GemTractorBeamSettings.IsEligibleForBeamVisual(ship, gem);
                    visibilityByPair.TryGetValue(key, out float visibility);

                    float fadeSpeed = wantsVisible ? FadeInPerSecond : FadeOutPerSecond;
                    visibility = Mathf.MoveTowards(visibility, wantsVisible ? 1f : 0f, fadeSpeed * dt);

                    if (visibility > 0.001f)
                        visibilityByPair[key] = visibility;
                    else
                        visibilityByPair.Remove(key);
                }
            }

            if (visibilityByPair.Count > touched.Count)
            {
                var stale = new List<long>(8);
                foreach (var kv in visibilityByPair)
                {
                    if (!touched.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    long key = stale[i];
                    float visibility = visibilityByPair[key];
                    visibility = Mathf.MoveTowards(visibility, 0f, FadeOutPerSecond * dt);
                    if (visibility > 0.001f)
                        visibilityByPair[key] = visibility;
                    else
                        visibilityByPair.Remove(key);
                }
            }
        }

        public static float GetVisibility(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return 0f;

            return visibilityByPair.TryGetValue(PairKey(ship.GetInstanceID(), gem.GetInstanceID()), out float visibility)
                ? visibility
                : 0f;
        }

        public static void Clear()
        {
            visibilityByPair.Clear();
            lastUpdateFrame = -1;
            GemTractorBeamDeployTracker.Clear();
        }

        private static long PairKey(int shipId, int gemId) => ((long)shipId << 32) | (uint)gemId;
    }
}
