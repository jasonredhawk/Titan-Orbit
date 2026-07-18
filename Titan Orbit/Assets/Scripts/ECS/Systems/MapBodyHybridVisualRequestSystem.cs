using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Intentionally disabled. Map ghosts bake <see cref="MapBodyHybridVisualPending"/>
    /// on the client prefab; <c>EcsWorldVisualizer</c> drains that small queue.
    /// <para>
    /// Player.log 2026-07-18 13:26: after settle, <c>MarkFromQuery</c> → <c>ToEntityArray</c> →
    /// Burst <c>GatherEntitiesWithoutFilter</c> → <c>Crash!!!</c>. Gathering Instantiated asteroids
    /// is never safe on Windows late-join — settle ending does not make it safe.
    /// </para>
    /// World: ClientSimulation (system kept so asmdef/references stay stable; OnUpdate is a no-op).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    public partial struct MapBodyHybridVisualRequestSystem : ISystem
    {
        /// <summary>No-op — see type summary. Pending must be baked on ghost prefabs.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.Enabled = false;
        }

        /// <summary>Disabled — never ToEntityArray map bodies.</summary>
        public void OnUpdate(ref SystemState state)
        {
            // Intentionally empty. Do not re-enable MarkFromQuery.
        }
    }
}
