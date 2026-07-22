using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Re-publishes <see cref="ClientJoinSettleCache.GhostSpawnBacklog"/> after
    /// <see cref="GhostSpawnSystem"/> runs each frame.
    /// <para>
    /// The join gate in <see cref="InitializationSystemGroup"/> samples GhostSpawnBuffer before
    /// GhostSpawn creates placeholders / Instantiates. On the TeamChoice ship-arrival frame that
    /// left the cache at <c>false</c> while Instantiates already ran — then
    /// <c>EcsWorldVisualizer.SyncShipProxyTransforms</c> <c>ToEntityArray</c> → Crash!!!
    /// (Player.log 2026-07-20). This system closes that one-frame hole for LateUpdate / onBeforeRender.
    /// </para>
    /// <para>
    /// Backlog is queue/placeholder non-empty <b>or</b> a short Instantiates hold
    /// (<see cref="ClientJoinSettleCache.ComputeGhostSpawnBacklog"/>) so ship systems stay gated
    /// after the placeholder is already gone (Player.log 2026-07-22 TeamChoiceResult → Crash!!!).
    /// </para>
    /// World: ClientSimulation. Group: GhostSpawnSystemGroup, after GhostSpawnSystem.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnSystemGroup))]
    [UpdateAfter(typeof(GhostSpawnSystem))]
    public partial struct TitanOrbitGhostSpawnBacklogRefreshSystem : ISystem
    {
        /// <summary>Placeholders waiting for Instantiates (1/frame).</summary>
        EntityQuery _placeholderQuery;

        /// <summary>Caches the placeholder query; requires an in-game connection.</summary>
        public void OnCreate(ref SystemState state)
        {
            _placeholderQuery = state.GetEntityQuery(ComponentType.ReadOnly<PendingSpawnPlaceholder>());
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// After GhostSpawn: if buffer or placeholders remain, mark backlog so ship presentation
        /// skips <c>ToEntityArray</c> / <c>WithEntityAccess</c> for the rest of this frame.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Live spawn queue length ---
            int spawnBufferLen = 0;
            if (SystemAPI.TryGetSingletonEntity<GhostSpawnQueue>(out Entity spawnQueue) &&
                state.EntityManager.HasBuffer<GhostSpawnBuffer>(spawnQueue))
            {
                spawnBufferLen = state.EntityManager.GetBuffer<GhostSpawnBuffer>(spawnQueue).Length;
            }

            // --- Placeholders still waiting for Instantiates ---
            int placeholderCount = _placeholderQuery.CalculateEntityCount();
            bool queueBusy = spawnBufferLen > 0 || placeholderCount > 0;

            // [TITAN-ORBIT] SetGhostSpawnBacklog also latches a short Instantiates hold so the
            // TeamChoice ship frame (placeholder already gone) still blocks ship queries.
            // Settling / quarantine stay owned by the join gate.
            ClientJoinSettleCache.SetGhostSpawnBacklog(queueBusy);
        }
    }
}
