using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Intentionally disabled on Windows late-join.
    /// <para>
    /// Player.log 2026-07-18 17:06: after Join Team (and even before Settling ON),
    /// any asteroid/planet <c>SystemAPI.Query(...).WithEntityAccess()</c> / chunk walk in this
    /// system → <c>ArchetypeChunk.GetEntityDataPtrRO(EntityTypeHandle)</c> NRE → <c>Crash!!!</c>.
    /// Settling gates were not enough — the gather itself is unsafe over Instantiated map bodies.
    /// </para>
    /// <para>
    /// Map visuals must come from baked <see cref="MapBodyHybridVisualPending"/> on client ghost
    /// prefabs (rebake SubScenes / EntityScenes) drained by <c>EcsWorldVisualizer</c>.
    /// Do not re-enable full-map marking here.
    /// </para>
    /// World: ClientSimulation (kept so asmdef stays stable; OnUpdate is a no-op).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    public partial struct MapBodyHybridVisualRequestSystem : ISystem
    {
        /// <summary>Disabled — see type summary. Never re-enable asteroid WithEntityAccess marking.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.Enabled = false;
        }

        /// <summary>No-op. Do not add MarkFromQuery / WithEntityAccess over map bodies.</summary>
        public void OnUpdate(ref SystemState state)
        {
            // Intentionally empty.
        }
    }
}
