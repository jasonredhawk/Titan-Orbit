using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Ensures bullet event buffers exist on client and server worlds (migration-safe).
    /// Runs once at startup in InitializationSystemGroup then disables itself — idempotent buffer
    /// repair for older saves or prefabs missing BulletSpawnEventElement / BulletHitEventElement.
    /// Paired with <see cref="BulletSimulationSystem"/> and <see cref="BulletPresentationSystem"/>.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct EnsureBulletBufferSystem : ISystem
    {
        /// <summary>
        /// [ECS/DOTS] One-shot: add missing buffers on the ActiveBulletsTag singleton, then disable.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Find bullet singleton ---
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;

            var em = state.EntityManager;

            // --- Repair missing buffers ---
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
