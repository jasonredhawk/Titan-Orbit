using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server RPC handler for bottom-bar ship attribute gem upgrades. Processes
    /// PurchaseAttributeUpgradeCommand entities created when the client calls
    /// MoonOrbitRpcClient.PurchaseAttributeUpgrade. Resolves sender NetworkId from
    /// ReceiveRpcCommandRequest, delegates to ShipAttributeUpgradeLogic, then destroys the RPC entity.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipAttributeUpgradeSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // [NETCODE] Each RPC arrives as a short-lived entity with command + request components.
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<PurchaseAttributeUpgradeCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                ShipAttributeUpgradeLogic.TryPurchaseForNetworkId(
                    state.EntityManager,
                    networkId,
                    cmd.ValueRO.AttributeIndex,
                    out _);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>Reads NetworkId from the connection entity that sent this RPC.</summary>
        static int GetSenderNetworkId(EntityManager em, Entity connection)
        {
            if (connection == Entity.Null || !em.HasComponent<NetworkId>(connection))
                return -1;
            return em.GetComponentData<NetworkId>(connection).Value;
        }
    }
}
