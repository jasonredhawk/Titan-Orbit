using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MoonOrbitRpcClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (result, entity) in SystemAPI.Query<RefRO<ContributedGemsResultRpc>>().WithEntityAccess())
            {
                MoonOrbitClientState.SetContributedGems(result.ValueRO.Amount);
                ecb.DestroyEntity(entity);
            }

            foreach (var (result, entity) in SystemAPI.Query<RefRO<OrbitStoreResultRpc>>().WithEntityAccess())
            {
                if (result.ValueRO.Success == 0 && !result.ValueRO.Message.IsEmpty)
                    MoonOrbitClientState.SetStoreMessage(result.ValueRO.Message.ToString());
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
