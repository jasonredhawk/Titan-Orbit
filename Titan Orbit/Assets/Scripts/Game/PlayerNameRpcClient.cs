using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client glue that publishes the Main Menu display name to the server after GoInGame.
    /// <para>
    /// [NETCODE] Dedicated / Relay clients send <see cref="SetPlayerNameCommand"/> from ClientWorld.
    /// Local Host injects the same command onto ServerWorld with
    /// <see cref="ReceiveRpcCommandRequest"/> already set — the same Instantiates-safe pattern as
    /// <c>TitanOrbitSessionManager.RequestTeam</c>. Under join load, SendRpc can vanish and the
    /// nameplate would stay on the "Player {id}" fallback forever.
    /// </para>
    /// Also upserts <see cref="PlayerNameRosterCache"/> immediately so <b>your</b> nameplate and
    /// leaderboard row never wait on a round-trip.
    /// </summary>
    public static class PlayerNameRpcClient
    {
        /// <summary>How often to retry a dropped RPC while still in-game (seconds).</summary>
        const float ResendIntervalSeconds = 4f;

        /// <summary>
        /// Extra sends after the first. Dedicated join can drop the first RPC before the
        /// connection is fully InGame on the server; a few retries cover that race.
        /// </summary>
        const int MaxSendsPerSession = 4;

        /// <summary>Sends completed this NetworkStreamInGame session.</summary>
        static int s_SendCount;

        /// <summary>[UNITY] Time.realtimeSinceStartup of the last send attempt.</summary>
        static float s_LastSendRealtime;

        /// <summary>
        /// [UNITY] Domain Reload off: send counters survive Play Mode. Clear so the next Play
        /// actually publishes the name again.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => ResetSession();

        /// <summary>Clears send counters (session leave / Play Mode).</summary>
        public static void ResetSession()
        {
            s_SendCount = 0;
            s_LastSendRealtime = 0f;
        }

        /// <summary>
        /// Publishes <see cref="LocalPlayerDisplayName"/> when the client is in-game.
        /// Safe to call every frame — rate-limited internally.
        /// </summary>
        public static void TrySendLocalName()
        {
            // --- Not in-game: wait (and reset so the next join starts at send 0) ---
            if (!EcsGameBridge.IsNetworkInGame())
            {
                ResetSession();
                return;
            }

            string name = LocalPlayerDisplayName.Get();
            int localId = EcsGameBridge.GetLocalNetworkId();

            // --- Immediate local plate / leaderboard ---
            // [TITAN-ORBIT] Do this even if the RPC is still in flight so the owner never sees
            // "Player 1" on their own hull.
            if (localId > 0)
                PlayerNameRosterCache.Upsert(localId, name);

            if (s_SendCount >= MaxSendsPerSession)
                return;

            if (s_SendCount > 0 &&
                Time.realtimeSinceStartup - s_LastSendRealtime < ResendIntervalSeconds)
                return;

            bool sent = TryEnqueueLocalHost(name, localId) || TrySendDedicatedRpc(name);
            if (!sent)
                return;

            s_SendCount++;
            s_LastSendRealtime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// [TITAN-ORBIT] Local Host: create the RPC entity on ServerWorld so
        /// <see cref="PlayerNameServerSystem"/> sees it next tick without IPC.
        /// </summary>
        /// <param name="name">Sanitized-enough raw name (server sanitizes again).</param>
        /// <param name="networkId">Local GhostOwner / connection id.</param>
        /// <returns>True when the server entity was created.</returns>
        static bool TryEnqueueLocalHost(string name, int networkId)
        {
            if (!EcsGameBridge.IsLocalHost())
                return false;
            if (networkId <= 0)
                return false;

            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var em = server.EntityManager;
            Entity connection = FindServerConnection(em, networkId);
            if (connection == Entity.Null)
                return false;

            var rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, new SetPlayerNameCommand
            {
                DisplayName = PlayerDisplayNameUtil.ToFixed(name),
            });
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        /// <summary>
        /// [NETCODE] Dedicated / Relay: SendRpc from ClientWorld. TargetConnection Null = "the
        /// server that owns this client connection."
        /// </summary>
        /// <param name="name">Name from PlayerPrefs / Main Menu.</param>
        /// <returns>True when the ClientWorld RPC entity was created.</returns>
        static bool TrySendDedicatedRpc(string name)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new SetPlayerNameCommand
            {
                DisplayName = PlayerDisplayNameUtil.ToFixed(name),
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }

        /// <summary>
        /// Finds the server connection entity whose <see cref="NetworkId"/> matches the local player.
        /// Local Host is one in-game connection, so this uses GetSingletonEntity (no ship/map gather).
        /// </summary>
        static Entity FindServerConnection(EntityManager em, int networkId)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            if (query.IsEmptyIgnoreFilter)
                return Entity.Null;

            // [TITAN-ORBIT] Local Host has a single in-game connection — GetSingletonEntity
            // avoids a client-hot entity gather (join-crash verifier).
            if (query.CalculateEntityCount() != 1)
                return Entity.Null;

            Entity connection = query.GetSingletonEntity();
            if (em.GetComponentData<NetworkId>(connection).Value != networkId)
                return Entity.Null;
            return connection;
        }
    }
}
