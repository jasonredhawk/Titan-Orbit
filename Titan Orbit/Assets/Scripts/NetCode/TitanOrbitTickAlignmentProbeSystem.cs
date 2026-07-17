using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Debug probe: logs client/server tick alignment and transport type once per second.
    /// Used to verify Local Host uses IPC and that command age tracks InputTargetTick vs server tick.
    /// Safe with 2+ server connections (MPPM Player 2) — does not call GetSingleton on acks.
    /// World: ClientSimulation. Temporary — session 6b87b4.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct TitanOrbitTickAlignmentProbeSystem : ISystem
    {
        /// <summary>Next realtimeSinceStartup deadline for a probe line (wall clock, not ElapsedTime).</summary>
        double _nextLogTime;

        /// <summary>Requires an in-game connection before probing.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>Appends one NDJSON line per wall-clock second with tick / RTT / transport fields.</summary>
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Throttle on wall clock — ElapsedTime runs fast when sim is hot (basics17),
            // which made probe intervals ~0.5s and confused H29 ratio math.
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
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

                // [NETCODE] Ack lives on each connection entity — with MPPM Player 2 there are 2+.
                // GetSingleton throws InvalidOperationException when count != 1 (console spam on join).
                using var ackQ = serverWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkSnapshotAck>(),
                    ComponentType.ReadOnly<NetworkStreamConnection>());
                int connectionCount = ackQ.CalculateEntityCount();
                if (connectionCount == 1)
                {
                    var sack = ackQ.GetSingleton<NetworkSnapshotAck>();
                    if (sack.LastReceivedSnapshotByRemote.IsValid)
                        lastSnapByRemote = sack.LastReceivedSnapshotByRemote.TickIndexForValidTick;
                }
                else if (connectionCount > 1)
                {
                    using var acks = ackQ.ToComponentDataArray<NetworkSnapshotAck>(Unity.Collections.Allocator.Temp);
                    for (int i = 0; i < acks.Length; i++)
                    {
                        if (!acks[i].LastReceivedSnapshotByRemote.IsValid)
                            continue;
                        lastSnapByRemote = acks[i].LastReceivedSnapshotByRemote.TickIndexForValidTick;
                        break;
                    }
                }
            }

            // #region agent log
            // Shared FileShare writer (basics30) — one line/sec/client, no exclusive AppendAllText.
            string data =
                "{\"transport\":\"" + transport +
                "\",\"hasServer\":" + (ClientServerBootstrap.HasServerWorld ? "true" : "false") +
                ",\"clientTick\":" + clientTick +
                ",\"inputTick\":" + inputTick +
                ",\"serverTick\":" + serverTick +
                ",\"lastSnapByRemote\":" + lastSnapByRemote +
                ",\"lastSnapLocal\":" + lastSnapLocal +
                ",\"latestSnap\":" + latestSnap +
                ",\"latestSnapEst\":" + latestSnapEst +
                ",\"predictTarget\":" + predictTarget +
                ",\"predictLead\":" + (predictTarget > 0 && lastSnapLocal > 0
                    ? ((int)predictTarget - (int)lastSnapLocal).ToString()
                    : "na") +
                ",\"snapAge\":" + snapAge.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"targetSlack\":" + targetSlack +
                ",\"cmdAge\":" + cmdAge.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"rttMs\":" + rtt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"simBatch\":" + simBatch +
                ",\"tickFrac\":" + tickFrac.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"netSim\":" + (netSim ? "true" : "false") +
                ",\"clientBehindServer\":" + (serverTick > 0 && clientTick > 0 ? ((int)serverTick - (int)clientTick).ToString() : "na") +
                ",\"clientBehindSnap\":" + (lastSnapLocal > 0 && clientTick > 0 ? ((int)lastSnapLocal - (int)clientTick).ToString() : "na") +
                ",\"fps\":" + (UnityEngine.Time.unscaledDeltaTime > 1e-6f
                    ? (1f / UnityEngine.Time.unscaledDeltaTime).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                    : "0") +
                ",\"targetFrameRate\":" + UnityEngine.Application.targetFrameRate +
                ",\"vSync\":" + UnityEngine.QualitySettings.vSyncCount + "}";
            TitanOrbit.Diagnostics.ShipFlightSmoothDebugLog.Write(
                "H30", "TitanOrbitTickAlignmentProbeSystem.OnUpdate", "tick alignment probe", data);
            // #endregion
        }
    }
}
