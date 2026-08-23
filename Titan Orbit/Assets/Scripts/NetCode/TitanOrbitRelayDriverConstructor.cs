using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Network driver factory. Dedicated matches use a normal UDP (or WebGL
    /// WebSocket) socket to the server's public IP:port. Local Host still uses NetCode's
    /// default IPC + UDP layout. Unity Relay is not used.
    /// </summary>
    public struct TitanOrbitRelayDriverConstructor : INetworkStreamDriverConstructor
    {
        /// <summary>Registers the client driver (UDP, or WebSocket on WebGL).</summary>
        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug,
                DefaultDriverBuilder.GetNetworkClientSettings());
        }

        /// <summary>
        /// Registers the server driver. Dedicated / headless is UDP-only (no IPC).
        /// Editor Local Host keeps IPC + UDP so the in-process client can use zero-latency IPC.
        /// </summary>
        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            if (IsDedicatedServerOnlyProcess())
            {
#if UNITY_WEBGL
                DefaultDriverBuilder.RegisterServerWebSocketDriver(
                    world, ref driverStore, netDebug, DefaultDriverBuilder.GetNetworkServerSettings());
#else
                DefaultDriverBuilder.RegisterServerUdpDriver(
                    world, ref driverStore, netDebug, DefaultDriverBuilder.GetNetworkServerSettings());
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
