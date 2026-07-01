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

#if UNITY_EDITOR
            if (HasExplicitDedicatedServerArg())
            {
                CreateServerWorld("ServerWorld");
                Debug.Log("[TitanOrbitBootstrap] Editor dedicated-server world created.");
                return true;
            }

            // MPPM additional editors share the host's port — client-only, connect to main editor.
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
            {
                TitanOrbitPlayModeUtility.WarnIfMppmServerBuildClone();
                CreateClientWorld("ClientWorld");
                Debug.Log("[TitanOrbitBootstrap] MPPM Player " + TitanOrbitPlayModeUtility.GetMppmPlayerNumber() +
                          ": ClientWorld only (buildSubTarget=" + TitanOrbitPlayModeUtility.GetMppmBuildSubtarget() +
                          ", connect to host on port " + AutoConnectPort + ").");
                return true;
            }

            CreateDefaultClientServerWorlds();
            Debug.Log("[TitanOrbitBootstrap] Editor play: " + RequestedPlayType + " worlds created.");
            return true;
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
