using System.Collections.Generic;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Client-side set of planet ghost entities that have finished GhostSpawn Instantiates.
    /// Populated one entity at a time from <see cref="Game.MapBodyHybridVisualInstantiateHook"/>
    /// (and hybrid proxy create as a backup) — never a full planet <c>ToEntityArray</c>.
    /// <para>
    /// [TITAN-ORBIT] Under session-long <see cref="ClientJoinSettleCache.TransformQuarantine"/>,
    /// client predicted ship drive cannot Collect planets via archetype gather (Crash!!!).
    /// This registry lets <see cref="PlanetMotorSnapshotCollection.CollectFromClientRegistry"/>
    /// build orbit / moon-shield snapshots with per-entity reads only, so passive orbit motor
    /// matches the server and reconciliation stays quiet (no choppy ring coast).
    /// </para>
    /// Lives in TitanOrbit.ECS so the Instantiates hook can call it without a Game↔ECS cycle.
    /// </summary>
    public static class PlanetClientEntityRegistry
    {
        /// <summary>Instantiated planet ghosts still considered live on this client.</summary>
        static readonly HashSet<Entity> LivePlanets = new HashSet<Entity>();

        /// <summary>
        /// Called after a planet ghost Instantiates (or when a hybrid planet proxy is first created).
        /// Idempotent — safe if both hook and visualizer notify the same entity.
        /// </summary>
        /// <param name="entity">Planet ghost entity that just became Instantiated / proxied.</param>
        public static void NotifyInstantiated(Entity entity)
        {
            // --- Guard null ---
            // [ECS/DOTS] Entity.Null is never a valid planet ghost.
            if (entity == Entity.Null)
                return;

            LivePlanets.Add(entity);
        }

        /// <summary>Removes a despawned planet from the live set (proxy destroy / leave session).</summary>
        /// <param name="entity">Planet entity that was destroyed or unproxied.</param>
        public static void NotifyDestroyed(Entity entity)
        {
            LivePlanets.Remove(entity);
        }

        /// <summary>
        /// Copies live Instantiated planet entities into <paramref name="dst"/> (cleared first).
        /// Used by quarantine-safe motor Collect — caller then does per-entity component reads.
        /// </summary>
        /// <param name="dst">Destination list; cleared then filled. Null is a no-op.</param>
        public static void CopyLive(List<Entity> dst)
        {
            if (dst == null)
                return;

            dst.Clear();
            foreach (var e in LivePlanets)
                dst.Add(e);
        }

        /// <summary>How many Instantiated planets are currently tracked (debug / loading).</summary>
        public static int Count => LivePlanets.Count;

        /// <summary>True when this Instantiated planet is still tracked as live.</summary>
        public static bool Contains(Entity entity) => LivePlanets.Contains(entity);

        /// <summary>Clears all tracking (leave NetworkStreamInGame / domain reload / SubsystemRegistration).</summary>
        public static void Clear()
        {
            LivePlanets.Clear();
        }
    }
}
