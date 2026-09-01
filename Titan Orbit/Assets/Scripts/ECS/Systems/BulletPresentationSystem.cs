using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [LEGACY] Server-world only: clears <see cref="BulletSpawnEventElement"/> after sim writes them.
    /// Client cosmetic tracers are owned by <see cref="Game.BulletVfxDriver"/> via
    /// <see cref="BulletSpawnRpc"/> / <see cref="BulletVfxBridge"/> — not ECS tracer entities.
    /// </summary>
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletPresentationSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ActiveBulletsTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // --- Drain spawn events so the server buffer does not grow unbounded ---
            // [LEGACY] Formerly created BulletTracerState entities; VFX is now bridge/RPC driven.
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;

            if (!state.EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;

            var events = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            if (events.Length > 0)
                events.Clear();
        }
    }

    /// <summary>
    /// [LEGACY] Disabled on clients — map-body tracer hit gathers were quarantine-unsafe.
    /// Impact VFX comes from server <see cref="BulletHitRpc"/> via <see cref="BulletVfxDriver"/>.
    /// Kept as an empty server stub so asmdef / type references stay stable.
    /// </summary>
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(BulletPresentationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct BulletTracerUpdateSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // Intentionally empty — see type summary.
        }
    }
}
