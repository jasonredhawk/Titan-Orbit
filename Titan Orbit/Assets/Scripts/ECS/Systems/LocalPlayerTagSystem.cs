using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LocalPlayerTagSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            int localNetworkId = GetLocalNetworkId(ref state);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (owner, entity) in SystemAPI.Query<RefRO<GhostOwner>>().WithAll<ShipTag>().WithEntityAccess())
            {
                bool isLocal = localNetworkId > 0 && owner.ValueRO.NetworkId == localNetworkId;
                if (isLocal && !state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.AddComponent<LocalPlayerShipTag>(entity);
                else if (!isLocal && state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.RemoveComponent<LocalPlayerShipTag>(entity);
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<GhostOwnerIsLocal>>().WithAll<ShipTag>().WithEntityAccess())
            {
                if (!state.EntityManager.HasComponent<LocalPlayerShipTag>(entity))
                    ecb.AddComponent<LocalPlayerShipTag>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        int GetLocalNetworkId(ref SystemState state)
        {
            foreach (var netId in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>())
                return netId.ValueRO.Value;
            return -1;
        }
    }
}
