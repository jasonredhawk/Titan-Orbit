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
        private static int lastCommitFrame = -1;

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
                return;

            float now = Time.time;
            for (int i = 0; i < gems.Count; i++)
            {
                Gem gem = gems[i];
                if (gem == null || !gem.IsSpawned || gem.IsInPool)
                    continue;

                currentSampleByGemId[gem.GetInstanceID()] = new Sample
                {
                    pos = GetWorldPosition(gem),
                    time = now
                };
            }
        }

        /// <summary>Speed (m/s) the gem is moving toward <paramref name="ship"/> based on the previous frame sample.</summary>
        public static float GetTowardShipSpeed(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return 0f;

            if (!previousSampleByGemId.TryGetValue(gem.GetInstanceID(), out Sample prev))
                return 0f;

            Vector3 cur = GetWorldPosition(gem);
            float dt = Time.time - prev.time;
            if (dt < 0.0001f)
                return 0f;

            Vector2 deltaXZ = ToroidalMap.ShortestOffsetXZ(prev.pos, cur);
            Vector3 delta = new Vector3(deltaXZ.x, 0f, deltaXZ.y);

            Vector3 toShip = ToroidalMap.ToroidalDirection(cur, GetShipPosition(ship));
            toShip.y = 0f;
            if (toShip.sqrMagnitude < 0.0001f)
                return 0f;

            return Vector3.Dot(delta / dt, toShip.normalized);
        }

        public static void Clear()
        {
            previousSampleByGemId.Clear();
            currentSampleByGemId.Clear();
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
