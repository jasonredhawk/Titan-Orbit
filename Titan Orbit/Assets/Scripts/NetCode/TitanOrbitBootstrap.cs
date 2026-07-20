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

            // --- Auto-connect port ---
            // [NETCODE] Port 0 = do not auto-listen / auto-connect. SessionManager + menu own connect.
            // Player.log (Windows client basics58): AutoConnectPort=7777 + CreateDefaultClientServerWorlds
            // IPC-connected immediately, skipped Main Menu / Join, jumped to Team Join, then Burst crash
            // after local MapGeneration. Match Editor: menu-driven only.
            AutoConnectPort = 0;

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

            // [EDITOR] ClientWorld + ServerWorld so GameplaySubScene streams into BOTH at Play enter.
            // Creating ServerWorld only on Local Host click leaves that world without SubScenes /
            // GamePrefabs — MapGenerationSystem never runs and the loading bar soft-crawls at ~12%.
            // SessionManager.Start suspends Server SimulationSystemGroup until Local Host/play so
            // map gen does not finish on the menu. Dedicated Join still Dispose()s ServerWorld
            // (basics38/40 dual-world Relay cost).
            CreateServerWorld("ServerWorld");
            CreateClientWorld("ClientWorld");
            Debug.Log("[TitanOrbitBootstrap] Editor play: ClientWorld+ServerWorld (server sim suspended until Local Host). AutoConnectPort=" + AutoConnectPort + ".");
            return true;
#endif

            // --- Player / dedicated builds (non-editor) ---
            if (ShouldRunDedicatedServer())
            {
                // [NETCODE] IL2CPP Linux/Windows headless — server world only.
                CreateServerWorld("ServerWorld");
                Debug.Log("[TitanOrbitBootstrap] Dedicated server build: ServerWorld created. AutoConnectPort=0.");
            }
            else if (ShouldRunLanHost())
            {
                // [TITAN-ORBIT] Local LAN host — one process runs authoritative server + predicted client.
                // PendingLanHost is set before scene reload from the main-menu Local host button.
                CreateServerWorld("ServerWorld");
                CreateClientWorld("ClientWorld");
                Debug.Log("[TitanOrbitBootstrap] LAN host player: ClientWorld+ServerWorld. AutoConnectPort=0 (Listen via SessionManager).");
            }
            else
            {
                // [TITAN-ORBIT] Standalone client (Windows / WebGL / Android) — ClientWorld only.
                // Join game → Relay. Do NOT CreateDefaultClientServerWorlds() (that auto-hosts).
                CreateClientWorld("ClientWorld");
                Debug.Log("[TitanOrbitBootstrap] Player client: ClientWorld only. AutoConnectPort=0 (Join game / Relay).");
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
