using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Infers gem motion from position deltas so tractor-beam visuals work on kinematic client proxies
    /// (NetworkRigidbody does not expose meaningful linearVelocity on clients).
    /// </summary>
    public static class GemTractorBeamMotionTracker
    {
        private struct Sample
        {
            public Vector3 pos;
            public float time;
        }

        private static readonly Dictionary<int, Sample> previousSampleByGemId = new Dictionary<int, Sample>(128);
        private static readonly Dictionary<int, Sample> currentSampleByGemId = new Dictionary<int, Sample>(128);
        private static readonly Dictionary<int, Vector3> smoothedVelocityByGemId = new Dictionary<int, Vector3>(128);
        private static int lastCommitFrame = -1;

        private const float VelocitySmoothing = 0.28f;

        /// <summary>
        /// Commits last frame's positions into <c>previous</c>, then samples current positions.
        /// Call from LateUpdate before camera rendering reads speeds.
        /// </summary>
        public static void LateUpdateTick()
        {
            if (Time.frameCount == lastCommitFrame)
                return;
            lastCommitFrame = Time.frameCount;

            previousSampleByGemId.Clear();
            foreach (var kv in currentSampleByGemId)
                previousSampleByGemId[kv.Key] = kv.Value;

            currentSampleByGemId.Clear();

            var gems = Gem.AllGems;
            if (gems == null)
            {
                smoothedVelocityByGemId.Clear();
                return;
            }

            var activeGemIds = new HashSet<int>(gems.Count);
            float now = Time.time;
            for (int i = 0; i < gems.Count; i++)
            {
                Gem gem = gems[i];
                if (gem == null || !gem.IsSpawned || gem.IsInPool)
                    continue;

                int gemId = gem.GetInstanceID();
                activeGemIds.Add(gemId);
                Vector3 cur = GetWorldPosition(gem);
                currentSampleByGemId[gemId] = new Sample
                {
                    pos = cur,
                    time = now
                };

                if (previousSampleByGemId.TryGetValue(gemId, out Sample prev))
                {
                    float dt = now - prev.time;
                    if (dt > 0.0001f)
                    {
                        Vector2 deltaXZ = ToroidalMap.ShortestOffsetXZ(prev.pos, cur);
                        Vector3 instantVel = new Vector3(deltaXZ.x, 0f, deltaXZ.y) / dt;
                        if (smoothedVelocityByGemId.TryGetValue(gemId, out Vector3 smoothedVel))
                            instantVel = Vector3.Lerp(smoothedVel, instantVel, VelocitySmoothing);
                        smoothedVelocityByGemId[gemId] = instantVel;
                    }
                }
            }

            if (smoothedVelocityByGemId.Count > activeGemIds.Count)
            {
                var staleGemIds = new List<int>(8);
                foreach (var kv in smoothedVelocityByGemId)
                {
                    if (!activeGemIds.Contains(kv.Key))
                        staleGemIds.Add(kv.Key);
                }

                for (int i = 0; i < staleGemIds.Count; i++)
                    smoothedVelocityByGemId.Remove(staleGemIds[i]);
            }
        }

        /// <summary>Speed (m/s) the gem is moving toward <paramref name="ship"/> based on the previous frame sample.</summary>
        public static float GetTowardShipSpeed(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return 0f;

            int gemId = gem.GetInstanceID();
            if (!smoothedVelocityByGemId.TryGetValue(gemId, out Vector3 velocity))
                return 0f;

            Vector3 cur = GetWorldPosition(gem);
            Vector3 toShip = ToroidalMap.ToroidalDirection(cur, GetShipPosition(ship));
            toShip.y = 0f;
            if (toShip.sqrMagnitude < 0.0001f)
                return 0f;

            return Vector3.Dot(velocity, toShip.normalized);
        }

        public static void Clear()
        {
            previousSampleByGemId.Clear();
            currentSampleByGemId.Clear();
            smoothedVelocityByGemId.Clear();
            lastCommitFrame = -1;
        }

        private static Vector3 GetShipPosition(Starship ship)
        {
            var rb = ship.GetComponent<Rigidbody>();
            Vector3 pos = rb != null ? rb.position : ship.transform.position;
            pos.y = 0f;
            return pos;
        }

        private static Vector3 GetWorldPosition(Gem gem)
        {
            var rb = gem.GetComponent<Rigidbody>();
            Vector3 pos = rb != null ? rb.position : gem.transform.position;
            pos.y = 0f;
            return pos;
        }
    }
}
