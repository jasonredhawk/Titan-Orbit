using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client join safety: disables <see cref="TransformSystemGroup"/> while NetCode
    /// Instantiates the late-join map ghost backlog, then re-enables when the backlog is idle.
    /// <para>
    /// Windows player hard-crashed during Relay late-join with hundreds of asteroids — first in
    /// Burst LocalToWorld, then in UnityPlayer even with LocalToWorld alone disabled. A fixed
    /// frame timer was not enough (ghosts can still be Instantiating after 20s). This system
    /// waits on a real backlog: <see cref="GhostSpawnBuffer"/> + <see cref="PendingSpawnPlaceholder"/>.
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup (before simulation/transform).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TitanOrbitClientJoinTransformGateSystem : ISystem
    {
        /// <summary>
        /// Consecutive idle frames required before ending settle (empty spawn buffer + no placeholders).
        /// </summary>
        public const int IdleFramesRequired = 30;

        /// <summary>
        /// Minimum in-game frames before settle may end — prevents exiting before the first
        /// GhostReceive packets arrive (buffer empty at frame 0 would otherwise look "idle").
        /// </summary>
        public const int MinInGameFramesBeforeExit = 120;

        /// <summary>
        /// Hard timeout so a stuck spawn never soft-locks transforms forever (~90s at 60 Hz).
        /// </summary>
        public const int HardTimeoutFrames = 5400;

        /// <summary>Cached query for in-game connections.</summary>
        EntityQuery _inGameQuery;

        /// <summary>PendingSpawnPlaceholder entities (GhostSpawn delayed Instantiates).</summary>
        EntityQuery _placeholderQuery;

        /// <summary>Last applied TransformSystemGroup enabled flag (-1 unknown, 0 off, 1 on).</summary>
        int _lastTransformsEnabled;

        /// <summary>Builds queries and creates the settle singleton.</summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Queries ---
            // [NETCODE] NetworkStreamInGame is on connection entities after GoInGame is accepted.
            _inGameQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            // [NETCODE] PendingSpawnPlaceholder — temp entity holding snapshot until real Instantiates.
            _placeholderQuery = state.GetEntityQuery(ComponentType.ReadOnly<PendingSpawnPlaceholder>());
            _lastTransformsEnabled = -1;

            // --- Singleton ---
            // [ECS/DOTS] One ClientJoinSettleState per client world for hybrid readers + moon ensure.
            if (!SystemAPI.HasSingleton<ClientJoinSettleState>())
            {
                var ent = state.EntityManager.CreateSingleton(new ClientJoinSettleState
                {
                    Settling = 0,
                    IdleClearFrames = 0,
                    InGameFrames = 0,
                    SawSpawnActivity = 0,
                });
                state.EntityManager.SetName(ent, "ClientJoinSettleState");
            }

            state.RequireForUpdate<ClientJoinSettleState>();
        }

        /// <summary>
        /// Updates settle state from GhostSpawn backlog and toggles TransformSystemGroup.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            ref var settle = ref SystemAPI.GetSingletonRW<ClientJoinSettleState>().ValueRW;
            bool inGame = !_inGameQuery.IsEmptyIgnoreFilter;

            // --- Not in-game: reset and restore transforms ---
            if (!inGame)
            {
                settle.Settling = 0;
                settle.IdleClearFrames = 0;
                settle.InGameFrames = 0;
                settle.SawSpawnActivity = 0;
                ClientJoinSettleCache.Clear();
                SetTransformGroupEnabled(ref state, enabled: true);
                return;
            }

            settle.InGameFrames++;

            // --- Measure GhostSpawn backlog ---
            // [NETCODE] GhostSpawnQueue singleton holds GhostSpawnBuffer + SnapshotDataBuffer.
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

            // --- Decide settle ---
            // Stay settling from the first in-game frame until spawn backlog is idle for
            // IdleFramesRequired AND MinInGameFramesBeforeExit has elapsed (or hard timeout).
            // We intentionally do NOT read MapSessionMetaCache here — that type lives in
            // TitanOrbit.NetCode and would create an asmdef cycle (NetCode already references ECS).
            bool hardTimeout = settle.InGameFrames >= HardTimeoutFrames;
            bool minTime = settle.InGameFrames >= MinInGameFramesBeforeExit;
            // If we never saw spawn activity, still wait minTime+idle (empty/thin sessions).
            // If we saw activity, require a longer idle stretch so mid-stream gaps do not re-enable
            // TransformSystemGroup while the next GhostReceive wave is still in flight.
            int idleNeeded = settle.SawSpawnActivity != 0
                ? IdleFramesRequired * 2
                : IdleFramesRequired;
            bool canExit = hardTimeout || (minTime && settle.IdleClearFrames >= idleNeeded);

            bool shouldSettle = !canExit;
            byte newSettling = (byte)(shouldSettle ? 1 : 0);
            bool settlingChanged = settle.Settling != newSettling;
            settle.Settling = newSettling;

            ClientJoinSettleCache.Set(shouldSettle, settle.InGameFrames);
            SetTransformGroupEnabled(ref state, enabled: !shouldSettle);

            // --- Diagnostics ---
            if (settlingChanged)
            {
                if (shouldSettle)
                {
                    UnityEngine.Debug.Log(
                        "[JoinSettle] TransformSystemGroup DISABLED (backlog-gated). " +
                        "spawnBuf=" + spawnBufferLen + " placeholders=" + placeholderCount);
                }
                else
                {
                    UnityEngine.Debug.Log(
                        "[JoinSettle] TransformSystemGroup RE-ENABLED after join settle. " +
                        "inGameFrames=" + settle.InGameFrames +
                        " idleClear=" + settle.IdleClearFrames +
                        " sawSpawn=" + settle.SawSpawnActivity +
                        (hardTimeout ? " (hard timeout)" : string.Empty));
                }
            }
        }

        /// <summary>
        /// Enables or disables Unity's <see cref="TransformSystemGroup"/> on this client world
        /// (ParentSystem + LocalToWorldSystem and related transform work).
        /// </summary>
        void SetTransformGroupEnabled(ref SystemState state, bool enabled)
        {
            int flag = enabled ? 1 : 0;
            if (_lastTransformsEnabled == flag)
                return;

            // --- Managed system group ---
            // [ECS/DOTS] TransformSystemGroup is a ComponentSystemGroup (managed), not an ISystem.
            var group = state.World.GetExistingSystemManaged<TransformSystemGroup>();
            if (group != null)
                group.Enabled = enabled;

            // --- Also toggle LocalToWorld explicitly ---
            // [TITAN-ORBIT] Belt-and-suspenders: some Entities versions leave child systems running
            // briefly when the group flag flips; disabling LTW directly matches the prior gate.
            SystemHandle ltwHandle = state.WorldUnmanaged.GetExistingUnmanagedSystem<LocalToWorldSystem>();
            if (ltwHandle != SystemHandle.Null)
            {
                ref SystemState ltwState = ref state.WorldUnmanaged.ResolveSystemStateRef(ltwHandle);
                ltwState.Enabled = enabled;
            }

            _lastTransformsEnabled = flag;
        }
    }
}
