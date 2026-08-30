using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Headless dedicated server launch parameters parsed from command line (GCE systemd, Edgegap Docker,
    /// local testing). Consumed by <see cref="TitanOrbitDedicatedServerAutoBoot"/> and lobby registration.
    /// Defaults match production deploy; override via --maxPlayers=, --serverPort=, --publicAddress=, etc.
    /// When running on Edgegap, <see cref="TitanOrbitEdgegapEnvironment"/> may override port from ARBITRIUM_* env.
    /// </summary>
    public sealed class TitanOrbitServerCommandLine
    {
        /// <summary>Default player cap when --maxPlayers is omitted.</summary>
        public const int DefaultMaxPlayers = 60;
        public const ushort DefaultServerPort = 7777;
        /// <summary>
        /// Idle empty timeout. <c>0</c> = keep the empty match running and listed (funnel policy).
        /// Occupied matches never use this clock.
        /// </summary>
        public const int DefaultEmptyMatchRecreateSeconds = 0;

        /// <summary>
        /// Age rotation: when IsLatest and players are present (not full), spawn a successor as the
        /// new IsLatest after this many seconds. Default 24h so one match fills before a sibling
        /// is opened. Occupied lobby is demoted but stays open.
        /// </summary>
        public const int DefaultAgeThresholdSeconds = 24 * 60 * 60;

        /// <summary>When our lobby is closed or heartbeat-stale and empty, recreate after this many seconds (faster than empty idle refresh).</summary>
        public const int DefaultStaleLobbyRecreateSeconds = 120;

        /// <summary>
        /// After this many successful <b>30-minute idle</b> in-process recreates
        /// (<c>empty_match_recreate</c> only) in one process lifetime, exit so systemd/Edgegap
        /// starts a fresh binary. Does <b>not</b> count stale/self-heal/heartbeat recreates.
        /// Default 6 ≈ 3 hours of continuous empty idle — Unity IL2CPP needs process recycle
        /// for real memory reclaim (in-process lobby swap does not free the map ServerWorld).
        /// <c>0</c> disables count-based recycle.
        /// </summary>
        public const int DefaultMaxInProcessEmptyRecreates = 6;

        /// <summary>
        /// If the Unity main thread stops ticking for this many seconds, hard-exit so the host
        /// restarts. Paused during Relay/lobby recreate. <c>0</c> disables hang quit.
        /// </summary>
        public const int DefaultMainThreadHangQuitSeconds = 300;

        /// <summary>
        /// When empty, exit if WorkingSet RSS reaches this many MiB. Proven 2026-07-26: host
        /// went STRUGGLING then SSH-dead without a hang-watchdog trip. <c>0</c> disables.
        /// Default 3500 suits ~8 GiB VMs with headroom for the OS.
        /// </summary>
        public const int DefaultRssRecycleMb = 3500;

        /// <summary>
        /// When empty, exit after this many consecutive ~10s netdiag windows that look overloaded
        /// (catch-up ≥ 50% or avg frame ≥ 50 ms). Default 3 ≈ 30s of sustained thrash.
        /// <c>0</c> disables struggling recycle.
        /// </summary>
        public const int DefaultStrugglingSamplesBeforeRecycle = 3;

        /// <summary>How often to append RSS/entity telemetry while hosting (seconds). <c>0</c> = off.</summary>
        public const int DefaultMemoryLogIntervalSeconds = 60;

        public int MaxPlayers { get; private set; } = DefaultMaxPlayers;
        public ushort ServerPort { get; private set; } = DefaultServerPort;
        /// <summary>Public IPv4 clients connect to (Edgegap / GCE / --publicAddress). Not the bind address.</summary>
        public string PublicAddress { get; private set; }
        /// <summary>Public UDP/WSS port clients connect to (defaults to <see cref="ServerPort"/>).</summary>
        public ushort PublicPort { get; private set; } = DefaultServerPort;
        public string ServerListenAddress { get; private set; } = "0.0.0.0";
        public bool IsLatest { get; private set; } = true;
        public int EmptyMatchRecreateSeconds { get; private set; } = DefaultEmptyMatchRecreateSeconds;
        public long AgeThresholdSeconds { get; private set; } = DefaultAgeThresholdSeconds;
        /// <summary>Fast recreate when our published lobby is closed or heartbeat-stale while the server is empty.</summary>
        public int StaleLobbyRecreateSeconds { get; private set; } = DefaultStaleLobbyRecreateSeconds;

        /// <summary>
        /// Max successful empty in-process recreates before process exit (orchestrator restart).
        /// See <see cref="DefaultMaxInProcessEmptyRecreates"/>.
        /// </summary>
        public int MaxInProcessEmptyRecreates { get; private set; } = DefaultMaxInProcessEmptyRecreates;

        /// <summary>
        /// Main-thread hang hard-exit threshold in seconds. See <see cref="DefaultMainThreadHangQuitSeconds"/>.
        /// </summary>
        public int MainThreadHangQuitSeconds { get; private set; } = DefaultMainThreadHangQuitSeconds;

        /// <summary>Empty-process RSS recycle budget (MiB). See <see cref="DefaultRssRecycleMb"/>.</summary>
        public int RssRecycleMb { get; private set; } = DefaultRssRecycleMb;

        /// <summary>
        /// Consecutive struggling netdiag samples before empty-process exit.
        /// See <see cref="DefaultStrugglingSamplesBeforeRecycle"/>.
        /// </summary>
        public int StrugglingSamplesBeforeRecycle { get; private set; } = DefaultStrugglingSamplesBeforeRecycle;

        /// <summary>Periodic memory/entity log interval. See <see cref="DefaultMemoryLogIntervalSeconds"/>.</summary>
        public int MemoryLogIntervalSeconds { get; private set; } = DefaultMemoryLogIntervalSeconds;

        /// <summary>Optional absolute path to headless binary for <c>SpawnNextMatch</c> (GCE when auto-resolve fails).</summary>
        public string ServerExecutablePath { get; private set; }
        public int BootMaxAttempts { get; private set; } = 15;
        public int BootRetryDelaySeconds { get; private set; } = 5;
        public int WaitNetworkManagerSeconds { get; private set; } = 120;

        /// <summary>
        /// Parses all supported CLI flags from <see cref="System.Environment.GetCommandLineArgs"/>.
        /// </summary>
        public static TitanOrbitServerCommandLine Parse()
        {
            // --- Parse ---
            var config = new TitanOrbitServerCommandLine();
            config.MaxPlayers = Mathf.Max(2, GetArgInt("maxPlayers", DefaultMaxPlayers));
            config.ServerPort = (ushort)Mathf.Clamp(GetArgInt("serverPort", DefaultServerPort), 1, 65535);
            config.ServerListenAddress = GetArgString("serverListenAddress", "0.0.0.0");
            config.PublicAddress = ResolvePublicAddress(config.ServerListenAddress);
            int publicPortArg = GetArgInt("publicPort", 0);
            ushort? edgegapExternal = TitanOrbitEdgegapEnvironment.TryGetGameportExternal();
            if (publicPortArg >= 1 && publicPortArg <= 65535)
                config.PublicPort = (ushort)publicPortArg;
            else if (edgegapExternal.HasValue)
                config.PublicPort = edgegapExternal.Value;
            else
                config.PublicPort = config.ServerPort;
            config.IsLatest = GetArgBool("isLatest", true);
            int emptyRecreate = GetArgInt("emptyMatchRecreateSeconds", DefaultEmptyMatchRecreateSeconds);
            // [TITAN-ORBIT] 0 = never recycle an empty listed match (players join whenever they want).
            config.EmptyMatchRecreateSeconds = emptyRecreate <= 0 ? 0 : Mathf.Max(60, emptyRecreate);
            config.AgeThresholdSeconds = Mathf.Max(60, GetArgInt("ageThresholdSeconds", DefaultAgeThresholdSeconds));
            config.StaleLobbyRecreateSeconds = Mathf.Max(30, GetArgInt("staleLobbyRecreateSeconds", DefaultStaleLobbyRecreateSeconds));
            // [TITAN-ORBIT] 0 = unlimited in-process empty recreates (not recommended for 24/7 hosts).
            config.MaxInProcessEmptyRecreates = Mathf.Max(0, GetArgInt("maxInProcessEmptyRecreates", DefaultMaxInProcessEmptyRecreates));
            // [TITAN-ORBIT] 0 = disable background hang watchdog.
            config.MainThreadHangQuitSeconds = Mathf.Max(0, GetArgInt("mainThreadHangQuitSeconds", DefaultMainThreadHangQuitSeconds));
            // [TITAN-ORBIT] 0 = disable RSS-based empty recycle / struggling recycle / memory log.
            config.RssRecycleMb = Mathf.Max(0, GetArgInt("rssRecycleMb", DefaultRssRecycleMb));
            config.StrugglingSamplesBeforeRecycle = Mathf.Max(0, GetArgInt("strugglingSamplesBeforeRecycle", DefaultStrugglingSamplesBeforeRecycle));
            config.MemoryLogIntervalSeconds = Mathf.Max(0, GetArgInt("memoryLogIntervalSeconds", DefaultMemoryLogIntervalSeconds));
            string exePath = GetArgString("serverExecutablePath", null);
            config.ServerExecutablePath = string.IsNullOrWhiteSpace(exePath) ? null : exePath.Trim();
            config.BootMaxAttempts = Mathf.Max(1, GetArgInt("bootMaxAttempts", 15));
            config.BootRetryDelaySeconds = Mathf.Max(1, GetArgInt("bootRetryDelaySeconds", 5));
            config.WaitNetworkManagerSeconds = Mathf.Max(10, GetArgInt("waitNetworkManagerSeconds", 120));

            // [TITAN-ORBIT] Edgegap containers inject ARBITRIUM_* env vars at deploy time (port mapping, deployment id).
            ushort? edgegapPort = TitanOrbitEdgegapEnvironment.TryGetGameportInternal();
            if (edgegapPort.HasValue)
                config.ServerPort = edgegapPort.Value;
            TitanOrbitEdgegapEnvironment.LogBootIfPresent(config.ServerPort);
            return config;
        }

        /// <summary>
        /// True when launch args include --titanOrbitDedicated or --titanOrbitDedicated=true/1.
        /// Gates dedicated server auto-boot marker.
        /// </summary>
        public static bool HasDedicatedFlag()
        {
            // --- HasDedicatedFlag ---
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
        /// Fills <see cref="PublicAddress"/> if still empty (GCS extract often starts the raw
        /// player without the wrapper env). Returns the source used for logging.
        /// </summary>
        public async Task<string> EnsurePublicAddressAsync()
        {
            if (!string.IsNullOrWhiteSpace(PublicAddress))
                return "already-set";

            string resolved = ResolvePublicAddress(ServerListenAddress, out string source);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                resolved = await TryGetGceMetadataPublicIpAsync();
                source = string.IsNullOrWhiteSpace(resolved) ? "none" : "gce-metadata";
            }

            PublicAddress = resolved;
            return source;
        }

        /// <summary>
        /// Client-facing host IP: --publicAddress, TITANORBIT_PUBLIC_ADDRESS, Edgegap
        /// <c>ARBITRIUM_PUBLIC_IP</c>, or a unicast --serverListenAddress.
        /// </summary>
        static string ResolvePublicAddress(string listenAddress)
        {
            return ResolvePublicAddress(listenAddress, out _);
        }

        static string ResolvePublicAddress(string listenAddress, out string source)
        {
            string cli = GetArgString("publicAddress", null);
            if (!string.IsNullOrWhiteSpace(cli))
            {
                source = "cli";
                return cli.Trim();
            }

            string env = Environment.GetEnvironmentVariable("TITANORBIT_PUBLIC_ADDRESS");
            if (!string.IsNullOrWhiteSpace(env))
            {
                source = "env";
                return env.Trim();
            }

            string edgegap = TitanOrbitEdgegapEnvironment.TryGetPublicIp();
            if (!string.IsNullOrWhiteSpace(edgegap))
            {
                source = "edgegap";
                return edgegap;
            }

            if (IsUnicastListenAddress(listenAddress))
            {
                source = "listen";
                return listenAddress.Trim();
            }

            source = "none";
            return null;
        }

        /// <summary>GCE instance public IPv4 from the metadata server (no wrapper required).</summary>
        static async Task<string> TryGetGceMetadataPublicIpAsync()
        {
            const string url =
                "http://metadata.google.internal/computeMetadata/v1/instance/network-interfaces/0/access-configs/0/external-ip";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Metadata-Flavor", "Google");
            req.timeout = 2;
            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success)
                return null;
            string ip = req.downloadHandler != null ? req.downloadHandler.text : null;
            if (string.IsNullOrWhiteSpace(ip))
                return null;
            ip = ip.Trim();
            return IsUnicastListenAddress(ip) ? ip : null;
        }

        /// <summary>True when listen address is a real client-reachable unicast (not 0.0.0.0 / *).</summary>
        public static bool IsUnicastListenAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return false;
            string a = address.Trim();
            return a != "0.0.0.0" && a != "*" && a != "::" && a != "[::]";
        }

        static int GetArgInt(string name, int defaultValue)
        {
            // --- Compute value ---
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
            // --- Compute value ---
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
            // --- Compute value ---
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
