using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Plain-text diagnostics written next to the player data folder (same directory as on Linux: deploy root).
    /// Use on the VM alongside <c>Player.log</c> when diagnosing missing UGS lobbies.
    /// </summary>
    public static class DedicatedServerFileLog
    {
        private static readonly object Gate = new object();
        private const int MaxFileBytes = 512 * 1024;

        public static void Append(string phase, string message, Exception ex = null)
        {
            try
            {
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
            }
        }
    }
}
