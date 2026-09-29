using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// WebGL boot breadcrumbs. Chrome dies in native <c>_main</c> after GhostBehaviour;
    /// these logs mark the last managed step that ran.
    /// </summary>
    public static class WebGlBootDebugProbe
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void TitanOrbitDebug_Log(
            string hypothesisId, string location, string message, string dataJson);
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnSubsystemRegistration()
        {
            Emit("A", "WebGlBootDebugProbe.OnSubsystemRegistration", "subsystem-registration",
                MemAndPipelineJson());
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OnBeforeSceneLoad()
        {
            Emit("B", "WebGlBootDebugProbe.OnBeforeSceneLoad", "before-scene-load",
                MemAndPipelineJson());
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnAfterSceneLoad()
        {
            Emit("B", "WebGlBootDebugProbe.OnAfterSceneLoad", "after-scene-load",
                MemAndPipelineJson());
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.Log("CONNECT_JOIN player-stamp hydrate-probe-h19");
#endif
        }

        /// <summary>Call from bootstrap / world create so Chrome shows the last managed step.</summary>
        public static void Emit(string hypothesisId, string location, string message, string dataJson = "{}")
        {
            // #region agent log
            Debug.Log("[WebGLBoot][" + hypothesisId + "] " + location + " | " + message + " | " + dataJson);
#if UNITY_EDITOR
            WriteEditorDebugLog(hypothesisId, location, message, dataJson);
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                TitanOrbitDebug_Log(hypothesisId, location, message, dataJson ?? "{}");
            }
            catch
            {
            }
#endif
            // #endregion
        }

#if UNITY_EDITOR
        static void WriteEditorDebugLog(string hypothesisId, string location, string message, string dataJson)
        {
            try
            {
                string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    Application.dataPath, "..", "..", "debug-1a7cd0.log"));
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line = "{\"sessionId\":\"1a7cd0\",\"hypothesisId\":\"" + Escape(hypothesisId) +
                    "\",\"location\":\"" + Escape(location) +
                    "\",\"message\":\"" + Escape(message) +
                    "\",\"data\":" + (string.IsNullOrEmpty(dataJson) ? "{}" : dataJson) +
                    ",\"timestamp\":" + ts +
                    ",\"runId\":\"editor\"}\n";
                System.IO.File.AppendAllText(path, line);
            }
            catch
            {
            }
        }
#endif

        static string MemAndPipelineJson()
        {
            string pipeline = "none";
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset != null)
                pipeline = asset.name;

            int quality = QualitySettings.GetQualityLevel();
            string qualityName = quality >= 0 && quality < QualitySettings.names.Length
                ? QualitySettings.names[quality]
                : quality.ToString();

            return
                "{\"systemMemoryMB\":" + SystemInfo.systemMemorySize +
                ",\"gfxMemoryMB\":" + SystemInfo.graphicsMemorySize +
                ",\"gfxDevice\":\"" + Escape(SystemInfo.graphicsDeviceType.ToString()) +
                "\",\"pipeline\":\"" + Escape(pipeline) +
                "\",\"quality\":\"" + Escape(qualityName) +
                "\"}";
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
