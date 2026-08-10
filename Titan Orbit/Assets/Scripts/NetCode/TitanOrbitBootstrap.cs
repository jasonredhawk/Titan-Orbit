using System;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
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
            // [NETCODE] Do not set Application.targetFrameRate on dedicated server here — Sleep mode
            // (ClientServerTickRate.Auto → Sleep under UNITY_SERVER) owns it via AdjustTargetFrameRate.
            // Pinning 60 fights that loop and spams NetcodeServerRateManager sleep-mode warnings.
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
#if UNITY_WEBGL && !UNITY_EDITOR
                // [TITAN-ORBIT] WebGL needs a filtered ClientWorld — stock CreateClientWorld OOBs
                // during system OnCreate / first Update (Chrome 2026-08-09 / 2026-08-10).
                CreateWebGlClientWorld();
#else
                CreateClientWorld("ClientWorld");
#endif
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

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// Creates a WebGL ClientWorld with systems that proven-OOB on Chrome stripped out, then
        /// keeps that world out of the player loop so the main menu can stay up.
        ///
        /// Proven WASM OOB loci (2026-08-09 / 2026-08-10 Chrome):
        /// <list type="bullet">
        /// <item>Entities Graphics / Physics GraphicsIntegration OnCreate (no compute).</item>
        /// <item>VariableRateSimulation* and every *CommandBufferSystem* OnCreate.</item>
        /// <item>GhostSpawnSystem OnCreate (~0x281af*).</item>
        /// <item>First player-loop Update after menu-ready when ClientWorld ticks without ECBs
        /// (Browser_mainLoop_runner ~0x4028cf7) — untick avoids that until a WebGL-safe ECB /
        /// GhostSpawn OnCreate path exists.</item>
        /// </list>
        /// Uses stock <see cref="ClientServerBootstrap.CreateClientWorld(string, NativeList{SystemTypeIndex})"/>
        /// so <c>Netcode.Client.Init()</c> still runs. Relay join remains blocked until ClientWorld
        /// is re-ticked with GhostSpawn + CommandBuffers restored.
        /// </summary>
        static void CreateWebGlClientWorld()
        {
            // --- Collect + filter ClientSimulation | Presentation systems ---
            NativeList<SystemTypeIndex> all = DefaultWorldInitialization.GetAllSystemTypeIndices(
                WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);

            var filtered = new NativeList<SystemTypeIndex>(all.Length, Allocator.Temp);
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    SystemTypeIndex index = all[i];
                    string systemName = TypeManager.GetSystemName(index).ToString();
                    if (IsWebGlExcludedSystemName(systemName))
                        continue;
                    filtered.Add(index);
                }

                // --- Create world (registers systems + Netcode.Client.Init) ---
                World world = CreateClientWorld("ClientWorld", filtered);

                // --- Menu-safe: do not tick ClientWorld yet ---
                // [TITAN-ORBIT] With CommandBufferSystems excluded, Simulation/Physics group Update
                // OOBs on the first Browser_mainLoop frames after MainMenuUiBootstrap. Unticking
                // keeps GO/UI (main menu) alive. Re-append when WebGL-safe ECB + GhostSpawn exist.
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(world);
                Debug.Log("[TitanOrbitBootstrap] WebGL ClientWorld created (filtered, unticked for menu boot).");
            }
            finally
            {
                if (filtered.IsCreated)
                    filtered.Dispose();
                if (all.IsCreated)
                    all.Dispose();
            }
        }

        /// <summary>
        /// Systems that must not register on WebGL (proven OnCreate OOB or unsupported without compute).
        /// </summary>
        /// <param name="systemName">Full system type name from <see cref="TypeManager.GetSystemName"/>.</param>
        /// <returns>True when the system must be omitted from the WebGL ClientWorld.</returns>
        static bool IsWebGlExcludedSystemName(string systemName)
        {
            if (string.IsNullOrEmpty(systemName))
                return false;

            // [UNITY] Entities Graphics — no compute shaders on WebGL.
            if (systemName.StartsWith("Unity.Rendering.", StringComparison.Ordinal))
                return true;
            if (systemName.StartsWith("Unity.Entities.Graphics.", StringComparison.Ordinal))
                return true;

            // [UNITY] Physics↔EG bridge — hybrid GameObject proxies own visuals on WebGL.
            if (systemName.StartsWith("Unity.Physics.GraphicsIntegration.", StringComparison.Ordinal))
                return true;

            // [TITAN-ORBIT] Proven WASM OOB on BeginVariableRateSimulationEntityCommandBufferSystem
            // OnCreate. Titan Orbit uses NetCode predicted fixed-step, not VariableRateSimulation.
            if (systemName.IndexOf("VariableRateSimulation", StringComparison.Ordinal) >= 0)
                return true;

            // [TITAN-ORBIT] Proven WASM OOB on GhostSpawnSystem OnCreate. Join cannot spawn ghosts
            // until a WebGL-safe OnCreate path exists — keep excluded with the menu-untick policy.
            if (systemName == "Unity.NetCode.GhostSpawnSystem")
                return true;

            // [TITAN-ORBIT] Proven WASM OOB on every *CommandBufferSystem* OnCreate hit on WebGL
            // (VariableRate, FixedStep, Presentation, Initialization, PredictedSimulation,
            // PreLateUpdate, BeginSimulation). Strip all until OnCreate is WebGL-safe.
            if (systemName.IndexOf("CommandBufferSystem", StringComparison.Ordinal) >= 0)
                return true;

            // [TITAN-ORBIT] Multiplayer Center NetcodeForEntities example systems are not used by
            // Titan Orbit gameplay — omit from WebGL ClientWorld.
            if (systemName.IndexOf("Unity.Multiplayer.Center", StringComparison.Ordinal) >= 0
                || systemName.IndexOf("Unity_Multiplayer_Center", StringComparison.Ordinal) >= 0)
                return true;

            // [TITAN-ORBIT] People-transport spawn RPC client — omitted on WebGL menu boot path
            // (not required until ClientWorld is re-ticked for in-game join).
            if (systemName == "TitanOrbit.ECS.PeopleTransportSpawnRpcClientSystem")
                return true;

            return false;
        }

#endif
    }
}
