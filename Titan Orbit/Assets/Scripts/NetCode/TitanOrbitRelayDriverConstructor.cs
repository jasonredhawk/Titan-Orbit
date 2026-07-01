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
                DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug, ref relay);
                return;
            }
            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug,
                DefaultDriverBuilder.GetNetworkClientSettings());
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            if (TitanOrbitRelayState.TryGetServerRelay(out var relay))
            {
                DefaultDriverBuilder.RegisterServerDriver(world, ref driverStore, netDebug, ref relay);
                return;
            }
            DefaultDriverBuilder.RegisterServerDriver(world, ref driverStore, netDebug,
                DefaultDriverBuilder.GetNetworkServerSettings());
        }
    }
}
