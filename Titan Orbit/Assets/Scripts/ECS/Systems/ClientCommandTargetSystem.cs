using TitanOrbit.Core;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Client-only: points the connection's <see cref="CommandTarget"/> at the locally
    /// owned ship ghost so NetCode packages <see cref="ShipInput"/> commands every tick.
    /// Required for both dedicated clients and Local Host (Client+Server in one process) —
    /// skipping Local Host left the server ship without input while the client predicted thrust,
    /// which showed up as cmdAge≈24 and forward-then-snap-back. Runs first in
    /// GhostInputSystemGroup, before ShipInputApplySystem. World: ClientSimulation.
    /// </summary>
    [UpdateInGroup(typeof(GhostInputSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ClientCommandTargetSystem : ISystem
    {
        /// <summary>Requires an in-game client connection before binding the command target.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// [NETCODE] Each GhostInput tick: find local ship ghost and set CommandTarget.targetEntity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Skip during team-pick / rejoin UI ---
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

            // [NETCODE] CommandTarget on the client connection — without this, IInputComponentData
            // is never sent and the server ship coasts while the client predicts motion.
            foreach (var cmd in SystemAPI.Query<RefRW<CommandTarget>>().WithAll<NetworkStreamConnection, NetworkStreamInGame>())
            {
                if (cmd.ValueRO.targetEntity != shipEntity)
                    cmd.ValueRW = new CommandTarget { targetEntity = shipEntity };
            }
        }

        /// <summary>Reads this client's NetworkId from the in-game connection.</summary>
        int GetLocalNetworkId(ref SystemState state)
        {
            foreach (var netId in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>())
                return netId.ValueRO.Value;
            return -1;
        }
    }
}
