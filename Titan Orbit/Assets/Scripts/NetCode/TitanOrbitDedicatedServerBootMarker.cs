using System;
using TitanOrbit.Diagnostics;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Earliest dedicated-server log hook — runs BeforeSceneLoad on GCE/Linux headless boots so
    /// journald and <see cref="DedicatedServerFileLog"/> capture pid and command line even if
    /// later scene bootstrap fails. Client and Editor builds no-op immediately.
    /// </summary>
    public static class TitanOrbitDedicatedServerBootMarker
    {
        /// <summary>Delegates to <see cref="TitanOrbitDedicatedServerAutoBoot"/>.</summary>
        static bool IsDedicatedServerProcess() => TitanOrbitDedicatedServerAutoBoot.IsDedicatedServerProcess();

        /// <summary>
        /// [UNITY] RuntimeInitializeOnLoadMethod — fires before any scene loads on dedicated builds.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void BeforeSceneLoad()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return;
            if (!IsDedicatedServerProcess())
                return;

            // --- Console markers for cloud log agents ---
            string markerLine = "[TitanOrbitDedicatedServerBootMarker] BeforeSceneLoad dedicated server detected.";
            Console.Error.WriteLine(markerLine);
            Console.Out.WriteLine(markerLine);

            // --- File log with truncated cmdline (avoid huge argv in journal) ---
            string cmd = System.Environment.CommandLine ?? string.Empty;
            if (cmd.Length > 1800)
                cmd = cmd.Substring(0, 1797) + "...";
            DedicatedServerFileLog.Append(
                "boot",
                "BeforeSceneLoad pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                " batchMode=" + Application.isBatchMode +
#if UNITY_SERVER
                " build=UNITY_SERVER" +
#else
                " build=player" +
#endif
                " cmdline=" + cmd);
            Debug.Log("[TitanOrbitDedicatedServerBootMarker] Dedicated server bootstrap will run after scene load.");
        }
    }
}
