using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TitanOrbit.ECS;
using TitanOrbit.Services;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>Session orchestration replacing NGO NetworkGameManager + DedicatedMatchServerBootstrap.</summary>
    public class TitanOrbitSessionManager : MonoBehaviour
    {
        public static TitanOrbitSessionManager Instance { get; private set; }

        public static bool PendingLanHost { get; set; }

        const ushort DefaultServerPort = 7777;

        [SerializeField] int maxPlayers = 60;
        [SerializeField] ushort serverPort = DefaultServerPort;

        const string LobbyRelayCodeKey = "RelayJoinCode";
        const string LobbyGameNameKey = "GameName";
        const string LobbyGameNameValue = "TitanOrbit";
        const string LobbyIsOpenKey = "IsOpen";
        const string LobbyIsLatestKey = "IsLatest";
        const string LobbyCreatedAtEpochKey = "CreatedAtEpoch";
        const string LobbyServerAliveEpochKey = "ServerAliveAt";
        const string LobbyRelayProtocolKey = "RelayProtocol";
        const string LobbyServerListenAddressKey = "ServerListenAddress";
        const string LobbyActivePlayersKey = "ActivePlayers";

        static readonly SemaphoreSlim LobbyApiGate = new SemaphoreSlim(1, 1);

        string _activeLobbyId;
        Coroutine _connectWatch;

        public bool IsInGame { get; private set; }
        public string LastStatusMessage { get; private set; }
        public string CurrentLobbyId => _activeLobbyId;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            if (ShouldRunHeadlessServerBoot())
            {
#if UNITY_SERVER
                if (ShouldAutoBootDedicatedRelay())
                    StartCoroutine(BootDedicatedServer());
                else
                    StartCoroutine(BootMppmLanServer());
#endif
                Debug.Log("[TitanOrbitSessionManager] Headless server boot (no client UI flow).");
                return;
            }

            Debug.Log("[TitanOrbitSessionManager] Client play instance ready — press Play on the main menu to connect.");
        }

        static bool ShouldRunHeadlessServerBoot()
        {
#if UNITY_EDITOR
            return HasExplicitDedicatedServerArg();
#else
#if UNITY_SERVER
            return true;
#else
            return false;
#endif
#endif
        }

        static bool HasExplicitDedicatedServerArg()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "--titanOrbitDedicated")
                    return true;
            }
            return false;
        }

        static bool ShouldAutoBootDedicatedRelay()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "--titanOrbitDedicated")
                    return true;
            }
#if UNITY_EDITOR
            return false;
#else
            return true;
