using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Append-only plain-text log beside the player data folder (Linux deploy root on GCE).
    /// Used when journald or Unity Player.log is insufficient — e.g. UGS lobby registration
    /// failures on headless boot. Thread-safe via lock; rotates at 512 KB to .prev file.
    /// Not compiled into WebGL client builds in practice (dedicated server path only).
    /// </summary>
    public static class DedicatedServerFileLog
    {
        // [STANDARD] Serialize concurrent Append calls from boot marker and session manager.
        private static readonly object Gate = new object();

        /// <summary>Max log size before rename to TitanOrbitDedicatedServer.log.prev.</summary>
        private const int MaxFileBytes = 512 * 1024;

        /// <summary>
        /// Appends one UTC-stamped line to TitanOrbitDedicatedServer.log and mirrors to Debug.Log.
        /// Swallows all IO errors so boot never fails because logging failed.
        /// </summary>
        /// <param name="phase">Short category (boot, lobby, relay) for grep on the VM.</param>
        /// <param name="message">Human-readable detail without newlines.</param>
        /// <param name="ex">Optional exception — type, message, and capped stack trace appended.</param>
        public static void Append(string phase, string message, Exception ex = null)
        {
            try
            {
                // --- Resolve log path next to build root (parent of Assets on server) ---
                string dir = Application.dataPath != null ? Path.GetDirectoryName(Application.dataPath) : null;
                if (string.IsNullOrEmpty(dir))
                    return;

                string path = Path.Combine(dir, "TitanOrbitDedicatedServer.log");
                var sb = new StringBuilder(384);
                sb.Append(DateTime.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(" | ").Append(phase).Append(" | ").Append(message);
                if (ex != null)
                {
                    sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message ?? string.Empty);
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                    {
                        int cap = Math.Min(480, ex.StackTrace.Length);
                        sb.Append(" | ").Append(ex.StackTrace, 0, cap);
                    }
                }

                sb.AppendLine();
                string line = sb.ToString();
                Debug.Log("[DedicatedServerFileLog] " + line.TrimEnd());

                // --- Atomic append under lock ---
                lock (Gate)
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            if (new FileInfo(path).Length > MaxFileBytes)
                            {
                                string prev = path + ".prev";
                                try
                                {
                                    if (File.Exists(prev))
                                        File.Delete(prev);
                                }
                                catch
                                {
                                    // [STANDARD] Best-effort rotation — ignore delete failures.
                                }

                                File.Move(path, prev);
                            }
                        }
                        catch
                        {
                        }
                    }

                    File.AppendAllText(path, line);
                }
            }
            catch
            {
                // [STANDARD] Logging must never crash the dedicated server process.
            }
        }
    }
}
