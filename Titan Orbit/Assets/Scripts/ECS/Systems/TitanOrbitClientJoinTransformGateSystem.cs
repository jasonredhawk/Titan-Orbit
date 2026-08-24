using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client join settle state publisher for the seed-hydrate join model.
    /// <para>
    /// Asteroids are built locally from the match seed (<see cref="ClientMapHydrateSystem"/>),
    /// so the old Instantiates=1 / session-long TransformQuarantine workaround is no longer
    /// required for map load. <see cref="TransformSystemGroup"/> stays <b>enabled</b> on desktop.
    /// WebGL keeps it <b>disabled</b> — enabling it then ticking ClientWorld OOBs in Chrome.
    /// </para>
    /// <para>
    /// Settling tracks pre-InGame hydrate + short post-InGame dynamic ghost catch-up.
    /// Ship presentation still uses <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>
    /// around TeamChoice Instantiates (<see cref="GhostSpawnBacklog"/> / holds).
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TitanOrbitClientJoinTransformGateSystem : ISystem
    {
        /// <summary>Minimum InGame frames before Settling can exit (dynamic ghost catch-up).</summary>
        public const int MinInGameFramesBeforeExit = 30;

        /// <summary>Hard escape if something stalls Settling forever.</summary>
        public const int HardTimeoutFrames = 3600;

        EntityQuery _inGameQuery;
        EntityQuery _placeholderQuery;
        int _lastGroupEnabled;

        /// <summary>Builds queries and settle singleton.</summary>
        public void OnCreate(ref SystemState state)
        {
            _inGameQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            _placeholderQuery = state.GetEntityQuery(ComponentType.ReadOnly<PendingSpawnPlaceholder>());
            _lastGroupEnabled = -1;

            if (!SystemAPI.HasSingleton<ClientJoinSettleState>())
            {
                var ent = state.EntityManager.CreateSingleton(new ClientJoinSettleState
                {
                    Settling = 0,
                    IdleClearFrames = 0,
                    InGameFrames = 0,
                    SawSpawnActivity = 0,
                    JoinSettleCompleted = 0,
                });
                state.EntityManager.SetName(ent, "ClientJoinSettleState");
            }

            state.RequireForUpdate<ClientJoinSettleState>();
        }

        /// <summary>Publishes Settling / backlog; Transform stays on in play except WebGL.</summary>
        public void OnUpdate(ref SystemState state)
        {
            ref var settle = ref SystemAPI.GetSingletonRW<ClientJoinSettleState>().ValueRW;
            bool inGame = !_inGameQuery.IsEmptyIgnoreFilter;

            // --- Transform group ---
            // Desktop: ON (seed-hydrate — no session quarantine).
            // WebGL: OFF — enabling TransformSystemGroup then World.Update OOBs in Chrome
            // (2026-08-13 join: last C# line was TransformSystemGroup ENABLED, then WASM OOB).
#if UNITY_WEBGL && !UNITY_EDITOR
            SetTransformGroupEnabled(ref state, enabled: false);
#else
            SetTransformGroupEnabled(ref state, enabled: true);
#endif

            if (!inGame)
            {
                // --- Pre-InGame: settling while recipe hydrate runs ---
                bool hydrating = ClientMapHydrateCache.HasFullRecipe && !ClientMapHydrateCache.IsComplete;
                settle.Settling = (byte)(hydrating ? 1 : 0);
                settle.IdleClearFrames = 0;
                settle.InGameFrames = 0;
                settle.SawSpawnActivity = 0;
                settle.JoinSettleCompleted = 0;
                ClientJoinSettleCache.Set(
                    hydrating,
                    transformQuarantine: false,
                    inGameFrames: 0,
                    joinSettleCompleted: false,
                    ghostSpawnBacklog: false);
                // --- Proxy-ready is owned by the hybrid visualizer / EcsGameBridge ---
                // [TITAN-ORBIT] Do not treat hydrate-complete as GO-ready — Join Team waits on
                // MapLoadingProxyCount separately (second loading bar).
                return;
            }

            settle.InGameFrames++;

            int spawnBufferLen = 0;
            if (SystemAPI.TryGetSingletonEntity<GhostSpawnQueue>(out Entity spawnQueue) &&
                state.EntityManager.HasBuffer<GhostSpawnBuffer>(spawnQueue))
            {
                spawnBufferLen = state.EntityManager.GetBuffer<GhostSpawnBuffer>(spawnQueue).Length;
            }

            int placeholderCount = _placeholderQuery.CalculateEntityCount();
            bool backlog = spawnBufferLen > 0 || placeholderCount > 0;
            if (backlog)
                settle.SawSpawnActivity = 1;

            if (backlog)
                settle.IdleClearFrames = 0;
            else
                settle.IdleClearFrames++;

            bool hardTimeout = settle.InGameFrames >= HardTimeoutFrames;
            bool minTime = settle.InGameFrames >= MinInGameFramesBeforeExit;
            // --- Hydrate / recipe readiness ---
            // Do NOT treat "no recipe yet" as ready — that exited Settling before meta arrived
            // and opened Join Team with zero asteroids.
            bool hydrateReady;
            if (ClientMapHydrateCache.HasFullRecipe)
                hydrateReady = ClientMapHydrateCache.IsComplete;
            else if (ClientMapHydrateCache.HasRecipe)
                hydrateReady = true; // counts-only / legacy meta (no seed hydrate)
            else
                hydrateReady = false;
            // MapProxyBuildReady is published by EcsWorldVisualizer / EcsGameBridge from GO counts.

            // --- Exit Settling when hydrate done + brief InGame catch-up (or timeout) ---
            // [TITAN-ORBIT] Settling exit does NOT wait for GO proxies — the loading overlay /
            // Join Team gate does (IsMapLoadingComplete → proxy-ready). That keeps Transform /
            // backlog logic separate from hybrid Instantiates drain.
            bool canExit = hardTimeout || (minTime && hydrateReady);

            bool shouldSettle;
            if (settle.JoinSettleCompleted != 0)
            {
                shouldSettle = false;
            }
            else
            {
                shouldSettle = !canExit;
                if (!shouldSettle)
                    settle.JoinSettleCompleted = 1;
            }

            byte newSettling = (byte)(shouldSettle ? 1 : 0);
            bool settlingChanged = settle.Settling != newSettling;
            settle.Settling = newSettling;

            // --- No TransformQuarantine — ships/map use normal Transform + hybrid as needed ---
            ClientJoinSettleCache.Set(
                shouldSettle,
                transformQuarantine: false,
                settle.InGameFrames,
                settle.JoinSettleCompleted != 0,
                ghostSpawnBacklog: backlog);

            if (settlingChanged)
            {
                if (shouldSettle)
                {
                    UnityEngine.Debug.Log(
                        "[JoinSettle] Settling ON (seed-hydrate / dynamic catch-up). " +
#if UNITY_WEBGL && !UNITY_EDITOR
                        "TransformSystemGroup OFF (WebGL). " +
#else
                        "TransformSystemGroup ON. " +
#endif
                        "spawnBuf=" + spawnBufferLen +
                        " placeholders=" + placeholderCount +
                        " hydrate=" + ClientMapHydrateCache.BuiltBodies +
                        "/" + ClientMapHydrateCache.ExpectedBodies);
                }
                else
                {
                    PlanetConnectionGraphCache.RequestClientRebuild();
                    UnityEngine.Debug.Log(
#if UNITY_WEBGL && !UNITY_EDITOR
                        "[JoinSettle] Settling OFF — TransformSystemGroup OFF (WebGL). " +
#else
                        "[JoinSettle] Settling OFF — TransformSystemGroup ON (seed-hydrate model). " +
#endif
                        "inGameFrames=" + settle.InGameFrames +
                        " joinSettleCompleted=" + settle.JoinSettleCompleted +
                        " hydrateComplete=" + ClientMapHydrateCache.IsComplete +
                        (hardTimeout ? " (hard timeout)" : string.Empty));
                }
            }
        }

        /// <summary>Forces TransformSystemGroup + LocalToWorldSystem on or off.</summary>
        void SetTransformGroupEnabled(ref SystemState state, bool enabled)
        {
            int flag = enabled ? 1 : 0;
            if (_lastGroupEnabled == flag)
                return;

            var group = state.World.GetExistingSystemManaged<TransformSystemGroup>();
            if (group != null)
                group.Enabled = enabled;

            SystemHandle ltwHandle = state.WorldUnmanaged.GetExistingUnmanagedSystem<LocalToWorldSystem>();
            if (ltwHandle != SystemHandle.Null)
            {
                ref SystemState ltwState = ref state.WorldUnmanaged.ResolveSystemStateRef(ltwHandle);
                ltwState.Enabled = enabled;
            }

            _lastGroupEnabled = flag;
            UnityEngine.Debug.Log(
                "[JoinSettle] TransformSystemGroup " + (enabled ? "ENABLED" : "DISABLED") +
#if UNITY_WEBGL && !UNITY_EDITOR
                " (WebGL — Transform stays off).");
#else
                " (seed-hydrate join model).");
#endif
        }
    }
}
