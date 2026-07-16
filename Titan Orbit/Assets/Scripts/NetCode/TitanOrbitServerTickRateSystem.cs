using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Creates / updates the authoritative <see cref="ClientServerTickRate"/> singleton on server boot.
    /// Simulation and network both run at 60 Hz. Never batches ticks into a larger physics dt, but allows
    /// up to 4 discrete steps per frame so low display FPS can still maintain 60 Hz simulation.
    /// World: ServerSimulation. Group: InitializationSystemGroup (first).
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
        /// Max discrete fixed steps per Unity frame when catching up. Kept equal on client and server.
        /// Product with <see cref="ClientServerTickRate.MaxSimulationStepBatchSize"/> = 1 caps
        /// NetworkTimeSystem's Local Host delta truncation at 4 ticks/frame (~15 FPS floor at 60 Hz).
        /// </summary>
        public const int MaxStepsPerFrame = 4;

        /// <summary>
        /// Re-applies every frame so package defaults / refresh RPCs cannot restore tick batching.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            ClientServerTickRate tickRate;
            Entity tickEntity;
            using (var query = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>()))
            {
                if (query.IsEmpty)
                {
                    tickRate = new ClientServerTickRate();
                    tickRate.ResolveDefaults();
                    tickEntity = state.EntityManager.CreateEntity(typeof(ClientServerTickRate));
                }
                else
                {
                    tickEntity = query.GetSingletonEntity();
                    tickRate = state.EntityManager.GetComponentData<ClientServerTickRate>(tickEntity);
                }
            }

            tickRate.SimulationTickRate = SimulationHz;
            tickRate.NetworkTickRate = NetworkHz;
            // [NETCODE] Catch up after hitches with discrete steps — never merge into one large dt.
            // H13 raised this to 16; basics4 rejected it (cmdAge/spikes unchanged) — restored to 4.
            tickRate.MaxSimulationStepsPerFrame = MaxStepsPerFrame;
            tickRate.MaxSimulationStepBatchSize = 1;

            state.EntityManager.SetComponentData(tickEntity, tickRate);

            // #region agent log
            if (!_loggedOnce)
            {
                _loggedOnce = true;
                try
                {
                    string path = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "..", "debug-6b87b4.log"));
                    long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string line =
                        "{\"sessionId\":\"6b87b4\",\"runId\":\"basics6\",\"hypothesisId\":\"H13\"," +
                        "\"location\":\"TitanOrbitServerTickRateSystem.OnUpdate\"," +
                        "\"message\":\"Server ClientServerTickRate forced\"," +
                        "\"data\":{\"simHz\":" + tickRate.SimulationTickRate +
                        ",\"maxBatch\":" + tickRate.MaxSimulationStepBatchSize +
                        ",\"maxSteps\":" + tickRate.MaxSimulationStepsPerFrame + "}," +
                        "\"timestamp\":" + ts + "}\n";
                    System.IO.File.AppendAllText(path, line);
                }
                catch { /* debug I/O only */ }
            }
            // #endregion
        }

        // #region agent log
        bool _loggedOnce;
        // #endregion
    }
}
