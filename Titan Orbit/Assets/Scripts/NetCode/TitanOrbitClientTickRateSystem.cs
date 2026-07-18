using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Client tick tuning for predicted ship physics.
    /// Forces prediction step batch size = 1 (no merged N×dt physics) and uses a higher
    /// <see cref="ClientServerTickRate.MaxSimulationStepsPerFrame"/> than Editor Local Host server
    /// so Relay clients can repay command-age debt.
    /// <para>
    /// basics34 (dedicated GCE): MaxSteps must stay high (8) for join catch-up.
    /// basics51 / H59: cruise MaxSteps=2 did <b>not</b> reduce
    /// <c>NetworkTime.SimulationStepBatchSize</c> — on clients that field is predict-target
    /// delta (2–3 at ~30 FPS), not MaxSteps. Reverted. Presentation: raw+soft-track; H64 = player FPS test.
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup (first).
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

            // --- ClientServerTickRate: match Hz, allow Relay catch-up ---
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
            // [TITAN-ORBIT] Always allow join catch-up (basics34). Do not cruise-cap MaxSteps (H59 rejected).
            sharedTickRate.MaxSimulationStepsPerFrame = TitanOrbitServerTickRateSystem.ClientMaxStepsPerFrame;
            sharedTickRate.MaxSimulationStepBatchSize = 1;
            sharedTickRate.PredictedFixedStepSimulationTickRatio = 1;
            sharedTickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.Sleep;
            state.EntityManager.SetComponentData(sharedTickEntity, sharedTickRate);
        }
    }
}
