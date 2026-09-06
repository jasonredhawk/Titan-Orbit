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

        /// <summary>Blueprint slot → live (or soft-destroyed zombie) entity. O(1) HitRpc apply.</summary>
        static readonly Dictionary<int, Entity> SlotToEntity = new Dictionary<int, Entity>(512);

        /// <summary>Reverse map so <see cref="NotifyDestroyed"/> can drop the slot entry.</summary>
        static readonly Dictionary<Entity, int> EntityToSlot = new Dictionary<Entity, int>(512);

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

        /// <summary>Removes a despawned asteroid from the live set and slot map.</summary>
        public static void NotifyDestroyed(Entity entity)
        {
            LiveAsteroids.Remove(entity);
            if (EntityToSlot.TryGetValue(entity, out int slot))
            {
                EntityToSlot.Remove(entity);
                SlotToEntity.Remove(slot);
            }
        }

        /// <summary>
        /// Binds a blueprint slot to this entity. Overwrites a previous occupant of the same slot
        /// (respawn Instantiates a replacement after the zombie is hard-destroyed).
        /// </summary>
        public static void RegisterSlot(Entity entity, int slot)
        {
            if (entity == Entity.Null || slot < 0)
                return;

            if (EntityToSlot.TryGetValue(entity, out int oldSlot) && oldSlot != slot)
                SlotToEntity.Remove(oldSlot);
            if (SlotToEntity.TryGetValue(slot, out Entity previous) && previous != entity)
                EntityToSlot.Remove(previous);

            SlotToEntity[slot] = entity;
            EntityToSlot[entity] = slot;
        }

        /// <summary>O(1) slot lookup. False when the slot was never hydrated or was hard-destroyed.</summary>
        public static bool TryGetBySlot(int slot, out Entity entity)
        {
            if (slot < 0)
            {
                entity = Entity.Null;
                return false;
            }

            return SlotToEntity.TryGetValue(slot, out entity);
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
            SlotToEntity.Clear();
            EntityToSlot.Clear();
        }
    }
}
