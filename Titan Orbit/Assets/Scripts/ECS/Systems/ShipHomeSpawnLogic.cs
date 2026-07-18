using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared helper that finds a team's home-planet spawn point on the XZ flight plane.
    /// Used by death respawn, rejoin resume, and any server path that must place a ship at home
    /// rather than at its last world position. Client prediction does not call this — spawn pose
    /// is server-authoritative and replicated via the ship ghost.
    /// </summary>
    public static class ShipHomeSpawnLogic
    {
        /// <summary>
        /// World-space offset from the home planet center so the hull does not spawn inside the
        /// planet collider. Matches <see cref="TeamManagementSystem"/> new-ship spawn.
        /// </summary>
        public const float HomeSpawnOffsetX = 20f;

        /// <summary>
        /// Resolves spawn position for <paramref name="team"/>: live <see cref="HomePlanetTag"/>
        /// entity first, then baked <see cref="MapLayoutEntryElement"/> fallback, then origin.
        /// </summary>
        /// <param name="em">Server EntityManager used for planet and layout queries.</param>
        /// <param name="team">Team whose home planet we want.</param>
        /// <returns>World position near the home planet (planet + X offset).</returns>
        public static float3 FindHomeSpawnPosition(EntityManager em, TeamId team)
        {
            // --- Prefer live home planet entities ---
            // [ECS/DOTS] HomePlanetTag marks team capitals after map generation.
            float3 homePos = float3.zero;
            bool found = false;

            using (var homes = em.CreateEntityQuery(
                       ComponentType.ReadOnly<PlanetState>(),
                       ComponentType.ReadOnly<LocalTransform>(),
                       ComponentType.ReadOnly<PlanetTag>(),
                       ComponentType.ReadOnly<HomePlanetTag>()))
            using (var entities = homes.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var planet = em.GetComponentData<PlanetState>(entities[i]);
                    // Ownership must match — neutrals are never homes.
                    if (planet.Ownership != team)
                        continue;

                    homePos = em.GetComponentData<LocalTransform>(entities[i]).Position;
                    found = true;
                    break;
                }
            }

            // --- Fallback: baked map layout buffer on MapStateSingleton ---
            // [TITAN-ORBIT] EntityKind 1 = home planet slot written during map generation.
            if (!found)
            {
                using var mapQuery = em.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
                if (mapQuery.CalculateEntityCount() == 1)
                {
                    var mapEntity = mapQuery.GetSingletonEntity();
                    if (em.HasBuffer<MapLayoutEntryElement>(mapEntity))
                    {
                        var layout = em.GetBuffer<MapLayoutEntryElement>(mapEntity);
                        for (int i = 0; i < layout.Length; i++)
                        {
                            var entry = layout[i];
                            if (entry.EntityKind == 1 && entry.Team == team)
                            {
                                homePos = entry.Position;
                                found = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (!found)
                return float3.zero;

            return homePos + new float3(HomeSpawnOffsetX, 0f, 0f);
        }
    }
}
