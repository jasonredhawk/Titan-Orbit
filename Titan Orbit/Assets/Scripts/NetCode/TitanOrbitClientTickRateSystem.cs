using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Client tick tuning for predicted ship physics (OwnerPredicted hull after GhostReceive).
    /// Forces prediction step batch size = 1 (no merged N×dt physics) and uses a higher
    /// <see cref="ClientServerTickRate.MaxSimulationStepsPerFrame"/> than Editor Local Host server
    /// so Relay clients can repay command-age debt.
    /// <para>
    /// <see cref="ExperimentalForcedInputLatencyTicks"/> is 0 (package default). The
    /// 1-tick experiment fought Relay <c>TargetCommandSlack=2</c> and added dedicated
    /// reconcile snaps. Do not also register <c>GhostPredictionSmoothing</c> — that
    /// blends poses and fights authority/display coast.
    /// </para>
    /// <para>
    /// basics34 (dedicated GCE): MaxSteps must stay high (8) for join catch-up.
    /// basics51 / H59: cruise MaxSteps=2 did <b>not</b> reduce
    /// <c>NetworkTime.SimulationStepBatchSize</c> — on clients that field is predict-target
    /// delta (2–3 at ~30 FPS), not MaxSteps. Reverted. Presentation: raw+soft-track; H64 = player FPS test.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Join Team does <b>not</b> Instantiates a client predicted hull. The ship
    /// arrives via GhostReceive; prediction starts when that ghost has PredictedGhost. Keep
    /// <c>PredictionLoopUpdateMode.RequirePredictedGhost</c> (do not AlwaysRun the session).
    /// Extra predicted-ghost lifetime still helps bullets / other predicted spawns.
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup (first).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct TitanOrbitClientTickRateSystem : ISystem
    {
        /// <summary>
        /// Extra simulation ticks a client predicted ghost stays alive while waiting for the
        /// matching server snapshot. Package default is 0 (despawn as soon as interpolation
        /// passes the spawn tick — often ~2 ticks / ~30ms). Used for bullets / other predicted
        /// spawns — not a Join Team ship Instantiates.
        /// </summary>
        public const ushort PredictedGhostExtraLifetimeTicks = 120;

        /// <summary>
        /// ± tick window for NetCode's default predicted-spawn classifier (bullets / other
        /// predicted ghosts). Package default is 5.
        /// </summary>
        public const ushort PredictedSpawnClassificationTickPeriod = 64;

        /// <summary>
        /// Forced input delay in 60 Hz ticks. 0 = off (package default). A value of 1 was
        /// an experiment that added dedicated reconcile snaps under Relay slack=2.
        /// </summary>
        public const byte ExperimentalForcedInputLatencyTicks = 0;

        /// <summary>
        /// Re-applies every frame so a later handshake cannot restore merged tick batches
        /// or the package's too-short predicted-spawn lifetime.
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
            // [NETCODE] PredictedGhostDespawnSystem destroys unmatched predicted spawns once
            // InterpolationTick passes spawnTick + this extra lifetime (bullets, not Join Team ships).
            clientTickRate.NumAdditionalClientPredictedGhostLifetimeTicks = PredictedGhostExtraLifetimeTicks;
            // [NETCODE] DefaultGhostSpawnClassificationSystem matches by ghost type + spawn tick.
            clientTickRate.DefaultClassificationAllowableTickPeriod = PredictedSpawnClassificationTickPeriod;
            // [NETCODE] RequirePredictedGhost — prediction runs after GhostReceive delivers the
            // owner ship. Do not AlwaysRun; Join Team no longer Instantiates a fake hull.
            clientTickRate.PredictionLoopUpdateMode = PredictionLoopUpdateMode.RequirePredictedGhost;
            // [NETCODE] Official input-delay knob — not a second pose smoother.
            clientTickRate.ForcedInputLatencyTicks = ExperimentalForcedInputLatencyTicks;
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
            // [NETCODE] TargetFrameRateMode is a server pacing knob. Auto matches package defaults
            // (BusyWait in Editor/client builds). Do not force Sleep — that fights SessionManager /
            // CrossPlatformManager targetFrameRate and triggers NetcodeServerRateManager spam.
            sharedTickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.Auto;
            state.EntityManager.SetComponentData(sharedTickEntity, sharedTickRate);
        }
    }
}
