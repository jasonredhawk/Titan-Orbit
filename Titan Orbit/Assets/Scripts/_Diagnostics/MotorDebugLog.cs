using System;
using System.IO;
using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Internal NDJSON debug logger for ship motor investigation sessions. Writes one JSON object
    /// per line to a repo-relative debug log file. Not used in production builds — safe to no-op
    /// on IO failure. Strip or gate before shipping if motor debugging is complete.
    /// </summary>
    internal static class MotorDebugLog
    {
        private const string SessionId = "5484ca";

        /// <summary>Absolute path two levels above Assets — agent/debug session file.</summary>
        private static string LogPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "debug-5484ca.log"));

        /// <summary>
        /// Appends one structured log line for hypothesis-driven motor debugging.
        /// </summary>
        /// <param name="hypothesisId">Short id tying this line to a debug hypothesis.</param>
        /// <param name="location">Code location or system name.</param>
        /// <param name="message">Human-readable summary.</param>
        /// <param name="dataJson">Raw JSON object (not escaped) merged into the line.</param>
        /// <param name="runId">Groups lines from one play session or repro attempt.</param>
        public static void Write(string hypothesisId, string location, string message, string dataJson, string runId = "pre-fix")
        {
            // --- Write ---
            // #region agent log
            try
            {
                string line =
                    $"{{\"sessionId\":\"{SessionId}\",\"runId\":\"{runId}\",\"hypothesisId\":\"{hypothesisId}\",\"location\":\"{Escape(location)}\",\"message\":\"{Escape(message)}\",\"data\":{dataJson},\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}\n";
                File.AppendAllText(LogPath, line);
            }
            catch
            {
                // [STANDARD] Debug logging must not affect gameplay.
            }
            // #endregion
        }

        /// <summary>Escapes backslashes and quotes for JSON string fields.</summary>
        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
