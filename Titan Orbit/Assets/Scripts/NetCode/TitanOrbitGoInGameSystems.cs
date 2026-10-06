using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Empty RPC payload — client tells server "I am ready to receive ghosts and sim."
    /// </summary>
    public struct GoInGameRequest : IRpcCommand { }

    /// <summary>
    /// [NETCODE] Client-side go-in-game handshake.
    /// <para>
    /// [TITAN-ORBIT] Solid join: do <b>not</b> add <see cref="NetworkStreamInGame"/> until
    /// <see cref="ClientMapHydrateCache.IsComplete"/> (local seed map built) and the ghost
    /// collection is present. Then send <see cref="GoInGameRequest"/>. This matches Unity's
    /// lobby→game pattern: hydrate first, occupancy catch-up, then GhostSpawn Instantiates of
    /// live ships/planets/gems.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapSessionMetaClientSystem))]
    public partial struct TitanOrbitGoInGameClientSystem : ISystem
    {
        /// <summary>Caches the driver query and the not-yet-InGame connection query.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamDriver>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkId>()
                .WithNone<NetworkStreamInGame>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        /// <summary>
        /// After local asteroid hydrate finishes, marks the connection InGame and sends
        /// <see cref="GoInGameRequest"/> so the server will start ghost snapshots.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Gate: local map hydrate must finish before ghost stream ---
            // [TITAN-ORBIT] Seed-hydrate join: wait for a full recipe, then IsComplete.
            // Do not treat counts-only / HasMeta as ready — that skipped hydrate, left the
            // loading bar on the 8% crawl with no 0/N, and never spawned local asteroids.
            if (!ClientMapHydrateCache.HasFullRecipe || !ClientMapHydrateCache.IsComplete)
                return;

            // --- Ghost prefabs registered ---
            if (!SystemAPI.HasSingleton<GhostCollection>())
                return;

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (id, entity) in SystemAPI.Query<RefRO<NetworkId>>().WithEntityAccess()
                         .WithNone<NetworkStreamInGame>())
            {
                commandBuffer.AddComponent<NetworkStreamInGame>(entity);

                var req = commandBuffer.CreateEntity();
                commandBuffer.AddComponent<GoInGameRequest>(req);
                commandBuffer.AddComponent(req, new SendRpcCommandRequest { TargetConnection = entity });
                Debug.Log(
                    "[TitanOrbitGoInGame] Client sending GoInGameRequest networkId=" + id.ValueRO.Value +
                    " hydrateComplete=" + ClientMapHydrateCache.IsComplete +
                    " built=" + ClientMapHydrateCache.BuiltBodies +
                    "/" + ClientMapHydrateCache.ExpectedBodies);
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }

    /// <summary>
    /// Marks connections that were already in the match when it was won.
    /// Later connections are dropped so a finished game cannot be rejoined.
    /// </summary>
    public struct FinishedMatchParticipant : IComponentData
    {
    }

    /// <summary>
    /// After a winner is declared, keeps the players who are already in the match
    /// (they still need the congrats card) and disconnects anyone who connects later.
    /// Runs before <see cref="TitanOrbitGoInGameServerSystem"/> so a late join is
    /// not accepted on the finished map.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(TitanOrbitGoInGameServerSystem))]
    public partial struct FinishedMatchJoinGateSystem : ISystem
    {
        /// <summary>1 after the in-match connections have been tagged.</summary>
        byte _sealed;

        /// <summary>Tags current players once, then disconnects every connection that appears after the win.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!MatchEndServerSignal.IsMatchWon)
                return;

            var em = state.EntityManager;
            if (_sealed == 0)
            {
                var keep = new NativeList<Entity>(8, Allocator.Temp);
                foreach (var (_, entity) in SystemAPI.Query<RefRO<NetworkStreamConnection>>()
                             .WithAll<NetworkStreamInGame>()
                             .WithNone<FinishedMatchParticipant>()
                             .WithEntityAccess())
                    keep.Add(entity);

                for (int i = 0; i < keep.Length; i++)
                    em.AddComponent<FinishedMatchParticipant>(keep[i]);
                keep.Dispose();
                _sealed = 1;
            }

            var drop = new NativeList<Entity>(4, Allocator.Temp);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<NetworkStreamConnection>>()
                         .WithNone<FinishedMatchParticipant>()
                         .WithEntityAccess())
                drop.Add(entity);

            for (int i = 0; i < drop.Length; i++)
            {
                Entity entity = drop[i];
                if (em.HasComponent<NetworkStreamRequestDisconnect>(entity))
                    continue;
                em.AddComponent<NetworkStreamRequestDisconnect>(entity);
                Debug.Log("[FinishedMatchJoinGate] Disconnected a join — this match is already over.");
            }

            drop.Dispose();
        }
    }

    /// <summary>
    /// [NETCODE] Server accepts <see cref="GoInGameRequest"/> and marks the source connection in-game.
    /// Recipe meta is sent earlier by <see cref="MapSessionMetaServerCatchUpSystem"/> (pre-InGame).
    /// A finished match refuses the request and disconnects the connection.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TitanOrbitGoInGameServerSystem : ISystem
    {
        /// <summary>Requires an inbound GoInGameRequest receive entity.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamDriver>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GoInGameRequest>()
                .WithAll<ReceiveRpcCommandRequest>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        /// <summary>
        /// Marks the source connection InGame and sends a late recipe if catch-up missed this client.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // --- Late meta if catch-up missed (should be rare) ---
            bool hasMeta = MapSessionMetaCache.TryBuildRecipeRpc(state.World, out var meta);

            foreach (var (reqSrc, reqEntity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<GoInGameRequest>().WithEntityAccess())
            {
                Entity connection = reqSrc.ValueRO.SourceConnection;

                // Finished map: do not stream ghosts to a new joiner. The dedicated host
                // is already publishing a fresh lobby for the next game.
                if (MatchEndServerSignal.IsMatchWon &&
                    !state.EntityManager.HasComponent<FinishedMatchParticipant>(connection))
                {
                    if (!state.EntityManager.HasComponent<NetworkStreamRequestDisconnect>(connection))
                        commandBuffer.AddComponent<NetworkStreamRequestDisconnect>(connection);
                    commandBuffer.DestroyEntity(reqEntity);
                    Debug.Log("[TitanOrbitGoInGame] Rejected GoInGame — match already finished.");
                    continue;
                }

                if (!state.EntityManager.HasComponent<NetworkStreamInGame>(connection))
                    commandBuffer.AddComponent<NetworkStreamInGame>(connection);

                if (state.EntityManager.HasComponent<NetworkId>(connection))
                {
                    var networkId = state.EntityManager.GetComponentData<NetworkId>(connection);
                    Debug.Log("[TitanOrbitGoInGame] Server accepted GoInGame networkId=" + networkId.Value);
                }

                if (hasMeta && !state.EntityManager.HasComponent<MapSessionMetaSent>(connection))
                {
                    MapSessionMetaCache.QueueRecipeRpc(commandBuffer, connection, meta);
                    commandBuffer.AddComponent(connection, new MapSessionMetaSent());
                }

                commandBuffer.DestroyEntity(reqEntity);
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }
}
