using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Baked entity references to ghost prefabs for runtime spawning. Populated by
    /// <see cref="GamePrefabsRegistryAuthoring"/> at bake time. Server systems (map generation,
    /// team spawn, gem economy) instantiate entities from these prefab entities via ECB.
    /// </summary>
    public struct GamePrefabs : IComponentData
    {
        /// <summary>Starship ghost prefab — player-controlled ship.</summary>
        public Entity Ship;
        /// <summary>Planet ghost prefab — home and neutral planets.</summary>
        public Entity Planet;
        /// <summary>Asteroid ghost prefab — mineable gem source.</summary>
        public Entity Asteroid;
        /// <summary>Gem ghost prefab — collectible currency pickup.</summary>
        public Entity Gem;
        /// <summary>People transport ghost prefab — population transfer projectile.</summary>
        public Entity PeopleTransport;
    }
}
