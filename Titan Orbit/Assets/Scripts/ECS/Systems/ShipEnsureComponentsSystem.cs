using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>Ensures runtime-spawned ship ghosts have kinematics even if the subscene bake is stale.</summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ShipMovementSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipEnsureComponentsSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipKinematics>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipKinematics());

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
