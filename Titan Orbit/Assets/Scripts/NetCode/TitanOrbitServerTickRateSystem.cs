using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Creates / updates the authoritative <see cref="ClientServerTickRate"/> singleton on server boot.
    /// Forces 60 Hz sim + network, never batches ticks into a larger physics dt, and caps catch-up
    /// steps per frame. World: ServerSimulation. Group: InitializationSystemGroup (first).
    /// <para>
    /// basics17: MaxSteps=4 in Editor Local Host (Client+Server) caused ~2× sim speed when the
    /// ServerWorld was also double-ticked. Editor dual-world therefore stays at MaxSteps=2.
    /// basics34 dedicated GCE: clients were stuck at <c>cmdAge≈18–21</c> with <c>simBatchMax≈13</c>
    /// — headless needs MaxSteps≥4 so the server can hold 60 Hz under hitch without starving.
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
        /// Editor Local Host (ClientWorld present): max discrete steps per frame.
        /// Keeps dual-world Editor from running sim at ~2× (basics17).
        /// </summary>
        public const int EditorLocalHostMaxStepsPerFrame = 2;

        /// <summary>
        /// Headless / dedicated server catch-up budget — enough for 60 Hz after frame hitches.
        /// </summary>
        public const int DedicatedMaxStepsPerFrame = 4;

        /// <summary>
        /// Client prediction catch-up budget (Relay). basics34: MaxSteps=2 left cmdAge≈20 and
        /// simBatchMax≈13 on GCE joins — client could not climb out of the hard-snap path.
        /// basics51 H59 cruise MaxSteps=2 rejected — client SimulationStepBatchSize is predict
        /// delta, not MaxSteps. Presentation tricks (H60–H63) rejected at Editor ~30 FPS.
        /// </summary>
        public const int ClientMaxStepsPerFrame = 8;

        /// <summary>
        /// Server MaxSteps for this process: Editor Local Host → 2; otherwise dedicated → 4.
        /// </summary>
        public static int MaxStepsPerFrame => ResolveServerMaxSteps();

        /// <summary>Picks server catch-up cap from world layout (not a single global for clients).</summary>
        public static int ResolveServerMaxSteps()
        {
#if UNITY_EDITOR
            // [UNITY] Editor Play Mode with Client+Server: dual worlds share one frame budget.
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                return EditorLocalHostMaxStepsPerFrame;
#endif
            return DedicatedMaxStepsPerFrame;
        }

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

            int maxSteps = ResolveServerMaxSteps();
            tickRate.SimulationTickRate = SimulationHz;
            tickRate.NetworkTickRate = NetworkHz;
            // [NETCODE] Catch up after hitches with discrete steps — never merge into one large dt.
            tickRate.MaxSimulationStepsPerFrame = maxSteps;
            tickRate.MaxSimulationStepBatchSize = 1;
            tickRate.PredictedFixedStepSimulationTickRatio = 1;
            tickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.Sleep;

            state.EntityManager.SetComponentData(tickEntity, tickRate);
        }
    }
}
