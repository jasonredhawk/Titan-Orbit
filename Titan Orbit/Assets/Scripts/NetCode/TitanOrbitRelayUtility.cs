using System;
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

        /// <summary>
        /// True when this process stored a client Relay join allocation.
        /// Prefer this from Game/UI assemblies — avoids pulling <see cref="RelayServerData"/> / UTP refs.
        /// </summary>
        public static bool HasClientRelay => s_HasClientRelay;
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
        public static RelayServerData FromAllocation(Allocation allocation, string connectionType = null)
        {
            return allocation.ToRelayServerData(SanitizeRelayProtocolForRelaySdk(connectionType));
        }

        /// <summary>[NETCODE] Converts client join allocation to UTP RelayServerData.</summary>
        public static RelayServerData FromJoinAllocation(JoinAllocation allocation, string connectionType = null)
        {
            string protocol = SanitizeRelayProtocolForRelaySdk(connectionType);
#if UNITY_EDITOR
            // AllocationUtils.ToRelayServerData rejects dtls while the WebGL build target is
            // active (UNITY_WEBGL is defined in the Editor). The Editor player is still desktop
            // and must use the allocation's dtls endpoint over UDP.
            if (protocol == "dtls" || protocol == "udp")
                return FromJoinAllocationDesktop(allocation, "dtls");
#endif
            return allocation.ToRelayServerData(protocol);
        }

#if UNITY_EDITOR
        static RelayServerData FromJoinAllocationDesktop(JoinAllocation allocation, string connectionType)
        {
            if (allocation?.ServerEndpoints == null)
                throw new InvalidOperationException("Join allocation has no Relay endpoints.");

            RelayServerEndpoint match = null;
            for (int i = 0; i < allocation.ServerEndpoints.Count; i++)
            {
                RelayServerEndpoint ep = allocation.ServerEndpoints[i];
                if (ep != null && string.Equals(ep.ConnectionType, connectionType, StringComparison.OrdinalIgnoreCase))
                {
                    match = ep;
                    break;
                }
            }

            if (match == null)
                throw new InvalidOperationException("Join allocation has no " + connectionType + " endpoint.");

            return new RelayServerData(
                match.Host,
                (ushort)match.Port,
                allocation.AllocationIdBytes,
                allocation.ConnectionData,
                allocation.HostConnectionData,
                allocation.Key,
                match.Secure,
                isWebSocket: false);
        }
#endif

        /// <summary>True when Relay endpoint parsed successfully from allocation.</summary>
        public static bool IsRelayEndpointValid(RelayServerData relay)
        {
            return relay.Endpoint.IsValid;
        }

        /// <summary>
        /// True for a WebGL player build. The Editor stays false: Play Mode is a desktop
        /// player and uses DTLS even when the active build target defines <c>UNITY_WEBGL</c>.
        /// </summary>
        public static bool PlatformRequiresWebSocketRelay()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }

        /// <summary>Relay connection type for joining clients (not the host listen type).</summary>
        public static string ClientConnectionTypeForPlatform()
        {
            return PlatformRequiresWebSocketRelay() ? "wss" : "dtls";
        }

        /// <summary>
        /// Relay connection type for the dedicated host allocation. GCE may pass <c>--relayProtocol=udp</c>;
        /// that is normalized to <c>dtls</c> for MPS 2.0 (same as legacy NGO dedicated bootstrap).
        /// A WebGL player is coerced to <c>wss</c>. The Editor and Linux dedicated stay <c>dtls</c>.
        /// </summary>
        public static string HostConnectionTypeForPlatform(string commandLineOverride = null)
        {
            return SanitizeRelayProtocolForRelaySdk(commandLineOverride);
        }

        /// <summary>Maps lobby/CLI relay tokens to a UTP Relay connection type the current SDK accepts.</summary>
        public static string SanitizeRelayProtocolForRelaySdk(string raw)
        {
            if (PlatformRequiresWebSocketRelay())
            {
                // Lobby/CLI may still advertise dtls (Linux dedicated host). This process
                // joins the same allocation via its wss endpoint — Relay bridges protocols.
                return "wss";
            }

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
