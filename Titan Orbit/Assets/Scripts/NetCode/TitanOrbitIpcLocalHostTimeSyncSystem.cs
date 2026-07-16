using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Local Host IPC time-sync: overwrite NetCode's predicted timeline every frame to
    /// <c>lastReceivedSnapshot + 1</c>, matching <see cref="NetworkTimeSystem"/>'s documented IPC intent.
    /// <para>
    /// basics4: <see cref="NetworkTimeSystemData.latestSnapshotEstimate"/> lagged ~20 ticks behind
    /// received snapshots → <c>cmdAge~24</c> and 12–15 tick hard snaps.
    /// basics5: thresholded correction fixed the estimate lag (<c>cmdAge~7</c>, no 12+ batches) but
    /// still fought NetworkTimeSystem with 3–4 tick jumps (<c>simBatch</c> stuck at 5–6).
    /// This revision always applies the IPC tandem timeline after NetworkTimeSystem so the rate
    /// manager never sees the drifted target.
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup, after NetworkTimeSystem.
    /// Remote Socket clients: no-op.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(NetworkTimeSystem))]
    public partial struct TitanOrbitIpcLocalHostTimeSyncSystem : ISystem
    {
        /// <summary>Throttle NDJSON logs to once per second.</summary>
        double _nextLogTime;

        /// <summary>Requires an in-game connection before correcting the timeline.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<NetworkTimeSystemData>();
            state.RequireForUpdate<NetworkSnapshotAck>();
        }

        /// <summary>
        /// Every Local Host IPC frame: force estimate + predict target to snapshot+1, and keep
        /// <see cref="ClientTickRate.TargetCommandSlack"/> at 0.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Local Host IPC only ---
            // [NETCODE] Remote Socket clients must keep RTT / command-age feedback.
            if (!ClientServerBootstrap.HasServerWorld)
                return;

            if (!SystemAPI.TryGetSingleton<NetworkStreamDriver>(out var streamDriver))
                return;
            if (streamDriver.DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId) != TransportType.IPC)
                return;

            if (!SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack) ||
                !ack.LastReceivedSnapshotByLocal.IsValid)
                return;

            // --- Persist IPC ClientTickRate (NetCode only overrides a stack copy) ---
            if (SystemAPI.TryGetSingletonRW<ClientTickRate>(out var clientTickRate))
            {
                if (clientTickRate.ValueRO.TargetCommandSlack != 0)
                    clientTickRate.ValueRW.TargetCommandSlack = 0;
            }

            ref var netTimeData = ref SystemAPI.GetSingletonRW<NetworkTimeSystemData>().ValueRW;

            NetworkTick snap = ack.LastReceivedSnapshotByLocal;

            // Diagnostics: how wrong NetworkTimeSystem was before we overwrite.
            int estimateBehindSnap = 0;
            if (netTimeData.latestSnapshotEstimate.IsValid)
                estimateBehindSnap = snap.TicksSince(netTimeData.latestSnapshotEstimate);

            // [NETCODE] IPC ideal: predictTargetTick = latestSnapshot + 1 (zero RTT tandem).
            NetworkTick idealPredict = snap;
            idealPredict.Add(1);

            int predictDeltaFromIdeal = 0;
            if (netTimeData.predictTargetTick.IsValid)
                predictDeltaFromIdeal = idealPredict.TicksSince(netTimeData.predictTargetTick);

            // --- Always overwrite (basics5: thresholded snaps fought NetworkTimeSystem) ---
            // NetcodeClientRateManager reads predictTargetTick after InitializationSystemGroup.
            // Advancing only as fast as LastReceivedSnapshotByLocal avoids artificial 3–4 tick jumps.
            netTimeData.latestSnapshot = snap;
            netTimeData.latestSnapshotEstimate = snap;
            netTimeData.latestSnapshotAge = 0;
            netTimeData.predictTargetTick = idealPredict;
            netTimeData.subPredictTargetTick = 0f;

            // #region agent log
            double now = SystemAPI.Time.ElapsedTime;
            if (now >= _nextLogTime)
            {
                _nextLogTime = now + 1.0;
                try
                {
                    string path = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "..", "debug-6b87b4.log"));
                    long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string line =
                        "{\"sessionId\":\"6b87b4\",\"runId\":\"basics6\",\"hypothesisId\":\"H14\"," +
                        "\"location\":\"TitanOrbitIpcLocalHostTimeSyncSystem.OnUpdate\"," +
                        "\"message\":\"IPC tandem overwrite\"," +
                        "\"data\":{\"snap\":" + snap.TickIndexForValidTick +
                        ",\"estimateBehindBefore\":" + estimateBehindSnap +
                        ",\"predictDeltaBefore\":" + predictDeltaFromIdeal +
                        ",\"idealPredict\":" + idealPredict.TickIndexForValidTick +
                        "},\"timestamp\":" + ts + "}\n";
                    System.IO.File.AppendAllText(path, line);
                }
                catch { /* debug I/O only */ }
            }
            // #endregion
        }
    }
}
