using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Debug-session NDJSON writer (session db511d). File + HTTP ingest. Swallow all errors.
    /// </summary>
    public static class TitanOrbitDebugSessionLog
    {
        const string SessionId = "db511d";
        const string FilePath = @"c:\Users\jason\Documents\repo\Titan-Orbit\debug-db511d.log";
        const string IngestLocal = "http://127.0.0.1:7774/ingest/30ccdc0d-4064-42d7-ab07-612840f5e6a2";
        const string IngestDocker = "http://host.docker.internal:7774/ingest/30ccdc0d-4064-42d7-ab07-612840f5e6a2";

        static readonly object Gate = new object();

        public static void Write(string hypothesisId, string location, string message, string dataJson)
        {
            try
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line =
                    "{\"sessionId\":\"" + SessionId +
                    "\",\"hypothesisId\":\"" + Esc(hypothesisId) +
                    "\",\"location\":\"" + Esc(location) +
                    "\",\"message\":\"" + Esc(message) +
                    "\",\"data\":" + (string.IsNullOrEmpty(dataJson) ? "{}" : dataJson) +
                    ",\"timestamp\":" + ts + "}";
                lock (Gate)
                {
                    File.AppendAllText(FilePath, line + "\n");
                }

                Post(IngestLocal, line);
#if UNITY_SERVER && !UNITY_EDITOR
                Post(IngestDocker, line);
#endif
            }
            catch
            {
            }
        }

        static void Post(string url, string json)
        {
            try
            {
                var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("X-Debug-Session-Id", SessionId);
                req.SendWebRequest();
            }
            catch
            {
            }
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
