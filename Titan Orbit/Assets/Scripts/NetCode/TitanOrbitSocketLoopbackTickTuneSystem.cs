using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Socket tick tune for <b>true local loopback only</b> (MPPM Player 2 → 127.0.0.1).
    /// <para>
    /// basics33 / dedicated GCE evidence: this system used <c>EstimatedRTT &lt; 40ms</c> as a
    /// "loopback" gate. Relay joins often report ~35–38 ms for a few frames at connect, so we
    /// forced <c>TargetCommandSlack=0</c> and <c>EstimatedRTT=one tick</c>. Real Relay RTT then
    /// settled ~70 ms while slack stayed 0 → <c>cmdAge≈20–24</c>, <c>maxDelta≈2.6</c>, both
    /// players choppy. Local Host never hit this path (IPC tandem).
    /// </para>
    /// <para>
    /// Fix: never apply loopback overrides when <see cref="TitanOrbitRelayState"/> has a client
    /// Relay allocation. On Relay, restore package <c>TargetCommandSlack</c> if we mangled it.
    /// </para>
    /// World: ClientSimulation. Before <see cref="NetworkTimeSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateBefore(typeof(NetworkTimeSystem))]
    public partial struct TitanOrbitSocketLoopbackTickTuneSystem : ISystem
    {
        /// <summary>Max EstimatedRTT (ms) for non-Relay Socket loopback (MPPM → localhost).</summary>
        public const float LoopbackRttMsMax = 15f;

        /// <summary>Package default command slack (NetCode DefaultClientTickRate).</summary>
        const uint DefaultTargetCommandSlack = 2;

        /// <summary>One-shot path log.</summary>
        bool _loggedPath;

        /// <summary>Requires an in-game connection before tuning.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<ClientTickRate>();
            state.RequireForUpdate<NetworkSnapshotAck>();
        }

        /// <summary>
        /// Relay → restore slack=2, do nothing else.
        /// Non-Relay Socket + very low RTT → IPC-like slack=0 + one-tick RTT (MPPM localhost only).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonRW<ClientTickRate>(out var clientTickRate))
                return;

            // --- Dedicated / Join game via Unity Relay: FIRST (before HasServerWorld) ---
            // [NETCODE] Local Host may have ServerWorld (SessionManager); player clients are ClientWorld-only.
            // basics38: HasServerWorld was true during GCE Relay join, so we early-returned here and
            // never restored Relay slack. Check Relay before any Local Host gate.
            if (TitanOrbitRelayState.HasClientRelay || TitanOrbitSessionManager.IsDedicatedOnlineClient)
            {
                if (clientTickRate.ValueRO.TargetCommandSlack != DefaultTargetCommandSlack)
                    clientTickRate.ValueRW.TargetCommandSlack = DefaultTargetCommandSlack;

                // #region agent log
                if (!_loggedPath)
                {
                    _loggedPath = true;
                    float rtt = 0f;
                    if (SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack))
                        rtt = ack.EstimatedRTT;
                    string data =
                        "{\"path\":\"relay\",\"targetSlack\":" + DefaultTargetCommandSlack +
                        ",\"rttMs\":" + rtt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                        ",\"hasServer\":" + (ClientServerBootstrap.HasServerWorld ? "true" : "false") +
                        ",\"dedicatedOnline\":" + (TitanOrbitSessionManager.IsDedicatedOnlineClient ? "true" : "false") + "}";
                    TitanOrbit.Diagnostics.ShipFlightSmoothDebugLog.Write(
                        "H38",
                        "TitanOrbitSocketLoopbackTickTuneSystem.OnUpdate",
                        "Relay client — restore slack, skip loopback tune",
                        data);
                }
                // #endregion
                return;
            }

            // --- Local Host Client+Server: IPC tandem owns timeline ---
            if (ClientServerBootstrap.HasServerWorld)
                return;

            if (!SystemAPI.TryGetSingleton<NetworkStreamDriver>(out var streamDriver))
                return;
            if (streamDriver.DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId) != TransportType.Socket)
                return;

            // --- MPPM / LAN Socket without Relay (true loopback) ---
            if (!SystemAPI.TryGetSingletonRW<NetworkSnapshotAck>(out var ackRw))
                return;

            float measuredRtt = ackRw.ValueRO.EstimatedRTT;
            // Tight gate: Relay rarely stays under 15 ms; localhost Socket often does.
            if (measuredRtt > LoopbackRttMsMax)
                return;

            float oneTickMs = 1000f / TitanOrbitServerTickRateSystem.SimulationHz;
            if (ackRw.ValueRO.EstimatedRTT > oneTickMs * 1.01f || ackRw.ValueRO.DeviationRTT > 0.01f)
            {
                ackRw.ValueRW.EstimatedRTT = oneTickMs;
                ackRw.ValueRW.DeviationRTT = 0f;
            }

            if (clientTickRate.ValueRO.TargetCommandSlack != 0)
                clientTickRate.ValueRW.TargetCommandSlack = 0;

            // #region agent log
            if (!_loggedPath)
            {
                _loggedPath = true;
                string data =
                    "{\"path\":\"loopback\",\"measuredRttBefore\":" +
                    measuredRtt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"oneTickMs\":" + oneTickMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"targetSlack\":0,\"hasServer\":false}";
                TitanOrbit.Diagnostics.ShipFlightSmoothDebugLog.Write(
                    "H38",
                    "TitanOrbitSocketLoopbackTickTuneSystem.OnUpdate",
                    "non-Relay Socket loopback tune",
                    data);
            }
            // #endregion
        }
    }
}
