using System;
using TitanOrbit.ECS;
using TitanOrbit.Services;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Publishes a stable player id after GoInGame so the server can restore this
    /// player's ship for the rest of the match. Uses the signed-in UGS player id,
    /// or a guid kept in PlayerPrefs when auth is not signed in.
    /// Local Host injects the RPC onto ServerWorld the same way display names do.
    /// </summary>
    public static class SessionPlayerIdRpcClient
    {
        const string PrefsKey = "TitanOrbit_SessionPlayerKey_v1";
        const float ResendIntervalSeconds = 4f;
        const int MaxSendsPerSession = 4;

        static int s_SendCount;
        static float s_LastSendRealtime;

        /// <summary>How many times this match has told the server the account is signed in.</summary>
        static int s_DurableSends;

        static float s_LastDurableRealtime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => ResetSession();

        public static void ResetSession()
        {
            s_SendCount = 0;
            s_LastSendRealtime = 0f;
            s_DurableSends = 0;
            s_LastDurableRealtime = 0f;
        }

        /// <summary>Id the server should key this player's saved ship on.</summary>
        public static string Get()
        {
            string ugs = UnityGameServicesBootstrap.PlayerId;
            if (!string.IsNullOrEmpty(ugs))
                return ugs;

            string saved = PlayerPrefs.GetString(PrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(saved))
                return saved;

            saved = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(PrefsKey, saved);
            PlayerPrefs.Save();
            return saved;
        }

        /// <summary>Safe to call every frame. Rate-limited internally.</summary>
        public static void TrySend()
        {
            if (!EcsGameBridge.IsNetworkInGame())
            {
                ResetSession();
                return;
            }

            int localId = EcsGameBridge.GetLocalNetworkId();
            TrySendPlayerId(localId);
            // Signed-in flag has its own counter. It still goes out after the
            // player id has finished its four sends.
            TrySendDurableAccount(localId);
        }

        /// <summary>Publishes the stable id the server uses for this connection's ship.</summary>
        static void TrySendPlayerId(int localId)
        {
            if (s_SendCount >= MaxSendsPerSession)
                return;
            if (s_SendCount > 0 &&
                Time.realtimeSinceStartup - s_LastSendRealtime < ResendIntervalSeconds)
                return;

            FixedString128Bytes id = ToFixed(Get());
            if (id.Length <= 0)
                return;

            bool sent = TryEnqueueLocalHost(id, localId) || TrySendDedicatedRpc(id);
            if (!sent)
                return;

            s_SendCount++;
            s_LastSendRealtime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Tells the server this Unity account is signed in with Google or a
        /// username. Guests skip it, so they cannot be gifted. Own retry counter,
        /// so a guest id that already finished sending does not block this.
        /// </summary>
        static void TrySendDurableAccount(int localNetworkId)
        {
            if (!UnityGameServicesBootstrap.HasDurablePlayerAccount())
                return;
            if (s_DurableSends >= MaxSendsPerSession)
                return;
            if (s_DurableSends > 0 &&
                Time.realtimeSinceStartup - s_LastDurableRealtime < ResendIntervalSeconds)
                return;

            bool sent = TryEnqueueDurableLocalHost(localNetworkId) || TrySendDurableDedicated();
            if (!sent)
                return;

            s_DurableSends++;
            s_LastDurableRealtime = Time.realtimeSinceStartup;
        }

        static bool TryEnqueueDurableLocalHost(int networkId)
        {
            if (!EcsGameBridge.IsLocalHost() || networkId <= 0)
                return false;
            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var em = server.EntityManager;
            Entity connection = FindConnection(em, networkId);
            if (connection == Entity.Null)
                return false;

            Entity rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, new SessionDurableAccountCommand());
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        static bool TrySendDurableDedicated()
        {
            if (EcsGameBridge.IsLocalHost())
                return false;
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            Entity entity = em.CreateEntity();
            em.AddComponentData(entity, new SessionDurableAccountCommand());
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }

        static bool TryEnqueueLocalHost(in FixedString128Bytes playerId, int networkId)
        {
            if (!EcsGameBridge.IsLocalHost())
                return false;
            if (networkId <= 0)
                return false;

            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var em = server.EntityManager;
            Entity connection = FindConnection(em, networkId);
            if (connection == Entity.Null)
                return false;

            Entity rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, new SetSessionPlayerIdCommand { PlayerId = playerId });
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        static bool TrySendDedicatedRpc(in FixedString128Bytes playerId)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            Entity entity = em.CreateEntity();
            em.AddComponentData(entity, new SetSessionPlayerIdCommand { PlayerId = playerId });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }

        /// <summary>
        /// Local Host may have more than one in-game connection. Match this
        /// player's network id instead of assuming the host is alone.
        /// </summary>
        static Entity FindConnection(EntityManager em, int networkId)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp);
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i].Value == networkId)
                    return entities[i];
            }

            return Entity.Null;
        }

        static FixedString128Bytes ToFixed(string id)
        {
            if (string.IsNullOrEmpty(id))
                return default;
            if (id.Length > 120)
                id = id.Substring(0, 120);
            return new FixedString128Bytes(id);
        }
    }
}
