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

        public static void NoteFrame()
        {
            Frames++;
        }

        public static Scope Measure(int slot)
        {
            return new Scope(slot);
        }

        public static void CopyAndReset(long[] allocOut, long[] monoOut, out int frames)
        {
            frames = Frames;
            Frames = 0;
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
