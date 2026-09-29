using TitanOrbit.Diagnostics;
using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Scenes;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [TITAN-ORBIT] WebGL ClientWorld tick that never runs Transform / predicted-fixed Burst.
    /// <para>
    /// Chrome 2026-08-09/10: stock <c>World.Update</c> after join OOBs once
    /// <see cref="TransformSystemGroup"/> is enabled (same native class as Windows Join Team
    /// Crash!!!). Menu boot strips GhostSpawn + CommandBuffers and unticks the world; join still
    /// called <c>clientWorld.Update()</c> 30× then every <c>ClientConnectWatch</c> frame.
    /// </para>
    /// Desktop / Editor keep a normal <c>World.Update</c>.
    /// </summary>
    public static class TitanOrbitWebGlClientTick
    {
        static int s_WebGlTick;
        static string s_LastUpdatedSys = "";
        static bool s_LastSimShould = true;
        static bool s_RecvSeen;
        static bool s_RecvEnabled;
        static bool s_RecvShouldRun;
        static int s_RecvManagedUpdates;
#if UNITY_WEBGL && !UNITY_EDITOR
        static bool s_LoggedFirstIncomingRpc;
#endif
        static bool s_LoggedResolveLeftEnabled;

        public static bool LastRecvSeen { get; private set; }
        public static bool LastRecvEnabled { get; private set; }
        public static bool LastRecvShouldRun { get; private set; }
        public static int RecvManagedUpdates => s_RecvManagedUpdates;
        public static bool LogPostConnectSystems;

        public static void ResetJoinRecvCounters()
        {
            s_RecvManagedUpdates = 0;
            LastRecvSeen = false;
            LastRecvEnabled = false;
            LastRecvShouldRun = false;
            s_WebGlTick = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
            s_LoggedFirstIncomingRpc = false;
#endif
        }

        /// <summary>
        /// Ticks a client world. On WebGL, forces Transform / predicted / fixed-step off first.
        /// </summary>
        /// <param name="world">ClientWorld (or any world on WebGL — server is not created there).</param>
        public static void SafeUpdate(World world)
        {
            if (world == null || !world.IsCreated)
                return;

#if UNITY_WEBGL && !UNITY_EDITOR
            // Chrome 2026-09-24: first leaf-walk completed (first-tick-end) then the next
            // SafeUpdate fell through to World.Update() and WASM-OOBd. Never use stock
            // World.Update on WebGL — keep Transform / predicted / presentation parked and
            // walk Init + Simulation the same way every join tick.
            DisableUnsafeGroups(world);
            TitanOrbitWebGlBeginSimulationEcb.Ensure(world);
            TitanOrbitWebGlDynamicAssemblyList.Ensure(world);
            // World.Update() rewinds DoubleUpdateAllocators before any group runs.
            world.Unmanaged.ResetUpdateAllocator();
            s_WebGlTick++;
            s_LastUpdatedSys = "";
            if (s_WebGlTick == 1)
                Debug.Log("[WebGLClientTick] SafeUpdate — Transform/Predicted/FixedStep forced OFF.");
            // #region agent log
            if (s_WebGlTick <= 8 || (s_WebGlTick % 30) == 0)
                Debug.Log("CONNECT_JOIN tick-start n=" + s_WebGlTick);
            bool tickLog = s_WebGlTick <= 3 || (s_WebGlTick % 120) == 0;
            if (tickLog)
            {
                WebGlBootDebugProbe.Emit("AD", "TitanOrbitWebGlClientTick.SafeUpdate", "allocator-reset-after",
                    "{\"n\":" + s_WebGlTick + "}");
                WebGlBootDebugProbe.Emit("J", "TitanOrbitWebGlClientTick.SafeUpdate", "webgl-tick-start",
                    "{\"n\":" + s_WebGlTick + ",\"systemMemoryMB\":" + SystemInfo.systemMemorySize + "}");
            }
            // #endregion
            s_RecvSeen = false;
            s_RecvEnabled = false;
            s_RecvShouldRun = false;
            TickRootGroup<InitializationSystemGroup>(world, "Initialization");
            TickRootGroup<SimulationSystemGroup>(world, "Simulation");
            TickRootGroup<PresentationSystemGroup>(world, "Presentation");
            // #region agent log
            if (tickLog)
            {
                WebGlBootDebugProbe.Emit("J", "TitanOrbitWebGlClientTick.SafeUpdate", "webgl-tick-end",
                    "{\"n\":" + s_WebGlTick + "}");
            }
            if (s_WebGlTick <= 8 || (s_WebGlTick % 30) == 0)
                EmitHandshakeSnapshot(world, "handshake-snapshot");
            if (LogPostConnectSystems)
            {
                Debug.Log("WEBGL_SYS tick-complete n=" + s_WebGlTick);
                LogPostConnectSystems = false;
            }
            // #endregion
            return;
#endif
            world.Update();
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        static void TickRootGroup<T>(World world, string name) where T : ComponentSystemGroup
        {
            var group = world.GetExistingSystemManaged<T>();
            bool found = group != null;
            bool enabled = found && group.Enabled;
            bool verbose = s_WebGlTick <= 2;
            // #region agent log
            if (verbose)
            {
                WebGlBootDebugProbe.Emit("K", "TitanOrbitWebGlClientTick.SafeUpdate", "group-before",
                    "{\"name\":\"" + name + "\",\"found\":" + (found ? "true" : "false") +
                    ",\"enabled\":" + (enabled ? "true" : "false") + "}");
            }
            // #endregion
            if (enabled)
            {
                if (typeof(T) == typeof(InitializationSystemGroup))
                    TickInitializationChildren(world, group);
                else if (typeof(T) == typeof(SimulationSystemGroup))
                    TickSimulationChildren(world, group);
                else
                    group.Update();
            }
            // #region agent log
            if (verbose)
            {
                WebGlBootDebugProbe.Emit("K", "TitanOrbitWebGlClientTick.SafeUpdate", "group-after",
                    "{\"name\":\"" + name + "\"}");
            }
            // #endregion
        }

        static void TickInitializationChildren(World world, ComponentSystemGroup group)
        {
            TickGroupChildren(world, group, "init", "P");
        }

        static void TickSimulationChildren(World world, ComponentSystemGroup group)
        {
            bool hasRate = group.RateManager != null;
            bool tickLog = s_WebGlTick <= 3 || (s_WebGlTick % 120) == 0;
            // #region agent log
            if (tickLog)
            {
                WebGlBootDebugProbe.Emit("K", "TitanOrbitWebGlClientTick.TickSimulationChildren", "sim-rate-before",
                    "{\"hasRate\":" + (hasRate ? "true" : "false") + "}");
            }
            // #endregion
            if (hasRate)
            {
                bool should = group.RateManager.ShouldGroupUpdate(group);
                s_LastSimShould = should;
                // #region agent log
                if (tickLog)
                {
                    WebGlBootDebugProbe.Emit("K", "TitanOrbitWebGlClientTick.TickSimulationChildren", "sim-rate-after",
                        "{\"should\":" + (should ? "true" : "false") + "}");
                }
                // #endregion
                if (!should)
                    return;
            }
            else
            {
                s_LastSimShould = true;
            }

            TickGroupChildren(world, group, "sim", "K");
            TitanOrbitWebGlBeginSimulationEcb.Playback(world);

            // Stock ComponentSystemGroup.OnUpdate calls ShouldGroupUpdate again to PopTime.
            if (hasRate)
            {
                // #region agent log
                if (tickLog)
                    WebGlBootDebugProbe.Emit("K", "TitanOrbitWebGlClientTick.TickSimulationChildren", "sim-rate-pop-before", "{}");
                // #endregion
                group.RateManager.ShouldGroupUpdate(group);
                // #region agent log
                if (tickLog)
                    WebGlBootDebugProbe.Emit("K", "TitanOrbitWebGlClientTick.TickSimulationChildren", "sim-rate-pop-after", "{}");
                // #endregion
            }
        }

        static void TickGroupChildren(World world, ComponentSystemGroup group, string tag, string hypothesisId)
        {
            group.SortSystems();
            NativeList<SystemHandle> handles = group.GetAllSystems(Allocator.Temp);
            bool verbose = false;
            try
            {
                // #region agent log
                if (verbose)
                {
                    WebGlBootDebugProbe.Emit(hypothesisId, "TitanOrbitWebGlClientTick.TickGroupChildren", tag + "-walk-start",
                        "{\"count\":" + handles.Length + "}");
                }
                // #endregion
                for (int i = 0; i < handles.Length; i++)
                {
                    SystemHandle handle = handles[i];
                    SystemTypeIndex typeIndex = world.Unmanaged.GetSystemTypeIndex(handle);
                    string sysName = TypeManager.GetSystemName(typeIndex).ToString();
                    bool isGroup = !string.IsNullOrEmpty(sysName) &&
                                   (sysName.EndsWith("SystemGroup") || sysName.IndexOf("SystemGroup+", System.StringComparison.Ordinal) >= 0);
                    ref SystemState sysState = ref world.Unmanaged.ResolveSystemStateRef(handle);
                    bool sysEnabled = sysState.Enabled;
                    // #region agent log
                    if (verbose)
                    {
                        WebGlBootDebugProbe.Emit(hypothesisId, "TitanOrbitWebGlClientTick.TickGroupChildren", tag + "-child-before",
                            "{\"i\":" + i + ",\"name\":\"" + EscapeJson(sysName) +
                            "\",\"enabled\":" + (sysEnabled ? "true" : "false") +
                            ",\"group\":" + (isGroup ? "true" : "false") + "}");
                    }
                    // #endregion
                    if (sysName == "Unity.NetCode.NetworkStreamReceiveSystem")
                    {
                        s_RecvSeen = true;
                        s_RecvEnabled = sysEnabled;
                        s_RecvShouldRun = sysState.ShouldRunSystem();
                        LastRecvSeen = true;
                        LastRecvEnabled = sysEnabled;
                        LastRecvShouldRun = s_RecvShouldRun;
                        bool hasEcb = false;
                        using (var q = world.EntityManager.CreateEntityQuery(
                                   ComponentType.ReadOnly<NetworkGroupCommandBufferSystem.Singleton>()))
                            hasEcb = !q.IsEmptyIgnoreFilter;
                        // #region agent log
                        if (verbose)
                        {
                            WebGlBootDebugProbe.Emit("Z", "TitanOrbitWebGlClientTick.TickGroupChildren", "recv-precheck",
                                "{\"hasEcb\":" + (hasEcb ? "true" : "false") +
                                ",\"shouldRun\":" + (s_RecvShouldRun ? "true" : "false") + "}");
                        }
                        // #endregion
                    }
                    if (sysEnabled)
                    {
                        bool isRpcGroup = sysName.IndexOf("RpcCommandRequestSystemGroup", System.StringComparison.Ordinal) >= 0;
                        bool isRpcSystem = sysName == "Unity.NetCode.RpcSystem";
                        // #region agent log
                        if (isRpcGroup && s_WebGlTick <= 8)
                        {
                            WebGlBootDebugProbe.Emit("H14", "TitanOrbitWebGlClientTick.TickGroupChildren",
                                "rpc-group-before", "{\"n\":" + s_WebGlTick + "}");
                        }
                        if (isRpcSystem && s_WebGlTick <= 8)
                        {
                            WebGlBootDebugProbe.Emit("H21", "TitanOrbitWebGlClientTick.TickGroupChildren",
                                "rpc-system-before", "{\"n\":" + s_WebGlTick + "}");
                            Debug.Log("CONNECT_JOIN rpc-system-before n=" + s_WebGlTick);
                        }
                        // #endregion
                        bool isResolve = sysName.IndexOf("ResolveSceneReferenceSystem", System.StringComparison.Ordinal) >= 0;
                        if (isResolve)
                        {
                            TitanOrbitWebGlSceneResolve.EnsureHeaderRecipe(world);
                            // #region agent log
                            if (s_WebGlTick <= 8 || (s_WebGlTick % 120) == 0)
                            {
                                WebGlBootDebugProbe.Emit("H-SR", "TitanOrbitWebGlClientTick.TickGroupChildren",
                                    "resolve-before", "{\"n\":" + s_WebGlTick + "}");
                                Debug.Log("CONNECT_JOIN resolve-before n=" + s_WebGlTick);
                            }
                            // #endregion
                        }
                        if (isGroup)
                        {
                            var childGroup = world.GetExistingSystemManaged(typeIndex) as ComponentSystemGroup;
                            if (childGroup != null)
                            {
                                string childTag = sysName.IndexOf("SceneSystemGroup", System.StringComparison.Ordinal) >= 0
                                    ? "scene"
                                    : "nested";
                                TickGroupChildren(world, childGroup, childTag, "T");
                            }
                            else
                                handle.Update(world.Unmanaged);
                        }
                        else
                            handle.Update(world.Unmanaged);
                        if (isResolve)
                        {
                            // #region agent log
                            if (s_WebGlTick <= 8 || (s_WebGlTick % 120) == 0)
                            {
                                WebGlBootDebugProbe.Emit("H-SR", "TitanOrbitWebGlClientTick.TickGroupChildren",
                                    "resolve-after", "{\"n\":" + s_WebGlTick + "}");
                                Debug.Log("CONNECT_JOIN resolve-after n=" + s_WebGlTick);
                            }
                            // #endregion
                        }
                        s_LastUpdatedSys = sysName;
                        // #region agent log
                        if (isRpcGroup && s_WebGlTick <= 8)
                        {
                            WebGlBootDebugProbe.Emit("H14", "TitanOrbitWebGlClientTick.TickGroupChildren",
                                "rpc-group-after", "{\"n\":" + s_WebGlTick + "}");
                        }
                        if (isRpcSystem && s_WebGlTick <= 8)
                        {
                            WebGlBootDebugProbe.Emit("H21", "TitanOrbitWebGlClientTick.TickGroupChildren",
                                "rpc-system-after", "{\"n\":" + s_WebGlTick + "}");
                            Debug.Log("CONNECT_JOIN rpc-system-after n=" + s_WebGlTick);
                        }
                        // #endregion
                        if (sysName == "Unity.NetCode.NetworkStreamReceiveSystem")
                            s_RecvManagedUpdates++;
                    }
                    // #region agent log
                    if (verbose)
                    {
                        WebGlBootDebugProbe.Emit(hypothesisId, "TitanOrbitWebGlClientTick.TickGroupChildren", tag + "-child-after",
                            "{\"i\":" + i + ",\"name\":\"" + EscapeJson(sysName) + "\"}");
                    }
                    // #endregion
                }
                // #region agent log
                if (verbose)
                {
                    WebGlBootDebugProbe.Emit(hypothesisId, "TitanOrbitWebGlClientTick.TickGroupChildren", tag + "-walk-end", "{}");
                }
                // #endregion
            }
            finally
            {
                if (handles.IsCreated)
                    handles.Dispose();
            }
        }

        static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// One-shot-friendly ClientWorld prefab + SubScene telemetry. Distinguishes
        /// missing singleton vs Null Asteroid vs scene-stream stall.
        /// </summary>
        static string PrefabSceneTelemetryJson(World world)
        {
            var em = world.EntityManager;
            int gpCount = 0;
            int ast = -1;
            int ship = -1;
            int planet = -1;
            int gem = -1;
            using (var q = em.CreateEntityQuery(typeof(GamePrefabs)))
            {
                gpCount = q.CalculateEntityCount();
                if (gpCount == 1)
                {
                    var gp = q.GetSingleton<GamePrefabs>();
                    ast = gp.Asteroid.Index;
                    ship = gp.Ship.Index;
                    planet = gp.Planet.Index;
                    gem = gp.Gem.Index;
                }
            }

            int reqScene = 0;
            int sceneRef = 0;
            int section = 0;
            using (var q = em.CreateEntityQuery(typeof(RequestSceneLoaded)))
                reqScene = q.CalculateEntityCount();
            using (var q = em.CreateEntityQuery(typeof(SceneReference)))
                sceneRef = q.CalculateEntityCount();
            using (var q = em.CreateEntityQuery(typeof(SceneSectionData)))
                section = q.CalculateEntityCount();

            int defGp = -1;
            var inj = World.DefaultGameObjectInjectionWorld;
            if (inj != null && inj.IsCreated && inj != world)
            {
                using (var q = inj.EntityManager.CreateEntityQuery(typeof(GamePrefabs)))
                    defGp = q.CalculateEntityCount();
            }

            int resolveE = -1;
            int sceneSysE = -1;
            int streamE = -1;
            var sceneGroup = world.GetExistingSystemManaged<SceneSystemGroup>();
            if (sceneGroup != null)
            {
                NativeList<SystemHandle> handles = sceneGroup.GetAllSystems(Allocator.Temp);
                try
                {
                    for (int i = 0; i < handles.Length; i++)
                    {
                        SystemHandle handle = handles[i];
                        SystemTypeIndex typeIndex = world.Unmanaged.GetSystemTypeIndex(handle);
                        string sysName = TypeManager.GetSystemName(typeIndex).ToString();
                        bool enabled = world.Unmanaged.ResolveSystemStateRef(handle).Enabled;
                        if (sysName.IndexOf("ResolveSceneReferenceSystem", System.StringComparison.Ordinal) >= 0)
                            resolveE = enabled ? 1 : 0;
                        else if (sysName == "Unity.Scenes.SceneSystem" || sysName.EndsWith(".SceneSystem", System.StringComparison.Ordinal))
                            sceneSysE = enabled ? 1 : 0;
                        else if (sysName.IndexOf("SceneSectionStreamingSystem", System.StringComparison.Ordinal) >= 0)
                            streamE = enabled ? 1 : 0;
                    }
                }
                finally
                {
                    if (handles.IsCreated)
                        handles.Dispose();
                }
            }

            int subGo = 0;
            int autoLoad = 0;
            var subs = Object.FindObjectsByType<SubScene>(FindObjectsSortMode.None);
            if (subs != null)
            {
                subGo = subs.Length;
                for (int i = 0; i < subs.Length; i++)
                {
                    if (subs[i] != null && subs[i].AutoLoadScene)
                        autoLoad++;
                }
            }

            return "\"gpCount\":" + gpCount +
                   ",\"ast\":" + ast +
                   ",\"ship\":" + ship +
                   ",\"planet\":" + planet +
                   ",\"gem\":" + gem +
                   ",\"reqScene\":" + reqScene +
                   ",\"sceneRef\":" + sceneRef +
                   ",\"section\":" + section +
                   ",\"defGp\":" + defGp +
                   ",\"resolveE\":" + resolveE +
                   ",\"sceneSysE\":" + sceneSysE +
                   ",\"streamE\":" + streamE +
                   ",\"subGo\":" + subGo +
                   ",\"autoLoad\":" + autoLoad;
        }

        static void EmitHandshakeSnapshot(World world, string message)
        {
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            int connections = 0;
            int withNetworkId = 0;
            int inGame = 0;
            string state = "none";
            bool hasEcb = false;
            bool hasGhost = false;
            using (var q = em.CreateEntityQuery(typeof(NetworkStreamConnection)))
            {
                connections = q.CalculateEntityCount();
                if (connections > 0)
                    state = q.GetSingleton<NetworkStreamConnection>().CurrentState.ToString();
            }
            using (var q = em.CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkId)))
                withNetworkId = q.CalculateEntityCount();
            using (var q = em.CreateEntityQuery(typeof(NetworkStreamInGame)))
                inGame = q.CalculateEntityCount();
            using (var q = em.CreateEntityQuery(typeof(NetworkGroupCommandBufferSystem.Singleton)))
                hasEcb = !q.IsEmptyIgnoreFilter;
            using (var q = em.CreateEntityQuery(typeof(GhostCollection)))
                hasGhost = !q.IsEmptyIgnoreFilter;

            bool zombie = connections > 0 && withNetworkId == 0;
            int dal = -1;
            int rpcInLen = 0;
            int rpcFirst = -1;
            bool hasRpcIn = false;
            using (var q = em.CreateEntityQuery(typeof(RpcCollection)))
            {
                if (!q.IsEmptyIgnoreFilter)
                    dal = q.GetSingleton<RpcCollection>().DynamicAssemblyList ? 1 : 0;
            }
            using (var q = em.CreateEntityQuery(typeof(NetworkStreamConnection), typeof(IncomingRpcDataStreamBuffer)))
            {
                hasRpcIn = !q.IsEmptyIgnoreFilter;
                if (hasRpcIn && q.CalculateEntityCount() == 1)
                {
                    var buf = q.GetSingletonBuffer<IncomingRpcDataStreamBuffer>();
                    rpcInLen = buf.Length;
                    if (rpcInLen > 0)
                        rpcFirst = buf[0].Value;
                }
            }
            // #region agent log
            if (rpcInLen > 0 && !s_LoggedFirstIncomingRpc)
            {
                s_LoggedFirstIncomingRpc = true;
                WebGlBootDebugProbe.Emit("H-DAL-B", "TitanOrbitWebGlClientTick.EmitHandshakeSnapshot",
                    "first-incoming-rpc",
                    "{\"n\":" + s_WebGlTick +
                    ",\"dal\":" + dal +
                    ",\"rpcInLen\":" + rpcInLen +
                    ",\"rpcFirst\":" + rpcFirst +
                    ",\"hasRpcIn\":" + (hasRpcIn ? "true" : "false") + "}");
                Debug.Log("CONNECT_JOIN first-incoming-rpc n=" + s_WebGlTick +
                          " dal=" + dal + " rpcInLen=" + rpcInLen + " rpcFirst=" + rpcFirst);
            }
            string prefabScene = PrefabSceneTelemetryJson(world);
            WebGlBootDebugProbe.Emit("H-HYD", "TitanOrbitWebGlClientTick.EmitHandshakeSnapshot", message,
                "{\"n\":" + s_WebGlTick +
                ",\"connections\":" + connections +
                ",\"withNetworkId\":" + withNetworkId +
                ",\"inGame\":" + inGame +
                ",\"zombie\":" + (zombie ? "true" : "false") +
                ",\"state\":\"" + EscapeJson(state) + "\"" +
                ",\"dal\":" + dal +
                ",\"rpcInLen\":" + rpcInLen +
                ",\"rpcFirst\":" + rpcFirst +
                ",\"hasRpcIn\":" + (hasRpcIn ? "true" : "false") +
                ",\"hasEcb\":" + (hasEcb ? "true" : "false") +
                ",\"hasGhostCollection\":" + (hasGhost ? "true" : "false") +
                ",\"hasRecipe\":" + (ClientMapHydrateCache.HasFullRecipe ? "true" : "false") +
                ",\"hydrateComplete\":" + (ClientMapHydrateCache.IsComplete ? "true" : "false") +
                ",\"hydrateStarted\":" + (ClientMapHydrateCache.HydrateStarted ? "true" : "false") +
                ",\"waitingPrefabs\":" + (ClientMapHydrateCache.WaitingForPrefabs ? "true" : "false") +
                ",\"built\":" + ClientMapHydrateCache.BuiltBodies +
                ",\"expected\":" + ClientMapHydrateCache.ExpectedBodies +
                "," + prefabScene +
                ",\"simShould\":" + (s_LastSimShould ? "true" : "false") +
                ",\"recvSeen\":" + (s_RecvSeen ? "true" : "false") +
                ",\"recvEnabled\":" + (s_RecvEnabled ? "true" : "false") +
                ",\"recvShouldRun\":" + (s_RecvShouldRun ? "true" : "false") +
                ",\"recvUpdates\":" + s_RecvManagedUpdates +
                "," + TitanOrbitSessionManager.ConnectTelemetryJsonFragment() +
                "}");
            Debug.Log("CONNECT_JOIN n=" + s_WebGlTick +
                      " state=" + state +
                      " nid=" + withNetworkId +
                      " inGame=" + inGame +
                      " zombie=" + zombie +
                      " dal=" + dal +
                      " rpcIn=" + rpcInLen +
                      " recv=" + s_RecvManagedUpdates +
                      " recvRun=" + s_RecvShouldRun +
                      " recipe=" + ClientMapHydrateCache.HasFullRecipe +
                      " hydrate=" + ClientMapHydrateCache.IsComplete +
                      " built=" + ClientMapHydrateCache.BuiltBodies +
                      "/" + ClientMapHydrateCache.ExpectedBodies +
                      " ghosts=" + hasGhost +
                      " " + prefabScene.Replace("\"", "").Replace(",", " ") +
                      " lastSys=" + s_LastUpdatedSys);
            // #endregion
        }
