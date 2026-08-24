using System.Collections.Generic;
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

        /// <summary>
        /// Precomputed moon body radius in world units at collect time —
        /// used by Burst moon-dock attach (surface contact) without calling managed Mathf helpers.
        /// </summary>
        public float MoonBodyRadiusWorld;
    }

    /// <summary>
    /// Builds a <see cref="PlanetMotorSnapshot"/> list once per movement/drive tick.
    /// [ECS/DOTS] Server uses CreateEntityQuery + <c>ToEntityArray</c>; client under
    /// TransformQuarantine uses <see cref="CollectFromClientRegistry"/> (Instantiates registry)
    /// so passive orbit prediction matches authority without a Crash!!! planet gather.
    /// </summary>
    public static class PlanetMotorSnapshotCollection
    {
        /// <summary>
        /// [STANDARD] Reused managed scratch for registry → NativeList copies.
        /// Avoids allocating a new <see cref="List{T}"/> every predicted fixed step.
        /// </summary>
        static readonly List<Entity> s_RegistryScratch = new List<Entity>(32);

        /// <summary>
        /// Scans all planet entities and returns snapshots for orbit + shield math this tick.
        /// Caller must Dispose the list (or Dispose via job dependency).
        /// <para>
        /// [TITAN-ORBIT] Uses planet <c>ToEntityArray</c> — safe on the <b>server</b> only.
        /// On clients under <see cref="ClientJoinSettleCache.TransformQuarantine"/> this gather
        /// Crash!!! — use <see cref="CollectFromClientRegistry"/> instead
        /// (see <see cref="ShipPhysicsDriveSystem"/>).
        /// </para>
        /// </summary>
        /// <param name="state">System state used only for EntityManager access.</param>
        /// <param name="allocator">Usually <see cref="Allocator.TempJob"/> when feeding a parallel job.</param>
        /// <returns>Native list of planet snapshots (empty if no planets).</returns>
        public static NativeList<PlanetMotorSnapshot> Collect(ref SystemState state, Allocator allocator)
        {
            var list = new NativeList<PlanetMotorSnapshot>(allocator);
            var em = state.EntityManager;

            // [ECS/DOTS] CreateEntityQuery (not state.GetEntityQuery) — caller-owned; safe to dispose.
            // [TITAN-ORBIT] Full planet ToEntityArray — server motor only. Client must not call this
            // while TransformQuarantine is on (session-long after late-join).
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Allocator.Temp);

            // --- One snapshot per planet (shared by all ships this drive tick) ---
            for (int i = 0; i < entities.Length; i++)
                TryAddSnapshot(em, entities[i], list);

            return list;
        }

        /// <summary>
        /// Quarantine-safe Collect for client prediction: walks
        /// <see cref="PlanetClientEntityRegistry"/> (Instantiates-hook entities) and reads each
        /// planet with <c>Exists</c> / <c>HasComponent</c> / <c>GetComponentData</c> only.
        /// <para>
        /// [TITAN-ORBIT] Never uses <c>ToEntityArray</c> / archetype gathers — required while
        /// <see cref="ClientJoinSettleCache.TransformQuarantine"/> is session-long. Without this,
        /// predicted drive had an empty planet list → coast friction while server applied orbit
        /// → reconcile stepped the hull in the ring (choppy coast).
        /// </para>
        /// </summary>
        /// <param name="state">Client system state (EntityManager for per-entity reads).</param>
        /// <param name="allocator">Usually <see cref="Allocator.TempJob"/> when feeding a parallel job.</param>
        /// <returns>Native list of snapshots for Instantiated planets still alive this tick.</returns>
        public static NativeList<PlanetMotorSnapshot> CollectFromClientRegistry(
            ref SystemState state,
            Allocator allocator)
        {
            var list = new NativeList<PlanetMotorSnapshot>(allocator);
            var em = state.EntityManager;

            // --- Copy Instantiates-tracked planet entities (managed set → scratch list) ---
            // [HYBRID] Registry is filled one entity at a time from GhostSpawn Instantiates /
            // hybrid proxy create — same join-safe idea as GemClientEntityRegistry.
            PlanetClientEntityRegistry.CopyLive(s_RegistryScratch);

            // --- Per-entity snapshot (no archetype gather) ---
            for (int i = 0; i < s_RegistryScratch.Count; i++)
                TryAddSnapshot(em, s_RegistryScratch[i], list);

            return list;
        }

        /// <summary>
        /// Appends one planet snapshot when the entity still exists and has planet components.
        /// Skips despawned registry leftovers and incomplete Instantiates frames.
        /// </summary>
        /// <param name="em">World EntityManager for component reads.</param>
        /// <param name="entity">Candidate planet entity.</param>
        /// <param name="list">Destination snapshot list (mutated).</param>
        static void TryAddSnapshot(EntityManager em, Entity entity, NativeList<PlanetMotorSnapshot> list)
        {
            // --- Still alive + has motor components ---
            // [ECS/DOTS] Per-entity Exists/HasComponent is safe under TransformQuarantine;
            // full planet archetype ToEntityArray is not.
            if (entity == Entity.Null ||
                !em.Exists(entity) ||
                !em.HasComponent<PlanetTag>(entity) ||
                !em.HasComponent<PlanetState>(entity) ||
                !em.HasComponent<LocalTransform>(entity))
                return;

            var planet = em.GetComponentData<PlanetState>(entity);
            var transform = em.GetComponentData<LocalTransform>(entity);
            // Moon component may be missing for a frame before PlanetGemMoonEnsureSystem runs.
            var moon = em.HasComponent<PlanetGemMoonState>(entity)
                ? em.GetComponentData<PlanetGemMoonState>(entity)
                : default;

            float planetSize = math.max(0.25f, transform.Scale);
            list.Add(new PlanetMotorSnapshot
            {
                Planet = planet,
                Moon = moon,
                Transform = transform,
                ShieldOuterRadiusWorld = PlanetGemMoonMath.GetMoonShieldOuterRadiusWorld(
                    planetSize,
                    planet.IsHomePlanet),
                // [TITAN-ORBIT] Collected on main thread — PlanetGemMoonMath uses Mathf (not Burst-safe).
                MoonBodyRadiusWorld = PlanetGemMoonMath.GetMoonBodyRadiusWorld(
                    planetSize,
                    planet.IsHomePlanet),
            });
        }
    }
}
