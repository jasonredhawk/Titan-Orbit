using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// MPPM / Socket loopback tick tune for client-only players (no in-process ServerWorld).
    /// <para>
    /// basics29 evidence (Player 2): <c>transport=Socket</c>, <c>targetSlack=2</c> (package default),
    /// raw <c>cmdAge≈-2</c>, <c>predictLead≈3–4</c> — chronically over-predicting vs Local Host IPC
    /// (slack 0, cmdAge≈1). That over-predict + hard reconcile reads as blurry jitter on the local ship.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Sets <see cref="ClientTickRate.TargetCommandSlack"/> = 0 only. Does <b>not</b>
    /// overwrite <c>predictTargetTick</c> (full Socket tandem froze P2 snapshots in an earlier run).
    /// Does not change Relay / high-RTT clients (RTT gate).
    /// </para>
    /// World: ClientSimulation. Group: InitializationSystemGroup, after NetworkTimeSystem.
    /// Paired with <see cref="TitanOrbitIpcLocalHostTimeSyncSystem"/> (IPC host path).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(NetworkTimeSystem))]
    public partial struct TitanOrbitSocketLoopbackTickTuneSystem : ISystem
    {
        /// <summary>
        /// Max EstimatedRTT (ms) treated as loopback / LAN. Above this we leave package slack alone
        /// so Relay clients keep their 2-tick command cushion.
        /// </summary>
        const float LoopbackRttMsMax = 40f;

        /// <summary>Requires an in-game connection before tuning.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<ClientTickRate>();
        }

        /// <summary>
        /// Socket client-only + low RTT → TargetCommandSlack = 0 (match IPC Local Host cushion).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Skip Local Host (IPC tandem system owns that path) ---
            if (ClientServerBootstrap.HasServerWorld)
                return;

            if (!SystemAPI.TryGetSingleton<NetworkStreamDriver>(out var streamDriver))
                return;
            if (streamDriver.DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId) != TransportType.Socket)
                return;

            // --- RTT gate: only loopback / LAN ---
            // [NETCODE] EstimatedRTT is milliseconds on NetworkSnapshotAck.
            if (SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack))
            {
                if (ack.EstimatedRTT > LoopbackRttMsMax)
                    return;
            }

            if (!SystemAPI.TryGetSingletonRW<ClientTickRate>(out var clientTickRate))
                return;

            // [NETCODE] DefaultClientTickRate.TargetCommandSlack = 2. Loopback does not need it;
            // keeping 2 forces prediction ~2 ticks ahead of the host (basics29 cmdAge≈-2).
            if (clientTickRate.ValueRO.TargetCommandSlack != 0)
                clientTickRate.ValueRW.TargetCommandSlack = 0;
        }
    }
}
