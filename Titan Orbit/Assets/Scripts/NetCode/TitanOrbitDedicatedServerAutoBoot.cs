using TitanOrbit.Diagnostics;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Detects whether this process should run as a dedicated authoritative server (GCE Linux,
    /// Windows headless, or batch mode). Used by boot markers and session code to skip client UI
    /// and start Relay + lobby without relying on scene load order. Editor play always returns false.
    /// </summary>
    public static class TitanOrbitDedicatedServerAutoBoot
    {
        /// <summary>
        /// True for UNITY_SERVER builds, batch mode, or explicit -dedicatedServer CLI flag.
        /// False in Editor and WebGL player builds.
        /// </summary>
        public static bool IsDedicatedServerProcess()
        {
            // --- IsDedicatedServerProcess ---
#if UNITY_EDITOR
            // [EDITOR] Editor uses play-mode menus — never auto-classify as dedicated server.
            return false;
#else
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return false;
#if UNITY_SERVER
            return true;
#else
            // [UNITY] Batch mode headless Windows/Linux test runs.
            if (Application.isBatchMode)
                return true;
            return TitanOrbitServerCommandLine.HasDedicatedFlag();
#endif
#endif
        }
    }
}
