using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Socket clients keep package <c>TargetCommandSlack=2</c> (not IPC slack=0).
    /// <para>
    /// Slack=0 is IPC Local Host only (<see cref="TitanOrbitIpcLocalHostTimeSyncSystem"/>).
    /// Forcing it on MPPM / LAN Socket (P2 → 127.0.0.1) removed the interpolation buffer —
    /// remote yaw snapped between snapshots and looked like it was fighting heading.
    /// After Relay removal that path is every second player, not just Relay.
    /// </para>
    /// World: ClientSimulation. Before <see cref="NetworkTimeSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateBefore(typeof(NetworkTimeSystem))]
    public partial struct TitanOrbitSocketLoopbackTickTuneSystem : ISystem
    {
        /// <summary>Package default command slack (NetCode DefaultClientTickRate).</summary>
        const uint DefaultTargetCommandSlack = 2;

        /// <summary>Requires an in-game connection before tuning.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<ClientTickRate>();
            state.RequireForUpdate<NetworkSnapshotAck>();
        }

        /// <summary>
        /// Socket / dedicated: keep slack=2. Never rewrite RTT — that collapsed interpolation
        /// onto the snapshot tick and remotes fought heading.
        /// IPC Local Host is owned by <see cref="TitanOrbitIpcLocalHostTimeSyncSystem"/>.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonRW<ClientTickRate>(out var clientTickRate))
                return;

            // --- Local Host Client+Server: IPC tandem owns slack=0 ---
            if (ClientServerBootstrap.HasServerWorld &&
                !TitanOrbitSessionManager.IsDedicatedOnlineClient &&
                !TitanOrbitRelayState.HasClientRelay)
                return;

            if (clientTickRate.ValueRO.TargetCommandSlack != DefaultTargetCommandSlack)
                clientTickRate.ValueRW.TargetCommandSlack = DefaultTargetCommandSlack;
        }
    }
}
