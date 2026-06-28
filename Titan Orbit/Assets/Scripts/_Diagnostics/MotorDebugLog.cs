using System;
using System.IO;
using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>NDJSON debug logger for ship motor investigation.</summary>
    internal static class MotorDebugLog
    {
        private const string SessionId = "5484ca";

        private static string LogPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "debug-5484ca.log"));

        public static void Write(string hypothesisId, string location, string message, string dataJson, string runId = "pre-fix")
        {
            // #region agent log
            try
            {
                string line =
                    $"{{\"sessionId\":\"{SessionId}\",\"runId\":\"{runId}\",\"hypothesisId\":\"{hypothesisId}\",\"location\":\"{Escape(location)}\",\"message\":\"{Escape(message)}\",\"data\":{dataJson},\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}\n";
                File.AppendAllText(LogPath, line);
            }
            catch
            {
            }
            // #endregion
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
