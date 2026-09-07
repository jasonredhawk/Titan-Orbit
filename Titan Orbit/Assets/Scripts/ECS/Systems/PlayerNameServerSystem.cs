using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Tag on a server connection after the current name roster has been dumped via
    /// <see cref="PlayerNameAnnounceRpc"/>. New names still broadcast to every in-game client.
    /// Same idea as <c>MapSessionMetaSent</c> — late joiners need a one-shot catch-up.
    /// </summary>
    public struct PlayerNameRosterSent : IComponentData { }

    /// <summary>
    /// Server: accepts <see cref="SetPlayerNameCommand"/> from clients, writes
    /// <see cref="PlayerNameElement"/> on the match singleton, and broadcasts
    /// <see cref="PlayerNameAnnounceRpc"/> so every client can paint nameplates / leaderboards.
    /// <para>
    /// Also dumps the full roster to connections that have a <see cref="NetworkId"/> but have not
    /// received names yet (late join). World: ServerSimulation. Group: SimulationSystemGroup.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Local Host injects <see cref="SetPlayerNameCommand"/> with
    /// <see cref="ReceiveRpcCommandRequest"/> already present (see <c>PlayerNameRpcClient</c>)
    /// because client→server SendRpc can drop under Join Team Instantiates load.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PlayerNameServerSystem : ISystem
    {
        /// <summary>
        /// [ECS/DOTS] Names live on the match singleton created by <see cref="GameBootstrapSystem"/>.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TeamStateSingleton>();
        }

        /// <summary>
        /// Drains name RPCs, then catch-up dumps the roster to new connections.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            if (!SystemAPI.TryGetSingletonBuffer<PlayerNameElement>(out var names))
            {
                ecb.Dispose();
                return;
            }

            // --- Phase 1: client → server SetPlayerName ---
            // [NETCODE] ReceiveRpcCommandRequest pairs the RPC with the sending connection.
            // We never trust a client-supplied NetworkId (the command has none) — SourceConnection
            // is the anti-spoof source of truth.
            foreach (var (cmd, req, rpcEntity) in SystemAPI
                         .Query<RefRO<SetPlayerNameCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(rpcEntity);

                Entity connection = req.ValueRO.SourceConnection;
                if (!em.HasComponent<NetworkId>(connection))
                    continue;

                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                if (networkId <= 0)
                    continue;

                FixedString64Bytes displayName = PlayerDisplayNameUtil.SanitizeFixed(cmd.ValueRO.DisplayName);
                int badgeId = PlayerBadgeIdUtil.Sanitize(cmd.ValueRO.BadgeId);
                UpsertRoster(names, networkId, displayName, badgeId);
                PlayerNameRosterCache.Upsert(networkId, displayName, badgeId);
                BroadcastName(ecb, networkId, displayName, badgeId);

                Debug.Log("[PlayerName] Server stored name for networkId=" + networkId +
                          " name=" + displayName + " badge=" + badgeId);
            }

            // --- Phase 2: late-join catch-up ---
            // [NETCODE] New connections miss broadcasts that already happened. Dump every stored
            // row once, then tag the connection so we do not resend every tick.
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>()
                         .WithNone<PlayerNameRosterSent>()
                         .WithEntityAccess())
            {
                for (int i = 0; i < names.Length; i++)
                {
                    PlayerNameElement row = names[i];
                    if (row.NetworkId <= 0 || row.DisplayName.IsEmpty)
                        continue;

                    Entity announce = ecb.CreateEntity();
                    ecb.AddComponent(announce, new PlayerNameAnnounceRpc
                    {
                        NetworkId = row.NetworkId,
                        DisplayName = row.DisplayName,
                        BadgeId = row.BadgeId,
                    });
                    ecb.AddComponent(announce, new SendRpcCommandRequest { TargetConnection = connection });
                }

                ecb.AddComponent<PlayerNameRosterSent>(connection);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// Inserts or replaces one roster row keyed by NetworkId.
        /// </summary>
        /// <param name="names">Match-singleton name buffer.</param>
        /// <param name="networkId">Owning connection id.</param>
        /// <param name="displayName">Sanitized name.</param>
        /// <param name="badgeId">Sanitized filename-stable badge id, or 0 for none.</param>
        static void UpsertRoster(
            DynamicBuffer<PlayerNameElement> names,
            int networkId,
            in FixedString64Bytes displayName,
            int badgeId)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].NetworkId != networkId)
                    continue;

                PlayerNameElement row = names[i];
                row.DisplayName = displayName;
                row.BadgeId = badgeId;
                names[i] = row;
                return;
            }

            names.Add(new PlayerNameElement
            {
                NetworkId = networkId,
                DisplayName = displayName,
                BadgeId = badgeId,
            });
        }

        /// <summary>
        /// [NETCODE] TargetConnection = Null means every connected client (including the sender).
        /// Dedicated clients apply this in <see cref="PlayerNameAnnounceClientSystem"/>.
        /// Local Host already wrote <see cref="PlayerNameRosterCache"/> in-process above.
        /// </summary>
        static void BroadcastName(
            EntityCommandBuffer ecb,
            int networkId,
            in FixedString64Bytes displayName,
            int badgeId)
        {
            Entity announce = ecb.CreateEntity();
            ecb.AddComponent(announce, new PlayerNameAnnounceRpc
            {
                NetworkId = networkId,
                DisplayName = displayName,
                BadgeId = badgeId,
            });
            ecb.AddComponent(announce, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}
