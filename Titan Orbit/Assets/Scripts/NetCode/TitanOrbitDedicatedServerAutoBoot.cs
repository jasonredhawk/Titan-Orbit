using TitanOrbit.Diagnostics;
using Unity.NetCode;
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

        /// <summary>
        /// True when GameObject visual proxies, UI flow, and other client presentation should run.
        /// False on headless dedicated builds — skips client presentation and UI flow.
        /// </summary>
        public static bool ShouldRunClientPresentation()
        {
#if UNITY_EDITOR
            // [EDITOR] Always show main menu UI — even when the active build target is Dedicated Server
            // (Unity defines UNITY_SERVER for that target, which would otherwise strip all client UI).
            return true;
#else
#if UNITY_SERVER
            // [UNITY] IL2CPP dedicated server build — no ClientWorld, no rendering.
            return false;
#else
            if (IsDedicatedServerProcess())
                return false;

            // [NETCODE] Player builds need an active client simulation world.
            var client = ClientServerBootstrap.ClientWorld;
            return client != null && client.IsCreated;
#endif
#endif
        }
    }
}
