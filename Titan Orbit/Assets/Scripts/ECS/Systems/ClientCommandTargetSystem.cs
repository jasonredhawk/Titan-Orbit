using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-only: points the connection's CommandTarget at the locally owned ship ghost so NetCode
    /// packages ShipInput commands for the dedicated-server path. Team spawn is server-side, so the
    /// client must discover its ship via GhostOwner.NetworkId after the server assigns ownership.
    /// Runs first in GhostInputSystemGroup, before ShipInputApplySystem. Skipped for local host play
    /// (ShipServerControlSystem writes input directly on the server world).
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

            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return;

            int localNetworkId = GetLocalNetworkId(ref state);
            if (localNetworkId <= 0)
                return;

            // --- Find the ship ghost owned by this client ---
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

            // [NETCODE] CommandTarget.targetEntity — NetCode sends input to this ghost each tick.
            foreach (var cmd in SystemAPI.Query<RefRW<CommandTarget>>().WithAll<NetworkStreamConnection, NetworkStreamInGame>())
            {
                if (cmd.ValueRO.targetEntity == shipEntity)
                    continue;
                cmd.ValueRW = new CommandTarget { targetEntity = shipEntity };
            }
        }

        /// <summary>True when client and server worlds both have an in-game connection (MPPM / host).</summary>
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
