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
    /// <see cref="ShipCommsServerSystem"/>.
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

            // --- Drain broadcast RPCs ---
            // [NETCODE] ReceiveRpcCommandRequest marks inbound RPC entities from the network.
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<ShipCommsRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                ShipCommsRpc row = rpc.ValueRO;
                ShipCommsInbox.Enqueue(row.NetworkId, row.Count, row.K0, row.K1, row.K2);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
