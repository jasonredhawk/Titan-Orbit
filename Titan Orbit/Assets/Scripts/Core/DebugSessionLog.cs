using System;
using System.IO;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>Debug session instrumentation: appends NDJSON to debug-bac766.log. Remove after debugging.</summary>
    public static class DebugSessionLog
    {
        private static string LogPath => Path.Combine(Application.persistentDataPath, "debug-bac766.log");

        public static void Write(string location, string message, string dataJson, string hypothesisId)
        {
            // #region agent log
            try
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string escaped = (message ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                string locEsc = (location ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                string data = string.IsNullOrEmpty(dataJson) ? "{}" : dataJson;
                string line = "{\"sessionId\":\"bac766\",\"timestamp\":" + ts + ",\"location\":\"" + locEsc + "\",\"message\":\"" + escaped + "\",\"data\":" + data + ",\"hypothesisId\":\"" + (hypothesisId ?? "") + "\"}\n";
                File.AppendAllText(LogPath, line);
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[DebugSessionLog] " + ex.Message); }
            // #endregion
        }
    }
}
