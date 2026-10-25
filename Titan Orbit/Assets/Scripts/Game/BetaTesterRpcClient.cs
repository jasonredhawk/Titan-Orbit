using TitanOrbit;
using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Sends this player's beta-tester toggle bits to the server.
    /// Dedicated clients use a normal client→server RPC. Local Host writes the
    /// command onto the server world, same path as the gift clicks, because
    /// SendRpc can disappear while Join Team is spawning ships.
    /// The server ignores the bits unless that connection owns the item.
    /// </summary>
    public static class BetaTesterRpcClient
    {
        /// <summary>
        /// Asks the server to store <paramref name="flags"/>.
        /// False when this client is not in a match yet, so the caller can retry.
        /// </summary>
        public static bool SendToggles(byte flags)
        {
            var command = new SetBetaTesterTogglesCommand
            {
                Flags = (byte)(flags & BetaTesterClientFlags.Mask),
            };
            int networkId = EcsGameBridge.GetLocalNetworkId();
            if (TryEnqueueLocalHost(command, networkId))
                return true;
            return SendDedicated(command);
        }

        static bool TryEnqueueLocalHost(SetBetaTesterTogglesCommand command, int networkId)
        {
            if (!EcsGameBridge.IsLocalHost() || networkId <= 0)
                return false;
            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var em = server.EntityManager;
            if (!TryServerConnection(em, networkId, out Entity connection))
                return false;

            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, command);
            em.AddComponentData(rpc, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        static bool SendDedicated(SetBetaTesterTogglesCommand command)
        {
            if (EcsGameBridge.IsLocalHost() || !EcsGameBridge.IsNetworkInGame())
                return false;
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, command);
            em.AddComponentData(rpc, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }

        static bool TryServerConnection(EntityManager em, int networkId, out Entity connection)
        {
            connection = Entity.Null;
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
}
