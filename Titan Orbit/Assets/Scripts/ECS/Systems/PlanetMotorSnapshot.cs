using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-tick planet data for ship motor orbit detection and moon-shield repel.
    /// Collected once per movement system update — never queried per ship.
    /// </summary>
    public struct PlanetMotorSnapshot
    {
        public PlanetState Planet;
        public PlanetGemMoonState Moon;
        public LocalTransform Transform;
        /// <summary>Precomputed at collect time — avoids Mathf in Burst shield repel.</summary>
        public float ShieldOuterRadiusWorld;
    }

    /// <summary>Builds <see cref="PlanetMotorSnapshot"/> once per movement system tick.</summary>
    public static class PlanetMotorSnapshotCollection
    {
        public static NativeList<PlanetMotorSnapshot> Collect(ref SystemState state, Allocator allocator)
        {
            var list = new NativeList<PlanetMotorSnapshot>(allocator);
            var em = state.EntityManager;
            // [ECS/DOTS] CreateEntityQuery (not state.GetEntityQuery) — caller-owned; safe to dispose.
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Allocator.Temp);

            // --- One snapshot per planet (shared by all ships this movement tick) ---
            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var planet = em.GetComponentData<PlanetState>(entity);
                var transform = em.GetComponentData<LocalTransform>(entity);
                var moon = em.HasComponent<PlanetGemMoonState>(entity)
                    ? em.GetComponentData<PlanetGemMoonState>(entity)
                    : default;

                list.Add(new PlanetMotorSnapshot
                {
                    Planet = planet,
                    Moon = moon,
                    Transform = transform,
                    ShieldOuterRadiusWorld = PlanetGemMoonMath.GetMoonShieldOuterRadiusWorld(
                        math.max(0.25f, transform.Scale),
                        planet.IsHomePlanet),
                });
            }

            return list;
        }
    }
}
