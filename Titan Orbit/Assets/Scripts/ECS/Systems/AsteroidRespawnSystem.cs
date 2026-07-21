using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: respawns destroyed asteroids after a delay at the same pose and gem capacity.
    /// Restores the NGO-era <c>AsteroidRespawnManager</c> behavior (default 30s) under NetCode/ECS.
    /// <para>
    /// Flow: <see cref="AsteroidDestructionSystem"/> enqueues <see cref="PendingAsteroidRespawnElement"/>
    /// → this system Instantiates a fresh asteroid ghost when <c>ElapsedTime</c> is due.
    /// Fresh instances avoid carrying stale destroyed state (same reason the original despawned + respawned).
    /// </para>
    /// World: ServerSimulation. Group: SimulationSystemGroup, after destruction.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AsteroidDestructionSystem))]
    public partial struct AsteroidRespawnSystem : ISystem
    {
        /// <summary>
        /// Ensures the respawn-queue singleton exists and waits for asteroid prefabs.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Respawn queue singleton ---
            // [ECS/DOTS] DynamicBuffer on a tagged entity — not a ghost; server-only bookkeeping.
            AsteroidSpawning.EnsureRespawnQueue(state.EntityManager);
            state.RequireForUpdate<GamePrefabs>();
            state.RequireForUpdate<AsteroidRespawnQueueTag>();
        }

        /// <summary>
        /// Spawns every due pending asteroid this tick (same position / scale / MaxGems as before destroy).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Prefab + settings ---
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Asteroid == Entity.Null)
                return;

            // --- Drain due entries ---
            // Delay was baked into RespawnAtElapsedTime at schedule time (settings.AsteroidRespawnDelaySeconds).
            // [STANDARD] Walk backward so RemoveAt is O(1) per removal.
            var buffer = SystemAPI.GetSingletonBuffer<PendingAsteroidRespawnElement>();
            double now = SystemAPI.Time.ElapsedTime;
            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                var pending = buffer[i];
                if (now < pending.RespawnAtElapsedTime)
                    continue;

                AsteroidSpawning.Spawn(
                    state.EntityManager,
                    prefabs.Asteroid,
                    pending.Position,
                    pending.Scale,
                    pending.GemValue);
                buffer.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Shared asteroid Instantiates helper for map bootstrap and timed respawn.
    /// Writes <see cref="AsteroidState"/> with full RemainingGems / Health / MaxGems.
    /// </summary>
    public static class AsteroidSpawning
    {
        /// <summary>
        /// Creates the server-only respawn queue entity if missing.
        /// Safe to call from OnCreate of destruction or respawn systems.
        /// </summary>
        public static void EnsureRespawnQueue(EntityManager em)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<AsteroidRespawnQueueTag>());
            if (!query.IsEmptyIgnoreFilter)
                return;

            // --- One singleton for the whole match ---
            Entity queue = em.CreateEntity();
            em.AddComponent<AsteroidRespawnQueueTag>(queue);
            em.AddBuffer<PendingAsteroidRespawnElement>(queue);
        }

        /// <summary>
        /// Instantiates one asteroid ghost at <paramref name="position"/> with uniform scale and full gem capacity.
        /// </summary>
        /// <param name="em">Server EntityManager.</param>
        /// <param name="asteroidPrefab">Ghost prefab from <see cref="GamePrefabs.Asteroid"/>.</param>
        /// <param name="position">World position (Y forced to 0).</param>
        /// <param name="uniformScale">LocalTransform scale (map gen uses cmax of non-uniform layout).</param>
        /// <param name="gemValue">Full mineable value and health for the fresh rock.</param>
        /// <returns>New asteroid entity, or Null if the prefab is missing.</returns>
        public static Entity Spawn(
            EntityManager em,
            Entity asteroidPrefab,
            float3 position,
            float uniformScale,
            float gemValue)
        {
            if (asteroidPrefab == Entity.Null)
                return Entity.Null;

            // --- Pose ---
            // [TITAN-ORBIT] Keep asteroids on the play plane (Y = 0), same as original respawn manager.
            position.y = 0f;
            float scale = math.max(0.01f, uniformScale);
            float gems = math.max(GemEconomyConstants.MinGemSpawnValue, gemValue);

            Entity e = em.Instantiate(asteroidPrefab);
            em.SetComponentData(e, LocalTransform.FromPositionRotationScale(position, quaternion.identity, scale));

            // --- Mineable state ---
            // MaxGems is server-only so destroy→respawn can restore capacity after RemainingGems hits 0.
            var asteroidState = new AsteroidState
            {
                RemainingGems = gems,
                Health = gems,
                IsDestroyed = false,
                TerritoryTeam = TeamId.None,
                MaxGems = gems,
            };
            if (em.HasComponent<AsteroidState>(e))
                em.SetComponentData(e, asteroidState);
            else
                em.AddComponentData(e, asteroidState);

            if (!em.HasComponent<AsteroidTag>(e))
                em.AddComponent<AsteroidTag>(e);

            return e;
        }

        /// <summary>
        /// Enqueues a respawn for a destroyed asteroid (original ScheduleRespawn).
        /// </summary>
        /// <param name="buffer">Pending respawn buffer on the queue singleton.</param>
        /// <param name="position">Destroy pose.</param>
        /// <param name="uniformScale">Scale to restore.</param>
        /// <param name="gemValue">MaxGems to restore (not RemainingGems, which is often 0).</param>
        /// <param name="nowElapsed">Current server ElapsedTime.</param>
        /// <param name="delaySeconds">Seconds until spawn (settings default 30).</param>
        public static void ScheduleRespawn(
            DynamicBuffer<PendingAsteroidRespawnElement> buffer,
            float3 position,
            float uniformScale,
            float gemValue,
            double nowElapsed,
            float delaySeconds)
        {
            position.y = 0f;
            buffer.Add(new PendingAsteroidRespawnElement
            {
                Position = position,
                Scale = math.max(0.01f, uniformScale),
                GemValue = math.max(GemEconomyConstants.MinGemSpawnValue, gemValue),
                RespawnAtElapsedTime = nowElapsed + math.max(1.0, delaySeconds),
            });
        }
    }
}
