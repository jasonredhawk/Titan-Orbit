using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Custom network driver factory for Unity Transport + Relay. Implements
    /// INetworkStreamDriverConstructor so NetCode picks Relay endpoints from
    /// <see cref="TitanOrbitRelayState"/> instead of raw LAN sockets. Dedicated server uses
    /// UDP-only Relay listen (no IPC) so remote clients reach the host allocation. Paired with
    /// <see cref="TitanOrbitRelayUtility"/> for queue sizing and connection types.
    /// </summary>
    public struct TitanOrbitRelayDriverConstructor : INetworkStreamDriverConstructor
    {
        /// <summary>
        /// Registers client driver — Relay when join allocation is set, else default LAN/WebSocket.
        /// </summary>
        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            if (TitanOrbitRelayState.TryGetClientRelay(out var relay))
            {
                // --- Relay join path ---
                var settings = TitanOrbitRelayUtility.ApplyRelayFriendlyNetworkSettings(
                    DefaultDriverBuilder.GetNetworkClientSettings());
                settings = settings.WithRelayParameters(ref relay);
#if !UNITY_WEBGL || UNITY_EDITOR
                DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
#else
                DefaultDriverBuilder.RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
                return;
            }

            // --- Local / direct connect ---
            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug,
                DefaultDriverBuilder.GetNetworkClientSettings());
        }

        /// <summary>
        /// Registers server driver — Relay listen when host allocation is set. Dedicated headless
        /// binds Relay UDP only (IPC + Relay caused missed remote connections).
        /// </summary>
        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            if (TitanOrbitRelayState.TryGetServerRelay(out var relay))
            {
                var settings = TitanOrbitRelayUtility.ApplyRelayFriendlyNetworkSettings(
                    DefaultDriverBuilder.GetNetworkServerSettings());
                settings = settings.WithRelayParameters(ref relay);

                // [TITAN-ORBIT] Headless dedicated: relay UDP only — no IPC alongside Relay.
                if (IsDedicatedServerOnlyProcess())
                {
#if !UNITY_WEBGL || UNITY_EDITOR
                    DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#else
                    DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
                    return;
                }

                // Editor host: IPC for local client + Relay UDP for remote.
                DefaultDriverBuilder.RegisterServerIpcDriver(world, ref driverStore, netDebug,
                    DefaultDriverBuilder.GetNetworkServerSettings());
#if !UNITY_WEBGL || UNITY_EDITOR
                DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#else
                DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
                return;
            }

            DefaultDriverBuilder.RegisterServerDriver(world, ref driverStore, netDebug,
                DefaultDriverBuilder.GetNetworkServerSettings());
        }

        /// <summary>True when this process is server-only (GCE headless or Play Mode Server world).</summary>
        static bool IsDedicatedServerOnlyProcess()
        {
#if UNITY_SERVER
            return true;
#else
            return ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.Server;
#endif
        }
    }
}
