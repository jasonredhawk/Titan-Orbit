using System;
using System.Collections;
using System.IO;
using Unity.Collections;
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
            go.AddComponent<PhaseEarly>();
            go.AddComponent<PhaseUpdateEnd>();
            go.AddComponent<PhaseSimEnd>();
            go.AddComponent<PhaseLate>();
            go.AddComponent<PhaseEndOfFrame>();
            go.AddComponent<Runner>();
        }

        [DefaultExecutionOrder(-32000)]
        sealed class PhaseEarly : MonoBehaviour
        {
            void Update()
            {
                WebGlAllocBuckets.MarkEarly();
            }
        }

        [DefaultExecutionOrder(32000)]
        sealed class PhaseUpdateEnd : MonoBehaviour
        {
            void Update()
            {
                WebGlAllocBuckets.MarkUpdateEnd();
            }
        }

        /// <summary>
        /// Runs before every other LateUpdate. SimulationSystemGroup is the last Update child,
        /// so this mark is the first point after the client tick.
        /// </summary>
        [DefaultExecutionOrder(-32000)]
        sealed class PhaseSimEnd : MonoBehaviour
        {
            void LateUpdate()
            {
                WebGlAllocBuckets.MarkSimEnd();
            }
        }

        [DefaultExecutionOrder(32000)]
        sealed class PhaseLate : MonoBehaviour
        {
            void LateUpdate()
            {
                WebGlAllocBuckets.MarkLate();
            }
        }

        sealed class PhaseEndOfFrame : MonoBehaviour
        {
            void OnEnable()
            {
                StartCoroutine(Run());
            }

            IEnumerator Run()
            {
                var wait = new WaitForEndOfFrame();
                while (isActiveAndEnabled)
                {
                    yield return wait;
                    WebGlAllocBuckets.MarkEndOfFrame();
                }
            }
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
                WebGlAllocBuckets.CopyAndReset(
                    _alloc, _mono, out int frames, out long post, out long early, out long updatePhase, out long latePhase, out long simPhase, out long lateUiPhase, out long simGhost, out long simPhysBuild, out long simPhysSolve, out long simPred, out long simRest, out int simPhysSteps, out int endOfFrameMarks, out long physInit, out long physPairs, out long physContacts, out long physJac, out long physSolveOnly, out long physAfter, out long physPreBuild);

                long streamProbe = 0L;
                long oneStream = 0L;
                long oneArray = 0L;
                long worldAlloc = WebGlAllocBuckets.SimPhysWorldAlloc;
                long physReset = WebGlAllocBuckets.PhysResetAlloc;
                long physMid = WebGlAllocBuckets.PhysMidAlloc;
                long physStream = WebGlAllocBuckets.PhysStreamAlloc;
                long physCopy = WebGlAllocBuckets.PhysCopyAlloc;
                long physTree = WebGlAllocBuckets.PhysTreeAlloc;
                long physHead = WebGlAllocBuckets.PhysHeadAlloc;
                long physData = WebGlAllocBuckets.PhysDataAlloc;
                long physCount = WebGlAllocBuckets.PhysCountAlloc;
                long physCohere = WebGlAllocBuckets.PhysCohereAlloc;
                long physCohereDyn = WebGlAllocBuckets.PhysCohereDynAlloc;
                long physCohereSta = WebGlAllocBuckets.PhysCohereStaAlloc;
                long physCohereCombine = WebGlAllocBuckets.PhysCohereCombineAlloc;
                long physDispose = WebGlAllocBuckets.PhysDisposeAlloc;
                long physEpilogue = WebGlAllocBuckets.PhysEpilogueAlloc;
                long physList = WebGlAllocBuckets.PhysListDisposeAlloc;
                long physSingleton = WebGlAllocBuckets.PhysSingletonAlloc;
                long accounted = physHead + physData + physCount + physReset + physMid + physStream + physCopy + physTree
                    + physCohere + physDispose + physEpilogue + physList + physSingleton;
                long gap = worldAlloc - accounted;
                WebGlAllocBuckets.SimPhysWorldAlloc = 0L;
                WebGlAllocBuckets.PhysResetAlloc = 0L;
                WebGlAllocBuckets.PhysMidAlloc = 0L;
                WebGlAllocBuckets.PhysStreamAlloc = 0L;
                WebGlAllocBuckets.PhysCopyAlloc = 0L;
                WebGlAllocBuckets.PhysTreeAlloc = 0L;
                WebGlAllocBuckets.PhysHeadAlloc = 0L;
                WebGlAllocBuckets.PhysDataAlloc = 0L;
                WebGlAllocBuckets.PhysCountAlloc = 0L;
                WebGlAllocBuckets.PhysCohereAlloc = 0L;
                WebGlAllocBuckets.PhysCohereDynAlloc = 0L;
                WebGlAllocBuckets.PhysCohereStaAlloc = 0L;
                WebGlAllocBuckets.PhysCohereCombineAlloc = 0L;
                WebGlAllocBuckets.PhysDisposeAlloc = 0L;
                WebGlAllocBuckets.PhysEpilogueAlloc = 0L;
                WebGlAllocBuckets.PhysListDisposeAlloc = 0L;
                WebGlAllocBuckets.PhysSingletonAlloc = 0L;
#if UNITY_WEBGL && !UNITY_EDITOR
                long sizeBefore = Profiler.GetTotalAllocatedMemoryLong();
                var sizedStream = new NativeStream(1, Allocator.Persistent);
                oneStream = Profiler.GetTotalAllocatedMemoryLong() - sizeBefore;
                sizedStream.Dispose();
                long arrayBefore = Profiler.GetTotalAllocatedMemoryLong();
                var sizedArray = new NativeArray<int>(1, Allocator.Persistent);
                oneArray = Profiler.GetTotalAllocatedMemoryLong() - arrayBefore;
                sizedArray.Dispose();
                long streamBefore = Profiler.GetTotalAllocatedMemoryLong();
                var streamA = new NativeStream(1, Allocator.Persistent);
                var streamB = new NativeStream(1, Allocator.Persistent);
                var streamC = new NativeStream(1, Allocator.Persistent);
                streamA.Dispose();
                streamB.Dispose();
                streamC.Dispose();
                streamProbe = Profiler.GetTotalAllocatedMemoryLong() - streamBefore;
#endif

                // #region agent log
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json =
                    "{\"sessionId\":\"b1f93a\",\"runId\":\"post-fix-2\",\"hypothesisId\":\"H28\"," +
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
                    ",\"eof\":" + endOfFrameMarks +
                    ",\"s0\":" + _sec[0] +
                    ",\"s1\":" + _sec[1] +
                    ",\"s2\":" + _sec[2] +
                    ",\"s3\":" + _sec[3] +
                    ",\"s4\":" + _sec[4] +
                    ",\"post\":" + (post / 1024L) +
                    ",\"early\":" + (early / 1024L) +
                    ",\"upd\":" + (updatePhase / 1024L) +
                    ",\"late\":" + (latePhase / 1024L) +
                    ",\"sim\":" + (simPhase / 1024L) +
                    ",\"lateUi\":" + (lateUiPhase / 1024L) +
                    ",\"ghost\":" + (simGhost / 1024L) +
                    ",\"physB\":" + (simPhysBuild / 1024L) +
                    ",\"physS\":" + (simPhysSolve / 1024L) +
                    ",\"pred\":" + (simPred / 1024L) +
                    ",\"simR\":" + (simRest / 1024L) +
                    ",\"physN\":" + simPhysSteps +
                    ",\"pre\":" + (physPreBuild / 1024L) +
                    ",\"wld\":" + (worldAlloc / 1024L) +
                    ",\"rst\":" + (physReset / 1024L) +
                    ",\"mid\":" + (physMid / 1024L) +
                    ",\"strm\":" + (physStream / 1024L) +
                    ",\"cpy\":" + (physCopy / 1024L) +
                    ",\"tree\":" + (physTree / 1024L) +
                    ",\"head\":" + (physHead / 1024L) +
                    ",\"dat\":" + (physData / 1024L) +
                    ",\"cnt\":" + (physCount / 1024L) +
                    ",\"coh\":" + (physCohere / 1024L) +
                    ",\"dyn\":" + (physCohereDyn / 1024L) +
                    ",\"sta\":" + (physCohereSta / 1024L) +
                    ",\"cmb\":" + (physCohereCombine / 1024L) +
                    ",\"dsp\":" + (physDispose / 1024L) +
                    ",\"epi\":" + (physEpilogue / 1024L) +
                    ",\"lst\":" + (physList / 1024L) +
                    ",\"sng\":" + (physSingleton / 1024L) +
                    ",\"gap\":" + (gap / 1024L) +
                    ",\"bld\":" + (physInit / 1024L) +
                    ",\"brd\":" + (physPairs / 1024L) +
                    ",\"con\":" + (physContacts / 1024L) +
                    ",\"jac\":" + (physJac / 1024L) +
                    ",\"sol\":" + (physSolveOnly / 1024L) +
                    ",\"aft\":" + (physAfter / 1024L) +
                    ",\"streamProbe\":" + streamProbe +
                    ",\"oneStream\":" + oneStream +
                    ",\"oneArray\":" + oneArray +
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
                try
                {
                    File.AppendAllText(
                        @"c:\Users\jason\Documents\repo\Titan-Orbit\debug-b1f93a.log",
                        json + "\n");
                }
                catch (Exception)
                {
                }
                // #endregion
            }
        }
    }
}
