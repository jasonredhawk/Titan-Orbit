using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Local Host IPC tandem time-sync (proven by basics4–8).
    /// <para>
    /// Glues the snapshot estimate to <c>LastReceivedSnapshotByLocal</c> and forces
    /// <c>predictTargetTick = lastSnapshot + 1</c> after <see cref="NetworkTimeSystem"/>.
    /// basics8 (estimate-only) let <c>cmdAge</c> climb back to ~16 and restored 12–16 tick dumps.
    /// basics6 (this overwrite) kept <c>bigBatch&gt;=10</c> at zero and <c>cmdAge~6.5</c>.
    /// Forces <c>subPredictTargetTick = 1</c> on IPC (basics11); basics13 natural partials
    /// raised spikes to 13.5% and simBatch~5 via the partial +1 pad — rejected.
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup, after NetworkTimeSystem.
    /// Remote Socket / Relay clients: no-op (also blocked when <see cref="TitanOrbitSessionManager.IsDedicatedOnlineClient"/>).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(NetworkTimeSystem))]
    public partial struct TitanOrbitIpcLocalHostTimeSyncSystem : ISystem
    {
        /// <summary>Requires an in-game connection before correcting the timeline.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<NetworkTimeSystemData>();
            state.RequireForUpdate<NetworkSnapshotAck>();
        }

        /// <summary>
        /// IPC Local Host: estimate = snap, predictTarget = snap+1, TargetCommandSlack = 0,
        /// EstimatedRTT = one simulation tick, and <c>subPredictTargetTick = 1</c> (full ticks).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Local Host IPC only (never while joining/playing dedicated Relay) ---
            if (!ClientServerBootstrap.HasServerWorld)
                return;
            if (TitanOrbitSessionManager.IsDedicatedOnlineClient || TitanOrbitRelayState.HasClientRelay)
                return;

            if (!SystemAPI.TryGetSingleton<NetworkStreamDriver>(out var streamDriver))
                return;
            if (streamDriver.DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId) != TransportType.IPC)
                return;

            if (!SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack) ||
                !ack.LastReceivedSnapshotByLocal.IsValid)
                return;

            // --- Persist IPC ClientTickRate / RTT ---
            if (SystemAPI.TryGetSingletonRW<ClientTickRate>(out var clientTickRate))
            {
                if (clientTickRate.ValueRO.TargetCommandSlack != 0)
                    clientTickRate.ValueRW.TargetCommandSlack = 0;
            }

            float oneTickMs = 1000f / TitanOrbitServerTickRateSystem.SimulationHz;
            if (SystemAPI.TryGetSingletonRW<NetworkSnapshotAck>(out var ackRw))
            {
                if (ackRw.ValueRO.EstimatedRTT > oneTickMs * 1.5f || ackRw.ValueRO.DeviationRTT > 0.01f)
                {
                    ackRw.ValueRW.EstimatedRTT = oneTickMs;
                    ackRw.ValueRW.DeviationRTT = 0f;
                }
            }

            ref var netTimeData = ref SystemAPI.GetSingletonRW<NetworkTimeSystemData>().ValueRW;
            NetworkTick snap = ack.LastReceivedSnapshotByLocal;

            int estimateBehindSnap = 0;
            if (netTimeData.latestSnapshotEstimate.IsValid)
                estimateBehindSnap = snap.TicksSince(netTimeData.latestSnapshotEstimate);

            // [NETCODE] Documented IPC tandem: predictTarget = latestSnapshot + 1.
            // Do not use ServerWorld tick lead — basics7 showed that increased pull size / spikes.
            NetworkTick idealPredict = snap;
            idealPredict.Add(1);

            int predictDeltaFromIdeal = 0;
            if (netTimeData.predictTargetTick.IsValid)
                predictDeltaFromIdeal = idealPredict.TicksSince(netTimeData.predictTargetTick);

            // --- Tandem overwrite (basics6 — only approach that kept bigBatch at 0) ---
            netTimeData.latestSnapshot = snap;
            netTimeData.latestSnapshotEstimate = snap;
            netTimeData.latestSnapshotAge = 0;
            netTimeData.predictTargetTick = idealPredict;
            // [NETCODE] NetcodeClientRateManager does ++SimulationStepBatchSize when the previous
            // frame's ServerTickFraction < 1. basics13 (package partials) confirmed: simBatch~5,
            // spikes 13.5%. Force a full tick fraction on Local Host IPC (basics11 best).
            netTimeData.subPredictTargetTick = 1f;

            // estimateBehindSnap / predictDeltaFromIdeal kept for easy probe re-enable.
            _ = estimateBehindSnap;
            _ = predictDeltaFromIdeal;
        }
    }
}
