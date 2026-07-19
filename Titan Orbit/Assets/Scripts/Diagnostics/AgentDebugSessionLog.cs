using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// [EDITOR]/[TITAN-ORBIT] Session NDJSON logger for Cursor debug mode (session cdce8b).
    /// Writes to the workspace <c>debug-cdce8b.log</c> and mirrors critical NetCode disconnect
    /// lines so we can prove why Windows clients return to the Main Menu.
    /// </summary>
    public static class AgentDebugSessionLog
    {
        /// <summary>Workspace-relative NDJSON path for this debug session.</summary>
        const string LogFileName = "debug-cdce8b.log";

        /// <summary>Cursor debug session id.</summary>
        const string SessionId = "cdce8b";

        static readonly object Gate = new object();
        static bool s_Hooked;
        static string s_LogPath;

        /// <summary>
        /// Absolute path to the NDJSON file. Windows player builds do not live under the repo, so we
        /// pin the Cursor workspace path for this debug session (falls back to persistentDataPath).
        /// </summary>
        public static string LogPath
        {
            get
            {
                if (!string.IsNullOrEmpty(s_LogPath))
                    return s_LogPath;

                // [TITAN-ORBIT] Debug-session pin — must match Cursor debug_mode log path.
                const string WorkspaceLog =
                    @"c:\Users\jason\Documents\repo\Titan-Orbit\debug-cdce8b.log";
                try
                {
                    string dir = Path.GetDirectoryName(WorkspaceLog);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        s_LogPath = WorkspaceLog;
                        return s_LogPath;
                    }
                }
                catch
                {
                    // Fall through to persistentDataPath.
                }

                s_LogPath = Path.Combine(Application.persistentDataPath, LogFileName);
                return s_LogPath;
            }
        }

        /// <summary>[UNITY] Install Unity log hook once so RpcSystem errors are captured.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            if (s_Hooked)
                return;
            s_Hooked = true;
            Application.logMessageReceivedThreaded += OnUnityLog;
            Write("boot", "H0", "AgentDebugSessionLog.Install", "debug logger installed",
                "{\"path\":\"" + Escape(LogPath) + "\"}");
        }

        /// <summary>Append one NDJSON line for hypothesis testing.</summary>
        public static void Write(string runId, string hypothesisId, string location, string message, string dataJsonObject)
        {
            // #region agent log
            try
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sb = new StringBuilder(256);
                sb.Append("{\"sessionId\":\"").Append(SessionId).Append("\",");
                sb.Append("\"runId\":\"").Append(Escape(runId)).Append("\",");
                sb.Append("\"hypothesisId\":\"").Append(Escape(hypothesisId)).Append("\",");
                sb.Append("\"location\":\"").Append(Escape(location)).Append("\",");
                sb.Append("\"message\":\"").Append(Escape(message)).Append("\",");
                sb.Append("\"data\":").Append(string.IsNullOrEmpty(dataJsonObject) ? "{}" : dataJsonObject).Append(',');
                sb.Append("\"timestamp\":").Append(ts).Append('}');
                lock (Gate)
                {
                    File.AppendAllText(LogPath, sb.ToString() + "\n");
                }
            }
            catch
            {
                // Swallow — never break gameplay for debug I/O.
            }
            // #endregion
        }

        static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            // #region agent log
            if (string.IsNullOrEmpty(condition))
                return;

            bool rpcSkipped = condition.IndexOf("SKIPPING unknown rpc hash", StringComparison.OrdinalIgnoreCase) >= 0
                              || condition.IndexOf("SKIPPING bad deserialize", StringComparison.OrdinalIgnoreCase) >= 0
                              || condition.IndexOf("SKIPPING rpc index", StringComparison.OrdinalIgnoreCase) >= 0;
            bool rpcInvalidHash = condition.IndexOf("invalid hash", StringComparison.OrdinalIgnoreCase) >= 0;
            bool rpcBitsMismatch = condition.IndexOf("bits read", StringComparison.OrdinalIgnoreCase) >= 0
                                   && condition.IndexOf("RpcSystem", StringComparison.OrdinalIgnoreCase) >= 0;
            bool rpcDisconnect = condition.IndexOf("InvalidRpc", StringComparison.OrdinalIgnoreCase) >= 0
                                 || condition.IndexOf("connection will soon be closed", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!rpcSkipped && !rpcInvalidHash && !rpcBitsMismatch && !rpcDisconnect)
                return;

            string hyp = rpcSkipped ? "A" : rpcInvalidHash ? "A" : rpcBitsMismatch ? "B" : "A";
            string msg = rpcSkipped ? "netcode_rpc_skipped_stay_connected" : "netcode_rpc_error";
            Write("post-fix", hyp, "Application.logMessageReceived", msg,
                "{\"logType\":\"" + type + "\",\"condition\":\"" + Escape(condition) + "\"}");
            // #endregion
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }
    }
}
