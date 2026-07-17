using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Creates / updates the authoritative <see cref="ClientServerTickRate"/> singleton on server boot.
    /// Forces 60 Hz sim + network, never batches ticks into a larger physics dt, and caps catch-up
    /// steps per frame. World: ServerSimulation. Group: InitializationSystemGroup (first).
    /// <para>
    /// basics17 (H29): with MaxSteps=4 at ~30 FPS Editor Local Host, serverTick advanced at ~120 Hz
    /// (ratio sim/wall ≈ 2.0) — ships, moon orbits (ElapsedTime), and all fixed-step sim felt ~2×.
    /// MaxSteps=2 caps catch-up at 60 Hz when the Game view is ~30 FPS. TargetFrameRateMode=Sleep
    /// avoids BusyWait (Editor Auto default) fighting the frame budget.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitServerTickRateSystem : ISystem
    {
        /// <summary>Authoritative simulation rate (server + matched client).</summary>
        public const int SimulationHz = 60;

        /// <summary>Ghost send rate — matched to <see cref="SimulationHz"/>.</summary>
        public const int NetworkHz = 60;

        /// <summary>
        /// Max discrete fixed steps per Unity frame when catching up.
        /// [TITAN-ORBIT] 2 — enough for 60 Hz at ≥30 FPS display; 4 caused ~120 Hz (2× speed) in basics17.
        /// </summary>
        public const int MaxStepsPerFrame = 2;

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
            tickRate.MaxSimulationStepsPerFrame = MaxStepsPerFrame;
            tickRate.MaxSimulationStepBatchSize = 1;
            // [NETCODE] Predicted physics group at 1× SimulationTickRate (not a higher multiple).
            tickRate.PredictedFixedStepSimulationTickRatio = 1;
            // [NETCODE] Editor Client+Server Auto defaults to BusyWait; Sleep respects targetFrameRate.
            tickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.Sleep;

            state.EntityManager.SetComponentData(tickEntity, tickRate);

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
                        "\"location\":\"TitanOrbitServerTickRateSystem.OnUpdate\"," +
                        "\"message\":\"Server ClientServerTickRate forced\"," +
                        "\"data\":{\"simHz\":" + tickRate.SimulationTickRate +
                        ",\"maxBatch\":" + tickRate.MaxSimulationStepBatchSize +
                        ",\"maxSteps\":" + tickRate.MaxSimulationStepsPerFrame +
                        ",\"predRatio\":" + tickRate.PredictedFixedStepSimulationTickRatio +
                        ",\"frameMode\":\"Sleep\"" +
                        "},\"timestamp\":" + ts + "}\n";
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
