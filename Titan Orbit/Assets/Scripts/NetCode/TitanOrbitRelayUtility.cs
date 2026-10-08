using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

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
            // AllocationUtils.ToRelayServerData rejects dtls while the WebGL build target is active,
            // including in the Editor. This builder accepts the protocol the Editor actually dials.
            RelayServerData data = CreateRelayServerData(allocation.ServerEndpoints, allocation.AllocationIdBytes,
                allocation.ConnectionData, allocation.ConnectionData, allocation.Key, protocol);
#else
            RelayServerData data = allocation.ToRelayServerData(protocol);
#endif
            return PinEditorRelayEndpointToIpv4(data, allocation.ServerEndpoints, protocol);
        }

        /// <summary>[NETCODE] Converts client join allocation to UTP RelayServerData.</summary>
        public static RelayServerData FromJoinAllocation(JoinAllocation allocation, string connectionType = null)
        {
            string protocol = SanitizeRelayProtocolForRelaySdk(connectionType);
#if UNITY_EDITOR && UNITY_WEBGL
            RelayServerData data = CreateRelayServerData(allocation.ServerEndpoints, allocation.AllocationIdBytes,
                allocation.ConnectionData, allocation.HostConnectionData, allocation.Key, protocol);
#else
            RelayServerData data = allocation.ToRelayServerData(protocol);
#endif
            return PinEditorRelayEndpointToIpv4(data, allocation.ServerEndpoints, protocol);
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
        /// The Editor is a separate case: it also dials <c>wss</c> (see
        /// <see cref="ClientConnectionTypeForPlatform"/>) but this flag stays false so a
        /// Linux dedicated host is not forced onto WebSocket.
        /// </summary>
        public static bool PlatformRequiresWebSocketRelay()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }

        /// <summary>
        /// Relay connection type for joining clients (the host listen type is separate).
        /// The published WebGL client and the Editor both use <c>wss</c> on the dedicated
        /// allocation. Relay carries that onto the Linux host's <c>dtls</c> listen.
        /// </summary>
        public static string ClientConnectionTypeForPlatform()
        {
#if UNITY_EDITOR
            return "wss";
#else
            return PlatformRequiresWebSocketRelay() ? "wss" : "dtls";
#endif
        }

        /// <summary>
        /// Relay connection type for the dedicated host allocation. GCE may pass <c>--relayProtocol=udp</c>;
        /// that is normalized to <c>dtls</c> for MPS 2.0 (same as legacy NGO dedicated bootstrap).
        /// The WebGL player and the Editor join with <c>wss</c>. Linux <c>UNITY_SERVER</c> stays dtls.
        /// Relay delivers both onto the same host allocation.
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

        /// <summary>
        /// Editor DNS often returns IPv6 first. Unity Transport then dials that address and the
        /// Relay handshake stays on Connecting. The TLS name stays the Relay hostname (set when
        /// <see cref="RelayServerData"/> was built). Only the socket address is pinned to IPv4.
        /// </summary>
        static RelayServerData PinEditorRelayEndpointToIpv4(
            RelayServerData data,
            List<RelayServerEndpoint> endpoints,
            string connectionType)
        {
#if !UNITY_EDITOR
            return data;
#else
            if (data.Endpoint.IsValid && data.Endpoint.Family == NetworkFamily.Ipv4)
                return data;

            string host = null;
            ushort port = 0;
            if (endpoints != null)
            {
                for (int i = 0; i < endpoints.Count; i++)
                {
                    if (!string.Equals(endpoints[i].ConnectionType, connectionType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    host = endpoints[i].Host;
                    port = (ushort)endpoints[i].Port;
                    break;
                }
            }

            if (string.IsNullOrEmpty(host) || port == 0)
                return data;

            if (!TryResolveRelayIpv4(host, port, out NetworkEndpoint ipv4))
            {
                Debug.LogWarning("[TitanOrbitRelay] Editor could not resolve an IPv4 address for " + host +
                                 ". The Relay handshake may stay on Connecting.");
                return data;
            }

            Debug.Log("[TitanOrbitRelay] Editor Relay " + connectionType + " " + host + " -> " + ipv4 + ".");
            data.Endpoint = ipv4;
            return data;
#endif
        }

#if UNITY_EDITOR
        /// <summary>Resolves <paramref name="host"/> to an IPv4 <see cref="NetworkEndpoint"/>.</summary>
        static bool TryResolveRelayIpv4(string host, ushort port, out NetworkEndpoint endpoint)
        {
            endpoint = default;
            if (NetworkEndpoint.TryParse(host, port, out endpoint, NetworkFamily.Ipv4) && endpoint.IsValid)
                return true;

            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (NetworkEndpoint.TryParse(addresses[i].ToString(), port, out endpoint, NetworkFamily.Ipv4) &&
                        endpoint.IsValid)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TitanOrbitRelay] IPv4 lookup failed for " + host + ": " + ex.Message);
            }

            endpoint = default;
            return false;
        }
#endif
    }
}
