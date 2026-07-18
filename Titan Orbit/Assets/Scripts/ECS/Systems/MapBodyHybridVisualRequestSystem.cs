using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Client post-settle orphan catcher: marks Instantiated planet/asteroid/gem ghosts
    /// that still need a GameObject proxy. Normal join path bakes
    /// <see cref="MapBodyHybridVisualPending"/> on client ghost prefabs so Instantiates already
    /// queue visuals without this system.
    /// <para>
    /// CRITICAL (Player.log 2026-07-18): <c>MarkFromQuery</c> → <c>ToEntityArray</c> Crash!!! when
    /// placeholders and spawn buffer were empty mid-join settle — gathering hundreds of Instantiated
    /// asteroids is unsafe until <see cref="ClientJoinSettleCache.Settling"/> is false.
    /// This system must no-op for the entire settle window, not only while placeholders exist.
    /// </para>
    /// World: ClientSimulation. Group: SimulationSystemGroup (after GhostSimulationSystemGroup).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    public partial struct MapBodyHybridVisualRequestSystem : ISystem
    {
        /// <summary>
        /// Max new Pending tags per frame after settle (orphan catch-up only).
        /// Keep low — each AddComponent is a structural change.
        /// </summary>
        public const int MaxMarksPerFrame = 8;

        EntityQuery _asteroidQuery;
        EntityQuery _planetQuery;
        EntityQuery _gemQuery;
        EntityQuery _placeholderQuery;

        /// <summary>Builds filtered queries (no SystemAPI entity foreach).</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();

            _placeholderQuery = state.GetEntityQuery(ComponentType.ReadOnly<PendingSpawnPlaceholder>());

            _asteroidQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.Exclude<PendingSpawnPlaceholder>(),
                ComponentType.Exclude<MapBodyHybridVisualPending>(),
                ComponentType.Exclude<MapBodyHybridVisualLinked>());

            _planetQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.Exclude<PendingSpawnPlaceholder>(),
                ComponentType.Exclude<MapBodyHybridVisualPending>(),
                ComponentType.Exclude<MapBodyHybridVisualLinked>());

            _gemQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.Exclude<PendingSpawnPlaceholder>(),
                ComponentType.Exclude<MapBodyHybridVisualPending>(),
                ComponentType.Exclude<MapBodyHybridVisualLinked>());
        }

        /// <summary>
        /// Adds Pending tags only after join settle — never during GhostSpawn Instantiates settle.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete();

            // --- Join settle gate (hard crash if violated) ---
            // [TITAN-ORBIT] Player.log: MarkFromQuery ToEntityArray Crash!!! while Settling even
            // though placeholders and GhostSpawnBuffer were empty for a frame between Instantiates waves.
            if (ClientJoinSettleCache.Settling)
                return;

            // --- Instantiates structural safety ---
            // Belt-and-suspenders if settle exited early but GhostSpawn is still mutating.
            if (!_placeholderQuery.IsEmptyIgnoreFilter)
                return;

            var em = state.EntityManager;
            if (SystemAPI.TryGetSingletonEntity<GhostSpawnQueue>(out Entity spawnQueue) &&
                em.HasBuffer<GhostSpawnBuffer>(spawnQueue) &&
                em.GetBuffer<GhostSpawnBuffer>(spawnQueue).Length > 0)
                return;

            int marked = 0;
            marked += MarkFromQuery(em, _asteroidQuery, MaxMarksPerFrame - marked);
            if (marked < MaxMarksPerFrame)
                marked += MarkFromQuery(em, _planetQuery, MaxMarksPerFrame - marked);
            if (marked < MaxMarksPerFrame)
                MarkFromQuery(em, _gemQuery, MaxMarksPerFrame - marked);
        }

        /// <summary>
        /// Marks up to <paramref name="maxMarks"/> Instantiated map bodies with Pending.
        /// Only called after settle — full gather is then safe (same window as DrawAsteroids).
        /// </summary>
        static int MarkFromQuery(EntityManager em, EntityQuery query, int maxMarks)
        {
            if (maxMarks <= 0 || query.IsEmptyIgnoreFilter)
                return 0;

            using var entities = query.ToEntityArray(Allocator.Temp);
            int marked = 0;
            for (int i = 0; i < entities.Length && marked < maxMarks; i++)
            {
                Entity entity = entities[i];
                if (!em.Exists(entity))
                    continue;
                if (em.HasComponent<PendingSpawnPlaceholder>(entity) ||
                    em.HasComponent<MapBodyHybridVisualPending>(entity) ||
                    em.HasComponent<MapBodyHybridVisualLinked>(entity))
                    continue;

                em.AddComponentData(entity, new MapBodyHybridVisualPending());
                marked++;
            }

            return marked;
        }
    }
}
