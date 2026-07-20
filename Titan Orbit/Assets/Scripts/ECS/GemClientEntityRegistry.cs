using System.Collections.Generic;
using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Client-side set of gem ghost entities that have finished GhostSpawn Instantiates.
    /// Populated from <see cref="Game.MapBodyHybridVisualInstantiateHook"/> — one entity at a time,
    /// never a full gem <c>ToEntityArray</c>. Tractor beam VFX and urgent gem proxies read this
    /// under TransformQuarantine (same join-safe idea as the hybrid proxy dictionary).
    /// Lives in TitanOrbit.ECS so the Instantiates hook (ECS assembly) can call it without a
    /// circular Game↔ECS reference.
    /// </summary>
    public static class GemClientEntityRegistry
    {
        static readonly HashSet<Entity> LiveGems = new HashSet<Entity>();
        static readonly List<Entity> UrgentVisualQueue = new List<Entity>(8);

        /// <summary>
        /// Called after a gem ghost Instantiates. Tracks the entity and requests an immediate GO proxy.
        /// </summary>
        public static void NotifyInstantiated(Entity entity)
        {
            if (entity == Entity.Null)
                return;

            LiveGems.Add(entity);
            if (!UrgentVisualQueue.Contains(entity))
                UrgentVisualQueue.Add(entity);
        }

        /// <summary>Removes a despawned gem from the live set and urgent queue.</summary>
        public static void NotifyDestroyed(Entity entity)
        {
            LiveGems.Remove(entity);
            UrgentVisualQueue.Remove(entity);
        }

        /// <summary>Copies live Instantiated gem entities into <paramref name="dst"/> (cleared first).</summary>
        public static void CopyLive(List<Entity> dst)
        {
            if (dst == null)
                return;
            dst.Clear();
            foreach (var e in LiveGems)
                dst.Add(e);
        }

        /// <summary>
        /// Drains entities that need a GameObject proxy ASAP (bypass normal asteroid backlog).
        /// </summary>
        public static void DrainUrgentVisualQueue(List<Entity> dst)
        {
            if (dst == null)
                return;
            dst.Clear();
            if (UrgentVisualQueue.Count == 0)
                return;

            dst.AddRange(UrgentVisualQueue);
            UrgentVisualQueue.Clear();
        }

        /// <summary>True when this Instantiated gem is still tracked as live.</summary>
        public static bool Contains(Entity entity) => LiveGems.Contains(entity);

        /// <summary>Clears all tracking (leave NetworkStreamInGame / domain reload).</summary>
        public static void Clear()
        {
            LiveGems.Clear();
            UrgentVisualQueue.Clear();
        }
    }
}
