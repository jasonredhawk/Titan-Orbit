using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Debug probe: logs client/server tick alignment and transport type once per second.
    /// Used to verify Local Host uses IPC and that command age tracks InputTargetTick vs server tick.
    /// World: ClientSimulation. Temporary — session 6b87b4.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitTickAlignmentProbeSystem : ISystem
    {
        double _nextLogTime;

        /// <summary>Requires an in-game connection before probing.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>Appends one NDJSON line per second with tick / RTT / transport fields.</summary>
        public void OnUpdate(ref SystemState state)
        {
            double now = SystemAPI.Time.ElapsedTime;
            if (now < _nextLogTime)
                return;
            _nextLogTime = now + 1.0;

            uint clientTick = 0;
            uint inputTick = 0;
            int simBatch = 0;
            float tickFrac = 1f;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var nt))
            {
                clientTick = nt.ServerTick.IsValid ? nt.ServerTick.TickIndexForValidTick : 0u;
                inputTick = nt.InputTargetTick.IsValid ? nt.InputTargetTick.TickIndexForValidTick : 0u;
                simBatch = nt.SimulationStepBatchSize;
                tickFrac = nt.ServerTickFraction;
            }

            float cmdAge = 0f;
            float rtt = 0f;
            uint lastSnapLocal = 0;
            if (SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack))
            {
                cmdAge = ack.ServerCommandAge / 256f;
                rtt = ack.EstimatedRTT;
                // --- Snapshot / predict-target lag (H12) ---
                // basics3: IPC + current lastSnapByRemote, yet clientTick ~17 behind with cmdAge~24.
                if (ack.LastReceivedSnapshotByLocal.IsValid)
                    lastSnapLocal = ack.LastReceivedSnapshotByLocal.TickIndexForValidTick;
            }

            // [NETCODE] ClientUseSocketDriver forces Socket when Network Emulator is on — that
            // defeats Local Host IPC and leaves UDP-like command age / prediction catch-up.
            bool netSim = false;
#if UNITY_EDITOR || NETCODE_DEBUG
            netSim = NetworkSimulatorSettings.Enabled;
#endif

            string transport = "unknown";
            if (SystemAPI.TryGetSingleton<NetworkStreamDriver>(out var clientDriver))
            {
                var type = clientDriver.DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId);
                transport = type == TransportType.IPC ? "IPC" : (type == TransportType.Socket ? "Socket" : type.ToString());
            }

            uint predictTarget = 0;
            uint latestSnapEst = 0;
            uint latestSnap = 0;
            float snapAge = 0f;
            if (SystemAPI.TryGetSingleton<NetworkTimeSystemData>(out var ntsd))
            {
                if (ntsd.predictTargetTick.IsValid)
                    predictTarget = ntsd.predictTargetTick.TickIndexForValidTick;
                if (ntsd.latestSnapshotEstimate.IsValid)
                    latestSnapEst = ntsd.latestSnapshotEstimate.TickIndexForValidTick;
                if (ntsd.latestSnapshot.IsValid)
                    latestSnap = ntsd.latestSnapshot.TickIndexForValidTick;
                snapAge = ntsd.latestSnapshotAge / 256f;
            }

            uint targetSlack = 0;
            if (SystemAPI.TryGetSingleton<ClientTickRate>(out var ctr))
                targetSlack = ctr.TargetCommandSlack;

            // Server-world tick only (MostRecentFullCommandTick is internal to NetCode).
            uint serverTick = 0;
            uint lastSnapByRemote = 0;
            var serverWorld = ClientServerBootstrap.ServerWorld;
            if (serverWorld != null && serverWorld.IsCreated)
            {
                using var timeQ = serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkTime));
                if (timeQ.TryGetSingleton(out NetworkTime snt) && snt.ServerTick.IsValid)
                    serverTick = snt.ServerTick.TickIndexForValidTick;

                // [NETCODE] Public ack fields on the server connection entity (not a world singleton).
                using var ackQ = serverWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkSnapshotAck>(),
                    ComponentType.ReadOnly<NetworkStreamConnection>());
                if (ackQ.CalculateEntityCount() > 0)
                {
                    var sack = ackQ.GetSingleton<NetworkSnapshotAck>();
                    if (sack.LastReceivedSnapshotByRemote.IsValid)
                        lastSnapByRemote = sack.LastReceivedSnapshotByRemote.TickIndexForValidTick;
                }
            }

            // #region agent log
            try
            {
                string path = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "..", "debug-6b87b4.log"));
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line =
                    "{\"sessionId\":\"6b87b4\",\"runId\":\"basics6\",\"hypothesisId\":\"H12\"," +
                    "\"location\":\"TitanOrbitTickAlignmentProbeSystem.OnUpdate\"," +
                    "\"message\":\"tick alignment probe\"," +
                    "\"data\":{\"transport\":\"" + transport +
                    "\",\"clientTick\":" + clientTick +
                    ",\"inputTick\":" + inputTick +
                    ",\"serverTick\":" + serverTick +
                    ",\"lastSnapByRemote\":" + lastSnapByRemote +
                    ",\"lastSnapLocal\":" + lastSnapLocal +
                    ",\"latestSnap\":" + latestSnap +
                    ",\"latestSnapEst\":" + latestSnapEst +
                    ",\"predictTarget\":" + predictTarget +
                    ",\"snapAge\":" + snapAge.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"targetSlack\":" + targetSlack +
                    ",\"cmdAge\":" + cmdAge.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"rttMs\":" + rtt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"simBatch\":" + simBatch +
                    ",\"tickFrac\":" + tickFrac.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"netSim\":" + (netSim ? "true" : "false") +
                    ",\"clientBehindServer\":" + (serverTick > 0 && clientTick > 0 ? ((int)serverTick - (int)clientTick).ToString() : "na") +
                    ",\"clientBehindSnap\":" + (lastSnapLocal > 0 && clientTick > 0 ? ((int)lastSnapLocal - (int)clientTick).ToString() : "na") +
                    "},\"timestamp\":" + ts + "}\n";
                System.IO.File.AppendAllText(path, line);
            }
            catch { /* debug I/O only */ }
            // #endregion
        }
    }
}
