using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Creates / updates the authoritative <see cref="ClientServerTickRate"/> singleton on server boot.
    /// Forces 60 Hz sim + network, never batches ticks into a larger physics dt, and caps catch-up
    /// steps per frame. World: ServerSimulation. Group: InitializationSystemGroup (first).
    /// <para>
    /// Editor Local Host stays at MaxSteps=2 (dual-world). Dedicated MaxSteps=4 is a hitch
    /// cap only — wall-clock 60 Hz comes from a 16 ms Unity frame (vSync=0, targetFrameRate=60).
    /// GCE 2026-08-30: MaxSteps=60 + maxDt=1 ran 60 physics ticks per 3.5 s frame (wall ~6 Hz).
    /// </para>
    /// <para>
    /// Editor / LAN host: <see cref="ClientServerTickRate.FrameRateMode.Auto"/> (BusyWait).
    /// Dedicated: BusyWait plus Unity <c>targetFrameRate=60</c>. NCE Sleep on NullGfxDevice
    /// was the old 4 Hz present-wait; do not use Sleep to “fix” hitch catch-up.
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
        /// Headless catch-up cap. GCE 2026-08-30: MaxSteps=60 + maxDt=1 ran 60 physics ticks
        /// per ~3.5 s Unity frame (94% catch-up, wall sim ~6 Hz, client snap-back). Keep 4 so a
        /// hitch cannot spiral. Pace the player loop to 60 FPS instead (vSync=0, targetFrameRate=60).
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
        /// After join settle: cap predicted catch-up. Debug e2d7d2 ship-ram logs showed
        /// Player 2 hitting this 8-step budget (predBatch=8, cBounceTicks=17–22, dtMs=70–100)
        /// while bounce/ram/viz stayed cheap — contact divergence spiraled into 8 physics worlds
        /// per render frame. 3 keeps 60 Hz sim at 20 FPS without the 8-step hitch.
        /// </summary>
        public const int ClientCruiseMaxStepsPerFrame = 3;

        /// <summary>
        /// Server MaxSteps for this process: Editor Local Host → 2; dedicated → 4.
        /// </summary>
        public static int MaxStepsPerFrame => ResolveServerMaxSteps();

        static bool s_LoggedPace;

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
#if UNITY_SERVER && !UNITY_EDITOR
            // BusyWait: NCE must not Sleep. Unity targetFrameRate stays -1 (no WaitForTargetFPS).
            tickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.BusyWait;
            TitanOrbitSessionManager.ApplyDedicatedServerFramePace();
#else
            // [NETCODE] Auto → BusyWait in Editor / client+server. Do not force Sleep here —
            // that flooded the console whenever Editor dual-world frames slipped past 1/60s.
            tickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.Auto;
#endif

            state.EntityManager.SetComponentData(tickEntity, tickRate);

            // #region agent log
            if (!s_LoggedPace)
            {
                s_LoggedPace = true;
                TitanOrbit.Diagnostics.DedicatedServerFileLog.Append(
                    "pace",
                    "tickMode=" + tickRate.TargetFrameRateMode +
                    " simHz=" + tickRate.SimulationTickRate +
                    " netHz=" + tickRate.NetworkTickRate +
                    " maxSteps=" + tickRate.MaxSimulationStepsPerFrame +
                    " targetFps=" + UnityEngine.Application.targetFrameRate +
                    " vSync=" + UnityEngine.QualitySettings.vSyncCount +
                    " maxDt=" + UnityEngine.Time.maximumDeltaTime.ToString("F2"));
            }
            // #endregion
        }
    }
}
