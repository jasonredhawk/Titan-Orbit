using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Ensures bullet event buffers exist on client and server worlds (migration-safe).
    /// Runs once at startup then disables itself — idempotent buffer repair for older saves.</summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct EnsureBulletBufferSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;

            var em = state.EntityManager;
            // [STANDARD] Add missing buffers without destroying existing bullet data.
            if (!em.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                em.AddBuffer<BulletSpawnEventElement>(bulletEntity);
            if (!em.HasBuffer<BulletHitEventElement>(bulletEntity))
                em.AddBuffer<BulletHitEventElement>(bulletEntity);

            // [ECS/DOTS] One-shot bootstrap — no need to run every frame.
            state.Enabled = false;
        }
    }
}
