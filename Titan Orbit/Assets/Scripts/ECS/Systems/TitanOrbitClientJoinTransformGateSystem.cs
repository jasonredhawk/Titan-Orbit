using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client join settle + transform quarantine.
    /// <para>
    /// Proven (Player.log 2026-07-18 13:57): <c>TransformSystemGroup RE-ENABLED</c> after Instantiates
    /// ~630 asteroids → immediate Burst <c>Crash!!!</c> (even with MarkFromQuery disabled).
    /// So while NetworkStreamInGame, TransformSystemGroup stays <b>OFF</b>. Ships render via hybrid
    /// GameObject proxies when <see cref="ClientJoinSettleCache.TransformQuarantine"/> is true.
    /// </para>
    /// <para>
    /// Settling also exits when <see cref="ClientJoinSettleCache.MapProxyBuildReady"/> (hybrid GO
    /// proxies ≥ ~92% of meta N). Waiting for GhostSpawn idle forever left Join Team stuck at
    /// 314/315 while distance-importance Instantiates trickled.
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TitanOrbitClientJoinTransformGateSystem : ISystem
    {
        public const int IdleFramesRequired = 30;
        public const int MinInGameFramesBeforeExit = 120;
        public const int HardTimeoutFrames = 5400;

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

        /// <summary>Updates Settling; keeps TransformSystemGroup off for entire in-game session.</summary>
        public void OnUpdate(ref SystemState state)
        {
            ref var settle = ref SystemAPI.GetSingletonRW<ClientJoinSettleState>().ValueRW;
            bool inGame = !_inGameQuery.IsEmptyIgnoreFilter;

            if (!inGame)
            {
                settle.Settling = 0;
                settle.IdleClearFrames = 0;
                settle.InGameFrames = 0;
                settle.SawSpawnActivity = 0;
                settle.JoinSettleCompleted = 0;
                ClientJoinSettleCache.Clear();
                SetTransformGroupEnabled(ref state, enabled: true);
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
            // [TITAN-ORBIT] After any Instantiates activity, require a longer idle so we do not
            // declare settle complete mid-stream (distance importance still trickles ghosts).
            int idleNeeded = IdleFramesRequired * 2;

            // --- Exit after Instantiates were seen + idle, OR map GO build ready, OR hard timeout ---
            // [TITAN-ORBIT] Editor Local Host previously exited Settling at MinInGameFrames with
            // spawnBuf=0 / placeholders=0 (SawSpawnActivity never set). Meta already showed N
            // (e.g. 248) so the loading bar froze at 0/N while Instantiates had not started yet.
            // Keep Settling ON until GhostSpawn actually creates placeholders/Instantiates, then
            // idle-clear — unless HardTimeoutFrames elapses (map stream truly stuck).
            //
            // MapProxyBuildReady: Game publishes when planet/asteroid GOs ≥ ~92% of meta N.
            // Distance-importance Instantiates can keep placeholders non-empty forever — idle-clear
            // alone left Join Team unreachable at 314/315. Proxy-ready exits Settling safely;
            // TransformQuarantine stays on; GhostSpawnBacklog still tracks live queue.
            bool proxyBuildReady = ClientJoinSettleCache.MapProxyBuildReady;
            bool canExit = hardTimeout ||
                           (minTime &&
                            settle.SawSpawnActivity != 0 &&
                            settle.IdleClearFrames >= idleNeeded) ||
                           (minTime &&
                            settle.SawSpawnActivity != 0 &&
                            proxyBuildReady);

            // --- Settling policy ---
            // [TITAN-ORBIT] Initial join: Settling while Instantiates backlog drains.
            // After the first exit, NEVER re-enter Settling for post-team ship Instantiates —
            // Player.log 2026-07-18: TeamChoice → Settling ON (spawnBuf=1) → Crash!!!.
            bool shouldSettle;
            if (settle.JoinSettleCompleted != 0)
            {
                shouldSettle = false;
            }
            else
            {
                shouldSettle = !canExit;
                // Latch only when Instantiates were observed, proxy-ready, or hard-timeout escape.
                if (!shouldSettle && (settle.SawSpawnActivity != 0 || hardTimeout || proxyBuildReady))
                    settle.JoinSettleCompleted = 1;
            }

            byte newSettling = (byte)(shouldSettle ? 1 : 0);
            bool settlingChanged = settle.Settling != newSettling;
            settle.Settling = newSettling;

            // --- Quarantine: never RE-ENABLE TransformSystemGroup while in-game ---
            // GhostSpawnBacklog stays true during post-team ship Instantiates even when Settling
            // is latched off — presentation must not WithEntityAccess ships in that window.
            ClientJoinSettleCache.Set(
                shouldSettle,
                transformQuarantine: true,
                settle.InGameFrames,
                settle.JoinSettleCompleted != 0,
                ghostSpawnBacklog: backlog);
            SetTransformGroupEnabled(ref state, enabled: false);

            if (settlingChanged)
            {
                if (shouldSettle)
                {
                    UnityEngine.Debug.Log(
                        "[JoinSettle] Settling ON — TransformSystemGroup OFF (quarantine). " +
                        "Ships use hybrid GO proxies. spawnBuf=" + spawnBufferLen +
                        " placeholders=" + placeholderCount);
                }
                else
                {
                    UnityEngine.Debug.Log(
                        "[JoinSettle] Settling OFF — TransformSystemGroup stays OFF (quarantine; " +
                        "RE-ENABLE Crash!!!). Hybrid ship proxies remain. inGameFrames=" +
                        settle.InGameFrames + " idleClear=" + settle.IdleClearFrames +
                        " sawSpawn=" + settle.SawSpawnActivity +
                        " joinSettleCompleted=" + settle.JoinSettleCompleted +
                        " proxyReady=" + proxyBuildReady +
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
        }
    }
}
