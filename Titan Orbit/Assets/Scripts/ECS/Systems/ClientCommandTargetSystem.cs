using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Points the client connection CommandTarget at the locally owned ship ghost so NetCode
    /// packages ShipInput commands for the dedicated-server path (team spawn is server-side).
    /// </summary>
    [UpdateInGroup(typeof(GhostInputSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ClientCommandTargetSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (IsLocalHostPlay())
                return;

            int localNetworkId = GetLocalNetworkId(ref state);
            if (localNetworkId <= 0)
                return;

            Entity shipEntity = Entity.Null;
            foreach (var (owner, entity) in SystemAPI.Query<RefRO<GhostOwner>>().WithAll<ShipTag>().WithEntityAccess())
            {
                if (owner.ValueRO.NetworkId != localNetworkId)
                    continue;
                shipEntity = entity;
                break;
            }

            if (shipEntity == Entity.Null)
                return;

            foreach (var cmd in SystemAPI.Query<RefRW<CommandTarget>>().WithAll<NetworkStreamConnection, NetworkStreamInGame>())
            {
                if (cmd.ValueRO.targetEntity == shipEntity)
                    continue;
                cmd.ValueRW = new CommandTarget { targetEntity = shipEntity };
            }
        }

        static bool IsLocalHostPlay()
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            using var query = server.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame));
            return query.CalculateEntityCount() > 0;
        }

        int GetLocalNetworkId(ref SystemState state)
        {
            foreach (var netId in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>())
                return netId.ValueRO.Value;
            return -1;
        }
    }
}