#endif
        }

        IEnumerator BootMppmLanServer()
        {
            float readyDeadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < readyDeadline)
            {
                var serverWorld = ClientServerBootstrap.ServerWorld;
                if (serverWorld != null && serverWorld.IsCreated &&
                    serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0)
                    break;
                yield return null;
            }

            TitanOrbitRelayState.Clear();
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null)
            {
                Debug.LogError("[TitanOrbitSessionManager] Server world missing for MPPM server player.");
                yield break;
            }

            // Bootstrap AutoConnectPort already listens; only mark connections in-game.
            RequestGoInGame(server);
            IsInGame = true;
            Debug.Log("[TitanOrbitSessionManager] Dedicated-server play instance ready (listening on port " + serverPort + "). Use the main Editor Game tab to play as client.");
        }

        bool _localBootRunning;

        IEnumerator MaintainClientSession()
        {
            float deadline = Time.realtimeSinceStartup + 45f;
            while (Time.realtimeSinceStartup < deadline && !HasClientInGame())
            {
                var client = ClientServerBootstrap.ClientWorld;
                if (client != null && client.IsCreated)
                {
                    if (!HasClientConnection(client))
                        ConnectLocalClient(serverPort);
                    else
                        RequestGoInGame(client);
                }

                if (HasClientInGame())
                {
                    IsInGame = true;
                    LastStatusMessage = "Connected.";
                    Debug.Log("[TitanOrbitSessionManager] Client in-game.");
                    yield break;
                }

                yield return null;
            }

            if (!HasClientInGame())
            {
                var client = ClientServerBootstrap.ClientWorld;
                var server = ClientServerBootstrap.ServerWorld;
                Debug.LogWarning("[TitanOrbitSessionManager] Client never reached in-game. client=" +
                                 (client != null && client.IsCreated ? client.Name : "missing") +
                                 " server=" + (server != null && server.IsCreated ? server.Name : "missing") +
                                 ". Press Play on the main Editor Game view, or disable the MPPM Server virtual player.");
            }
        }

        public void StartLocalPlay()
        {
            LastStatusMessage = "Starting local play...";
            if (_localBootRunning || HasClientInGame())
                return;
            StartCoroutine(BootLanHost());
        }

        public bool StartLanHostForLocalTest()
        {
            PendingLanHost = true;
            return true;
        }

        IEnumerator BootLanHost()
        {
            _localBootRunning = true;
            try
            {
            LastStatusMessage = "Waiting for NetCode worlds...";
            float readyDeadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < readyDeadline)
            {
                var clientWorld = ClientServerBootstrap.ClientWorld;
                if (clientWorld != null && clientWorld.IsCreated &&
                    clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).CalculateEntityCount() > 0)
                    break;
                yield return null;
            }

            TitanOrbitRelayState.Clear();

            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
            {
                LastStatusMessage = "ClientWorld missing. Use the main Editor Game view.";
                Debug.LogError("[TitanOrbitSessionManager] ClientWorld required to connect.");
                yield break;
            }

            var server = ClientServerBootstrap.ServerWorld;
            bool localHost = server != null && server.IsCreated;

            LastStatusMessage = localHost ? "Starting local host..." : "Connecting to game server...";
            if (localHost)
                ListenServer(server, serverPort);
            ConnectLocalClient(serverPort);

            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (HasClientConnection(client))
                    RequestGoInGame(client);

                if (HasClientInGame())
                {
                    if (localHost)
                        RequestGoInGame(server);
                    IsInGame = true;
                    LastStatusMessage = "Connected.";
                    Debug.Log(localHost
                        ? "[TitanOrbitSessionManager] Local Client+Server connected."
                        : "[TitanOrbitSessionManager] Connected to game server on port " + serverPort + ".");
                    yield break;
                }

                if (localHost && HasLocalConnection(server, client))
                {
                    RequestGoInGame(server);
                    RequestGoInGame(client);
                }

                yield return null;
            }

            LastStatusMessage = "Connection timed out. Is port 7777 in use? Try disabling the MPPM Server player.";
            Debug.LogError("[TitanOrbitSessionManager] Timed out waiting for network connection.");
            }
            finally
            {
                _localBootRunning = false;
            }
        }

        static bool HasClientConnection(World client)
        {
            if (client == null || !client.IsCreated) return false;
            return client.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0;
        }

        static bool HasClientInGame()
        {
            return HasNetworkStreamInGame(ClientServerBootstrap.ClientWorld);
        }

        static bool HasLocalConnection(World server, World client)
        {
            if (server != null && server.IsCreated &&
                server.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0)
                return true;
            if (client != null && client.IsCreated &&
                client.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0)
                return true;
            return false;
        }

        IEnumerator BootDedicatedServer()
        {
            yield return null;
            Task<DedicatedServerPrep> prepTask = PrepareDedicatedServerAsync();
            while (!prepTask.IsCompleted) yield return null;
            if (prepTask.IsFaulted || prepTask.Result == null)
            {
                if (prepTask.Exception != null)
                    Debug.LogError("[TitanOrbitSessionManager] " + prepTask.Exception.GetBaseException());
                else
                    Debug.LogError("[TitanOrbitSessionManager] Dedicated server boot failed.");
                yield break;
            }

            var prep = prepTask.Result;
            TitanOrbitRelayState.SetServerRelay(prep.Relay);

            var serverWorld = ClientServerBootstrap.ServerWorld;
            if (serverWorld == null || !serverWorld.IsCreated)
            {
                Debug.LogError("Server world missing.");
                yield break;
            }

            yield return ClearNetworkConnections(serverWorld);

            try
            {
                ResetServerDriverIfNeeded();
                ListenServer(serverWorld, serverPort);
                RequestGoInGame(serverWorld);
                _activeLobbyId = prep.Lobby.Id;
                StartCoroutine(LobbyHeartbeatLoop());
                IsInGame = true;
                Debug.Log("[TitanOrbitSessionManager] Dedicated server live. Relay=" + prep.JoinCode + " Lobby=" + prep.Lobby.Id);
            }
            catch (Exception ex)
            {
                Debug.LogError("[TitanOrbitSessionManager] " + ex);
            }
        }

        sealed class DedicatedServerPrep
        {
            public RelayServerData Relay;
            public string JoinCode;
            public Lobby Lobby;
        }

        async Task<DedicatedServerPrep> PrepareDedicatedServerAsync()
        {
            if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                return null;

            int cap = Mathf.Max(2, maxPlayers);
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(Mathf.Max(1, cap - 1));
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            string protocol = TitanOrbitRelayUtility.ConnectionTypeForPlatform();
            long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lobby = await CreateDedicatedLobbyAsync(joinCode, protocol, createdAt, cap);
            return new DedicatedServerPrep
            {
                Relay = TitanOrbitRelayUtility.FromAllocation(allocation, protocol),
                JoinCode = joinCode,
                Lobby = lobby,
            };
        }

        static IEnumerator ClearNetworkConnections(World world)
        {
            if (world == null || !world.IsCreated) yield break;
            var em = world.EntityManager;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                using var connections = em.CreateEntityQuery(typeof(NetworkStreamConnection)).ToEntityArray(Allocator.Temp);
                if (connections.Length == 0)
                    yield break;

                for (int i = 0; i < connections.Length; i++)
                {
                    if (!em.HasComponent<NetworkStreamRequestDisconnect>(connections[i]))
                        em.AddComponent<NetworkStreamRequestDisconnect>(connections[i]);
                }

                world.Update();
                yield return null;
            }
        }

        public async Task<bool> JoinDedicatedLobbyAsync(string lobbyId)
        {
            try
            {
                if (!await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync())
                    return false;

                await AcquireLobbyApiGateAsync();
                Lobby lobby;
                try
                {
                    lobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobbyId);
                }
                finally
                {
                    LobbyApiGate.Release();
                }

                if (!lobby.Data.TryGetValue(LobbyRelayCodeKey, out var relayData))
                {
                    Debug.LogError("Lobby missing relay join code.");
                    return false;
                }

                string joinCode = relayData.Value;
                string protocol = lobby.Data.TryGetValue(LobbyRelayProtocolKey, out var proto)
                    ? proto.Value
                    : TitanOrbitRelayUtility.ConnectionTypeForPlatform();

                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
                TitanOrbitRelayState.SetClientRelay(TitanOrbitRelayUtility.FromJoinAllocation(joinAllocation, protocol));
                ResetClientDriverIfNeeded();

                var clientWorld = ClientServerBootstrap.ClientWorld;
                if (clientWorld == null)
                {
                    Debug.LogError("Client world missing.");
                    return false;
                }

                ConnectRelayClient(clientWorld);
                RequestGoInGame(clientWorld);
                _activeLobbyId = lobby.Id;
                _connectWatch = StartCoroutine(ClientConnectWatch(60f));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[TitanOrbitSessionManager] Join failed: " + ex.Message);
                return false;
            }
        }

        async Task<Lobby> CreateDedicatedLobbyAsync(string joinCode, string protocol, long createdAt, int cap)
        {
            await AcquireLobbyApiGateAsync();
            try
            {
                return await LobbyService.Instance.CreateLobbyAsync(
                    "TitanOrbit-" + createdAt.ToString(CultureInfo.InvariantCulture),
                    cap,
                    new CreateLobbyOptions
                    {
                        IsPrivate = false,
                        Data = new Dictionary<string, DataObject>
                        {
                            { LobbyRelayCodeKey, new DataObject(DataObject.VisibilityOptions.Member, joinCode) },
                            { LobbyGameNameKey, new DataObject(DataObject.VisibilityOptions.Public, LobbyGameNameValue, DataObject.IndexOptions.S1) },
                            { LobbyIsOpenKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N1) },
                            { LobbyIsLatestKey, new DataObject(DataObject.VisibilityOptions.Public, "1", DataObject.IndexOptions.N2) },
                            { LobbyCreatedAtEpochKey, new DataObject(DataObject.VisibilityOptions.Public, createdAt.ToString(CultureInfo.InvariantCulture), DataObject.IndexOptions.N3) },
                            { LobbyServerAliveEpochKey, new DataObject(DataObject.VisibilityOptions.Public, createdAt.ToString(CultureInfo.InvariantCulture)) },
                            { LobbyRelayProtocolKey, new DataObject(DataObject.VisibilityOptions.Public, protocol) },
                            { LobbyServerListenAddressKey, new DataObject(DataObject.VisibilityOptions.Public, "dedicated") },
                            { LobbyActivePlayersKey, new DataObject(DataObject.VisibilityOptions.Public, "0", DataObject.IndexOptions.N4) },
                        }
                    });
            }
            finally
            {
                LobbyApiGate.Release();
            }
        }

        IEnumerator LobbyHeartbeatLoop()
        {
            var wait = new WaitForSeconds(15f);
            while (true)
            {
                if (!string.IsNullOrEmpty(_activeLobbyId))
                {
                    Task heartbeat = SendHeartbeatAsync();
                    while (!heartbeat.IsCompleted) yield return null;
                }
                yield return wait;
            }
        }

        async Task SendHeartbeatAsync()
        {
            try
            {
                await AcquireLobbyApiGateAsync();
                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await LobbyService.Instance.SendHeartbeatPingAsync(_activeLobbyId);
                    await LobbyService.Instance.UpdateLobbyAsync(_activeLobbyId, new UpdateLobbyOptions
                    {
                        Data = new Dictionary<string, DataObject>
                        {
                            { LobbyServerAliveEpochKey, new DataObject(DataObject.VisibilityOptions.Public, now.ToString(CultureInfo.InvariantCulture)) },
                        }
                    });
                }
                finally
                {
                    LobbyApiGate.Release();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TitanOrbitSessionManager] Heartbeat failed: " + ex.Message);
            }
        }

        IEnumerator ClientConnectWatch(float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (HasNetworkStreamInGame(ClientServerBootstrap.ClientWorld))
                {
                    IsInGame = true;
                    yield break;
                }
                yield return null;
            }
            Debug.LogError("[TitanOrbitSessionManager] Client connect watchdog timed out.");
        }

        static bool HasNetworkStreamInGame(World world)
        {
            if (world == null || !world.IsCreated) return false;
            return world.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame)).CalculateEntityCount() > 0;
        }

        static void RequestGoInGame(World world)
        {
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            using var connections = em.CreateEntityQuery(typeof(NetworkStreamConnection))
                .ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; i++)
            {
                if (!em.HasComponent<NetworkStreamInGame>(connections[i]))
                    em.AddComponent<NetworkStreamInGame>(connections[i]);
            }
        }

        static bool IsServerListening(World world)
        {
            if (world == null || !world.IsCreated) return false;
            using var query = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            if (!query.TryGetSingleton<NetworkStreamDriver>(out var driver)) return false;
            ref var store = ref driver.DriverStore;
            for (int i = store.FirstDriver; i < store.LastDriver; ++i)
            {
                if (store.GetDriverInstanceRO(i).driver.Listening)
                    return true;
            }
            return false;
        }

        static void ListenServer(World world, ushort port)
        {
            if (IsServerListening(world))
                return;
            var driver = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingletonRW<NetworkStreamDriver>();
            driver.ValueRW.Listen(NetworkEndpoint.AnyIpv4.WithPort(port));
        }

        static void ConnectLocalClient(ushort port)
        {
            var clientWorld = ClientServerBootstrap.ClientWorld;
            if (clientWorld == null || !clientWorld.IsCreated) return;
            var em = clientWorld.EntityManager;
            if (em.CreateEntityQuery(typeof(NetworkStreamConnection)).CalculateEntityCount() > 0)
                return;
            var driver = em.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingletonRW<NetworkStreamDriver>();
            driver.ValueRW.Connect(em, NetworkEndpoint.LoopbackIpv4.WithPort(port));
        }

        static void ConnectRelayClient(World world)
        {
            if (!TitanOrbitRelayState.TryGetClientRelay(out var relay))
                return;
            var driver = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingletonRW<NetworkStreamDriver>();
            driver.ValueRW.Connect(world.EntityManager, relay.Endpoint);
        }

        static void ResetServerDriverIfNeeded()
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated) return;
            var driverEntity = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            if (!driverEntity.TryGetSingletonEntity<NetworkStreamDriver>(out var entity)) return;
            var driver = world.EntityManager.GetComponentData<NetworkStreamDriver>(entity);
            var store = new NetworkDriverStore();
            var netDebug = world.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
            new TitanOrbitRelayDriverConstructor().CreateServerDriver(world, ref store, netDebug);
            driver.ResetDriverStore(world.Unmanaged, ref store);
        }

        static void ResetClientDriverIfNeeded()
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated) return;
            var driverEntity = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            if (!driverEntity.TryGetSingletonEntity<NetworkStreamDriver>(out var entity)) return;
            var driver = world.EntityManager.GetComponentData<NetworkStreamDriver>(entity);
            var store = new NetworkDriverStore();
            var netDebug = world.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
            new TitanOrbitRelayDriverConstructor().CreateClientDriver(world, ref store, netDebug);
            driver.ResetDriverStore(world.Unmanaged, ref store);
        }

        static async Task AcquireLobbyApiGateAsync() => await LobbyApiGate.WaitAsync();

        public void RequestTeam(TitanOrbit.Core.TeamId team)
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogError("[TitanOrbitSessionManager] RequestTeam failed: ClientWorld is missing.");
                return;
            }

            if (!HasNetworkStreamInGame(world))
            {
                Debug.LogError("[TitanOrbitSessionManager] RequestTeam failed: client is not in-game yet. Wait for 'Client in-game' in the console.");
                return;
            }

            if (!HasClientConnection(world))
            {
                Debug.LogError("[TitanOrbitSessionManager] RequestTeam failed: no network connection on ClientWorld.");
                return;
            }

            var em = world.EntityManager;
            int networkId = GetLocalNetworkId(world);
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new RequestTeamCommand
            {
                NetworkId = networkId,
                RequestedTeam = (byte)team,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            Debug.Log($"[TitanOrbitSessionManager] RequestTeam {team} (networkId={networkId}).");
        }

        static int GetLocalNetworkId(World world)
        {
            var em = world.EntityManager;
            using var inGame = em.CreateEntityQuery(
                    typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            if (inGame.Length > 0)
                return inGame[0].Value;

            using var ids = em.CreateEntityQuery(typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : 0;
        }
    }
}
