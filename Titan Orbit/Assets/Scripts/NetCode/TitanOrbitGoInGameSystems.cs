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
    /// lobby→game pattern: hydrate first, then receive dynamic ghosts (ships/gems).
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapSessionMetaClientSystem))]
    public partial struct TitanOrbitGoInGameClientSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamDriver>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkId>()
                .WithNone<NetworkStreamInGame>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        public void OnUpdate(ref SystemState state)
        {
            // --- Gate: local map hydrate must finish before ghost stream ---
            // [TITAN-ORBIT] Full recipe → wait for IsComplete. Counts-only meta → allow InGame.
            // No meta yet → wait (server catch-up sends recipe pre-InGame).
            bool hydrateReady;
            if (ClientMapHydrateCache.HasFullRecipe)
                hydrateReady = ClientMapHydrateCache.IsComplete;
            else if (ClientMapHydrateCache.HasRecipe || MapSessionMetaCache.HasMeta)
                hydrateReady = true;
            else
                hydrateReady = false;
            if (!hydrateReady)
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
    /// [NETCODE] Server accepts <see cref="GoInGameRequest"/> and marks the source connection in-game.
    /// Recipe meta is sent earlier by <see cref="MapSessionMetaServerCatchUpSystem"/> (pre-InGame).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TitanOrbitGoInGameServerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamDriver>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GoInGameRequest>()
                .WithAll<ReceiveRpcCommandRequest>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // --- Late meta if catch-up missed (should be rare) ---
            bool hasMeta = MapSessionMetaCache.TryBuildRecipeRpc(state.World, out var meta);

            foreach (var (reqSrc, reqEntity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<GoInGameRequest>().WithEntityAccess())
            {
                Entity connection = reqSrc.ValueRO.SourceConnection;
                if (!state.EntityManager.HasComponent<NetworkStreamInGame>(connection))
                    commandBuffer.AddComponent<NetworkStreamInGame>(connection);

                if (state.EntityManager.HasComponent<NetworkId>(connection))
                {
                    var networkId = state.EntityManager.GetComponentData<NetworkId>(connection);
                    Debug.Log("[TitanOrbitGoInGame] Server accepted GoInGame networkId=" + networkId.Value);
                }

                if (hasMeta && !state.EntityManager.HasComponent<MapSessionMetaSent>(connection))
                {
                    Entity metaEntity = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(metaEntity, meta);
                    commandBuffer.AddComponent(metaEntity, new SendRpcCommandRequest { TargetConnection = connection });
                    commandBuffer.AddComponent<MapSessionMetaSent>(connection);
                }

                commandBuffer.DestroyEntity(reqEntity);
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }
}
