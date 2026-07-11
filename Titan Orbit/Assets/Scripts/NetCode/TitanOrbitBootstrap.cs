using System;
using TitanOrbit.Data;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Custom NetCode for Entities bootstrap — decides which simulation worlds exist at startup
    /// (client, server, or both). Runs once when the default world is created. Configures relay
    /// driver construction, auto-connect port, and dedicated-server vs editor vs LAN-host paths.
    /// Paired with <see cref="TitanOrbitSessionManager"/> for menu-driven connection and
    /// <see cref="TitanOrbitDedicatedServerBootRunner"/> for headless GCE binaries.
    /// </summary>
    public class TitanOrbitBootstrap : ClientServerBootstrap
    {
        /// <summary>
        /// NetCode entry point — called before any ghost systems run. Creates ClientWorld,
        /// ServerWorld, or both depending on build target and command-line flags.
        /// </summary>
        /// <param name="defaultWorldName">NetCode default world label (usually unused here).</param>
        /// <returns>True when world creation succeeded and sim should continue booting.</returns>
        public override bool Initialize(string defaultWorldName)
        {
            // [UNITY] Keep sim ticking when the game window loses focus (host + dedicated server).
            Application.runInBackground = true;
#if UNITY_SERVER
            // [TITAN-ORBIT] Headless server targets 60 Hz to match fixed-step sim and tick-rate systems.
            Application.targetFrameRate = 60;
#endif
            // [NETCODE] Custom UDP driver — supports Unity Relay and direct LAN sockets.
            NetworkStreamReceiveSystem.DriverConstructor = new TitanOrbitRelayDriverConstructor();
#if UNITY_EDITOR
            // [EDITOR] Port 0 = do not auto-listen; player picks Local / Join from main menu.
            AutoConnectPort = 0;
#else
            // [NETCODE] Dedicated builds must not bind 7777 until Relay/session config is ready.
            AutoConnectPort = ShouldRunDedicatedServer() ? (ushort)0 : (ushort)7777;
#endif

#if UNITY_EDITOR
            // --- Editor: dedicated-server test from menu or MPPM multi-client ---
            if (HasExplicitDedicatedServerArg())
            {
                CreateServerWorld("ServerWorld");
                Debug.Log("[TitanOrbitBootstrap] Editor dedicated-server world created.");
                return true;
            }

            // [NETCODE] MPPM additional editor instances are client-only — connect to host editor.
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
            {
                TitanOrbitPlayModeUtility.WarnIfMppmServerBuildClone();
                CreateClientWorld("ClientWorld");
                Debug.Log("[TitanOrbitBootstrap] MPPM Player " + TitanOrbitPlayModeUtility.GetMppmPlayerNumber() +
                          ": ClientWorld only (buildSubTarget=" + TitanOrbitPlayModeUtility.GetMppmBuildSubtarget() +
                          ", connect to host on port " + AutoConnectPort + ").");
                return true;
            }

            // [EDITOR] Default editor play — host + client worlds per NetCode play mode setting.
            CreateDefaultClientServerWorlds();
            Debug.Log("[TitanOrbitBootstrap] Editor play: " + RequestedPlayType + " worlds created. AutoConnectPort=" + AutoConnectPort + ".");
            return true;
#endif

            // --- Player / dedicated builds (non-editor) ---
            if (ShouldRunDedicatedServer())
            {
                // [NETCODE] IL2CPP Linux/Windows headless — server world only.
                CreateServerWorld("ServerWorld");
                Debug.Log("[TitanOrbitBootstrap] Dedicated server build: ServerWorld created.");
            }
            else if (ShouldRunLanHost())
            {
                // [TITAN-ORBIT] Local LAN host — one process runs authoritative server + predicted client.
                CreateServerWorld("ServerWorld");
                CreateClientWorld("ClientWorld");
            }
            else
            {
                CreateDefaultClientServerWorlds();
            }

            return true;
        }

        /// <summary>True when -dedicatedServer (or equivalent) was passed on the command line.</summary>
        static bool HasExplicitDedicatedServerArg() => TitanOrbitServerCommandLine.HasDedicatedFlag();

        /// <summary>
        /// Dedicated server when compiled with UNITY_SERVER or when CLI requests server-only boot.
        /// </summary>
        static bool ShouldRunDedicatedServer()
        {
#if UNITY_SERVER
            return true;
#else
            return HasExplicitDedicatedServerArg();
#endif
        }

        /// <summary>
        /// True when the main menu set <see cref="TitanOrbitSessionManager.PendingLanHost"/> for local host play.
        /// </summary>
        static bool ShouldRunLanHost()
        {
            return TitanOrbitSessionManager.PendingLanHost;
        }
    }
}
