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
    /// basics17: MaxSteps=4 in Editor Local Host (Client+Server) caused ~2× sim speed when the
    /// ServerWorld was also double-ticked. Editor dual-world therefore stays at MaxSteps=2.
    /// basics34 dedicated GCE: clients were stuck at <c>cmdAge≈18–21</c> with <c>simBatchMax≈13</c>
    /// — headless needs MaxSteps≥4 so the server can hold 60 Hz under hitch without starving.
    /// </para>
    /// <para>
    /// Editor Local Host stays <see cref="ClientServerTickRate.FrameRateMode.Auto"/> (BusyWait).
    /// Dedicated Docker / NullGfx is different from GCE Sleep on <c>main</c>: dummy present can
    /// be ~2 Hz, TimeManager clamps <c>deltaTime</c> to 0.1 s, and Sleep+MaxSteps=4 then runs
    /// ~12 wall-clock ticks/s (ships + moons crawl). Dedicated uses BusyWait, asks for 60
    /// Unity frames (never <c>targetFrameRate = -1</c>), turns VSync off, raises
    /// <see cref="Time.maximumDeltaTime"/>, and allows enough discrete catch-up steps.
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
        /// Headless catch-up budget. Docker + NullGfx can present at ~2 Hz; Unity then
        /// clamps <c>Time.deltaTime</c> to 100 ms unless we raise maximumDeltaTime.
        /// 64 discrete 16.67 ms steps cover a 1 s wall frame without merging dt.
        /// </summary>
        public const int DedicatedMaxStepsPerFrame = 64;

        /// <summary>
        /// Client prediction catch-up budget (Relay). basics34: MaxSteps=2 left cmdAge≈20 and
        /// simBatchMax≈13 on GCE joins — client could not climb out of the hard-snap path.
        /// basics51 H59 cruise MaxSteps=2 rejected — client SimulationStepBatchSize is predict
        /// delta, not MaxSteps. Presentation tricks (H60–H63) rejected at Editor ~30 FPS.
        /// </summary>
        public const int ClientMaxStepsPerFrame = 8;

        /// <summary>
        /// Server MaxSteps for this process: Editor Local Host → 2; otherwise dedicated → 64.
        /// </summary>
        public static int MaxStepsPerFrame => ResolveServerMaxSteps();

        static bool s_LoggedDedicatedPacing;

        /// <summary>
        /// Headless dedicated pacing. Dummy NullGfx in Docker treats <c>targetFrameRate = -1</c>
        /// as the platform present rate (~2 Hz). Ask Unity for 60 Unity frames, disable VSync,
        /// and raise <see cref="Time.maximumDeltaTime"/> so a slow present still feeds NetCode
        /// a full wall interval (see DedicatedMaxStepsPerFrame).
        /// </summary>
        public static void ApplyDedicatedHeadlessFramePacing()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            if (QualitySettings.vSyncCount != 0)
                QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != SimulationHz)
                Application.targetFrameRate = SimulationHz;
            // [UNITY] Project TimeManager Maximum Allowed Timestep is 0.1s. At 2 Unity FPS
            // that discards ~400 ms of wall time per frame (wallSim≈12 Hz). Dedicated only.
            if (Time.maximumDeltaTime < 1f)
                Time.maximumDeltaTime = 1f;

            if (!s_LoggedDedicatedPacing)
            {
                s_LoggedDedicatedPacing = true;
                Debug.Log(
                    "[TitanOrbitServerTick] dedicated pacing vSync=0 targetFrameRate=" +
                    SimulationHz + " maxDelta=1 MaxSteps=" + DedicatedMaxStepsPerFrame +
                    " mode=BusyWait (Docker NullGfx — not main GCE Sleep)");
            }
#endif
        }

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
            // [NETCODE] Auto → Sleep on UNITY_SERVER. Docker dummy present + 0.1s clamp +
            // MaxSteps=4 (main) yields ~12 Hz wall-clock play. BusyWait + raised maxDelta
            // lets each slow Unity frame run the missed 60 Hz ticks.
            tickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.BusyWait;
            ApplyDedicatedHeadlessFramePacing();
#else
            // [NETCODE] Auto → BusyWait in Editor / host. Do not force Sleep (warnings + hitch).
            tickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.Auto;
#endif

            state.EntityManager.SetComponentData(tickEntity, tickRate);
        }
    }
}
