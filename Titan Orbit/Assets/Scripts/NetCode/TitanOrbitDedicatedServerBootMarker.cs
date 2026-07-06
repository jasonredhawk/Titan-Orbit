using System;
using TitanOrbit.Diagnostics;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>Early log line on dedicated Linux/Windows server boots (GCE journal / TitanOrbitDedicatedServer.log).</summary>
    public static class TitanOrbitDedicatedServerBootMarker
    {
        static bool IsDedicatedServerProcess() => TitanOrbitDedicatedServerAutoBoot.IsDedicatedServerProcess();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void BeforeSceneLoad()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return;
            if (!IsDedicatedServerProcess())
                return;

            string markerLine = "[TitanOrbitDedicatedServerBootMarker] BeforeSceneLoad dedicated server detected.";
            Console.Error.WriteLine(markerLine);
            Console.Out.WriteLine(markerLine);

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
