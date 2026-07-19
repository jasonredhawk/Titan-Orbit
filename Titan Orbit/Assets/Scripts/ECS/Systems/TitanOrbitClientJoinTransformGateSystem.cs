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
            int idleNeeded = settle.SawSpawnActivity != 0
                ? IdleFramesRequired * 2
                : IdleFramesRequired;
            bool canExit = hardTimeout || (minTime && settle.IdleClearFrames >= idleNeeded);

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
                if (!shouldSettle && settle.SawSpawnActivity != 0)
                    settle.JoinSettleCompleted = 1;
            }

            byte newSettling = (byte)(shouldSettle ? 1 : 0);
            bool settlingChanged = settle.Settling != newSettling;
            settle.Settling = newSettling;

            // --- Quarantine: never RE-ENABLE TransformSystemGroup while in-game ---
            ClientJoinSettleCache.Set(
                shouldSettle,
                transformQuarantine: true,
                settle.InGameFrames,
                settle.JoinSettleCompleted != 0);
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
                        " joinSettleCompleted=1" +
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
