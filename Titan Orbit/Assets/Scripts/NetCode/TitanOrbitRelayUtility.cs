using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay.Models;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Thread-safe relay configuration consumed by <see cref="TitanOrbitRelayDriverConstructor"/>.
    /// Host and client Relay allocations are stored separately for dedicated-server boot.
    /// </summary>
    public static class TitanOrbitRelayState
    {
        static RelayServerData s_ServerRelay;
        static RelayServerData s_ClientRelay;
        static bool s_HasServerRelay;
        static bool s_HasClientRelay;

        /// <summary>Stores host listen Relay data after CreateAllocationAsync.</summary>
        public static void SetServerRelay(RelayServerData data)
        {
            s_ServerRelay = data;
            s_HasServerRelay = true;
        }

        /// <summary>Stores client join Relay data after JoinAllocationAsync.</summary>
        public static void SetClientRelay(RelayServerData data)
        {
            s_ClientRelay = data;
            s_HasClientRelay = true;
        }

        /// <summary>Clears both host and client relay slots — local LAN or disconnect.</summary>
        public static void Clear()
        {
            s_HasServerRelay = false;
            s_HasClientRelay = false;
        }

        /// <summary>Returns host RelayServerData when dedicated server has bound Relay listen.</summary>
        public static bool TryGetServerRelay(out RelayServerData data)
        {
            data = s_ServerRelay;
            return s_HasServerRelay;
        }

        /// <summary>Returns client RelayServerData when joining via join code.</summary>
        public static bool TryGetClientRelay(out RelayServerData data)
        {
            data = s_ClientRelay;
            return s_HasClientRelay;
        }
    }

    /// <summary>
    /// [NETCODE] Unity Relay + UTP helpers — connection types, packet queue sizing, allocation conversion.
    /// Used by TitanOrbitSessionManager and TitanOrbitRelayDriverConstructor.
    /// </summary>
    public static class TitanOrbitRelayUtility
    {
        const int MinRelayPacketQueueSize = 1024;

        /// <summary>UTP defaults are too small for Relay; mirrors legacy NGO <c>ApplyRelayFriendlyTransportSettings</c>.</summary>
        public static NetworkSettings ApplyRelayFriendlyNetworkSettings(NetworkSettings settings)
        {
            // --- Bump timeouts and queue sizes for Relay packet volume ---
            if (!settings.TryGet(out NetworkConfigParameter ncp))
                ncp = settings.GetNetworkConfigParameters();

            int connectTimeoutMs = ncp.connectTimeoutMS < 3000 ? 5000 : ncp.connectTimeoutMS;
            int heartbeatTimeoutMs = ncp.heartbeatTimeoutMS <= 0 || ncp.heartbeatTimeoutMS > 9000
                ? 3000
                : ncp.heartbeatTimeoutMS < 3000 ? 3000 : ncp.heartbeatTimeoutMS;
            int receiveQueue = ncp.receiveQueueCapacity < MinRelayPacketQueueSize
                ? MinRelayPacketQueueSize
                : ncp.receiveQueueCapacity;
            int sendQueue = ncp.sendQueueCapacity < MinRelayPacketQueueSize
                ? MinRelayPacketQueueSize
                : ncp.sendQueueCapacity;

            return settings.WithNetworkConfigParameters(
                connectTimeoutMS: connectTimeoutMs,
                maxConnectAttempts: ncp.maxConnectAttempts,
                disconnectTimeoutMS: ncp.disconnectTimeoutMS,
                heartbeatTimeoutMS: heartbeatTimeoutMs,
                reconnectionTimeoutMS: ncp.reconnectionTimeoutMS,
                maxMessageSize: ncp.maxMessageSize,
                receiveQueueCapacity: receiveQueue,
                sendQueueCapacity: sendQueue);
        }

        /// <summary>[NETCODE] Converts host Relay allocation to UTP RelayServerData.</summary>
        public static RelayServerData FromAllocation(Allocation allocation, string connectionType = "dtls")
        {
            return allocation.ToRelayServerData(connectionType);
        }

        /// <summary>[NETCODE] Converts client join allocation to UTP RelayServerData.</summary>
        public static RelayServerData FromJoinAllocation(JoinAllocation allocation, string connectionType = "dtls")
        {
            return allocation.ToRelayServerData(connectionType);
        }

        /// <summary>True when Relay endpoint parsed successfully from allocation.</summary>
        public static bool IsRelayEndpointValid(RelayServerData relay)
        {
            return relay.Endpoint.IsValid;
        }

        /// <summary>Relay connection type for joining clients (not the host listen type).</summary>
        public static string ClientConnectionTypeForPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "wss";
#else
            return "dtls";
#endif
        }

        /// <summary>
        /// Relay connection type for the dedicated host allocation. GCE may pass <c>--relayProtocol=udp</c>;
        /// that is normalized to <c>dtls</c> for MPS 2.0 (same as legacy NGO dedicated bootstrap).
        /// </summary>
        public static string HostConnectionTypeForPlatform(string commandLineOverride = null)
        {
            return SanitizeRelayProtocolForRelaySdk(commandLineOverride);
        }

        /// <summary>Maps lobby/CLI relay tokens to a UTP Relay connection type.</summary>
        public static string SanitizeRelayProtocolForRelaySdk(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return ClientConnectionTypeForPlatform();

            string x = raw.Trim().ToLowerInvariant();
            if (x == "wss")
                return "wss";
            if (x == "udp" || x == "dtls")
                return "dtls";
            return ClientConnectionTypeForPlatform();
        }

        /// <summary>Resolves connection type from override or platform defaults (host vs client).</summary>
        public static string ConnectionTypeForPlatform(string overrideType = null)
        {
            if (!string.IsNullOrWhiteSpace(overrideType))
                return HostConnectionTypeForPlatform(overrideType);
            return ClientConnectionTypeForPlatform();
        }
    }
}
