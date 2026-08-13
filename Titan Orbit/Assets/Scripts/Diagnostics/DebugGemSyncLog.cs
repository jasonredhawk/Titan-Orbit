using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Session-scoped NDJSON writer for gem client/server desync debugging (agent session 4f3442).
    /// Appends to the workspace debug log; swallows IO errors so gameplay never depends on it.
    /// </summary>
    public static class DebugGemSyncLog
    {
        const string LogPath = @"c:\Users\jason\Documents\repo\Titan-Orbit\debug-4f3442.log";
        const string SessionId = "4f3442";

        static readonly object Gate = new object();
        static readonly Dictionary<string, long> LastWriteMs = new Dictionary<string, long>(32);

        /// <summary>True when this key was written too recently (caller should skip).</summary>
        public static bool ShouldThrottle(string key, int intervalMs)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (Gate)
            {
                if (LastWriteMs.TryGetValue(key, out long last) && now - last < intervalMs)
                    return true;
                LastWriteMs[key] = now;
            }

            return false;
        }

        /// <summary>Appends one NDJSON line. <paramref name="dataJson"/> must be a JSON object literal.</summary>
        public static void Write(string hypothesisId, string location, string message, string dataJson, int spawnId = 0)
        {
            try
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sb = new StringBuilder(384);
                sb.Append("{\"sessionId\":\"").Append(SessionId);
                sb.Append("\",\"runId\":\"trail\"");
                sb.Append(",\"spawnId\":").Append(spawnId);
                sb.Append(",\"hypothesisId\":\"").Append(Escape(hypothesisId));
                sb.Append("\",\"location\":\"").Append(Escape(location));
                sb.Append("\",\"message\":\"").Append(Escape(message));
                sb.Append("\",\"data\":").Append(string.IsNullOrEmpty(dataJson) ? "{}" : dataJson);
                sb.Append(",\"timestamp\":").Append(ts);
                sb.Append("}\n");
                lock (Gate)
                    File.AppendAllText(LogPath, sb.ToString());
            }
            catch
            {
                // Debug ingest must never throw into simulation or presentation.
            }
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
