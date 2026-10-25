using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Sends the admin's roster and gift clicks. Dedicated clients use a normal
    /// client→server RPC. Local Host writes the command onto the server world,
    /// because SendRpc can disappear while Join Team is spawning ships.
    /// </summary>
    public static class OrbitUnlockedGiftRpcClient
    {
        /// <summary>Asks the server for the current match list once.</summary>
        public static void RequestRoster()
        {
            byte requestId = OrbitUnlockedGiftClientState.BeginRosterRequest();
            var command = new RequestGiftRosterCommand { RequestId = requestId };
            if (TryEnqueueLocalHost(command, EcsGameBridge.GetLocalNetworkId()))
                return;
            SendDedicated(command);
        }

        /// <summary>Asks the server to gift the connection with <paramref name="targetNetworkId"/>.</summary>
        public static void Gift(int targetNetworkId)
        {
            if (targetNetworkId <= 0)
                return;
            var command = new GiftOrbitUnlockedCommand { TargetNetworkId = targetNetworkId };
            if (TryEnqueueLocalHost(command, EcsGameBridge.GetLocalNetworkId()))
                return;
            SendDedicated(command);
        }

        /// <summary>Asks the server to gift the beta-tester item to <paramref name="targetNetworkId"/>.</summary>
        public static void GiftBeta(int targetNetworkId)
        {
            if (targetNetworkId <= 0)
                return;
            var command = new GiftBetaTesterCommand { TargetNetworkId = targetNetworkId };
            if (TryEnqueueLocalHost(command, EcsGameBridge.GetLocalNetworkId()))
                return;
            SendDedicated(command);
        }

        static bool TryEnqueueLocalHost(RequestGiftRosterCommand command, int networkId)
        {
            if (!TryServerConnection(networkId, out EntityManager em, out Entity connection))
                return false;
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, command);
            em.AddComponentData(rpc, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        static bool TryEnqueueLocalHost(GiftOrbitUnlockedCommand command, int networkId)
        {
            if (!TryServerConnection(networkId, out EntityManager em, out Entity connection))
                return false;
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, command);
            em.AddComponentData(rpc, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        static bool TryEnqueueLocalHost(GiftBetaTesterCommand command, int networkId)
        {
            if (!TryServerConnection(networkId, out EntityManager em, out Entity connection))
                return false;
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, command);
            em.AddComponentData(rpc, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        static void SendDedicated(RequestGiftRosterCommand command)
        {
            if (!TryClientEntityManager(out EntityManager em))
                return;
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, command);
            em.AddComponentData(rpc, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        static void SendDedicated(GiftOrbitUnlockedCommand command)
        {
            if (!TryClientEntityManager(out EntityManager em))
                return;
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, command);
            em.AddComponentData(rpc, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        static void SendDedicated(GiftBetaTesterCommand command)
        {
            if (!TryClientEntityManager(out EntityManager em))
                return;
            Entity rpc = em.CreateEntity();
            em.AddComponentData(rpc, command);
            em.AddComponentData(rpc, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Local Host writes the command onto the matching server connection.
        /// A second player in the same process does not hide this one.
        /// </summary>
        static bool TryServerConnection(int networkId, out EntityManager em, out Entity connection)
        {
            em = default;
            connection = Entity.Null;
            if (!EcsGameBridge.IsLocalHost() || networkId <= 0)
                return false;
            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            em = server.EntityManager;
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

        static bool TryClientEntityManager(out EntityManager em)
        {
            em = default;
            if (EcsGameBridge.IsLocalHost() || !EcsGameBridge.IsNetworkInGame())
                return false;
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;
            em = world.EntityManager;
            return true;
        }
    }
}
