using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TitanOrbit.Diagnostics
{
    /// <summary>Session 554581 debug ingest — writes NDJSON next to the repo root.</summary>
    public static class AgentDebugNdjson
    {
        const string SessionId = "554581";
        const string LogPath = @"c:\Users\jason\Documents\repo\Titan-Orbit\debug-554581.log";

        static readonly object Gate = new object();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Write(string hypothesisId, string location, string message, string dataJson)
        {
            // #region agent log
            try
            {
                var sb = new StringBuilder(384);
                sb.Append("{\"sessionId\":\"").Append(SessionId);
                sb.Append("\",\"hypothesisId\":\"").Append(hypothesisId);
                sb.Append("\",\"location\":\"").Append(location);
                sb.Append("\",\"message\":\"").Append(message);
                sb.Append("\",\"data\":").Append(string.IsNullOrEmpty(dataJson) ? "{}" : dataJson);
                sb.Append(",\"timestamp\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(Inv));
                sb.Append("}\n");
                lock (Gate)
                    File.AppendAllText(LogPath, sb.ToString());
            }
            catch
            {
                // Debug ingest must never break gameplay.
            }
            // #endregion
        }
    }
}