#endif

        /// <summary>
        /// Parks Burst-heavy groups that OOB on WebGL. Call after CreateClientWorld and before
        /// any join tick so JoinSettle cannot race them back on in the same Update.
        /// </summary>
        public static void DisableUnsafeGroups(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var transform = world.GetExistingSystemManaged<TransformSystemGroup>();
            if (transform != null)
                transform.Enabled = false;

            // LocalToWorldSystem is unmanaged — JoinSettle disables it on WebGL (ECS allowUnsafe).

            var predicted = world.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            if (predicted != null)
                predicted.Enabled = false;

            var fixedStep = world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            if (fixedStep != null)
                fixedStep.Enabled = false;

            var presentation = world.GetExistingSystemManaged<PresentationSystemGroup>();
            if (presentation != null)
                presentation.Enabled = false;

            // Chrome join: stock ScheduleHeaderLoadOnEntity AddComponentData of cleanup
            // RequestSceneHeader WASM-OOBd on first OnUpdate. Do not park this system —
            // GamePrefabs lives in the SubScene. TitanOrbitWebGlSceneResolve TypeSet-adds
            // the cleanup + hash before the stock update (same recipe as Connect).
            // #region agent log
            if (!s_LoggedResolveLeftEnabled)
            {
                var sceneGroup = world.GetExistingSystemManaged<SceneSystemGroup>();
                if (sceneGroup != null)
                {
                    var sceneSystems = sceneGroup.ManagedSystems;
                    for (int i = 0; i < sceneSystems.Count; i++)
                    {
                        var sys = sceneSystems[i];
                        if (sys == null || sys.GetType().Name != "ResolveSceneReferenceSystem")
                            continue;
                        s_LoggedResolveLeftEnabled = true;
                        WebGlBootDebugProbe.Emit("H-SR", "TitanOrbitWebGlClientTick.DisableUnsafeGroups",
                            "resolve-left-enabled", "{\"enabled\":" + (sys.Enabled ? "true" : "false") + "}");
                    }
                }
            }
            // #endregion

            // Chrome join 2026-09-24: first Simulation child OnUpdate WASM-OOBs
            // (sim-child-before i=0 ShipCommandRoleRefreshSystem, no after). RateManager and
            // SortSystems both finished (sim-rate-after, sim-walk-start count=62).
            SystemHandle role = world.Unmanaged.GetExistingUnmanagedSystem<ShipCommandRoleRefreshSystem>();
            if (role != SystemHandle.Null)
            {
                ref SystemState roleState = ref world.Unmanaged.ResolveSystemStateRef(role);
                if (roleState.Enabled)
                {
                    roleState.Enabled = false;
                    // #region agent log
                    WebGlBootDebugProbe.Emit("Y", "TitanOrbitWebGlClientTick.DisableUnsafeGroups",
                        "disabled-ship-command-role", "{}");
                    // #endregion
                }
            }
        }
    }
}
