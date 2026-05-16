using System;
using System.IO;
using UnityEngine;

namespace TitanOrbit.Debugging
{
    /// <summary>NDJSON append for debug session 7964bb (population transfer investigation).</summary>
    public static class AgentDebugNdjson7964bb
    {
        const string SessionId = "7964bb";
        const string RelativeLogName = "debug-7964bb.log";

        public static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public static void Log(string hypothesisId, string location, string message, string dataJsonObject)
        {
            if (string.IsNullOrEmpty(dataJsonObject)) dataJsonObject = "{}";
            try
            {
                string baseDir = Application.dataPath != null
                    ? Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                    : Directory.GetCurrentDirectory();
                string path = Path.Combine(baseDir, RelativeLogName);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line = "{\"sessionId\":\"" + SessionId + "\",\"hypothesisId\":\"" + JsonEscape(hypothesisId) +
                    "\",\"location\":\"" + JsonEscape(location) + "\",\"message\":\"" + JsonEscape(message) +
                    "\",\"timestamp\":" + ts + ",\"data\":" + dataJsonObject + "}\n";
                File.AppendAllText(path, line);
            }
            catch
            {
                // ignore logging failures (read-only FS, etc.)
            }
        }
    }
}
