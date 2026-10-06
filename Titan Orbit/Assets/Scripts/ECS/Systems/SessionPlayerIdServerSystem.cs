using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// How many times this connection has been told it has a saved ship.
    /// Stops the offer RPC from firing every tick. Not ghosted.
    /// </summary>
    public struct SessionShipOfferTracker : IComponentData
    {
        public byte Sends;
        public double LastSendElapsed;
    }

    /// <summary>
    /// Server: stores the stable player id for each connection, then offers a saved
    /// ship when this match still has one. Runs after orphan cleanup so a recycled
    /// <see cref="NetworkId"/> is bound only after the previous hull was snapshotted.
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(OrphanPlayerShipCleanupSystem))]
    public partial struct SessionPlayerIdServerSystem : ISystem
    {
        const int MaxOfferSends = 6;
        const double OfferIntervalSeconds = 2.0;

        EntityQuery _idRpcs;

        public void OnCreate(ref SystemState state)
        {
            _idRpcs = state.GetEntityQuery(
                ComponentType.ReadOnly<SetSessionPlayerIdCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
        }

        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            if (!_idRpcs.IsEmpty)
                ConsumeIdRpcs(ref state, em);

            if (MatchPlayerShipStore.SnapshotCount == 0)
                return;

            SendOffers(ref state, em);
        }

        /// <summary>Binds NetworkId to the client player id. The connection is the source of truth.</summary>
        void ConsumeIdRpcs(ref SystemState state, EntityManager em)
        {
            var networkIds = new NativeList<int>(4, Allocator.Temp);
            var playerIds = new NativeList<FixedString128Bytes>(4, Allocator.Temp);
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<SetSessionPlayerIdCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                Entity connection = req.ValueRO.SourceConnection;
                if (!em.Exists(connection) || !em.HasComponent<NetworkId>(connection))
                    continue;

                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                if (networkId <= 0)
                    continue;

                networkIds.Add(networkId);
                playerIds.Add(cmd.ValueRO.PlayerId);
            }

            ecb.Playback(em);
            ecb.Dispose();

            // Structural ship destroys happen after the RPC query so a recycled NetworkId
            // cannot keep the previous player's hull.
            for (int i = 0; i < networkIds.Length; i++)
                ApplyBind(em, networkIds[i], playerIds[i]);

            networkIds.Dispose();
            playerIds.Dispose();
        }

        /// <summary>
        /// If this NetworkId still names a different player, save and destroy that hull first.
        /// </summary>
        static void ApplyBind(EntityManager em, int networkId, FixedString128Bytes playerId)
        {
            if (playerId.Length <= 0)
                return;

            string next = playerId.ToString();
            if (MatchPlayerShipStore.TryGetPlayerId(networkId, out string existing) && existing != next)
                PlayerShipExit.DespawnOwner(em, networkId, releaseRoster: true);

            if (MatchPlayerShipStore.Bind(networkId, playerId))
                Debug.Log("[SessionPlayerId] Bound networkId=" + networkId + ".");
        }

        /// <summary>
        /// Tells a connection that has no live hull, and a saved ship, to show continue / start fresh.
        /// </summary>
        void SendOffers(ref SystemState state, EntityManager em)
        {
            double now = SystemAPI.Time.ElapsedTime;
            var liveOwners = new NativeHashSet<int>(8, Allocator.Temp);
            foreach (var owner in SystemAPI.Query<RefRO<GhostOwner>>().WithAll<ShipTag>())
            {
                int id = owner.ValueRO.NetworkId;
                if (id > 0)
                    liveOwners.Add(id);
            }

            var pending = new NativeList<Entity>(4, Allocator.Temp);
            var summaries = new NativeList<ShipState>(4, Allocator.Temp);
            foreach (var (netId, connection) in SystemAPI
                         .Query<RefRO<NetworkId>>()
                         .WithAll<NetworkStreamInGame>()
                         .WithNone<NetworkStreamRequestDisconnect>()
                         .WithEntityAccess())
            {
                int networkId = netId.ValueRO.Value;
                if (networkId <= 0 || liveOwners.Contains(networkId))
                    continue;
                if (MatchPlayerShipStore.OtherConnectionFlying(networkId, liveOwners))
                    continue;
                if (!MatchPlayerShipStore.HasSavedShip(networkId))
                    continue;
                if (!ShouldSendOffer(em, connection, now))
                    continue;
                if (!TryCopyOffer(networkId, out ShipState summary))
                    continue;

                pending.Add(connection);
                summaries.Add(summary);
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < pending.Length; i++)
            {
                Entity connection = pending[i];
                if (!em.Exists(connection))
                    continue;

                MarkOfferSent(em, connection, now);
                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                TryApplyLocalHostOffer(networkId, summaries[i]);

                Entity rpc = ecb.CreateEntity();
                ecb.AddComponent(rpc, new SessionShipOfferRpc { Ship = summaries[i] });
                ecb.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = connection });
            }

            ecb.Playback(em);
            ecb.Dispose();
            pending.Dispose();
            summaries.Dispose();
            liveOwners.Dispose();
        }

        /// <summary>
        /// Reads the saved summary without removing it. False when the id is not bound.
        /// </summary>
        static bool TryCopyOffer(int networkId, out ShipState summary)
        {
            summary = default;
            if (!MatchPlayerShipStore.TryTake(networkId, out MatchPlayerShipSnapshot snapshot, out string playerId))
                return false;

            summary = snapshot.Ship;
            MatchPlayerShipStore.ReturnSnapshot(playerId, snapshot);
            return summary.Team != TeamId.None && !summary.AwaitingTeamSelection;
        }

        static bool ShouldSendOffer(EntityManager em, Entity connection, double now)
        {
            if (!em.Exists(connection) || !em.HasComponent<SessionShipOfferTracker>(connection))
                return em.Exists(connection);

            SessionShipOfferTracker tracker = em.GetComponentData<SessionShipOfferTracker>(connection);
            if (tracker.Sends >= MaxOfferSends)
                return false;
            return now - tracker.LastSendElapsed >= OfferIntervalSeconds;
        }

        static void MarkOfferSent(EntityManager em, Entity connection, double now)
        {
            if (!em.HasComponent<SessionShipOfferTracker>(connection))
            {
                em.AddComponentData(connection, new SessionShipOfferTracker
                {
                    Sends = 1,
                    LastSendElapsed = now,
                });
                return;
            }

            SessionShipOfferTracker tracker = em.GetComponentData<SessionShipOfferTracker>(connection);
            tracker.Sends++;
            tracker.LastSendElapsed = now;
            em.SetComponentData(connection, tracker);
        }

        /// <summary>
        /// Local Host: write the offer straight into the client cache. SendRpc can drop
        /// under join load, and this process already owns that client.
        /// </summary>
        static void TryApplyLocalHostOffer(int networkId, ShipState summary)
        {
            var client = ClientServerBootstrap.ClientWorld;
            var server = ClientServerBootstrap.ServerWorld;
            if (client == null || !client.IsCreated || server == null || !server.IsCreated)
                return;

            var clientEm = client.EntityManager;
            using var query = clientEm.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkId>());
            using var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp);
            if (ids.Length == 0 || ids[0].Value != networkId)
                return;

            SessionShipOfferCache.Set(summary);
            ClientTeamFlowState.TryNotifyRejoinableShip(true);
            Debug.Log("[SessionPlayerId] Local Host offer applied team=" + summary.Team + ".");
        }
    }
}
