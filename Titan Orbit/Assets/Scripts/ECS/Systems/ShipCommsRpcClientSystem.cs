using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: drains <see cref="ShipCommsRpc"/> into <see cref="ShipCommsInbox"/>.
    /// The GameObject presenter (<c>ShipCommsBubblePresenter</c>) paints chips above the
    /// speaker's hull — this system never Instantiates UI.
    /// <para>
    /// World: ClientSimulation. Group: SimulationSystemGroup. Paired with
    /// <see cref="ShipCommsServerSystem"/>. Team-only rows only arrive when this
    /// connection is on the speaker's team (the server already filtered).
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipCommsRpcClientSystem : ISystem
    {
        /// <summary>
        /// Copies each inbound callout into the process-wide inbox, then destroys the RPC entity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Drain inbound RPCs ---
            // [NETCODE] ReceiveRpcCommandRequest marks inbound RPC entities from the network.
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<ShipCommsRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                ShipCommsRpc row = rpc.ValueRO;
                ShipCommsInbox.Enqueue(
                    row.NetworkId,
                    row.Count,
                    row.K0,
                    row.K1,
                    row.K2,
                    row.TeamOnly);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
