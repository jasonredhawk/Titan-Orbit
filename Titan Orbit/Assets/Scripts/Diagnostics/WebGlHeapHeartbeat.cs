using System;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using TitanOrbit;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Logs WASM / managed memory on a WebGL player.
    /// Counts only. No scene-wide object scans: those committed native pages every sample.
    /// </summary>
    static class WebGlHeapHeartbeat
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer && !Application.isEditor)
                return;

            var go = new GameObject(nameof(WebGlHeapHeartbeat));
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            float _nextSec;
            bool _hasPrev;
            bool _hasSec;
            int _secFill;
            long _secBaseAlloc;
            readonly long[] _sec = new long[5];
            readonly long[] _alloc = new long[WebGlAllocBuckets.Count];
            readonly long[] _mono = new long[WebGlAllocBuckets.Count];
            long _prevAllocated;
            long _prevReserved;
            long _prevMonoUsed;
            int _prevStoreLabels;

            void Update()
            {
                WebGlAllocBuckets.NoteFrame();
                if (Time.unscaledTime < _nextSec)
                    return;

                _nextSec = Time.unscaledTime + 1f;
                long secAllocated = Profiler.GetTotalAllocatedMemoryLong();
                _sec[_secFill] = _hasSec ? (secAllocated - _secBaseAlloc) / 1024L : 0L;
                _secBaseAlloc = secAllocated;
                _hasSec = true;
                _secFill++;
                if (_secFill < 5)
                    return;

                _secFill = 0;
                long allocated = Profiler.GetTotalAllocatedMemoryLong();
                long reserved = Profiler.GetTotalReservedMemoryLong();
                long monoUsed = Profiler.GetMonoUsedSizeLong();
                long monoHeap = Profiler.GetMonoHeapSizeLong();
                long dAllocated = _hasPrev ? allocated - _prevAllocated : 0L;
                long dReserved = _hasPrev ? reserved - _prevReserved : 0L;
                long dMonoUsed = _hasPrev ? monoUsed - _prevMonoUsed : 0L;
                int storeRefreshes = MaterialCloneProbe.StoreRefreshes;
                int storeLabels = MaterialCloneProbe.StoreLabelSets;
                int dStoreLabels = _hasPrev ? storeLabels - _prevStoreLabels : 0;
                _prevAllocated = allocated;
                _prevReserved = reserved;
                _prevMonoUsed = monoUsed;
                _prevStoreLabels = storeLabels;
                _hasPrev = true;
                WebGlAllocBuckets.CopyAndReset(_alloc, _mono, out int frames);

                // #region agent log
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json =
                    "{\"sessionId\":\"caa453\",\"runId\":\"post-slice\",\"hypothesisId\":\"H9\"," +
                    "\"location\":\"WebGlHeapHeartbeat.cs:Update\",\"message\":\"slice\"," +
                    "\"timestamp\":" + nowMs +
                    ",\"data\":{\"allocatedMB\":" + (allocated / 1048576L) +
                    ",\"reservedMB\":" + (reserved / 1048576L) +
                    ",\"monoUsedMB\":" + (monoUsed / 1048576L) +
                    ",\"monoHeapMB\":" + (monoHeap / 1048576L) +
                    ",\"dAllocatedKB\":" + (dAllocated / 1024L) +
                    ",\"dReservedKB\":" + (dReserved / 1024L) +
                    ",\"dMonoUsedKB\":" + (dMonoUsed / 1024L) +
                    ",\"gc0\":" + GC.CollectionCount(0) +
                    ",\"frames\":" + frames +
                    ",\"s0\":" + _sec[0] +
                    ",\"s1\":" + _sec[1] +
                    ",\"s2\":" + _sec[2] +
                    ",\"s3\":" + _sec[3] +
                    ",\"s4\":" + _sec[4] +
                    ",\"viz\":" + (_alloc[0] / 1024L) +
                    ",\"vizR\":" + (_alloc[1] / 1024L) +
                    ",\"bul\":" + (_alloc[2] / 1024L) +
                    ",\"map\":" + (_alloc[3] / 1024L) +
                    ",\"name\":" + (_alloc[4] / 1024L) +
                    ",\"spd\":" + (_alloc[5] / 1024L) +
                    ",\"tr\":" + (_alloc[6] / 1024L) +
                    ",\"pl\":" + (_alloc[7] / 1024L) +
                    ",\"vizM\":" + (_mono[0] / 1024L) +
                    ",\"vizRM\":" + (_mono[1] / 1024L) +
                    ",\"bulM\":" + (_mono[2] / 1024L) +
                    ",\"mapM\":" + (_mono[3] / 1024L) +
                    ",\"nameM\":" + (_mono[4] / 1024L) +
                    ",\"spdM\":" + (_mono[5] / 1024L) +
                    ",\"trM\":" + (_mono[6] / 1024L) +
                    ",\"plM\":" + (_mono[7] / 1024L) +
                    ",\"storeRefreshes\":" + storeRefreshes +
                    ",\"dStoreLabels\":" + dStoreLabels +
                    "}}";
                Debug.Log("[MemCheap] " + json);
                if (Application.isEditor)
                {
                    try
                    {
                        File.AppendAllText(
                            @"c:\Users\jason\Documents\repo\Titan-Orbit\debug-caa453.log",
                            json + "\n");
                    }
                    catch (Exception)
                    {
                    }
                }
                // #endregion
            }
        }
    }
}
