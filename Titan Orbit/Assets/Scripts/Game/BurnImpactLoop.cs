using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// One bullet-impact instance per burning body. Each damage-over-time tick re-emits the
    /// bank burst at the hit point and parents it so the fire follows a moving ship or rock.
    /// Cosmetic only — ticks still come from the server burn HitRpc.
    /// </summary>
    public static class BurnImpactLoop
    {
        /// <summary>Drop the instance this long after the last tick so a 0.5s DoT does not flicker off.</summary>
        const float HoldSeconds = 0.75f;

        const int MaxLive = 12;

        struct Live
        {
            public int Key;
            public Transform Parent;
            public bool HadParent;
            public GameObject Go;
            public int BankIndex;
            public TeamId Team;
            public float LastPulse;
        }

        static readonly List<Live> s_Live = new List<Live>(MaxLive);

        /// <summary>
        /// Refresh or start the impact loop at this burn tick. <paramref name="preferNonShip"/>
        /// keeps asteroid burns on the rock when a hull is also nearby.
        /// </summary>
        public static void Pulse(float3 logicalHit, int bankIndex, TeamId team, bool preferNonShip)
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;

            if (!TryPlace(logicalHit, preferNonShip, out Transform parent, out Vector3 worldPos))
                return;

            int key = KeyFor(parent, worldPos);
            float now = Time.time;
            for (int i = 0; i < s_Live.Count; i++)
            {
                Live live = s_Live[i];
                if (live.Key != key)
                    continue;

                if (live.Go == null || live.BankIndex != bankIndex || live.Team != team)
                {
                    ReleaseAt(i);
                    break;
                }

                Place(live.Go, parent, worldPos);
                VfxUrpCompat.ReplayParticleBursts(live.Go);
                live.Parent = parent;
                live.HadParent = parent != null;
                live.LastPulse = now;
                s_Live[i] = live;
                return;
            }

            if (s_Live.Count >= MaxLive)
                ReleaseOldest();

            if (!TryRent(parent, worldPos, bankIndex, team, out GameObject go))
                return;

            s_Live.Add(new Live
            {
                Key = key,
                Parent = parent,
                HadParent = parent != null,
                Go = go,
                BankIndex = bankIndex,
                Team = team,
                LastPulse = now,
            });
        }

        /// <summary>Stops the loop on the body at this point (asteroid kill — the boom plays separately).</summary>
        public static void StopAt(float3 logicalHit, bool preferNonShip)
        {
            if (!TryPlace(logicalHit, preferNonShip, out Transform parent, out Vector3 worldPos))
                return;

            int key = KeyFor(parent, worldPos);
            for (int i = s_Live.Count - 1; i >= 0; i--)
            {
                if (s_Live[i].Key == key)
                    ReleaseAt(i);
            }
        }

        /// <summary>Releases loops whose ticks have stopped, or whose parent proxy is gone.</summary>
        public static void Tick()
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
            {
                ReleaseAll();
                return;
            }

            float now = Time.time;
            for (int i = s_Live.Count - 1; i >= 0; i--)
            {
                Live live = s_Live[i];
                bool lostParent = live.HadParent && live.Parent == null;
                if (live.Go == null || lostParent || now - live.LastPulse > HoldSeconds)
                    ReleaseAt(i);
            }
        }

        public static void ReleaseAll()
        {
            for (int i = s_Live.Count - 1; i >= 0; i--)
                ReleaseAt(i);
        }

        static bool TryPlace(float3 logicalHit, bool preferNonShip, out Transform parent, out Vector3 worldPos)
        {
            BulletImpactAttach.TryResolveAtLogicalPoint(
                logicalHit, preferNonShip, out parent, out worldPos);
            if (parent == null && ToroidalDisplay.TryGetReferencePosition(out Vector3 reference))
                worldPos = ToroidalDisplay.ToDisplayPosition(logicalHit, reference);
            return true;
        }

        static bool TryRent(
            Transform parent,
            Vector3 worldPos,
            int bankIndex,
            TeamId team,
            out GameObject go)
        {
            go = null;
            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return false;

            GameObject prefab = bank.GetImpactPrefab(bankIndex, team);
            if (prefab == null)
                return false;
            if (!BulletOneShotVfxPool.TryRent(prefab, out go) || go == null)
                return false;

            go.name = prefab.name + "_BurnLoop";
            // Scale in world space first, then parent with worldPositionStays so a large
            // rock or hull does not shrink the burst. Same order as a normal bullet impact.
            go.transform.SetParent(null, false);
            go.transform.SetPositionAndRotation(worldPos, Quaternion.identity);
            float worldScale = BulletVisualFactory.GetImpactScale(bank, 1f, bankIndex);
            VfxUrpCompat.ApplyImpactVisualScale(go, worldScale);
            if (parent != null)
                go.transform.SetParent(parent, true);
            MuteAudio(go);
            VfxUrpCompat.SetParticleSystemsLooping(go, true);
            return true;
        }

        static void Place(GameObject go, Transform parent, Vector3 worldPos)
        {
            Transform t = go.transform;
            if (t.parent != parent)
                t.SetParent(parent, true);
            t.position = worldPos;
            t.rotation = Quaternion.identity;
        }

        static void ReleaseOldest()
        {
            int oldest = 0;
            float oldestTime = float.MaxValue;
            for (int i = 0; i < s_Live.Count; i++)
            {
                if (s_Live[i].LastPulse < oldestTime)
                {
                    oldestTime = s_Live[i].LastPulse;
                    oldest = i;
                }
            }

            ReleaseAt(oldest);
        }

        static void ReleaseAt(int index)
        {
            Live live = s_Live[index];
            s_Live.RemoveAt(index);
            if (live.Go == null)
                return;

            VfxUrpCompat.SetParticleSystemsLooping(live.Go, false);
            RestoreAudio(live.Go);
            BulletOneShotVfxPool.ReturnNow(live.Go);
        }

        static int KeyFor(Transform parent, Vector3 worldPos)
        {
            if (parent != null)
                return parent.GetInstanceID();

            int x = Mathf.RoundToInt(worldPos.x * 2f);
            int z = Mathf.RoundToInt(worldPos.z * 2f);
            return (x * 73856093) ^ (z * 19349663);
        }

        static void MuteAudio(GameObject root)
        {
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].enabled = false;
            }
        }

        static void RestoreAudio(GameObject root)
        {
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].enabled = true;
            }
        }
    }
}
