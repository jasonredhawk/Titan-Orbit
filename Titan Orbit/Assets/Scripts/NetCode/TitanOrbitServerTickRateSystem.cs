using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Configures authoritative simulation and snapshot rate (synced to clients on connect).
    /// 60 Hz reduces discrete-step stutter vs the 30 Hz recovery-scene default.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitServerTickRateSystem : ISystem
    {
        public const int SimulationHz = 60;
        public const int NetworkHz = 60;

        public void OnCreate(ref SystemState state)
        {
            using var query = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            if (!query.IsEmpty)
                return;

            var tickRate = new ClientServerTickRate();
            tickRate.ResolveDefaults();
            tickRate.SimulationTickRate = SimulationHz;
            tickRate.NetworkTickRate = NetworkHz;
            tickRate.MaxSimulationStepsPerFrame = 4;
            tickRate.MaxSimulationStepBatchSize = 4;

            var entity = state.EntityManager.CreateEntity(typeof(ClientServerTickRate));
            state.EntityManager.SetComponentData(entity, tickRate);
        }
    }
}
