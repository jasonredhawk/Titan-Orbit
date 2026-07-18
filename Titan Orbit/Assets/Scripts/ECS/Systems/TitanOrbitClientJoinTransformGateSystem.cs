using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client join settle tracker. Sets <see cref="ClientJoinSettleCache.Settling"/>
    /// while GhostSpawn still has a backlog so hybrid/UI code can skip unsafe map-body
    /// <c>ToEntityArray</c> scans (minimap, MarkFromQuery, full visualizer Draw*).
    /// <para>
    /// CRITICAL (Player.log 2026-07-18 12:17): This system used to disable
    /// <see cref="Unity.Transforms.TransformSystemGroup"/> during Instantiates, then re-enable
    /// after idleClear=60. Re-enabling after Instantiates ~700 asteroids with transforms off
    /// caused an immediate Burst <c>Crash!!!</c> (LocalToWorld flood). That path is forbidden.
    /// </para>
    /// <para>
    /// Real Instantiates safety = GhostSpawn Instantiates cap at 1/frame with transforms
    /// <b>always left enabled</b> so LocalToWorld stays warm (one new hull per frame).
    /// Placeholders must still be CreateEntity'd stock-style for SpawnedGhostEntityMap.
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup.
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
        /// Hard timeout so settle never soft-locks hybrid/UI gates forever (~90s at 60 Hz).
        /// </summary>
        public const int HardTimeoutFrames = 5400;

        /// <summary>Cached query for in-game connections.</summary>
        EntityQuery _inGameQuery;

        /// <summary>PendingSpawnPlaceholder entities (GhostSpawn delayed Instantiates).</summary>
        EntityQuery _placeholderQuery;

        /// <summary>Builds queries and creates the settle singleton.</summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Queries ---
            // [NETCODE] NetworkStreamInGame is on connection entities after GoInGame is accepted.
            _inGameQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            // [NETCODE] PendingSpawnPlaceholder — temp entity holding snapshot until real Instantiates.
            _placeholderQuery = state.GetEntityQuery(ComponentType.ReadOnly<PendingSpawnPlaceholder>());

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

            // --- Recover transforms if a previous build left the group disabled ---
            // [TITAN-ORBIT] Older clients disabled TransformSystemGroup during settle; ensure ON.
            EnsureTransformGroupEnabled(ref state);
        }

        /// <summary>
        /// Updates settle flags from GhostSpawn backlog. Does NOT disable TransformSystemGroup.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // Belt-and-suspenders: never leave transforms off across frames.
            EnsureTransformGroupEnabled(ref state);

            ref var settle = ref SystemAPI.GetSingletonRW<ClientJoinSettleState>().ValueRW;
            bool inGame = !_inGameQuery.IsEmptyIgnoreFilter;

            // --- Not in-game: reset ---
            if (!inGame)
            {
                settle.Settling = 0;
                settle.IdleClearFrames = 0;
                settle.InGameFrames = 0;
                settle.SawSpawnActivity = 0;
                ClientJoinSettleCache.Clear();
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

            // --- Decide settle (hybrid/UI gate only) ---
            // Stay settling from the first in-game frame until spawn backlog is idle for
            // IdleFramesRequired AND MinInGameFramesBeforeExit has elapsed (or hard timeout).
            bool hardTimeout = settle.InGameFrames >= HardTimeoutFrames;
            bool minTime = settle.InGameFrames >= MinInGameFramesBeforeExit;
            int idleNeeded = settle.SawSpawnActivity != 0
                ? IdleFramesRequired * 2
                : IdleFramesRequired;
            bool canExit = hardTimeout || (minTime && settle.IdleClearFrames >= idleNeeded);

            bool shouldSettle = !canExit;
            byte newSettling = (byte)(shouldSettle ? 1 : 0);
            bool settlingChanged = settle.Settling != newSettling;
            settle.Settling = newSettling;

            ClientJoinSettleCache.Set(shouldSettle, settle.InGameFrames);

            // --- Diagnostics ---
            if (settlingChanged)
            {
                if (shouldSettle)
                {
                    UnityEngine.Debug.Log(
                        "[JoinSettle] Settling ON (hybrid/UI gate; TransformSystemGroup stays enabled). " +
                        "spawnBuf=" + spawnBufferLen + " placeholders=" + placeholderCount);
                }
                else
                {
                    UnityEngine.Debug.Log(
                        "[JoinSettle] Settling OFF (Instantiates backlog idle). " +
                        "inGameFrames=" + settle.InGameFrames +
                        " idleClear=" + settle.IdleClearFrames +
                        " sawSpawn=" + settle.SawSpawnActivity +
                        (hardTimeout ? " (hard timeout)" : string.Empty));
                }
            }
        }

        /// <summary>
        /// Forces <see cref="TransformSystemGroup"/> and <see cref="LocalToWorldSystem"/> enabled.
        /// Disabling them during Instantiates then flipping back on crashes Windows Burst LTW.
        /// </summary>
        static void EnsureTransformGroupEnabled(ref SystemState state)
        {
            var group = state.World.GetExistingSystemManaged<TransformSystemGroup>();
            if (group != null && !group.Enabled)
                group.Enabled = true;

            SystemHandle ltwHandle = state.WorldUnmanaged.GetExistingUnmanagedSystem<LocalToWorldSystem>();
            if (ltwHandle == SystemHandle.Null)
                return;

            ref SystemState ltwState = ref state.WorldUnmanaged.ResolveSystemStateRef(ltwHandle);
            if (!ltwState.Enabled)
                ltwState.Enabled = true;
        }
    }
}
