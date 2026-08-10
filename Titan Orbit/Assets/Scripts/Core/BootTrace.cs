using System.IO;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Lightweight boot tracing helper that logs high-level startup milestones to a text file under
    /// Assets/_Diagnostics/boot-trace.txt. Enabled only in Editor and development builds via
    /// [Conditional] attributes — stripped from release player builds. Used to diagnose scene load
    /// order, NetCode world creation, and menu→game transitions without attaching a debugger.
    /// </summary>
    public static class BootTrace
    {
        /// <summary>[UNITY] Log file path next to Assets folder in the project.</summary>
        private static readonly string FilePath =
            Path.Combine(Application.dataPath, "_Diagnostics", "boot-trace.txt");

        /// <summary>
        /// Deletes the existing boot trace file (if any) at session start. Safe to call repeatedly.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Clear()
        {
            try
            {
                // --- Ensure diagnostics folder exists ---
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch
            {
                // [STANDARD] Swallow I/O errors — tracing must never crash the game.
            }
        }

        /// <summary>
        /// Appends one timestamped line to boot-trace.txt. No-op in non-development builds.
        /// </summary>
        /// <param name="message">Milestone label (e.g. "GameManager.Awake", "NetCode world ready").</param>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Mark(string message)
        {
            string line = System.DateTime.Now.ToString("HH:mm:ss.fff") + " | " + message;
            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath, line + "\n");
            }
            catch
            {
                // [STANDARD] Swallow I/O errors — tracing must never crash the game.
            }
        }
    }
}
