using System.Collections.Generic;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Client-side set of asteroid ghost entities that finished GhostSpawn Instantiates.
    /// Populated one entity at a time from <see cref="Game.MapBodyHybridVisualInstantiateHook"/>
    /// — never a full asteroid <c>ToEntityArray</c>.
    /// <para>
    /// [TITAN-ORBIT] Under session-long TransformQuarantine, the loading bar needs hybrid GO
    /// proxies. If Instantiates already ran but the visualizer dictionary was cleared (second
    /// Local Host / Play without Domain Reload), this registry lets the visualizer re-queue
    /// SpawnRequest for missing asteroid proxies without scanning all asteroids.
    /// </para>
    /// </summary>
    public static class AsteroidClientEntityRegistry
    {
        /// <summary>Instantiated asteroid ghosts still considered live on this client.</summary>
        static readonly HashSet<Entity> LiveAsteroids = new HashSet<Entity>();

        /// <summary>
        /// Called after an asteroid ghost Instantiates (or when a hybrid asteroid proxy is created).
        /// Idempotent.
        /// </summary>
        public static void NotifyInstantiated(Entity entity)
        {
            if (entity == Entity.Null)
                return;
            LiveAsteroids.Add(entity);
        }

        /// <summary>Removes a despawned asteroid from the live set.</summary>
        public static void NotifyDestroyed(Entity entity)
        {
            LiveAsteroids.Remove(entity);
        }

        /// <summary>
        /// Copies live Instantiated asteroid entities into <paramref name="dst"/> (cleared first).
        /// Quarantine-safe — dictionary walk only, not an ECS archetype gather.
        /// </summary>
        public static void CopyLive(List<Entity> dst)
        {
            if (dst == null)
                return;

            dst.Clear();
            foreach (var e in LiveAsteroids)
                dst.Add(e);
        }

        /// <summary>How many Instantiated asteroids are currently tracked.</summary>
        public static int Count => LiveAsteroids.Count;

        /// <summary>Clears all tracking (leave NetworkStreamInGame / SubsystemRegistration).</summary>
        public static void Clear()
        {
            LiveAsteroids.Clear();
        }
    }
}
