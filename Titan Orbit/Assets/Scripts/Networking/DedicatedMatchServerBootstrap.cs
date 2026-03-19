using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Dedicated server bootstrap (server-only): creates one Relay allocation + one UGS Lobby, then starts Netcode server.
    ///
    /// This script is intended to run in a dedicated server build (headless) and is started automatically on scene load.
    /// Match rotation is handled by extending this script in a later step.
    /// </summary>
    public static class DedicatedMatchServerBootstrap
    {
        private const string LobbyRelayCodeKey = "RelayJoinCode";
        private const string LobbyGameNameKey = "GameName";
        private const string LobbyGameNameValue = "TitanOrbit";
        private const string LobbyIsOpenKey = "IsOpen";
        private const string LobbyIsLatestKey = "IsLatest";
        private const string LobbyCreatedAtEpochKey = "CreatedAtEpoch";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            // Only run in headless server processes.
            // (This avoids accidentally starting a server in normal client/editor play.)
            if (!Application.isBatchMode)
                return;

            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return;

            _ = BootAsync();
        }

        private static int GetArgInt(string name, int defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg != null && arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Substring(prefix.Length), out int parsed))
                        return parsed;
                }
            }
            return defaultValue;
        }

        private static string GetArgString(string name, string defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg != null && arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(prefix.Length);
            }
            return defaultValue;
        }

        private static bool GetArgBool(string name, bool defaultValue)
        {
            string prefix = "--" + name + "=";
            foreach (var arg in Environment.GetCommandLineArgs())
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

        private static async Task<bool> EnsureUnityServicesInitializedAsync()
        {
            try
            {
                if (UnityServices.State == ServicesInitializationState.Initialized &&
                    AuthenticationService.Instance.IsSignedIn)
                {
                    return true;
                }

                if (UnityServices.State != ServicesInitializationState.Initialized)
                    await UnityServices.InitializeAsync();

                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DedicatedMatchServerBootstrap] Unity Services failed. " + e.Message);
                return false;
            }
        }

        private static void EnsurePlayerPrefabSet()
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab != null)
                return;

            GameObject fallback = Resources.Load<GameObject>("Prefabs/Starship");
            if (fallback != null && fallback.GetComponent<NetworkObject>() != null)
            {
                NetworkManager.Singleton.NetworkConfig.PlayerPrefab = fallback;
                Debug.Log("[DedicatedMatchServerBootstrap] Player Prefab missing; assigned from Resources/Prefabs/Starship.");
            }
        }

        private static async Task BootAsync()
        {
            int maxPlayers = GetArgInt("maxPlayers", 60);
            ushort serverPort = (ushort)GetArgInt("serverPort", 7777);
            string relayProtocol = GetArgString("relayProtocol", "wss"); // Browsers need wss.
            bool isLatest = GetArgBool("isLatest", true);
            long ageThresholdSeconds = GetArgInt("ageThresholdSeconds", 20 * 60);

            EnsurePlayerPrefabSet();
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("[DedicatedMatchServerBootstrap] Player Prefab not set on NetworkManager.");
                Application.Quit();
                return;
            }

            try
            {
                if (!await EnsureUnityServicesInitializedAsync())
                {
                    Debug.LogError("[DedicatedMatchServerBootstrap] Cannot initialize Unity Services on server.");
                    Application.Quit();
                    return;
                }

                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                if (transport == null)
                {
                    Debug.LogError("[DedicatedMatchServerBootstrap] UnityTransport not found on NetworkManager.");
                    Application.Quit();
                    return;
                }

                // Optional: set local listen port (relay allocates its own external connectivity).
                transport.SetConnectionData(transport.ConnectionData.Address, serverPort, transport.ConnectionData.ServerListenAddress);

                int maxConnections = Mathf.Max(1, maxPlayers - 1);
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, relayProtocol));

                // Create the UGS Lobby before starting server so clients can discover it immediately.
                long createdAtEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var createdLobby = await LobbyService.Instance.CreateLobbyAsync(
                    GameNames.GetRandomRoomName(),
                    maxPlayers,
                    new CreateLobbyOptions
                    {
                        IsPrivate = false,
                        Data = new Dictionary<string, DataObject>
                        {
                            { LobbyRelayCodeKey, new DataObject(DataObject.VisibilityOptions.Member, joinCode) },
                            { LobbyGameNameKey, new DataObject(DataObject.VisibilityOptions.Public, LobbyGameNameValue, DataObject.IndexOptions.S1) },
                            { LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N1) },
                            { LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, isLatest ? "1" : "0", DataObject.IndexOptions.N2) },
                            {
                                LobbyCreatedAtEpochKey,
                                new DataObject(
                                    DataObject.VisibilityOptions.Public,
                                    createdAtEpochSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    DataObject.IndexOptions.N3
                                )
                            }
                        }
                    }
                );

                Debug.Log($"[DedicatedMatchServerBootstrap] Starting server for lobby {createdLobby.Id} (isLatest={isLatest}).");

                // Start server-side Netcode.
                bool started = NetworkManager.Singleton.StartServer();
                if (!started)
                {
                    Debug.LogError("[DedicatedMatchServerBootstrap] StartServer failed.");
                    Application.Quit();
                    return;
                }

                _ = HeartbeatLoopAsync(createdLobby.Id);
                _ = RotationLoopAsync(
                    createdLobby.Id,
                    createdAtEpochSeconds,
                    maxPlayers,
                    serverPort,
                    relayProtocol,
                    isLatest,
                    ageThresholdSeconds
                );
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Application.Quit();
            }
        }

        private static async Task HeartbeatLoopAsync(string lobbyId)
        {
            // Lobbies require heartbeat pings to stay alive.
            // We match the existing NetworkGameManager heartbeat interval (15s).
            while (true)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(lobbyId))
                        await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[DedicatedMatchServerBootstrap] Heartbeat failed: " + e.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        }

        private static async Task RotationLoopAsync(
            string lobbyId,
            long createdAtEpochSeconds,
            int maxPlayers,
            ushort serverPort,
            string relayProtocol,
            bool initialIsLatest,
            long ageThresholdSeconds)
        {
            bool isLatest = initialIsLatest;
            bool spawnedFromAge = false;
            bool spawnedFromFull = false;

            while (true)
            {
                try
                {
                    // Player count on the host server includes the server itself.
                    int connectedClients = NetworkManager.Singleton != null ? NetworkManager.Singleton.ConnectedClients.Count : 0;
                    int playerCount = connectedClients + 1; // + host itself

                    bool isFull = playerCount >= maxPlayers;
                    long nowEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long ageSeconds = nowEpochSeconds - createdAtEpochSeconds;

                    // Age-based rotation (only when this match is the "latest" free target).
                    if (isLatest && !spawnedFromAge && ageSeconds >= ageThresholdSeconds && !isFull)
                    {
                        spawnedFromAge = true;
                        isLatest = false;

                        await UpdateLobbyIsLatestAsync(lobbyId, false);
                        Debug.Log($"[DedicatedMatchServerBootstrap] 20min rotation: spawned next match (old lobby {lobbyId} set not-latest).");
                        SpawnNextMatch(maxPlayers, serverPort, relayProtocol, nextIsLatest: true);
                    }

                    // Full-based rotation (when max players are reached).
                    if (!spawnedFromFull && isFull)
                    {
                        spawnedFromFull = true;

                        bool nextIsLatest = isLatest;
                        isLatest = false;

                        await UpdateLobbyFlagsAsync(lobbyId, isOpen: false, isLatest: false);
                        Debug.Log($"[DedicatedMatchServerBootstrap] Lobby full rotation: spawned next match (old lobby {lobbyId} closed). NextIsLatest={nextIsLatest}.");
                        SpawnNextMatch(maxPlayers, serverPort, relayProtocol, nextIsLatest: nextIsLatest);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[DedicatedMatchServerBootstrap] Rotation loop error: " + e.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        private static async Task UpdateLobbyIsLatestAsync(string lobbyId, bool isLatest)
        {
            await LobbyService.Instance.UpdateLobbyAsync(lobbyId, new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    { LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, isLatest ? "1" : "0", DataObject.IndexOptions.N2) }
                }
            });
        }

        private static async Task UpdateLobbyFlagsAsync(string lobbyId, bool isOpen, bool isLatest)
        {
            await LobbyService.Instance.UpdateLobbyAsync(lobbyId, new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    { LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, isOpen ? "1" : "0", DataObject.IndexOptions.N1) },
                    { LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, isLatest ? "1" : "0", DataObject.IndexOptions.N2) }
                },
                IsLocked = !isOpen
            });
        }

        private static void SpawnNextMatch(int maxPlayers, ushort serverPort, string relayProtocol, bool nextIsLatest)
        {
            try
            {
                // Allow multiple concurrent matches on one host by choosing a derived port.
                // (The relay connection itself is independent of this, but UnityTransport still binds a local socket.)
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                int derivedPort = serverPort + (pid % 2000) + UnityEngine.Random.Range(0, 2000);
                if (derivedPort > 65000) derivedPort = 65000;
                ushort childServerPort = (ushort)derivedPort;

                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule != null
                    ? System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName
                    : null;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    Debug.LogError("[DedicatedMatchServerBootstrap] Cannot determine server executable path for spawn.");
                    return;
                }

                string args =
                    $"--maxPlayers={maxPlayers} " +
                    $"--serverPort={childServerPort} " +
                    $"--relayProtocol={relayProtocol} " +
                    $"--isLatest={(nextIsLatest ? 1 : 0)}";

                var psi = new System.Diagnostics.ProcessStartInfo(exePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory
                };

                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DedicatedMatchServerBootstrap] SpawnNextMatch failed: " + e.Message);
            }
        }
    }
}

