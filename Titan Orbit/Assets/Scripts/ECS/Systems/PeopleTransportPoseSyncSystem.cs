using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Retired per-tick pose stream. PeopleTransportGhost Instantiates flooded Windows GhostSpawn,
    /// and a pose RPC every tick is not official NCE (continuous state belongs on a ghost).
    /// <para>
    /// Clients dead-reckon VFX from <see cref="PeopleTransportSpawnRpc"/> velocity.
    /// End-of-life still uses a one-off <see cref="PeopleTransportPoseRpc"/>
    /// (Consumed / Destroyed) from <see cref="PeopleTransportNetNotify.EndAndDestroy"/>.
    /// Re-enable a budgeted ghost spawn only after GhostSpawn stays safe on Windows join.
    /// </para>
    /// World: ServerSimulation. Disabled in <see cref="OnCreate"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    public partial struct PeopleTransportPoseSyncSystem : ISystem
    {
        /// <summary>Permanently off — per-tick pose RPCs are not the NCE path.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.Enabled = false;
        }

        /// <summary>No-op while disabled.</summary>
        public void OnUpdate(ref SystemState state)
        {
        }
    }
}
