using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Minimal client tick tuning for predicted ship physics.
    /// Only forces prediction / simulation step batch size = 1 so Unity Physics never runs with a
    /// merged N×dt (a known chicken-head source). Leaves NetCode's default command-age / RTT
    /// feedback alone — no patchwork latency inflation. World: ClientSimulation.
    /// Group: InitializationSystemGroup (first).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitClientTickRateSystem : ISystem
    {
        /// <summary>
        /// Re-applies every frame so a later handshake cannot restore merged tick batches.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- ClientTickRate: package defaults + no prediction batching ---
            ClientTickRate clientTickRate;
            Entity clientTickEntity;
            using (var query = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientTickRate>()))
            {
                if (query.IsEmpty)
                {
                    // [NETCODE] No ResolveDefaults API — seed from package defaults.
                    clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
                    clientTickEntity = state.EntityManager.CreateEntity(typeof(ClientTickRate));
                }
                else
                {
                    clientTickEntity = query.GetSingletonEntity();
                    clientTickRate = state.EntityManager.GetComponentData<ClientTickRate>(clientTickEntity);
                }
            }

            // [NETCODE] One predicted physics step per tick — required for deterministic hull motion.
            clientTickRate.MaxPredictionStepBatchSizeFirstTimeTick = 1;
            clientTickRate.MaxPredictionStepBatchSizeRepeatedTick = 1;
            state.EntityManager.SetComponentData(clientTickEntity, clientTickRate);

            // --- ClientServerTickRate: same batch rule + enough steps to hold 60 Hz at ~30 FPS ---
            ClientServerTickRate sharedTickRate;
            Entity sharedTickEntity;
            using (var query = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>()))
            {
                if (query.IsEmpty)
                {
                    sharedTickRate = new ClientServerTickRate();
                    sharedTickRate.ResolveDefaults();
                    sharedTickEntity = state.EntityManager.CreateEntity(typeof(ClientServerTickRate));
                }
                else
                {
                    sharedTickEntity = query.GetSingletonEntity();
                    sharedTickRate = state.EntityManager.GetComponentData<ClientServerTickRate>(sharedTickEntity);
                }
            }

            sharedTickRate.SimulationTickRate = TitanOrbitServerTickRateSystem.SimulationHz;
            sharedTickRate.NetworkTickRate = TitanOrbitServerTickRateSystem.NetworkHz;
            // [NETCODE] Discrete catch-up only — never merge ticks into one large physics dt.
            // H13 (steps=16) did not reduce cmdAge/spikes on Local Host; keep 4 for ~15 FPS floor.
            sharedTickRate.MaxSimulationStepsPerFrame = TitanOrbitServerTickRateSystem.MaxStepsPerFrame;
            sharedTickRate.MaxSimulationStepBatchSize = 1;
            state.EntityManager.SetComponentData(sharedTickEntity, sharedTickRate);

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
                        "\"location\":\"TitanOrbitClientTickRateSystem.OnUpdate\"," +
                        "\"message\":\"minimal tick rates (batch=1, steps=4)\"," +
                        "\"data\":{\"predBatch\":1,\"maxBatch\":" + sharedTickRate.MaxSimulationStepBatchSize +
                        ",\"maxSteps\":" + sharedTickRate.MaxSimulationStepsPerFrame +
                        ",\"hasServer\":" + (ClientServerBootstrap.HasServerWorld ? "true" : "false") + "}," +
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
