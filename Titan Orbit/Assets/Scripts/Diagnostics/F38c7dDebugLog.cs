using System;
using System.IO;
using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>NDJSON append for debug session f38c7d (Editor + builds). Do not log secrets.</summary>
    public static class F38c7dDebugLog
    {
        const string SessionId = "f38c7d";
        const string FileName = "debug-f38c7d.log";

        public static void Write(string hypothesisId, string location, string message, string dataJson = "{}")
        {
            // #region agent log
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                if (string.IsNullOrEmpty(root))
                    return;
                string path = Path.Combine(root, FileName);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string msg = (message ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                if (msg.Length > 160)
                    msg = msg.Substring(0, 160);
                string data = string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson;
                string line =
                    "{\"sessionId\":\"" + SessionId + "\",\"hypothesisId\":\"" + (hypothesisId ?? "") +
                    "\",\"location\":\"" + (location ?? "") + "\",\"message\":\"" + msg +
                    "\",\"data\":" + data + ",\"timestamp\":" + ts + "}\n";
                File.AppendAllText(path, line);
            }
            catch
            {
            }
            // #endregion
        }
    }
}
