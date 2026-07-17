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
    /// <para>
    /// [TITAN-ORBIT] Keep ≤ ~1 Hz. basics28 dense writes collapsed FPS to ~1.
    /// basics29: Editor + MPPM both appending caused cross-process file stalls — use
    /// <see cref="FileShare.ReadWrite"/> and never call this from per-frame paths.
    /// </para>
    /// </summary>
    public static class ShipFlightSmoothDebugLog
    {
        const string SessionId = "6b87b4";
        const string FileName = "debug-6b87b4.log";

        static readonly object Gate = new object();
        static string _path;
        static int _writes;

        /// <summary>Append one NDJSON line (≤1 Hz callers only).</summary>
        public static void Write(string hypothesisId, string location, string message, string dataJsonObject)
        {
            try
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sb = new StringBuilder(384);
                sb.Append("{\"sessionId\":\"").Append(SessionId).Append("\",");
                sb.Append("\"runId\":\"basics30\",");
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
                    // [STANDARD] Shared write so Editor + MPPM VP do not exclusive-lock each other.
                    using (var fs = new FileStream(
                               _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(fs, Encoding.UTF8))
                    {
                        writer.Write(sb.ToString());
                    }
                    _writes++;
                }
            }
            catch
            {
                // Ignored — never break gameplay for debug I/O.
            }
        }

        /// <summary>Successful write count (process-local).</summary>
        public static int WriteCount => _writes;

        /// <summary>Walks up from Assets to the folder that contains <c>.git</c> (shared by MPPM VPs).</summary>
        static string ResolvePath()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                    return Path.Combine(dir, FileName);
                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", FileName));
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
