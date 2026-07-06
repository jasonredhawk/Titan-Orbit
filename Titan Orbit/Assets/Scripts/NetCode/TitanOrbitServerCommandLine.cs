using System;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>Headless dedicated server launch parameters (GCE systemd, local testing).</summary>
    public sealed class TitanOrbitServerCommandLine
    {
        public const int DefaultMaxPlayers = 60;
        public const ushort DefaultServerPort = 7777;
        public const int DefaultEmptyMatchRecreateSeconds = 15 * 60;
        public const int DefaultAgeThresholdSeconds = 30 * 60;

        public int MaxPlayers { get; private set; } = DefaultMaxPlayers;
        public ushort ServerPort { get; private set; } = DefaultServerPort;
        public string RelayProtocol { get; private set; } = "dtls";
        public string ServerListenAddress { get; private set; } = "0.0.0.0";
        public bool IsLatest { get; private set; } = true;
        public int EmptyMatchRecreateSeconds { get; private set; } = DefaultEmptyMatchRecreateSeconds;
        public long AgeThresholdSeconds { get; private set; } = DefaultAgeThresholdSeconds;
        public int BootMaxAttempts { get; private set; } = 15;
        public int BootRetryDelaySeconds { get; private set; } = 5;
        public int WaitNetworkManagerSeconds { get; private set; } = 120;

        public static TitanOrbitServerCommandLine Parse()
        {
            var config = new TitanOrbitServerCommandLine();
            config.MaxPlayers = Mathf.Max(2, GetArgInt("maxPlayers", DefaultMaxPlayers));
            config.ServerPort = (ushort)Mathf.Clamp(GetArgInt("serverPort", DefaultServerPort), 1, 65535);
            config.RelayProtocol = SanitizeRelayProtocol(GetArgString("relayProtocol", "dtls"));
            config.ServerListenAddress = GetArgString("serverListenAddress", "0.0.0.0");
            config.IsLatest = GetArgBool("isLatest", true);
            config.EmptyMatchRecreateSeconds = Mathf.Max(60, GetArgInt("emptyMatchRecreateSeconds", DefaultEmptyMatchRecreateSeconds));
            config.AgeThresholdSeconds = Mathf.Max(60, GetArgInt("ageThresholdSeconds", DefaultAgeThresholdSeconds));
            config.BootMaxAttempts = Mathf.Max(1, GetArgInt("bootMaxAttempts", 15));
            config.BootRetryDelaySeconds = Mathf.Max(1, GetArgInt("bootRetryDelaySeconds", 5));
            config.WaitNetworkManagerSeconds = Mathf.Max(10, GetArgInt("waitNetworkManagerSeconds", 120));
            return config;
        }

        public static bool HasDedicatedFlag()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg == null)
                    continue;
                if (string.Equals(arg, "--titanOrbitDedicated", StringComparison.OrdinalIgnoreCase))
                    return true;
                const string prefix = "--titanOrbitDedicated=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string v = arg.Substring(prefix.Length);
                    return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        /// <summary>
        /// MPS 2.0 Relay: legacy CLI/lobby <c>udp</c> is hosted and joined as <c>dtls</c> (matches NGO
        /// <c>DedicatedMatchServerBootstrap.SanitizeRelayProtocolForSdk</c>).
        /// </summary>
        public static string SanitizeRelayProtocol(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "dtls";

            string x = raw.Trim().ToLowerInvariant();
            if (x == "wss")
                return "wss";
            if (x == "udp" || x == "dtls")
                return "dtls";
            return "dtls";
        }

        static int GetArgInt(string name, int defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg != null && arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(arg.Substring(prefix.Length), out int parsed))
                    return parsed;
            }

            return defaultValue;
        }

        static string GetArgString(string name, string defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg != null && arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(prefix.Length);
            }

            return defaultValue;
        }

        static bool GetArgBool(string name, bool defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg == null || !arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string raw = arg.Substring(prefix.Length);
                if (bool.TryParse(raw, out bool parsedBool))
                    return parsedBool;
                if (int.TryParse(raw, out int parsedInt))
                    return parsedInt != 0;
            }

            return defaultValue;
        }
    }
}
