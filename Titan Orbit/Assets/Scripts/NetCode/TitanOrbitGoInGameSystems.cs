using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Empty RPC payload — client tells server "I am ready to receive ghosts and sim."
    /// Required for dedicated Relay joins where connection must be explicitly marked in-game on both sides.
    /// </summary>
    public struct GoInGameRequest : IRpcCommand { }

    /// <summary>
    /// [NETCODE] Client-side go-in-game handshake. When a connection has <see cref="NetworkId"/> but
    /// not yet <see cref="NetworkStreamInGame"/>, adds InGame and sends <see cref="GoInGameRequest"/>
    /// to the server. World: ClientSimulation (and ThinClient). Runs each frame until all connections
    /// are in-game. Paired with <see cref="TitanOrbitGoInGameServerSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TitanOrbitGoInGameClientSystem : ISystem
    {
        /// <summary>Require network driver and at least one connection waiting to go in-game.</summary>
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
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // --- Connections not yet in-game ---
            // [NETCODE] NetworkStreamInGame — NetCode tag that enables ghost spawn/replication on this connection.
            foreach (var (id, entity) in SystemAPI.Query<RefRO<NetworkId>>().WithEntityAccess()
                         .WithNone<NetworkStreamInGame>())
            {
                commandBuffer.AddComponent<NetworkStreamInGame>(entity);

                // --- RPC to server ---
                var req = commandBuffer.CreateEntity();
                commandBuffer.AddComponent<GoInGameRequest>(req);
                commandBuffer.AddComponent(req, new SendRpcCommandRequest { TargetConnection = entity });
                Debug.Log("[TitanOrbitGoInGame] Client sending GoInGameRequest networkId=" + id.ValueRO.Value);
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }

    /// <summary>
    /// [NETCODE] Server accepts <see cref="GoInGameRequest"/> and marks the source connection in-game.
    /// World: ServerSimulation. Without this, dedicated server may not replicate ghosts to the client.
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

            foreach (var (reqSrc, reqEntity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<GoInGameRequest>().WithEntityAccess())
            {
                // [NETCODE] SourceConnection — entity for the client that sent this RPC.
                Entity connection = reqSrc.ValueRO.SourceConnection;
                if (!state.EntityManager.HasComponent<NetworkStreamInGame>(connection))
                    commandBuffer.AddComponent<NetworkStreamInGame>(connection);

                if (state.EntityManager.HasComponent<NetworkId>(connection))
                {
                    var networkId = state.EntityManager.GetComponentData<NetworkId>(connection);
                    Debug.Log("[TitanOrbitGoInGame] Server accepted GoInGame networkId=" + networkId.Value);
                }

                commandBuffer.DestroyEntity(reqEntity);
            }

            commandBuffer.Playback(state.EntityManager);
        }
    }
}
