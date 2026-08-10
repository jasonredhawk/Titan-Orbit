using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Baked entity references to ghost prefabs for runtime spawning. Populated by
    /// <see cref="Authoring.GamePrefabsRegistryAuthoring"/> at bake time into a singleton entity.
    /// Server systems (map generation, team spawn, gem economy) instantiate entities from these
    /// prefab entities via EntityCommandBuffer — never Instantiate GameObjects at runtime on server.
    /// </summary>
    public struct GamePrefabs : IComponentData
    {
        // --- Type members ---
        /// <summary>[NETCODE] Starship ghost prefab — player-controlled ship with physics hull.</summary>
        public Entity Ship;

        /// <summary>[NETCODE] Planet ghost prefab — home and neutral planets with static colliders.</summary>
        public Entity Planet;

        /// <summary>[NETCODE] Asteroid ghost prefab — mineable gem source with static collider.</summary>
        public Entity Asteroid;

        /// <summary>[NETCODE] Gem ghost prefab — collectible currency pickup (scripted motion, no hull collision).</summary>
        public Entity Gem;

        /// <summary>[NETCODE] People transport ghost prefab — population transfer projectile.</summary>
        public Entity PeopleTransport;
    }
}
