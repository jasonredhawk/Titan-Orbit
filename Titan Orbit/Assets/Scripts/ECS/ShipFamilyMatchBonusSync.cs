using TitanOrbit.Data;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Copies <see cref="MapStateSingleton.BlueprintSeed"/> into
    /// <see cref="ShipFamilyMatchBonuses"/> so stat apply and the fleet-bonus UI
    /// share one roll. The query runs once per world; later ticks read the cached
    /// map entity. A zero seed is left alone so a client that already learned the
    /// recipe is not wiped before the ghost arrives.
    /// </summary>
    public static class ShipFamilyMatchBonusSync
    {
        static World s_World;
        static Entity s_MapEntity;

        /// <summary>
        /// Publishes the live map seed when this world has one. Safe to call from
        /// ship stat apply — it does not allocate after the first lookup.
        /// </summary>
        public static void PublishFromMap(EntityManager em)
        {
            if (!TryReadSeed(em, out uint seed))
                return;

            ShipFamilyMatchBonuses.PublishMatchSeed(seed);
        }

        /// <summary>
        /// Reads the ghosted / server map seed. Returns false when the singleton
        /// is missing or still 0 (map generation has not rolled yet).
        /// </summary>
        static bool TryReadSeed(EntityManager em, out uint seed)
        {
            seed = 0;
            World world = em.World;
            if (world == null || !world.IsCreated)
                return false;

            // --- Cached map entity ---
            // [TITAN-ORBIT] CreateEntityQuery allocates. Do it once per world, then
            // read BlueprintSeed directly. A destroyed map (new game) misses the
            // cache and looks up again.
            if (s_World != world
                || s_MapEntity == Entity.Null
                || !em.Exists(s_MapEntity)
                || !em.HasComponent<MapStateSingleton>(s_MapEntity))
            {
                s_World = world;
                s_MapEntity = Entity.Null;
                EntityQuery query = em.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
                try
                {
                    if (query.IsEmptyIgnoreFilter)
                        return false;
                    s_MapEntity = query.GetSingletonEntity();
                }
                finally
                {
                    query.Dispose();
                }
            }

            int raw = em.GetComponentData<MapStateSingleton>(s_MapEntity).BlueprintSeed;
            if (raw == 0)
                return false;

            // Stored as int on the ghost. The bit pattern is the original uint seed.
            seed = (uint)raw;
            return true;
        }
    }
}
