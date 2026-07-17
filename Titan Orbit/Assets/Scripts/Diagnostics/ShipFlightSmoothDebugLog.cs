using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Session debug NDJSON writer for ship flight smoothness (agent session 6b87b4).
    /// Writes to repo-root <c>debug-6b87b4.log</c>. Temporary instrumentation — do not ship.
    /// </summary>
    public static class ShipFlightSmoothDebugLog
    {
        const string SessionId = "6b87b4";
        const string FileName = "debug-6b87b4.log";

        static readonly object Gate = new object();
        static string _path;
        static int _writes;

        /// <summary>Append one NDJSON line for hypothesis testing.</summary>
        public static void Write(string hypothesisId, string location, string message, string dataJsonObject)
        {
            try
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sb = new StringBuilder(384);
                sb.Append("{\"sessionId\":\"").Append(SessionId).Append("\",");
                sb.Append("\"runId\":\"basics18\",");
                sb.Append("\"hypothesisId\":\"").Append(Escape(hypothesisId)).Append("\",");
                sb.Append("\"location\":\"").Append(Escape(location)).Append("\",");
                sb.Append("\"message\":\"").Append(Escape(message)).Append("\",");
                sb.Append("\"data\":").Append(string.IsNullOrEmpty(dataJsonObject) ? "{}" : dataJsonObject).Append(',');
                sb.Append("\"timestamp\":").Append(ts.ToString(CultureInfo.InvariantCulture));
                sb.Append("}\n");

                lock (Gate)
                {
                    if (_path == null)
                        _path = ResolvePath();
                    File.AppendAllText(_path, sb.ToString());
                    _writes++;
                }
            }
            catch
            {
                // Ignored — never break gameplay for debug I/O.
            }
        }

        /// <summary>True once we have successfully written at least one line.</summary>
        public static int WriteCount => _writes;

        static string ResolvePath()
        {
            // Application.dataPath = .../Titan Orbit/Assets → repo root two levels up.
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            return Path.Combine(repoRoot, FileName);
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
