using TitanOrbit.Diagnostics;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Mirrors legacy <c>DedicatedMatchServerBootstrap</c>: boot Relay + UGS lobby without relying on scene load order.
    /// </summary>
    public static class TitanOrbitDedicatedServerAutoBoot
    {
        public static bool IsDedicatedServerProcess()
        {
#if UNITY_EDITOR
            return false;
#else
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return false;
#if UNITY_SERVER
            return true;
#else
            if (Application.isBatchMode)
                return true;
            return TitanOrbitServerCommandLine.HasDedicatedFlag();
#endif
#endif
        }
    }
}
