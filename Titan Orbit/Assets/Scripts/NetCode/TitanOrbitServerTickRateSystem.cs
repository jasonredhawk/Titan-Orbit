using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Creates the authoritative <see cref="ClientServerTickRate"/> singleton on server boot.
    /// Simulation and network both run at 60 Hz — movement speeds are in units/second, so raising
    /// Hz changes step count and snapshot rate, not how fast ships travel. Clients inherit the
    /// rate when they connect. World: ServerSimulation. Group: InitializationSystemGroup (first).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitServerTickRateSystem : ISystem
    {
        /// <summary>Fixed simulation steps per second on server and synced to clients.</summary>
        public const int SimulationHz = 60;

        /// <summary>Ghost snapshot send rate — matched to sim Hz for responsive replication.</summary>
        public const int NetworkHz = 60;

        /// <summary>
        /// Runs once — inserts ClientServerTickRate if missing (dedicated server cold start).
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // [ECS/DOTS] Singleton may already exist from NetCode defaults — do not duplicate.
            using var query = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            if (!query.IsEmpty)
                return;

            // --- Configure tick rates ---
            var tickRate = new ClientServerTickRate();
            tickRate.ResolveDefaults();
            tickRate.SimulationTickRate = SimulationHz;
            tickRate.NetworkTickRate = NetworkHz;
            // [NETCODE] Allow up to 4 sim steps per frame to catch up after hitch.
            tickRate.MaxSimulationStepsPerFrame = 4;
            tickRate.MaxSimulationStepBatchSize = 4;

            var entity = state.EntityManager.CreateEntity(typeof(ClientServerTickRate));
            state.EntityManager.SetComponentData(entity, tickRate);
        }
    }
}
