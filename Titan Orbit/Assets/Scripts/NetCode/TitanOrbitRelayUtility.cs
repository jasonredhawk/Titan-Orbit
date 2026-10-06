using System;
using System.Collections.Generic;
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
            string protocol = SanitizeRelayProtocolForRelaySdk(connectionType);
#if UNITY_EDITOR && UNITY_WEBGL
            return CreateRelayServerData(allocation.ServerEndpoints, allocation.AllocationIdBytes,
                allocation.ConnectionData, allocation.ConnectionData, allocation.Key, protocol);
#else
            return allocation.ToRelayServerData(protocol);
#endif
        }

        /// <summary>[NETCODE] Converts client join allocation to UTP RelayServerData.</summary>
        public static RelayServerData FromJoinAllocation(JoinAllocation allocation, string connectionType = null)
        {
            string protocol = SanitizeRelayProtocolForRelaySdk(connectionType);
#if UNITY_EDITOR && UNITY_WEBGL
            return CreateRelayServerData(allocation.ServerEndpoints, allocation.AllocationIdBytes,
                allocation.ConnectionData, allocation.HostConnectionData, allocation.Key, protocol);
#else
            return allocation.ToRelayServerData(protocol);
#endif
        }

        /// <summary>
        /// Builds <see cref="RelayServerData"/> without <c>AllocationUtils.ToRelayServerData</c>.
        /// That helper rejects <c>dtls</c> whenever <c>UNITY_WEBGL</c> is defined, including the Editor.
        /// </summary>
        static RelayServerData CreateRelayServerData(
            List<RelayServerEndpoint> endpoints,
            byte[] allocationId,
            byte[] connectionData,
            byte[] hostConnectionData,
            byte[] key,
            string connectionType)
        {
            RelayServerEndpoint endpoint = null;
            if (endpoints != null)
            {
                for (int i = 0; i < endpoints.Count; i++)
                {
                    if (string.Equals(endpoints[i].ConnectionType, connectionType, StringComparison.OrdinalIgnoreCase))
                    {
                        endpoint = endpoints[i];
                        break;
                    }
                }
            }

            if (endpoint == null)
                throw new ArgumentException("No Relay endpoint for connection type \"" + connectionType + "\".");

            bool isWebSocket = string.Equals(connectionType, "wss", StringComparison.OrdinalIgnoreCase)
                || string.Equals(connectionType, "ws", StringComparison.OrdinalIgnoreCase);
            return new RelayServerData(
                endpoint.Host,
                (ushort)endpoint.Port,
                allocationId,
                connectionData,
                hostConnectionData,
                key,
                endpoint.Secure,
                isWebSocket);
        }

        /// <summary>True when Relay endpoint parsed successfully from allocation.</summary>
        public static bool IsRelayEndpointValid(RelayServerData relay)
        {
            return relay.Endpoint.IsValid;
        }

        /// <summary>
        /// True for the WebGL player, which can only open Relay over <c>wss</c>.
        /// The Editor uses <c>dtls</c> even when the active build target is WebGL:
        /// its WebSocket driver stays in <c>Connecting</c> and never receives a NetworkId.
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
        /// The WebGL player is coerced to <c>wss</c>. The Editor stays <c>dtls</c> so play mode
        /// can join the same allocation the dedicated server listens on. Linux <c>UNITY_SERVER</c> stays dtls.
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
