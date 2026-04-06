using System.IO;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Lightweight boot tracing helper that logs high-level startup milestones
    /// to both the Unity console and a text file under Assets/_Diagnostics.
    /// Enabled only in editor and development builds via Conditional attributes.
    /// </summary>
    public static class BootTrace
    {
        private static readonly string FilePath =
            Path.Combine(Application.dataPath, "_Diagnostics", "boot-trace.txt");

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Clear()
        {
            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch
            {
                // Swallow I/O errors – tracing must never crash the game.
            }
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Mark(string message)
        {
            string line = System.DateTime.Now.ToString("HH:mm:ss.fff") + " | " + message;
            //Debug.Log("[BootTrace] " + message);
            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath, line + "\n");
            }
            catch
            {
                // Swallow I/O errors – tracing must never crash the game.
            }
        }
    }
}

