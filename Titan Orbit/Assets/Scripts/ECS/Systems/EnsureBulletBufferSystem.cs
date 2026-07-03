using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Ensures bullet event buffers exist on client and server worlds (migration-safe).</summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct EnsureBulletBufferSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;

            var em = state.EntityManager;
            if (!em.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                em.AddBuffer<BulletSpawnEventElement>(bulletEntity);
            if (!em.HasBuffer<BulletHitEventElement>(bulletEntity))
                em.AddBuffer<BulletHitEventElement>(bulletEntity);

            state.Enabled = false;
        }
    }
}
