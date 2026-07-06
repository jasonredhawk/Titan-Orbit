using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;

namespace TitanOrbit.NetCode
{
    public struct TitanOrbitRelayDriverConstructor : INetworkStreamDriverConstructor
    {
        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            if (TitanOrbitRelayState.TryGetClientRelay(out var relay))
            {
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

            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug,
                DefaultDriverBuilder.GetNetworkClientSettings());
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            if (TitanOrbitRelayState.TryGetServerRelay(out var relay))
            {
                var settings = TitanOrbitRelayUtility.ApplyRelayFriendlyNetworkSettings(
                    DefaultDriverBuilder.GetNetworkServerSettings());
                settings = settings.WithRelayParameters(ref relay);

                // Headless dedicated host: relay UDP only. IPC + relay UDP (default RegisterServerDriver)
                // can leave Listen bound to the wrong driver so Relay clients never reach the server.
                if (IsDedicatedServerOnlyProcess())
                {
#if !UNITY_WEBGL || UNITY_EDITOR
                    DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#else
                    DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
                    return;
                }

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
