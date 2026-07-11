using System;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Reads Edgegap deployment environment variables injected at container start (ARBITRIUM_*).
    /// Titan Orbit clients still join via UGS Lobby + Unity Relay — not direct IP:port — but Edgegap
    /// port metadata and deployment IDs are useful for logs, rotation diagnostics, and future matchmaking.
    /// Consumed from <see cref="TitanOrbitServerCommandLine.Parse"/>.
    /// </summary>
    public static class TitanOrbitEdgegapEnvironment
    {
        /// <summary>True when ARBITRIUM_REQUEST_ID is present (running inside an Edgegap deployment).</summary>
        public static bool IsEdgegapDeployment =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARBITRIUM_REQUEST_ID"));

        /// <summary>
        /// Returns Edgegap internal gameport when ARBITRIUM_PORT_GAMEPORT_INTERNAL is set and valid.
        /// Port name in the Edgegap app version must be <c>gameport</c> for this variable to exist.
        /// </summary>
        public static ushort? TryGetGameportInternal()
        {
            string internalPort = Environment.GetEnvironmentVariable("ARBITRIUM_PORT_GAMEPORT_INTERNAL");
            if (string.IsNullOrWhiteSpace(internalPort))
                return null;
            if (!int.TryParse(internalPort.Trim(), out int parsedPort))
                return null;
            if (parsedPort < 1 || parsedPort > 65535)
                return null;
            return (ushort)parsedPort;
        }

        /// <summary>Logs deployment metadata once at dedicated boot (no-op outside Edgegap).</summary>
        /// <param name="effectiveServerPort">Port after CLI + Edgegap override applied.</param>
        public static void LogBootIfPresent(ushort effectiveServerPort)
        {
            if (!IsEdgegapDeployment)
                return;

            string requestId = Environment.GetEnvironmentVariable("ARBITRIUM_REQUEST_ID");
            string publicIp = Environment.GetEnvironmentVariable("ARBITRIUM_PUBLIC_IP");
            string externalPort = Environment.GetEnvironmentVariable("ARBITRIUM_PORT_GAMEPORT_EXTERNAL");
            string protocol = Environment.GetEnvironmentVariable("ARBITRIUM_PORT_GAMEPORT_PROTOCOL");

            string summary =
                "Edgegap env requestId=" + (requestId ?? "(none)") +
                " publicIp=" + (publicIp ?? "(none)") +
                " gameportInternal=" + effectiveServerPort +
                " gameportExternal=" + (externalPort ?? "(none)") +
                " protocol=" + (protocol ?? "(none)");

            Diagnostics.DedicatedServerFileLog.Append("edgegap", summary);
            Debug.Log("[TitanOrbitEdgegapEnvironment] " + summary +
                      " (clients use UGS Lobby + Relay — not direct connect to external port).");
        }
    }
}
