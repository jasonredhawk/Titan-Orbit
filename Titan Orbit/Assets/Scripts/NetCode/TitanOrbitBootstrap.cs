using System;
using TitanOrbit.Data;
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
            NetworkStreamReceiveSystem.DriverConstructor = new TitanOrbitRelayDriverConstructor();
#if UNITY_EDITOR
            // Avoid loopback auto-connect fighting dedicated Relay joins in production-style editor testing.
            AutoConnectPort = TitanOrbitMultiplayerConfig.ShowLocalPlayOptions ? (ushort)7777 : (ushort)0;
#else
            // Dedicated headless must not auto-listen on 7777 before Relay is configured.
            AutoConnectPort = ShouldRunDedicatedServer() ? (ushort)0 : (ushort)7777;
#endif

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
            Debug.Log("[TitanOrbitBootstrap] Editor play: " + RequestedPlayType + " worlds created. AutoConnectPort=" + AutoConnectPort + ".");
            return true;
#endif

            if (ShouldRunDedicatedServer())
            {
                CreateServerWorld("ServerWorld");
                Debug.Log("[TitanOrbitBootstrap] Dedicated server build: ServerWorld created.");
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

        static bool HasExplicitDedicatedServerArg() => TitanOrbitServerCommandLine.HasDedicatedFlag();

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
