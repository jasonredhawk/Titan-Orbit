using TitanOrbit.Diagnostics;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Network driver factory. Dedicated matches use a UDP (or WebGL WebSocket)
    /// socket to the server's public IP:port, with the same UTP queue/timeout bumps that
    /// the Unity Relay path used. Local Host still uses NetCode's default IPC + UDP layout.
    /// </summary>
    public struct TitanOrbitRelayDriverConstructor : INetworkStreamDriverConstructor
    {
        /// <summary>Registers the client driver (dedicated UDP/WSS, else default LAN/IPC).</summary>
        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            if (TitanOrbitSessionManager.IsDedicatedOnlineClient)
            {
                // Same queues/timeouts as the working Relay join — UTP defaults drop snapshots.
                var settings = TitanOrbitRelayUtility.ApplyRelayFriendlyNetworkSettings(
                    DefaultDriverBuilder.GetNetworkClientSettings());
                LogDriverSettings("client-dedicated", settings);
#if UNITY_WEBGL
                DefaultDriverBuilder.RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
#else
                DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
#endif
                return;
            }

            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug,
                DefaultDriverBuilder.GetNetworkClientSettings());
        }

        /// <summary>
        /// Registers the server driver. Dedicated / headless is UDP-only (no IPC) with
        /// Relay-era queue sizes. Editor Local Host keeps IPC + UDP.
        /// </summary>
        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            if (IsDedicatedServerOnlyProcess())
            {
                var settings = TitanOrbitRelayUtility.ApplyRelayFriendlyNetworkSettings(
                    DefaultDriverBuilder.GetNetworkServerSettings());
                LogDriverSettings("server-dedicated", settings);
#if UNITY_WEBGL
                DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, settings);
#else
                DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
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

        // #region agent log
        static void LogDriverSettings(string role, NetworkSettings settings)
        {
            int send = 0;
            int recv = 0;
            int heartbeatMs = 0;
            int maxMsg = 0;
            if (settings.TryGet(out NetworkConfigParameter ncp))
            {
                send = ncp.sendQueueCapacity;
                recv = ncp.receiveQueueCapacity;
                heartbeatMs = ncp.heartbeatTimeoutMS;
                maxMsg = ncp.maxMessageSize;
            }

            DedicatedServerFileLog.Append(
                "driver",
                "role=" + role +
                " sendQ=" + send +
                " recvQ=" + recv +
                " heartbeatMs=" + heartbeatMs +
                " maxMsg=" + maxMsg);
            AgentDebugNdjson.Write(
                "T",
                "TitanOrbitRelayDriverConstructor.cs:LogDriverSettings",
                "driver settings",
                "{\"role\":\"" + role +
                "\",\"sendQ\":" + send +
                ",\"recvQ\":" + recv +
                ",\"heartbeatMs\":" + heartbeatMs +
                ",\"maxMsg\":" + maxMsg +
                ",\"hasRelay\":" + (TitanOrbitRelayState.HasClientRelay ? "true" : "false") + "}");
        }
        // #endregion
    }
}
