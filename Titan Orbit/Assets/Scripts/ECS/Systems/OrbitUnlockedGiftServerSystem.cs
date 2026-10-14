using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.Networking;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] On a server connection after that client says it is a signed-in
    /// Unity account (Google or username/password). Guests never get this.
    /// Removed when the connection entity is destroyed.
    /// </summary>
    public struct SessionDurableAccount : IComponentData { }

    /// <summary>
    /// [NETCODE] The gift button notice was already sent to this connection.
    /// Stops a second bind from showing the button twice.
    /// </summary>
    public struct GiftAdminAccessSent : IComponentData { }

    /// <summary>
    /// Server: lets an allowlisted Unity account gift Orbit Unlocked to someone
    /// in the same match. The gift is one HTTPS call to the checkout Worker,
    /// which writes the same Cloud Save flag a Stripe payment writes.
    /// World: ServerSimulation. Not a per-ship scan. The web call starts on the
    /// click and is polled until it finishes. A second click waits.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SessionPlayerIdServerSystem))]
    public partial struct OrbitUnlockedGiftServerSystem : ISystem
    {
        /// <summary>0 the Worker call failed or the secret is missing.</summary>
        public const byte StatusFailed = 0;

        /// <summary>1 Cloud Save now has orbit_unlocked for that player.</summary>
        public const byte StatusGranted = 1;

        /// <summary>2 that player already owned it. The purchase note is left alone.</summary>
        public const byte StatusAlready = 2;

        /// <summary>3 the previous gift's web call has not finished.</summary>
        public const byte StatusBusy = 3;

        const int MaxRoster = 32;

        EntityQuery _durableRpcs;
        EntityQuery _rosterRpcs;
        EntityQuery _giftRpcs;

        /// <summary>[ECS/DOTS] Cache the three client commands this system drains.</summary>
        public void OnCreate(ref SystemState state)
        {
            _durableRpcs = state.GetEntityQuery(
                ComponentType.ReadOnly<SessionDurableAccountCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _rosterRpcs = state.GetEntityQuery(
                ComponentType.ReadOnly<RequestGiftRosterCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _giftRpcs = state.GetEntityQuery(
                ComponentType.ReadOnly<GiftOrbitUnlockedCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            OrbitUnlockedAdminGrantTransport.Reset();
        }

        /// <summary>[UNITY] Domain Reload off: drop a half-finished web call between Play sessions.</summary>
        public void OnDestroy(ref SystemState state)
        {
            OrbitUnlockedAdminGrantTransport.Reset();
        }

        /// <summary>
        /// Polls one in-flight gift, then drains sign-in, roster, and gift commands.
        /// Empty ticks only look at those queries.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            if (OrbitUnlockedAdminGrantTransport.TryTakeResult(out int targetNetworkId, out byte status))
                FinishGrant(em, targetNetworkId, status);

            if (!_durableRpcs.IsEmpty)
                ConsumeDurable(ref state, em);
            if (!_rosterRpcs.IsEmpty)
            {
                bool hasNames = SystemAPI.TryGetSingletonBuffer<PlayerNameElement>(out var names);
                ConsumeRoster(ref state, em, hasNames, names);
            }
            if (!_giftRpcs.IsEmpty)
                ConsumeGifts(ref state, em);
        }

        /// <summary>
        /// Called after a connection's Unity player id is stored. If that player
        /// is an admin and has already marked the account as signed in, send the
        /// gift-button notice once.
        /// </summary>
        public static void NotifyPlayerBound(EntityManager em, int networkId)
        {
            if (!TryFindConnection(em, networkId, out Entity connection))
                return;
            TrySendAdminAccess(em, connection, networkId);
        }

        /// <summary>Marks the sending connection as a real Unity account.</summary>
        void ConsumeDurable(ref SystemState state, EntityManager em)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var seen = new NativeList<Entity>(4, Allocator.Temp);
            foreach (var (req, entity) in SystemAPI
                         .Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<SessionDurableAccountCommand>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                Entity connection = req.ValueRO.SourceConnection;
                if (!em.Exists(connection) || !em.HasComponent<NetworkId>(connection))
                    continue;
                if (ContainsEntity(seen, connection))
                    continue;
                seen.Add(connection);

                if (!em.HasComponent<SessionDurableAccount>(connection))
                    ecb.AddComponent<SessionDurableAccount>(connection);

                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                if (networkId > 0 &&
                    !em.HasComponent<GiftAdminAccessSent>(connection) &&
                    IsAdminNetwork(networkId))
                {
                    // The id is already bound. The sign-in tag is added above in
                    // this same buffer, before the button notice is created.
                    ecb.AddComponent<GiftAdminAccessSent>(connection);
                    QueueAdminAccess(ecb, connection, networkId);
                }
            }

            seen.Dispose();

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>One roster snapshot for the admin who asked. Not repeated next tick.</summary>
        void ConsumeRoster(
            ref SystemState state,
            EntityManager em,
            bool hasNames,
            DynamicBuffer<PlayerNameElement> names)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<RequestGiftRosterCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                Entity connection = req.ValueRO.SourceConnection;
                if (!em.Exists(connection) || !em.HasComponent<NetworkId>(connection))
                    continue;
                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                if (!IsAdminConnection(em, connection, networkId))
                    continue;

                SendRoster(em, ecb, connection, networkId, cmd.ValueRO.RequestId, hasNames, names);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>Starts one Worker call, or tells the admin to wait.</summary>
        void ConsumeGifts(ref SystemState state, EntityManager em)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<GiftOrbitUnlockedCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                Entity adminConnection = req.ValueRO.SourceConnection;
                if (!em.Exists(adminConnection) || !em.HasComponent<NetworkId>(adminConnection))
                    continue;

                int adminNetworkId = em.GetComponentData<NetworkId>(adminConnection).Value;
                if (!IsAdminConnection(em, adminConnection, adminNetworkId))
                    continue;

                int targetNetworkId = cmd.ValueRO.TargetNetworkId;
                if (!TryResolveGift(em, adminNetworkId, targetNetworkId, out string targetPlayerId, out string grantedBy))
                {
                    QueueResult(ecb, adminConnection, adminNetworkId, targetNetworkId, StatusFailed);
                    continue;
                }

                if (OrbitUnlockedAdminGrantTransport.IsBusy)
                {
                    QueueResult(ecb, adminConnection, adminNetworkId, targetNetworkId, StatusBusy);
                    continue;
                }

                if (!OrbitUnlockedAdminGrantTransport.TryBegin(targetPlayerId, grantedBy, targetNetworkId, adminNetworkId))
                    QueueResult(ecb, adminConnection, adminNetworkId, targetNetworkId, StatusFailed);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>Tells the admin how the Worker answered, and tells the player to read Cloud Save.</summary>
        static void FinishGrant(EntityManager em, int targetNetworkId, byte status)
        {
            int adminNetworkId = OrbitUnlockedAdminGrantTransport.AdminNetworkId;
            if (TryFindConnection(em, adminNetworkId, out Entity adminConnection))
            {
                var ecb = new EntityCommandBuffer(Allocator.Temp);
                QueueResult(ecb, adminConnection, adminNetworkId, targetNetworkId, status);
                if (status == StatusGranted || status == StatusAlready)
                {
                    if (TryFindConnection(em, targetNetworkId, out Entity targetConnection))
                        QueueGifted(ecb, targetConnection, targetNetworkId);
                }

                ecb.Playback(em);
                ecb.Dispose();
            }
            else if ((status == StatusGranted || status == StatusAlready) &&
                     TryFindConnection(em, targetNetworkId, out Entity targetOnly))
            {
                var ecb = new EntityCommandBuffer(Allocator.Temp);
                QueueGifted(ecb, targetOnly, targetNetworkId);
                ecb.Playback(em);
                ecb.Dispose();
            }
        }

        /// <summary>
        /// True when this network id is on the admin list and the connection said
        /// it is a signed-in account. The list is the server environment variable.
        /// </summary>
        static bool IsAdminConnection(EntityManager em, Entity connection, int networkId)
        {
            if (!em.Exists(connection) || !em.HasComponent<SessionDurableAccount>(connection))
                return false;
            return IsAdminNetwork(networkId);
        }

        static bool IsAdminNetwork(int networkId)
        {
            if (!MatchPlayerShipStore.TryGetPlayerId(networkId, out string playerId))
                return false;
            return OrbitUnlockedAdminAllowlist.Contains(playerId);
        }

        /// <summary>
        /// Resolves both Unity player ids. Fails for guests, a missing ship binding,
        /// or an id the Worker would reject.
        /// </summary>
        static bool TryResolveGift(
            EntityManager em,
            int adminNetworkId,
            int targetNetworkId,
            out string targetPlayerId,
            out string grantedBy)
        {
            targetPlayerId = null;
            grantedBy = null;
            if (targetNetworkId <= 0)
                return false;
            if (!TryFindConnection(em, targetNetworkId, out Entity target) ||
                !em.HasComponent<SessionDurableAccount>(target))
                return false;
            if (!MatchPlayerShipStore.TryGetPlayerId(targetNetworkId, out targetPlayerId) ||
                !MatchPlayerShipStore.TryGetPlayerId(adminNetworkId, out grantedBy))
                return false;
            return IsPlausiblePlayerId(targetPlayerId) && IsPlausiblePlayerId(grantedBy);
        }

        static void TrySendAdminAccess(EntityManager em, Entity connection, int networkId)
        {
            if (!em.Exists(connection) || em.HasComponent<GiftAdminAccessSent>(connection))
                return;
            if (!IsAdminConnection(em, connection, networkId))
                return;

            em.AddComponent<GiftAdminAccessSent>(connection);
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, new GiftAdminAccessRpc());
            em.AddComponentData(rpc, new SendRpcCommandRequest { TargetConnection = connection });
            ApplyLocalAdminAccess(networkId);
        }

        static void QueueAdminAccess(EntityCommandBuffer ecb, Entity connection, int networkId)
        {
            Entity rpc = ecb.CreateEntity();
            ecb.AddComponent(rpc, new GiftAdminAccessRpc());
            ecb.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = connection });
            ApplyLocalAdminAccess(networkId);
        }

        static void SendRoster(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity adminConnection,
            int adminNetworkId,
            byte requestId,
            bool hasNames,
            DynamicBuffer<PlayerNameElement> names)
        {
            var rows = new NativeList<GiftRosterEntryRpc>(8, Allocator.Temp);
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            using var connections = query.ToEntityArray(Allocator.Temp);
            using var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp);

            for (int i = 0; i < ids.Length && rows.Length < MaxRoster; i++)
            {
                int networkId = ids[i].Value;
                if (networkId <= 0)
                    continue;
                rows.Add(new GiftRosterEntryRpc
                {
                    RequestId = requestId,
                    NetworkId = networkId,
                    DisplayName = FindName(hasNames, names, networkId),
                    SignedIn = em.HasComponent<SessionDurableAccount>(connections[i]) ? (byte)1 : (byte)0,
                });
            }

            if (rows.Length == 0)
            {
                QueueRosterEntry(ecb, adminConnection, adminNetworkId, new GiftRosterEntryRpc
                {
                    RequestId = requestId,
                    Count = 0,
                });
            }
            else
            {
                byte count = (byte)rows.Length;
                for (int i = 0; i < rows.Length; i++)
                {
                    GiftRosterEntryRpc row = rows[i];
                    row.Index = (byte)i;
                    row.Count = count;
                    QueueRosterEntry(ecb, adminConnection, adminNetworkId, row);
                }
            }

            rows.Dispose();
        }

        static FixedString64Bytes FindName(bool hasNames, DynamicBuffer<PlayerNameElement> names, int networkId)
        {
            if (!hasNames)
                return default;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].NetworkId == networkId)
                    return names[i].DisplayName;
            }

            return default;
        }

        static void QueueRosterEntry(
            EntityCommandBuffer ecb,
            Entity adminConnection,
            int adminNetworkId,
            GiftRosterEntryRpc row)
        {
            Entity rpc = ecb.CreateEntity();
            ecb.AddComponent(rpc, row);
            ecb.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = adminConnection });
            ApplyLocalRoster(adminNetworkId, row);
        }

        static void QueueResult(
            EntityCommandBuffer ecb,
            Entity adminConnection,
            int adminNetworkId,
            int targetNetworkId,
            byte status)
        {
            Entity rpc = ecb.CreateEntity();
            ecb.AddComponent(rpc, new GiftOrbitUnlockedResultRpc
            {
                TargetNetworkId = targetNetworkId,
                Status = status,
            });
            ecb.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = adminConnection });
            ApplyLocalResult(adminNetworkId, targetNetworkId, status);
        }

        static void QueueGifted(EntityCommandBuffer ecb, Entity targetConnection, int targetNetworkId)
        {
            Entity rpc = ecb.CreateEntity();
            ecb.AddComponent(rpc, new OrbitUnlockedGiftedRpc());
            ecb.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = targetConnection });
            ApplyLocalGifted(targetNetworkId);
        }

        /// <summary>
        /// Local Host drops some server→client RPCs under load. The host client
        /// lives in this process, so write its panel state directly too.
        /// Dedicated servers have no client world and skip this.
        /// </summary>
        static bool IsLocalHostNetwork(int networkId)
        {
            var client = ClientServerBootstrap.ClientWorld;
            var server = ClientServerBootstrap.ServerWorld;
            if (client == null || !client.IsCreated || server == null || !server.IsCreated)
                return false;
            if (networkId <= 0)
                return false;

            // The host's own connection id lives on the client world. Reading it
            // here keeps this system inside the simulation assembly.
            var em = client.EntityManager;
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            if (query.IsEmptyIgnoreFilter)
                return false;
            using var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp);
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i].Value == networkId)
                    return true;
            }

            return false;
        }

        static void ApplyLocalAdminAccess(int networkId)
        {
            if (IsLocalHostNetwork(networkId))
                OrbitUnlockedGiftClientState.GrantAdmin();
        }

        static void ApplyLocalRoster(int adminNetworkId, GiftRosterEntryRpc row)
        {
            if (IsLocalHostNetwork(adminNetworkId))
                OrbitUnlockedGiftClientState.ApplyEntry(row);
        }

        static void ApplyLocalResult(int adminNetworkId, int targetNetworkId, byte status)
        {
            if (IsLocalHostNetwork(adminNetworkId))
                OrbitUnlockedGiftClientState.ApplyResult(targetNetworkId, status);
        }

        static void ApplyLocalGifted(int targetNetworkId)
        {
            if (!IsLocalHostNetwork(targetNetworkId))
                return;
            OrbitUnlockedGiftClientState.NoteGifted();
        }

        static bool ContainsEntity(NativeList<Entity> seen, Entity connection)
        {
            for (int i = 0; i < seen.Length; i++)
            {
                if (seen[i] == connection)
                    return true;
            }

            return false;
        }

        static bool TryFindConnection(EntityManager em, int networkId, out Entity connection)
        {
            connection = Entity.Null;
            if (networkId <= 0)
                return false;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp);
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i].Value != networkId)
                    continue;
                connection = entities[i];
                return true;
            }

            return false;
        }

        /// <summary>Same shape the Worker accepts: letters, digits, underscore, hyphen.</summary>
        static bool IsPlausiblePlayerId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128)
                return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'A' && c <= 'Z') ||
                          (c >= 'a' && c <= 'z') ||
                          (c >= '0' && c <= '9') ||
                          c == '_' || c == '-';
                if (!ok)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Unity player ids allowed to gift. Read once from
    /// <c>TITANORBIT_ADMIN_PLAYER_IDS</c> (comma-separated). Empty means nobody.
    /// The WebGL client never sees this list.
    /// </summary>
    static class OrbitUnlockedAdminAllowlist
    {
        static HashSet<string> _ids;
        static bool _loaded;

        /// <summary>[UNITY] Play Mode can change the environment between sessions.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _loaded = false;
            _ids = null;
        }

        public static bool Contains(string playerId)
        {
            if (!_loaded)
                Load();
            if (_ids == null || string.IsNullOrEmpty(playerId))
                return false;
            return _ids.Contains(playerId);
        }

        static void Load()
        {
            _loaded = true;
            _ids = new HashSet<string>(StringComparer.Ordinal);
            string raw = Environment.GetEnvironmentVariable("TITANORBIT_ADMIN_PLAYER_IDS");
            if (string.IsNullOrWhiteSpace(raw))
            {
                Debug.Log("[OrbitUnlockedGift] No admin player ids set. The gift button stays hidden.");
                return;
            }

            string[] parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string id = parts[i].Trim();
                if (id.Length > 0)
                    _ids.Add(id);
            }

            Debug.Log("[OrbitUnlockedGift] Admin allowlist count=" + _ids.Count + ".");
        }
    }

    /// <summary>
    /// One POST to the checkout Worker. Lives beside the server system because
    /// a Unity web request cannot sit inside an ECS component. At most one call
    /// at a time, started by a gift click.
    /// </summary>
    static class OrbitUnlockedAdminGrantTransport
    {
        const string GrantUrl = "https://titanorbit-checkout.jason-redhawk.workers.dev/admin/grant";

        static UnityWebRequest _request;
        static int _targetNetworkId;
        static int _adminNetworkId;
        static bool _busy;
        static bool _loggedMissingSecret;

        public static bool IsBusy => _busy;

        public static int AdminNetworkId => _adminNetworkId;

        /// <summary>Drops a request left over from the last Play Mode session.</summary>
        public static void Reset()
        {
            if (_request != null)
            {
                _request.Dispose();
                _request = null;
            }

            _busy = false;
            _loggedMissingSecret = false;
            _targetNetworkId = 0;
            _adminNetworkId = 0;
        }

        /// <summary>
        /// Starts the Worker call. False when a call is already running or the
        /// server secret is missing. Does not wait for the response.
        /// </summary>
        public static bool TryBegin(string targetPlayerId, string grantedBy, int targetNetworkId, int adminNetworkId)
        {
            if (_busy)
                return false;

            string secret = Environment.GetEnvironmentVariable("TITANORBIT_ADMIN_GRANT_SECRET");
            if (string.IsNullOrEmpty(secret))
            {
                if (!_loggedMissingSecret)
                {
                    _loggedMissingSecret = true;
                    Debug.LogWarning("[OrbitUnlockedGift] TITANORBIT_ADMIN_GRANT_SECRET is not set.");
                }

                return false;
            }

            // Both ids were checked to be free of quotes, so this JSON stays one object.
            string body = "{\"playerId\":\"" + targetPlayerId + "\",\"grantedBy\":\"" + grantedBy + "\"}";
            var request = new UnityWebRequest(GrantUrl, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + secret);
            request.SendWebRequest();

            _request = request;
            _targetNetworkId = targetNetworkId;
            _adminNetworkId = adminNetworkId;
            _busy = true;
            return true;
        }

        /// <summary>
        /// True on the tick the Worker answers. <paramref name="status"/> matches
        /// <see cref="OrbitUnlockedGiftServerSystem"/> status bytes.
        /// </summary>
        public static bool TryTakeResult(out int targetNetworkId, out byte status)
        {
            targetNetworkId = 0;
            status = OrbitUnlockedGiftServerSystem.StatusFailed;
            if (!_busy || _request == null || !_request.isDone)
                return false;

            targetNetworkId = _targetNetworkId;
            string text = _request.downloadHandler != null ? _request.downloadHandler.text : "";
            bool httpOk = _request.result == UnityWebRequest.Result.Success;
            if (httpOk && Contains(text, "\"alreadyOwned\":true"))
                status = OrbitUnlockedGiftServerSystem.StatusAlready;
            else if (httpOk && Contains(text, "\"ok\":true"))
                status = OrbitUnlockedGiftServerSystem.StatusGranted;
            else
            {
                status = OrbitUnlockedGiftServerSystem.StatusFailed;
                Debug.LogWarning(
                    "[OrbitUnlockedGift] Grant failed: " + _request.responseCode + " " + _request.error);
            }

            _request.Dispose();
            _request = null;
            _busy = false;
            return true;
        }

        static bool Contains(string text, string token)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            if (text.IndexOf(token, StringComparison.Ordinal) >= 0)
                return true;
            // The Worker may pretty-print a space after the colon.
            string spaced = token.Replace("\":", "\": ");
            return text.IndexOf(spaced, StringComparison.Ordinal) >= 0;
        }
    }
}
