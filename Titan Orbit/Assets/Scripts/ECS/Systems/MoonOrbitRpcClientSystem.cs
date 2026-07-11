using TitanOrbit.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-side handler for moon orbit store RPC replies — contributed gem balance and purchase
    /// results. Consumes ephemeral RPC entities spawned by the server and writes into
    /// <see cref="MoonOrbitClientState"/> for <see cref="UI.OrbitStationUI"/>. World: ClientSimulation.
    /// Paired with server handlers in <see cref="MoonOrbitStoreSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MoonOrbitRpcClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Contributed gem balance reply ---
            // [NETCODE] RPC entity — destroy after copying payload to client UI state.
            foreach (var (result, entity) in SystemAPI.Query<RefRO<ContributedGemsResultRpc>>().WithEntityAccess())
            {
                MoonOrbitClientState.SetContributedGems(result.ValueRO.Amount);
                ecb.DestroyEntity(entity);
            }

            // --- Store purchase success/failure messages ---
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
