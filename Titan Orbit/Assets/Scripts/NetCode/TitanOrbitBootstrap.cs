using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>Custom Netcode for Entities bootstrap for Titan Orbit.</summary>
    public class TitanOrbitBootstrap : ClientServerBootstrap
    {
        public override bool Initialize(string defaultWorldName)
        {
            Application.runInBackground = true;
            AutoConnectPort = 7777;
            NetworkStreamReceiveSystem.DriverConstructor = new TitanOrbitRelayDriverConstructor();

            // In the Editor, always use in-proc Client+Server unless explicitly testing dedicated server.
            // This avoids MPPM / Play Mode server-only instances that have no ClientWorld or UI wiring.
#if UNITY_EDITOR
            if (!HasExplicitDedicatedServerArg())
            {
                CreateDefaultClientServerWorlds();
                Debug.Log("[TitanOrbitBootstrap] Editor local play: Client+Server worlds created.");
                return true;
            }
#endif

            if (ShouldRunDedicatedServer())
            {
                CreateServerWorld("ServerWorld");
            }
            else if (ShouldRunLanHost())
            {
                CreateServerWorld("ServerWorld");
                CreateClientWorld("ClientWorld");
            }
            else
            {
                CreateDefaultClientServerWorlds();
            }

            return true;
        }

        static bool HasExplicitDedicatedServerArg()
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (arg == "--titanOrbitDedicated")
                    return true;
            }
            return false;
        }

        static bool ShouldRunDedicatedServer()
        {
#if UNITY_SERVER
            return true;
#else
            return HasExplicitDedicatedServerArg();
#endif
        }

        static bool ShouldRunLanHost()
        {
            return TitanOrbitSessionManager.PendingLanHost;
        }
    }
}
