using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-tick planet data for ship motor orbit detection and enemy moon-shield repel.
    /// Collected once per drive-system update on the main thread, then shared read-only by the
    /// Burst <see cref="ShipPhysicsDriveJob"/> for every ship — never queried per ship inside the job.
    /// </summary>
    public struct PlanetMotorSnapshot
    {
        /// <summary>Ownership, level, and planet id for orbit ring membership and team checks.</summary>
        public PlanetState Planet;

        /// <summary>Gem-moon shield and reservoir — used by shield repel when CurrentShield &gt; 0.</summary>
        public PlanetGemMoonState Moon;

        /// <summary>World pose at collect time — position and uniform scale (planet size).</summary>
        public LocalTransform Transform;

        /// <summary>
        /// Precomputed moon shield outer radius in world units at collect time —
        /// avoids repeating scale math inside Burst shield repel.
        /// </summary>
        public float ShieldOuterRadiusWorld;
    }

    /// <summary>
    /// Builds a <see cref="PlanetMotorSnapshot"/> list once per movement/drive tick.
    /// [ECS/DOTS] Uses CreateEntityQuery so the caller owns disposal; safe for TempJob + ScheduleParallel.
    /// </summary>
    public static class PlanetMotorSnapshotCollection
    {
        /// <summary>
        /// Scans all planet entities and returns snapshots for orbit + shield math this tick.
        /// Caller must Dispose the list (or Dispose via job dependency).
        /// </summary>
        /// <param name="state">System state used only for EntityManager access.</param>
        /// <param name="allocator">Usually <see cref="Allocator.TempJob"/> when feeding a parallel job.</param>
        /// <returns>Native list of planet snapshots (empty if no planets).</returns>
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

            // --- One snapshot per planet (shared by all ships this drive tick) ---
            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var planet = em.GetComponentData<PlanetState>(entity);
                var transform = em.GetComponentData<LocalTransform>(entity);
                // Moon component may be missing for a frame before PlanetGemMoonEnsureSystem runs.
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
