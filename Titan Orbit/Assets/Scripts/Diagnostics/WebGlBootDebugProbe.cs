using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// [TITAN-ORBIT] Temporary WebGL boot breadcrumbs for debug-mode OOB investigation.
    /// Posts to the local Cursor debug ingest (via jslib) and mirrors to the browser console.
    /// Hypotheses: A=heap OOM, B=ECS/EG world init, C=audio unload, D=URP/shader, E=scene AfterSceneLoad never reached.
    /// </summary>
    public static class WebGlBootDebugProbe
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void TitanOrbitDebug_Log(string hypothesisId, string location, string message, string dataJson);
#else
        static void TitanOrbitDebug_Log(string hypothesisId, string location, string message, string dataJson) { }
#endif

        static void Emit(string hypothesisId, string location, string message, string dataJson = "{}")
        {
            // --- Always mirror to Unity console (visible in Chrome DevTools) ---
            Debug.Log("[WebGLBoot][" + hypothesisId + "] " + location + " | " + message + " | " + dataJson);

            // --- Also POST to local debug ingest when running on the same machine as Cursor ---
            try
            {
                TitanOrbitDebug_Log(hypothesisId, location, message, dataJson);
            }
            catch
            {
                // [STANDARD] jslib missing in Editor / non-WebGL — ignore.
            }
        }

        static string MemJson()
        {
            // [UNITY] systemMemorySize is device RAM estimate; useful relative signal on WebGL.
            var sb = new StringBuilder(128);
            sb.Append("{\"platform\":\"").Append(Application.platform).Append("\",");
            sb.Append("\"systemMemoryMB\":").Append(SystemInfo.systemMemorySize).Append(',');
            sb.Append("\"graphicsMemoryMB\":").Append(SystemInfo.graphicsMemorySize).Append(',');
            sb.Append("\"supportsCompute\":").Append(SystemInfo.supportsComputeShaders ? "true" : "false").Append(',');
            sb.Append("\"graphicsDevice\":\"").Append(SystemInfo.graphicsDeviceName).Append("\",");
            sb.Append("\"urp\":\"").Append(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name
                : "null").Append("\"}");
            return sb.ToString();
        }

        /// <summary>[UNITY] Earliest managed hook after native subsystems register.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnSubsystemRegistration()
        {
            // #region agent log
            Emit("A", "WebGlBootDebugProbe.SubsystemRegistration", "enter", MemJson());
            Emit("B", "WebGlBootDebugProbe.SubsystemRegistration", "ecs-graphics-probe",
                "{\"entitiesGraphicsLikely\":\"check No SRP logs\",\"compute\":" +
                (SystemInfo.supportsComputeShaders ? "true" : "false") + "}");
            Emit("D", "WebGlBootDebugProbe.SubsystemRegistration", "render-pipeline", MemJson());
            // #endregion
        }

        /// <summary>[UNITY] Immediately before the first scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OnBeforeSceneLoad()
        {
            // #region agent log
            Emit("A", "WebGlBootDebugProbe.BeforeSceneLoad", "enter", MemJson());
            Emit("C", "WebGlBootDebugProbe.BeforeSceneLoad", "about-to-load-scene",
                "{\"activeScene\":\"pending\",\"note\":\"sound length warnings usually appear during scene awake\"}");
            // #endregion
        }

        /// <summary>[UNITY] After first scene Awake/OnEnable — if missing in console, crash was during scene load.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnAfterSceneLoad()
        {
            // #region agent log
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var audioSources = Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            int unloaded = 0;
            int total = audioSources != null ? audioSources.Length : 0;
            if (audioSources != null)
            {
                for (int i = 0; i < audioSources.Length; i++)
                {
                    var src = audioSources[i];
                    if (src == null || src.clip == null)
                        continue;
                    if (src.clip.loadState != AudioDataLoadState.Loaded)
                        unloaded++;
                }
            }

            Emit("E", "WebGlBootDebugProbe.AfterSceneLoad", "scene-alive",
                "{\"scene\":\"" + scene.name + "\",\"audioSources\":" + total +
                ",\"unloadedClips\":" + unloaded + "}");
            Emit("C", "WebGlBootDebugProbe.AfterSceneLoad", "audio-scan",
                "{\"audioSources\":" + total + ",\"unloadedClips\":" + unloaded + "}");
            Emit("A", "WebGlBootDebugProbe.AfterSceneLoad", "mem-after-scene", MemJson());
            // #endregion
        }
    }
}
