using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>Session debug NDJSON → debug-065367.log (project folder parent of Assets). Fold regions collapse in IDE.</summary>
    public static class DebugNdjson065367
    {
        private const string SessionId = "065367";
        private const int MaxLines = 120;
        private static int s_lines;

        private static string LogPath =>
            Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, "debug-065367.log");

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

        // #region agent log 065367
        public static void Write(string hypothesisId, string location, string message, string dataJsonObject)
        {
            try
            {
                if (s_lines >= MaxLines) return;
                s_lines++;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string data = string.IsNullOrWhiteSpace(dataJsonObject) ? "{}" : dataJsonObject.Trim();
                string line =
                    "{\"sessionId\":\"" + SessionId +
                    "\",\"hypothesisId\":\"" + Esc(hypothesisId) +
                    "\",\"location\":\"" + Esc(location) +
                    "\",\"message\":\"" + Esc(message) +
                    "\",\"data\":" + data +
                    ",\"timestamp\":" + ts.ToString(CultureInfo.InvariantCulture) +
                    ",\"runId\":\"pre-fix\"}\n";
                File.AppendAllText(LogPath, line);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DebugNdjson065367] " + ex.Message);
            }
        }
        // #endregion agent log 065367
    }
}
