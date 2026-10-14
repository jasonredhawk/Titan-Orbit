using System;
using UnityEngine.Profiling;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Per-frame allocation brackets for the WebGL heap heartbeat.
    /// Positive deltas only: a method that allocates and frees inside the same call stays near zero.
    /// </summary>
    public static class WebGlAllocBuckets
    {
        public const int Visualizer = 0;
        public const int VisualizerRender = 1;
        public const int Bullets = 2;
        public const int Minimap = 3;
        public const int Nameplates = 4;
        public const int Speedometer = 5;
        public const int Transport = 6;
        public const int Planets = 7;
        public const int Count = 8;

        public static readonly long[] Alloc = new long[Count];
        public static readonly long[] Mono = new long[Count];
        public static int Frames;

        /// <summary>Native growth from the end of LateUpdate through the end of the frame (render / PostLateUpdate).</summary>
        public static long PostAlloc;

        /// <summary>Native growth from end of frame until the next MonoBehaviour.Update (early player loop, ECS, physics).</summary>
        public static long EarlyAlloc;

        /// <summary>Native growth across MonoBehaviour.Update.</summary>
        public static long UpdateAlloc;

        /// <summary>Native growth from the end of Update through the end of LateUpdate (includes presentation).</summary>
        public static long LateAlloc;

        /// <summary>Native growth across SimulationSystemGroup (end of Update until the first LateUpdate).</summary>
        public static long SimAlloc;

        /// <summary>Native growth across LateUpdates that run at execution order 32000 or earlier.</summary>
        public static long LateUiAlloc;

        /// <summary>Ghost receive / snapshot group inside the simulation tick.</summary>
        public static long SimGhostAlloc;

        /// <summary>Physics build, broadphase, contacts, and jacobians.</summary>
        public static long SimPhysBuildAlloc;

        /// <summary>Physics solve, integrate, and export.</summary>
        public static long SimPhysSolveAlloc;

        /// <summary>BuildPhysicsWorld / physics initialize.</summary>
        public static long SimPhysInitAlloc;

        /// <summary>Broadphase body-pair creation.</summary>
        public static long SimPhysPairsAlloc;

        /// <summary>Narrowphase contact creation.</summary>
        public static long SimPhysContactsAlloc;

        /// <summary>Jacobian build.</summary>
        public static long SimPhysJacAlloc;

        /// <summary>Solve and integrate only.</summary>
        public static long SimPhysSolveOnlyAlloc;

        /// <summary>Export and AfterPhysics.</summary>
        public static long SimPhysAfterAlloc;

        /// <summary>Predicted simulation outside the physics group.</summary>
        public static long SimPredAlloc;

        /// <summary>SimulationSystemGroup outside ghost receive and prediction.</summary>
        public static long SimRestAlloc;

        /// <summary>How many physics steps the probes observed.</summary>
        public static int SimPhysSteps;

        static long _simCursor;
        static bool _simCursorOpen;

        public static void SimCursorReset()
        {
            _simCursor = Profiler.GetTotalAllocatedMemoryLong();
            _simCursorOpen = true;
        }

        public static void SimCursorAdd(ref long bucket)
        {
            long now = Profiler.GetTotalAllocatedMemoryLong();
            if (_simCursorOpen)
                bucket += now - _simCursor;

            _simCursor = now;
            _simCursorOpen = true;
        }

        /// <summary>How many times end-of-frame was observed. Zero means the render split did not run.</summary>
        public static int EndOfFrameMarks;

        static long _phaseMark;
        static int _phase;

        public static void NoteFrame()
        {
            Frames++;
        }

        public static void MarkEarly()
        {
            long now = Profiler.GetTotalAllocatedMemoryLong();
            if (_phase == 2)
            {
                long d = now - _phaseMark;
                if (d > 0L)
                    EarlyAlloc += d;
            }

            _phaseMark = now;
            _phase = 1;
        }

        public static void MarkUpdateEnd()
        {
            long now = Profiler.GetTotalAllocatedMemoryLong();
            if (_phase == 1)
            {
                long d = now - _phaseMark;
                if (d > 0L)
                    UpdateAlloc += d;
            }

            _phaseMark = now;
            _phase = 3;
        }

        /// <summary>First LateUpdate. Closes the simulation window opened by <see cref="MarkUpdateEnd"/>.</summary>
        public static void MarkSimEnd()
        {
            long now = Profiler.GetTotalAllocatedMemoryLong();
            if (_phase == 3)
            {
                long d = now - _phaseMark;
                if (d > 0L)
                {
                    SimAlloc += d;
                    LateAlloc += d;
                }
            }

            _phaseMark = now;
            _phase = 5;
        }

        public static void MarkLate()
        {
            long now = Profiler.GetTotalAllocatedMemoryLong();
            if (_phase == 5)
            {
                long d = now - _phaseMark;
                if (d > 0L)
                {
                    LateUiAlloc += d;
                    LateAlloc += d;
                }
            }
            else if (_phase == 3)
            {
                long d = now - _phaseMark;
                if (d > 0L)
                    LateAlloc += d;
            }

            _phaseMark = now;
            _phase = 4;
        }

        public static void MarkEndOfFrame()
        {
            EndOfFrameMarks++;
            long now = Profiler.GetTotalAllocatedMemoryLong();
            if (_phase == 4)
            {
                long d = now - _phaseMark;
                if (d > 0L)
                    PostAlloc += d;
            }

            _phaseMark = now;
            _phase = 2;
        }

        public static Scope Measure(int slot)
        {
            return new Scope(slot);
        }

        public static void CopyAndReset(
            long[] allocOut,
            long[] monoOut,
            out int frames,
            out long post,
            out long early,
            out long update,
            out long late,
            out long sim,
            out long lateUi,
            out long simGhost,
            out long simPhysBuild,
            out long simPhysSolve,
            out long simPred,
            out long simRest,
            out int simPhysSteps,
            out int endOfFrameMarks,
            out long physInit,
            out long physPairs,
            out long physContacts,
            out long physJac,
            out long physSolveOnly,
            out long physAfter)
        {
            frames = Frames;
            Frames = 0;
            post = PostAlloc;
            early = EarlyAlloc;
            update = UpdateAlloc;
            late = LateAlloc;
            sim = SimAlloc;
            lateUi = LateUiAlloc;
            simGhost = SimGhostAlloc;
            simPhysBuild = SimPhysBuildAlloc;
            simPhysSolve = SimPhysSolveAlloc;
            simPred = SimPredAlloc;
            simRest = SimRestAlloc;
            simPhysSteps = SimPhysSteps;
            endOfFrameMarks = EndOfFrameMarks;
            physInit = SimPhysInitAlloc;
            physPairs = SimPhysPairsAlloc;
            physContacts = SimPhysContactsAlloc;
            physJac = SimPhysJacAlloc;
            physSolveOnly = SimPhysSolveOnlyAlloc;
            physAfter = SimPhysAfterAlloc;
            simPhysBuild = physInit + physPairs + physContacts + physJac;
            simPhysSolve = physSolveOnly + physAfter;
            SimPhysInitAlloc = 0L;
            SimPhysPairsAlloc = 0L;
            SimPhysContactsAlloc = 0L;
            SimPhysJacAlloc = 0L;
            SimPhysSolveOnlyAlloc = 0L;
            SimPhysAfterAlloc = 0L;
            SimGhostAlloc = 0L;
            SimPhysBuildAlloc = 0L;
            SimPhysSolveAlloc = 0L;
            SimPredAlloc = 0L;
            SimRestAlloc = 0L;
            SimPhysSteps = 0;
            PostAlloc = 0L;
            EarlyAlloc = 0L;
            UpdateAlloc = 0L;
            LateAlloc = 0L;
            SimAlloc = 0L;
            LateUiAlloc = 0L;
            EndOfFrameMarks = 0;
            for (int i = 0; i < Count; i++)
            {
                allocOut[i] = Alloc[i];
                monoOut[i] = Mono[i];
                Alloc[i] = 0L;
                Mono[i] = 0L;
            }
        }

        public readonly struct Scope : IDisposable
        {
            readonly int _slot;
            readonly long _alloc;
            readonly long _mono;

            public Scope(int slot)
            {
                _slot = slot;
                _alloc = Profiler.GetTotalAllocatedMemoryLong();
                _mono = Profiler.GetMonoUsedSizeLong();
            }

            public void Dispose()
            {
                long dAlloc = Profiler.GetTotalAllocatedMemoryLong() - _alloc;
                long dMono = Profiler.GetMonoUsedSizeLong() - _mono;
                if (dAlloc > 0L)
                    Alloc[_slot] += dAlloc;
                if (dMono > 0L)
                    Mono[_slot] += dMono;
            }
        }
    }
}
