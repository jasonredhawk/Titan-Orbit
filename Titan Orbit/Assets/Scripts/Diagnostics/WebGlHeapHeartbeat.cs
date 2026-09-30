using System;
using UnityEngine;
using UnityEngine.Profiling;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Logs WASM / managed memory every 30 seconds on a WebGL player.
    /// A climbing <c>allocatedMB</c> with a flat ghost count is a client leak.
    /// The browser heap does not shrink after C# GC.
    /// </summary>
    static class WebGlHeapHeartbeat
    {
        const float IntervalSeconds = 30f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer)
                return;

            var go = new GameObject(nameof(WebGlHeapHeartbeat));
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            float _next;

            void Update()
            {
                if (Time.unscaledTime < _next)
                    return;

                _next = Time.unscaledTime + IntervalSeconds;
                long allocated = Profiler.GetTotalAllocatedMemoryLong();
                long reserved = Profiler.GetTotalReservedMemoryLong();
                long mono = GC.GetTotalMemory(false);
                Debug.Log(
                    "[WebGlHeap] allocatedMB=" + (allocated / 1048576L) +
                    " reservedMB=" + (reserved / 1048576L) +
                    " monoMB=" + (mono / 1048576L));
            }
        }
    }
}
