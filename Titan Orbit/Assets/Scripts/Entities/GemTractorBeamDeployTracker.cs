using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Client + server timing for tractor beam deploy: thin line extends to the gem (duration scales with distance), widens, then pull begins.
    /// </summary>
    public static class GemTractorBeamDeployTracker
    {
        /// <summary>How fast the thin beam line travels along the path (m/s).</summary>
        public const float ExtendLineSpeed = 16f;

        public const float MinExtendDuration = 0.07f;
        public const float MaxExtendDuration = 0.32f;
        public const float WidthExpandDuration = 0.05f;

        public const float ExtendLineThickness = 0.065f;

        private static readonly Dictionary<long, DeployState> stateByPair = new Dictionary<long, DeployState>(128);
        private static int lastUpdateFrame = -1;

        private struct DeployState
        {
            public float lockStartTime;
            public float lockDistance;
            public float extendDuration;
            /// <summary>Wing that established the lock (-1 = ship center fallback for wingless ships).</summary>
            public int lockingWingIndex;
        }

        public static void LateUpdateTick()
        {
            if (Time.frameCount == lastUpdateFrame)
                return;
            lastUpdateFrame = Time.frameCount;

            var ships = Starship.AllStarships;
            var gems = Gem.AllGems;
            if (ships == null || gems == null)
            {
                stateByPair.Clear();
                return;
            }

            float now = GetNow();
            var active = new HashSet<long>(stateByPair.Count);

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

                    if (!GemTractorBeamSettings.PassesBasicMagneticPullEligibility(ship, gem))
                        continue;
                    if (!GemTractorBeamSettings.IsWithinCandidateMagneticPullRange(ship, gem))
                        continue;

                    long key = PairKey(ship.GetInstanceID(), gem.GetInstanceID());
                    active.Add(key);

                    if (stateByPair.ContainsKey(key))
                        continue;

                    int lockingWingIndex = GemTractorBeamSettings.GetClosestInRangeWingIndex(ship, gem);
                    Vector3 origin = GemTractorBeamSettings.GetWingBeamOrigin(ship, lockingWingIndex);
                    Vector3 gemPos = GetGemWorldPosition(gem);
                    float dist = ToroidalMap.ToroidalDistance(origin, gemPos);
                    float extendDuration = ComputeExtendDuration(dist);

                    stateByPair[key] = new DeployState
                    {
                        lockStartTime = now,
                        lockDistance = dist,
                        extendDuration = extendDuration,
                        lockingWingIndex = lockingWingIndex
                    };
                }
            }

            if (stateByPair.Count > active.Count)
            {
                var stale = new List<long>(8);
                foreach (var kv in stateByPair)
                {
                    if (!active.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                    stateByPair.Remove(stale[i]);
            }
        }

        public static float ComputeExtendDuration(float toroidalDistance)
        {
            float dist = Mathf.Max(0f, toroidalDistance);
            return Mathf.Clamp(dist / ExtendLineSpeed, MinExtendDuration, MaxExtendDuration);
        }

        public static void EnsureDeployState(Starship ship, Gem gem)
        {
            if (ship == null || gem == null)
                return;
            if (TryGetState(ship, gem, out _))
                return;
            if (!GemTractorBeamSettings.PassesBasicMagneticPullEligibility(ship, gem))
                return;
            if (!GemTractorBeamSettings.IsWithinCandidateMagneticPullRange(ship, gem))
                return;

            int lockingWingIndex = GemTractorBeamSettings.GetClosestInRangeWingIndex(ship, gem);
            Vector3 origin = GemTractorBeamSettings.GetWingBeamOrigin(ship, lockingWingIndex);
            Vector3 gemPos = GetGemWorldPosition(gem);
            float dist = ToroidalMap.ToroidalDistance(origin, gemPos);
            float now = GetNow();
            long key = PairKey(ship.GetInstanceID(), gem.GetInstanceID());
            stateByPair[key] = new DeployState
            {
                lockStartTime = now,
                lockDistance = dist,
                extendDuration = ComputeExtendDuration(dist),
                lockingWingIndex = lockingWingIndex
            };
        }

        /// <summary>True when a deploy/tractor lock exists for this ship–gem pair (beam connected).</summary>
        public static bool HasActiveLock(Starship ship, Gem gem) => TryGetState(ship, gem, out _);

        public static bool TryGetLockingWingIndex(Starship ship, Gem gem, out int wingIndex)
        {
            wingIndex = -1;
            if (!TryGetState(ship, gem, out DeployState state))
                return false;

            wingIndex = state.lockingWingIndex;
            return true;
        }

        public static float GetExtendDuration(Starship ship, Gem gem)
        {
            EnsureDeployState(ship, gem);
            if (TryGetState(ship, gem, out DeployState state))
                return state.extendDuration;
            return MinExtendDuration;
        }

        public static float GetTotalDeployDuration(Starship ship, Gem gem) =>
            GetExtendDuration(ship, gem) + WidthExpandDuration;

        public static float GetElapsed(Starship ship, Gem gem)
        {
            EnsureDeployState(ship, gem);
            if (!TryGetState(ship, gem, out DeployState state))
                return 0f;

            return Mathf.Max(0f, GetNow() - state.lockStartTime);
        }

        /// <summary>0–1 while the thin beam line grows toward the gem.</summary>
        public static float GetExtensionProgress(Starship ship, Gem gem)
        {
            EnsureDeployState(ship, gem);
            float extendDuration = GetExtendDuration(ship, gem);
            if (extendDuration <= 0.0001f)
                return 1f;
            return Mathf.Clamp01(GetElapsed(ship, gem) / extendDuration);
        }

        /// <summary>0–1 while the beam widens at the gem after extension completes.</summary>
        public static float GetWidthExpandProgress(Starship ship, Gem gem)
        {
            float elapsed = GetElapsed(ship, gem);
            float extendDuration = GetExtendDuration(ship, gem);
            if (elapsed <= extendDuration)
                return 0f;
            return Mathf.Clamp01((elapsed - extendDuration) / WidthExpandDuration);
        }

        public static bool IsInDeployAnimation(Starship ship, Gem gem)
        {
            EnsureDeployState(ship, gem);
            return TryGetState(ship, gem, out _) && !IsPullPhysicsActive(ship, gem);
        }

        /// <summary>Reads existing deploy timing only — safe during <see cref="GemTractorBeamSettings"/> pull-set build.</summary>
        public static bool TryIsPullPhysicsActive(Starship ship, Gem gem)
        {
            if (!TryGetState(ship, gem, out DeployState state))
                return false;

            float elapsed = Mathf.Max(0f, GetNow() - state.lockStartTime);
            float total = state.extendDuration + WidthExpandDuration;
            return elapsed >= total - 0.0001f;
        }

        public static bool IsPullPhysicsActive(Starship ship, Gem gem)
        {
            EnsureDeployState(ship, gem);
            return TryIsPullPhysicsActive(ship, gem);
        }

        /// <summary>Toroidal distance locked when the beam first acquired this gem.</summary>
        public static float GetLockDistance(Starship ship, Gem gem)
        {
            if (TryGetState(ship, gem, out DeployState state))
                return state.lockDistance;
            return 0f;
        }

        public static void Clear()
        {
            stateByPair.Clear();
            lastUpdateFrame = -1;
        }

        private static bool TryGetState(Starship ship, Gem gem, out DeployState state)
        {
            state = default;
            if (ship == null || gem == null)
                return false;

            return stateByPair.TryGetValue(PairKey(ship.GetInstanceID(), gem.GetInstanceID()), out state);
        }

        private static float GetNow()
        {
            if (Application.isPlaying &&
                NetworkManager.Singleton != null &&
                NetworkManager.Singleton.IsListening)
            {
                return (float)NetworkManager.Singleton.ServerTime.Time;
            }

            return Time.time;
        }

        private static Vector3 GetGemWorldPosition(Gem gem)
        {
            var rb = gem.GetComponent<Rigidbody>();
            Vector3 pos = rb != null ? rb.position : gem.transform.position;
            pos.y = 0f;
            return pos;
        }

        private static long PairKey(int shipId, int gemId) => ((long)shipId << 32) | (uint)gemId;
    }
}
