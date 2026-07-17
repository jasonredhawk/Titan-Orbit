using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Minimal client tick tuning for predicted ship physics.
    /// Forces prediction step batch size = 1 (no merged N×dt physics) and matches server
    /// 60 Hz / catch-up caps. World: ClientSimulation. Group: InitializationSystemGroup (first).
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

            // --- ClientServerTickRate: match server Hz + no tick batching ---
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
            // [TITAN-ORBIT] Match server — MaxSteps=2 (basics17: 4 → ~120 Hz / 2× speed at ~30 FPS).
            sharedTickRate.MaxSimulationStepsPerFrame = TitanOrbitServerTickRateSystem.MaxStepsPerFrame;
            sharedTickRate.MaxSimulationStepBatchSize = 1;
            sharedTickRate.PredictedFixedStepSimulationTickRatio = 1;
            sharedTickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.Sleep;
            state.EntityManager.SetComponentData(sharedTickEntity, sharedTickRate);

            // #region agent log
            if (!_loggedOnce)
            {
                _loggedOnce = true;
                try
                {
                    string path = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(Application.dataPath, "..", "..", "debug-6b87b4.log"));
                    long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string line =
                        "{\"sessionId\":\"6b87b4\",\"runId\":\"basics18\",\"hypothesisId\":\"H29\"," +
                        "\"location\":\"TitanOrbitClientTickRateSystem.OnUpdate\"," +
                        "\"message\":\"client tick rates\"," +
                        "\"data\":{\"predBatch\":1,\"maxBatch\":" + sharedTickRate.MaxSimulationStepBatchSize +
                        ",\"maxSteps\":" + sharedTickRate.MaxSimulationStepsPerFrame +
                        ",\"simHz\":" + sharedTickRate.SimulationTickRate +
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
