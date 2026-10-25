using System;
using TitanOrbit;
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
    /// [NETCODE] On a server connection after we have asked the checkout Worker
    /// whether this Unity account owns the beta-tester item. Stops a second join
    /// bind from sending another lookup. Removed when the connection is destroyed.
    /// </summary>
    public struct BetaTesterLookupStarted : IComponentData { }

    /// <summary>
    /// [NETCODE] Server-only memory of one connection's beta tester state.
    /// Not a ghost. Other clients never see it. Purchases and field notes read it.
    /// </summary>
    public struct BetaTesterMatchState : IComponentData
    {
        /// <summary>1 after a Worker lookup, an in-match gift, or the local-host checkbox.</summary>
        public byte Owned;

        /// <summary>Bits the next purchase will honor. See <see cref="BetaTesterClientFlags"/>.</summary>
        public byte Flags;

        /// <summary>
        /// Last toggle request from the client. Copied into <see cref="Flags"/>
        /// once <see cref="Owned"/> is set, so a toggle that arrives early is not lost.
        /// </summary>
        public byte PendingFlags;
    }

    /// <summary>
    /// Reads <see cref="BetaTesterMatchState"/> for purchase and note checks.
    /// A lookup is not a scan of every ship. Callers already have a network id
    /// because a player just bought something or sent a note.
    /// </summary>
    public static class BetaTesterMatch
    {
        /// <summary>True when this connection has been verified as a beta tester.</summary>
        public static bool IsOwned(EntityManager em, Entity connection)
        {
            if (!em.Exists(connection) || !em.HasComponent<BetaTesterMatchState>(connection))
                return false;
            return em.GetComponentData<BetaTesterMatchState>(connection).Owned != 0;
        }

        /// <summary>
        /// True when this player owns beta tester and that toggle bit is on.
        /// False for everyone else, including a beta tester who left the switch off.
        /// </summary>
        public static bool Allows(EntityManager em, int networkId, byte flag)
        {
            if (!TryFindConnection(em, networkId, out Entity connection))
                return false;
            if (!em.HasComponent<BetaTesterMatchState>(connection))
                return false;
            BetaTesterMatchState state = em.GetComponentData<BetaTesterMatchState>(connection);
            return state.Owned != 0 && (state.Flags & flag) != 0;
        }

        /// <summary>The connection entity for a network id, or false when they have left.</summary>
        public static bool TryFindConnection(EntityManager em, int networkId, out Entity connection)
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
    }

    /// <summary>
    /// Server: verifies the beta-tester item once per connection, accepts that
    /// player's free-price toggles, and lets an admin gift the item.
    /// World: ServerSimulation. The Worker call is polled. It does not run every
    /// tick unless a lookup or a gift is in flight. A dedicated server never
    /// trusts the client's owned flag.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SessionPlayerIdServerSystem))]
    public partial struct BetaTesterServerSystem : ISystem
    {
        /// <summary>Set after the local-host checkbox has marked the host connection.</summary>
        bool _hostMarked;

        EntityQuery _toggleRpcs;
        EntityQuery _giftRpcs;
        EntityQuery _inGameConnections;

        /// <summary>[ECS/DOTS] Cache the client commands this system drains.</summary>
        public void OnCreate(ref SystemState state)
        {
            _toggleRpcs = state.GetEntityQuery(
                ComponentType.ReadOnly<SetBetaTesterTogglesCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _giftRpcs = state.GetEntityQuery(
                ComponentType.ReadOnly<GiftBetaTesterCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _inGameConnections = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            BetaTesterLookupTransport.Reset();
            BetaTesterGrantTransport.Reset();
            _hostMarked = false;
        }

        /// <summary>[UNITY] Domain Reload off: drop a half-finished web call between Play sessions.</summary>
        public void OnDestroy(ref SystemState state)
        {
            BetaTesterLookupTransport.Reset();
            BetaTesterGrantTransport.Reset();
            _hostMarked = false;
        }

        /// <summary>
        /// Polls one lookup and one gift, then drains toggle and gift commands.
        /// Empty ticks only look at those queries, plus the editor checkbox latch.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;

            // Editor / local host checkbox. Dedicated servers publish the flag off.
            if (TitanOrbitDebugFlags.BetaFeedbackEnabled && !_hostMarked)
                _hostMarked = TryMarkHost(em);
            else if (_hostMarked && _inGameConnections.IsEmptyIgnoreFilter)
                _hostMarked = false;

            if (BetaTesterLookupTransport.TryTakeResult(out int lookupNetworkId, out bool lookupOwned))
                ApplyLookup(em, lookupNetworkId, lookupOwned);
            BetaTesterLookupTransport.Pump();

            if (BetaTesterGrantTransport.TryTakeResult(out int targetNetworkId, out byte status))
                FinishGrant(em, targetNetworkId, status);

            if (!_toggleRpcs.IsEmpty)
                ConsumeToggles(ref state, em);
            if (!_giftRpcs.IsEmpty)
                ConsumeGifts(ref state, em);
        }

        /// <summary>
        /// Called after a connection's Unity player id is stored. Starts one
        /// Cloud Save lookup. A second bind does not send a second request.
        /// </summary>
        public static void NotifyPlayerBound(EntityManager em, int networkId)
        {
            if (!BetaTesterMatch.TryFindConnection(em, networkId, out Entity connection))
                return;
            if (em.HasComponent<BetaTesterLookupStarted>(connection))
                return;
            if (!MatchPlayerShipStore.TryGetPlayerId(networkId, out string playerId))
                return;
            if (!IsPlausiblePlayerId(playerId))
                return;

            em.AddComponent<BetaTesterLookupStarted>(connection);
            BetaTesterLookupTransport.Enqueue(networkId, playerId);
        }

        /// <summary>
        /// Marks the host connection owned without Cloud Save. Used so Editor
        /// field notes still work when the checkbox is on. Returns false until
        /// that connection exists, so the caller retries.
        /// </summary>
        bool TryMarkHost(EntityManager em)
        {
            if (_inGameConnections.IsEmptyIgnoreFilter)
                return false;

            using var entities = _inGameConnections.ToEntityArray(Allocator.Temp);
            using var ids = _inGameConnections.ToComponentDataArray<NetworkId>(Allocator.Temp);
            for (int i = 0; i < ids.Length; i++)
            {
                int networkId = ids[i].Value;
                if (!OrbitUnlockedGiftServerSystem.IsLocalHostNetwork(networkId))
                    continue;

                BetaTesterMatchState row = ReadState(em, entities[i]);
                row.Owned = 1;
                row.Flags = row.PendingFlags;
                WriteState(em, entities[i], row);
                SendSession(em, entities[i], networkId, row);
                return true;
            }

            return false;
        }

        /// <summary>
        /// A true lookup turns the item on and applies any toggle that arrived early.
        /// A false lookup does not clear an item already granted this match.
        /// </summary>
        static void ApplyLookup(EntityManager em, int networkId, bool owned)
        {
            if (!BetaTesterMatch.TryFindConnection(em, networkId, out Entity connection))
                return;

            BetaTesterMatchState row = ReadState(em, connection);
            if (owned)
            {
                row.Owned = 1;
                row.Flags = row.PendingFlags;
            }

            WriteState(em, connection, row);
            SendSession(em, connection, networkId, row);
        }

        /// <summary>Stores the toggle request. It affects prices only after ownership is verified.</summary>
        void ConsumeToggles(ref SystemState state, EntityManager em)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<SetBetaTesterTogglesCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                Entity connection = req.ValueRO.SourceConnection;
                if (!em.Exists(connection) || !em.HasComponent<NetworkId>(connection))
                    continue;

                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                bool has = em.HasComponent<BetaTesterMatchState>(connection);
                BetaTesterMatchState row = has
                    ? em.GetComponentData<BetaTesterMatchState>(connection)
                    : default;
                row.PendingFlags = (byte)(cmd.ValueRO.Flags & BetaTesterClientFlags.Mask);
                if (row.Owned != 0)
                    row.Flags = row.PendingFlags;

                if (has)
                    ecb.SetComponent(connection, row);
                else
                    ecb.AddComponent(connection, row);

                QueueSession(ecb, connection, networkId, row);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>Starts one Worker grant, or tells the admin to wait.</summary>
        void ConsumeGifts(ref SystemState state, EntityManager em)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<GiftBetaTesterCommand>, RefRO<ReceiveRpcCommandRequest>>()
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
                    QueueResult(ecb, adminConnection, adminNetworkId, targetNetworkId, OrbitUnlockedGiftServerSystem.StatusFailed);
                    continue;
                }

                if (BetaTesterGrantTransport.IsBusy)
                {
                    QueueResult(ecb, adminConnection, adminNetworkId, targetNetworkId, OrbitUnlockedGiftServerSystem.StatusBusy);
                    continue;
                }

                if (!BetaTesterGrantTransport.TryBegin(targetPlayerId, grantedBy, targetNetworkId, adminNetworkId))
                    QueueResult(ecb, adminConnection, adminNetworkId, targetNetworkId, OrbitUnlockedGiftServerSystem.StatusFailed);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>Tells the admin how the Worker answered, and turns the item on for the target now.</summary>
        static void FinishGrant(EntityManager em, int targetNetworkId, byte status)
        {
            int adminNetworkId = BetaTesterGrantTransport.AdminNetworkId;
            bool granted = status == OrbitUnlockedGiftServerSystem.StatusGranted ||
                           status == OrbitUnlockedGiftServerSystem.StatusAlready;
            Entity targetConnection = Entity.Null;
            BetaTesterMatchState row = default;
            if (granted && BetaTesterMatch.TryFindConnection(em, targetNetworkId, out targetConnection))
            {
                // Turn the item on before the command buffer, so the structural
                // change does not invalidate entities the buffer is about to record.
                row = ReadState(em, targetConnection);
                row.Owned = 1;
                row.Flags = row.PendingFlags;
                WriteState(em, targetConnection, row);
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            if (BetaTesterMatch.TryFindConnection(em, adminNetworkId, out Entity adminConnection))
                QueueResult(ecb, adminConnection, adminNetworkId, targetNetworkId, status);
            if (granted && targetConnection != Entity.Null && em.Exists(targetConnection))
            {
                QueueGifted(ecb, targetConnection, targetNetworkId);
                QueueSession(ecb, targetConnection, targetNetworkId, row);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        static bool IsAdminConnection(EntityManager em, Entity connection, int networkId)
        {
            if (!em.Exists(connection) || !em.HasComponent<SessionDurableAccount>(connection))
                return false;
            if (!MatchPlayerShipStore.TryGetPlayerId(networkId, out string playerId))
                return false;
            return OrbitUnlockedAdminAllowlist.Contains(playerId);
        }

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
            if (!BetaTesterMatch.TryFindConnection(em, targetNetworkId, out Entity target) ||
                !em.HasComponent<SessionDurableAccount>(target))
                return false;
            if (!MatchPlayerShipStore.TryGetPlayerId(targetNetworkId, out targetPlayerId) ||
                !MatchPlayerShipStore.TryGetPlayerId(adminNetworkId, out grantedBy))
                return false;
            return IsPlausiblePlayerId(targetPlayerId) && IsPlausiblePlayerId(grantedBy);
        }

        static BetaTesterMatchState ReadState(EntityManager em, Entity connection)
        {
            if (!em.HasComponent<BetaTesterMatchState>(connection))
                return default;
            return em.GetComponentData<BetaTesterMatchState>(connection);
        }

        static void WriteState(EntityManager em, Entity connection, BetaTesterMatchState row)
        {
            if (em.HasComponent<BetaTesterMatchState>(connection))
                em.SetComponentData(connection, row);
            else
                em.AddComponentData(connection, row);
        }

        static void SendSession(EntityManager em, Entity connection, int networkId, BetaTesterMatchState row)
        {
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, new BetaTesterSessionRpc { Owned = row.Owned, Flags = row.Flags });
            em.AddComponentData(rpc, new SendRpcCommandRequest { TargetConnection = connection });
            ApplyLocalSession(networkId, row);
        }

        static void QueueSession(EntityCommandBuffer ecb, Entity connection, int networkId, BetaTesterMatchState row)
        {
            Entity rpc = ecb.CreateEntity();
            ecb.AddComponent(rpc, new BetaTesterSessionRpc { Owned = row.Owned, Flags = row.Flags });
            ecb.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = connection });
            ApplyLocalSession(networkId, row);
        }

        static void QueueResult(
            EntityCommandBuffer ecb,
            Entity adminConnection,
            int adminNetworkId,
            int targetNetworkId,
            byte status)
        {
            Entity rpc = ecb.CreateEntity();
            ecb.AddComponent(rpc, new GiftBetaTesterResultRpc
            {
                TargetNetworkId = targetNetworkId,
                Status = status,
            });
            ecb.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = adminConnection });
            if (OrbitUnlockedGiftServerSystem.IsLocalHostNetwork(adminNetworkId))
                OrbitUnlockedGiftClientState.ApplyBetaResult(targetNetworkId, status);
        }

        static void QueueGifted(EntityCommandBuffer ecb, Entity targetConnection, int targetNetworkId)
        {
            Entity rpc = ecb.CreateEntity();
            ecb.AddComponent(rpc, new BetaTesterGiftedRpc());
            ecb.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = targetConnection });
            if (OrbitUnlockedGiftServerSystem.IsLocalHostNetwork(targetNetworkId))
                OrbitUnlockedGiftClientState.NoteBetaTesterGifted();
        }

        /// <summary>
        /// Local Host drops some server→client RPCs. The host client lives in
        /// this process, so write its price flags directly too.
        /// </summary>
        static void ApplyLocalSession(int networkId, BetaTesterMatchState row)
        {
            if (!OrbitUnlockedGiftServerSystem.IsLocalHostNetwork(networkId))
                return;
            BetaTesterClientFlags.ApplyServer(row.Owned != 0, row.Flags);
        }

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
    /// One POST to <c>/admin/lookup</c>. At most one call at a time. Further
    /// joins wait in a short queue. A missing secret fails closed (not owned).
    /// </summary>
    static class BetaTesterLookupTransport
    {
        const string LookupUrl = "https://titanorbit-checkout.jason-redhawk.workers.dev/admin/lookup";
        const int MaxQueue = 16;

        struct Pending
        {
            public int NetworkId;
            public string PlayerId;
        }

        static readonly List<Pending> Queue = new List<Pending>(8);
        static UnityWebRequest _request;
        static int _activeNetworkId;
        static bool _busy;
        static bool _loggedMissingSecret;

        public static void Reset()
        {
            if (_request != null)
            {
                _request.Dispose();
                _request = null;
            }

            Queue.Clear();
            _busy = false;
            _activeNetworkId = 0;
            _loggedMissingSecret = false;
        }

        /// <summary>Adds a join lookup. Duplicate network ids are ignored.</summary>
        public static void Enqueue(int networkId, string playerId)
        {
            for (int i = 0; i < Queue.Count; i++)
            {
                if (Queue[i].NetworkId == networkId)
                    return;
            }

            if (_busy && _activeNetworkId == networkId)
                return;
            if (Queue.Count >= MaxQueue)
                return;

            Queue.Add(new Pending { NetworkId = networkId, PlayerId = playerId });
        }

        /// <summary>Starts the next lookup when the previous web call has finished.</summary>
        public static void Pump()
        {
            if (_busy || Queue.Count == 0)
                return;

            Pending next = Queue[0];
            Queue.RemoveAt(0);
            if (!TryBegin(next.PlayerId, next.NetworkId))
            {
                // Fail closed: no result to apply, so the connection stays not owned.
                Debug.LogWarning("[BetaTester] Lookup did not start for network " + next.NetworkId);
            }
        }

        static bool TryBegin(string playerId, int networkId)
        {
            string secret = Environment.GetEnvironmentVariable("TITANORBIT_ADMIN_GRANT_SECRET");
            if (string.IsNullOrEmpty(secret))
            {
                if (!_loggedMissingSecret)
                {
                    _loggedMissingSecret = true;
                    Debug.LogWarning("[BetaTester] TITANORBIT_ADMIN_GRANT_SECRET is not set. Lookups fail closed.");
                }

                return false;
            }

            string body = "{\"playerId\":\"" + playerId + "\"}";
            var request = new UnityWebRequest(LookupUrl, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + secret);
            request.SendWebRequest();

            _request = request;
            _activeNetworkId = networkId;
            _busy = true;
            return true;
        }

        /// <summary>True on the tick the Worker answers. <paramref name="owned"/> is false on any failure.</summary>
        public static bool TryTakeResult(out int networkId, out bool owned)
        {
            networkId = 0;
            owned = false;
            if (!_busy || _request == null || !_request.isDone)
                return false;

            networkId = _activeNetworkId;
            string text = _request.downloadHandler != null ? _request.downloadHandler.text : "";
            bool httpOk = _request.result == UnityWebRequest.Result.Success;
            owned = httpOk && (Contains(text, "\"owned\":true") || Contains(text, "\"owned\": true"));
            if (!httpOk)
            {
                Debug.LogWarning(
                    "[BetaTester] Lookup failed: " + _request.responseCode + " " + _request.error);
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
            return text.IndexOf(token, StringComparison.Ordinal) >= 0;
        }
    }

    /// <summary>
    /// One POST to <c>/admin/grant-beta</c>. A second click waits until this returns.
    /// Same secret as the Orbit Unlocked gift.
    /// </summary>
    static class BetaTesterGrantTransport
    {
        const string GrantUrl = "https://titanorbit-checkout.jason-redhawk.workers.dev/admin/grant-beta";

        static UnityWebRequest _request;
        static int _targetNetworkId;
        static int _adminNetworkId;
        static bool _busy;
        static bool _loggedMissingSecret;

        public static bool IsBusy => _busy;

        public static int AdminNetworkId => _adminNetworkId;

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

        /// <summary>Starts the Worker call. False when a call is already running or the secret is missing.</summary>
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
                    Debug.LogWarning("[BetaTester] TITANORBIT_ADMIN_GRANT_SECRET is not set.");
                }

                return false;
            }

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

        /// <summary>True on the tick the Worker answers. Status bytes match the Orbit Unlocked gift.</summary>
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
                    "[BetaTester] Grant failed: " + _request.responseCode + " " + _request.error);
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
            string spaced = token.Replace("\":", "\": ");
            return text.IndexOf(spaced, StringComparison.Ordinal) >= 0;
        }
    }
}
