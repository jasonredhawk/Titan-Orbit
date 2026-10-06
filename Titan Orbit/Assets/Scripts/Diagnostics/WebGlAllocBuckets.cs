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

        public static void MarkLate()
        {
            long now = Profiler.GetTotalAllocatedMemoryLong();
            if (_phase == 3)
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
            out int endOfFrameMarks)
        {
            frames = Frames;
            Frames = 0;
            post = PostAlloc;
            early = EarlyAlloc;
            update = UpdateAlloc;
            late = LateAlloc;
            endOfFrameMarks = EndOfFrameMarks;
            PostAlloc = 0L;
            EarlyAlloc = 0L;
            UpdateAlloc = 0L;
            LateAlloc = 0L;
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
