using System;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Reads Edgegap deployment environment variables injected at container start (ARBITRIUM_*).
    /// Clients join the advertised public IP:port from UGS Lobby (no Unity Relay).
    /// Consumed from <see cref="TitanOrbitServerCommandLine.Parse"/>.
    /// </summary>
    public static class TitanOrbitEdgegapEnvironment
    {
        /// <summary>True when ARBITRIUM_REQUEST_ID is present (running inside an Edgegap deployment).</summary>
        public static bool IsEdgegapDeployment =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARBITRIUM_REQUEST_ID"));

        /// <summary>
        /// Edgegap Unity plugin <b>Test locally</b> injects dummy ARBITRIUM_* values
        /// (<c>ARBITRIUM_ENV_DEBUG=true</c>, <c>ARBITRIUM_PUBLIC_IP=162.254.141.66</c>).
        /// Those are not reachable from the Editor — clients must use 127.0.0.1 + published UDP.
        /// </summary>
        public static bool IsLocalPluginTest
        {
            get
            {
                if (!IsEdgegapDeployment)
                    return false;
                string debug = Environment.GetEnvironmentVariable("ARBITRIUM_ENV_DEBUG");
                if (string.Equals(debug, "true", StringComparison.OrdinalIgnoreCase) || debug == "1")
                    return true;
                string tags = Environment.GetEnvironmentVariable("ARBITRIUM_DEPLOYMENT_TAGS");
                return string.Equals(tags, "tag1,tag2", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Edgegap containers cannot spawn a sibling Unity process (no GCE systemd handoff).
        /// Process-recycle must stay off or the only lobby dies.
        /// </summary>
        public static bool CanSpawnSiblingProcess => !IsEdgegapDeployment;

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

        /// <summary>Edgegap public IPv4 when <c>ARBITRIUM_PUBLIC_IP</c> is set.</summary>
        public static string TryGetPublicIp()
        {
            string ip = Environment.GetEnvironmentVariable("ARBITRIUM_PUBLIC_IP");
            return string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
        }

        /// <summary>Edgegap external gameport when <c>ARBITRIUM_PORT_GAMEPORT_EXTERNAL</c> is set.</summary>
        public static ushort? TryGetGameportExternal()
        {
            string externalPort = Environment.GetEnvironmentVariable("ARBITRIUM_PORT_GAMEPORT_EXTERNAL");
            if (string.IsNullOrWhiteSpace(externalPort))
                return null;
            if (!int.TryParse(externalPort.Trim(), out int parsedPort))
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
            if (IsLocalPluginTest)
            {
                Debug.Log("[TitanOrbitEdgegapEnvironment] " + summary +
                          " LOCAL PLUGIN TEST — lobby Host will be 127.0.0.1:" + effectiveServerPort +
                          " (ignore dummy ARBITRIUM_PUBLIC_IP; publish UDP " + effectiveServerPort + " on Docker).");
                return;
            }

            Debug.Log("[TitanOrbitEdgegapEnvironment] " + summary +
                      " (clients connect to publicIp:gameportExternal — no Unity Relay).");
        }
    }
}
