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
            // [NETCODE] Dedicated Docker / NullGfx: disable VSync and raise maxDelta before
            // CrossPlatformManager can wait on a dummy present (main GCE Sleep does not need this).
            TitanOrbitServerTickRateSystem.ApplyDedicatedHeadlessFramePacing();
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
                // [TITAN-ORBIT] WebGL client omits Entities Graphics / VariableRate only.
                // GhostSpawn and command buffers stay so Relay snapshots can spawn ghosts.
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
        /// Creates a WebGL ClientWorld that can receive snapshots and spawn ghosts.
        /// <para>
        /// Still omitted: Entities Graphics, Physics GraphicsIntegration (no compute; hybrid
        /// GameObjects draw the match), VariableRateSimulation (unused), and Multiplayer Center
        /// samples. GhostSpawn, command buffers, and the people-transport RPC client are included.
        /// </para>
        /// <para>
        /// GhostSpawn and every command-buffer system are created in the same pass as
        /// <c>RpcSystem</c> and <c>NetworkStreamReceiveSystem</c>. Those systems sort
        /// <c>UpdateAfter</c> the command buffers; adding the buffers later left the Relay
        /// handshake and map-recipe RPCs without a playback point, so the loading bar sat at
        /// the warmup tail and the connect watch timed out.
        /// <see cref="FixedStepSimulationSystemGroup"/> stays disabled: ship motors live in
        /// <see cref="PredictedFixedStepSimulationSystemGroup"/>, and the Unity fixed-step group
        /// trapped on Chrome in August 2026. Transform and predicted simulation stay on.
        /// </para>
        /// </summary>
        static void CreateWebGlClientWorld()
        {
            // --- Collect + filter ClientSimulation | Presentation systems ---
            NativeList<SystemTypeIndex> all = DefaultWorldInitialization.GetAllSystemTypeIndices(
                WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);

            var systems = new NativeList<SystemTypeIndex>(all.Length, Allocator.Temp);
            int commandBuffers = 0;
            int ghostSpawn = 0;
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    SystemTypeIndex index = all[i];
                    string systemName = TypeManager.GetSystemName(index).ToString();
                    if (IsWebGlExcludedSystemName(systemName))
                        continue;
                    if (systemName == "Unity.NetCode.GhostSpawnSystem")
                        ghostSpawn++;
                    else if (systemName.IndexOf("CommandBufferSystem", StringComparison.Ordinal) >= 0)
                        commandBuffers++;
                    systems.Add(index);
                }

                if (commandBuffers == 0)
                    Debug.LogError("[WebGLClient] No CommandBuffer systems registered. Snapshots and Relay RPCs cannot be applied.");
                if (ghostSpawn == 0)
                    Debug.LogError("[WebGLClient] GhostSpawnSystem was not registered. Ghosts cannot spawn.");

                // --- One create so RpcSystem / NetworkReceive sort against the command buffers ---
                Debug.Log("[WebGLClient] OnCreate begin. systems=" + systems.Length +
                          " commandBuffers=" + commandBuffers + " ghostSpawn=" + ghostSpawn + ".");
                World world = CreateClientWorld("ClientWorld", systems);
                Debug.Log("[WebGLClient] OnCreate complete.");

                // Ship motors run in PredictedFixedStep, which is inside PredictedSimulation,
                // not in FixedStepSimulationSystemGroup. Leave that Unity rate group off.
                var fixedStep = world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
                if (fixedStep != null)
                    fixedStep.Enabled = false;

                Debug.Log("[WebGLClient] ClientWorld on player loop. Transform and PredictedSimulation ON. " +
                          "FixedStep OFF. Next trap after this line is the first Update " +
                          "(see '[WebGLClient] First Update begin').");
            }
            finally
            {
                if (systems.IsCreated)
                    systems.Dispose();
                if (all.IsCreated)
                    all.Dispose();
            }
        }

        /// <summary>
        /// Systems that must not register on WebGL. Communication systems (GhostSpawn, command
        /// buffers, people-transport RPC) stay in the world.
        /// </summary>
        /// <param name="systemName">Full system type name from <see cref="TypeManager.GetSystemName"/>.</param>
        /// <returns>True when the system must be omitted from the WebGL ClientWorld.</returns>
        static bool IsWebGlExcludedSystemName(string systemName)
        {
            if (string.IsNullOrEmpty(systemName))
                return false;

            // [UNITY] Entities Graphics — no compute shaders on WebGL. Hybrid proxies draw ships.
            if (systemName.StartsWith("Unity.Rendering.", StringComparison.Ordinal))
                return true;
            if (systemName.StartsWith("Unity.Entities.Graphics.", StringComparison.Ordinal))
                return true;

            // [UNITY] Physics↔EG bridge — hybrid GameObject proxies own visuals on WebGL.
            if (systemName.StartsWith("Unity.Physics.GraphicsIntegration.", StringComparison.Ordinal))
                return true;

            // [TITAN-ORBIT] Unused. Also drops BeginVariableRateSimulationEntityCommandBufferSystem.
            // Predicted fixed-step is the ship tick. Other command buffers stay registered.
            if (systemName.IndexOf("VariableRateSimulation", StringComparison.Ordinal) >= 0)
                return true;

            // [TITAN-ORBIT] Multiplayer Center samples are not used by Titan Orbit.
            // Their SetRpcSystemDynamicAssemblyListSystem is what turns the server handshake
            // bit on. WebGL must not rely on that sample: TitanOrbitRpcDynamicAssemblyListSystem
            // sets the same bit on this ClientWorld.
            if (systemName.IndexOf("Unity.Multiplayer.Center", StringComparison.Ordinal) >= 0
                || systemName.IndexOf("Unity_Multiplayer_Center", StringComparison.Ordinal) >= 0)
                return true;

            return false;
        }

#endif
    }
}
