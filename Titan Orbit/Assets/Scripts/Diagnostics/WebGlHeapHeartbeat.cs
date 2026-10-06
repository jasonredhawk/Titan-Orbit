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
        const float IntervalSeconds = 5f;

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
            float _next;
            bool _hasPrev;
            long _prevAllocated;
            long _prevReserved;
            long _prevMonoUsed;
            int _prevStoreRefreshes;
            int _prevStoreLabels;

            void Update()
            {
                if (Time.unscaledTime < _next)
                    return;

                _next = Time.unscaledTime + IntervalSeconds;
                long allocated = Profiler.GetTotalAllocatedMemoryLong();
                long reserved = Profiler.GetTotalReservedMemoryLong();
                long monoUsed = Profiler.GetMonoUsedSizeLong();
                long monoHeap = Profiler.GetMonoHeapSizeLong();
                long dAllocated = _hasPrev ? allocated - _prevAllocated : 0L;
                long dReserved = _hasPrev ? reserved - _prevReserved : 0L;
                long dMonoUsed = _hasPrev ? monoUsed - _prevMonoUsed : 0L;
                int storeRefreshes = MaterialCloneProbe.StoreRefreshes;
                int storeLabels = MaterialCloneProbe.StoreLabelSets;
                int dStoreRefreshes = _hasPrev ? storeRefreshes - _prevStoreRefreshes : 0;
                int dStoreLabels = _hasPrev ? storeLabels - _prevStoreLabels : 0;
                _prevAllocated = allocated;
                _prevReserved = reserved;
                _prevMonoUsed = monoUsed;
                _prevStoreRefreshes = storeRefreshes;
                _prevStoreLabels = storeLabels;
                _hasPrev = true;

                // #region agent log
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json =
                    "{\"sessionId\":\"caa453\",\"runId\":\"post-query\",\"hypothesisId\":\"H8\"," +
                    "\"location\":\"WebGlHeapHeartbeat.cs:Update\",\"message\":\"cheap\"," +
                    "\"timestamp\":" + nowMs +
                    ",\"data\":{\"allocatedMB\":" + (allocated / 1048576L) +
                    ",\"reservedMB\":" + (reserved / 1048576L) +
                    ",\"monoUsedMB\":" + (monoUsed / 1048576L) +
                    ",\"monoHeapMB\":" + (monoHeap / 1048576L) +
                    ",\"dAllocatedKB\":" + (dAllocated / 1024L) +
                    ",\"dReservedKB\":" + (dReserved / 1024L) +
                    ",\"dMonoUsedKB\":" + (dMonoUsed / 1024L) +
                    ",\"gc0\":" + GC.CollectionCount(0) +
                    ",\"storeRefreshes\":" + storeRefreshes +
                    ",\"dStoreRefreshes\":" + dStoreRefreshes +
                    ",\"storeLabelSets\":" + storeLabels +
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
