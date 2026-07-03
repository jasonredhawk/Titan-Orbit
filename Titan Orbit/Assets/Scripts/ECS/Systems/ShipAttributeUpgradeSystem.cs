using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Server RPC handler for bottom-bar ship attribute gem upgrades.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipAttributeUpgradeSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

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

        static int GetSenderNetworkId(EntityManager em, Entity connection)
        {
            if (connection == Entity.Null || !em.HasComponent<NetworkId>(connection))
                return -1;
            return em.GetComponentData<NetworkId>(connection).Value;
        }
    }
}
