using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Baked map-generation parameters copied from <see cref="Data.MapGenerationSettings"/>
    /// ScriptableObject at bake time. Singleton read by map generation systems on server boot to roll
    /// procedural layout (home planets, neutrals, asteroids). Not ghost-replicated — clients receive
    /// the finished layout via <see cref="MapLayoutEntryElement"/> buffer on <see cref="MapStateSingleton"/>.
    /// </summary>
    public struct MapGenerationConfig : IComponentData
    {
        // --- Type members ---
        /// <summary>[TITAN-ORBIT] Deterministic seed for planet/asteroid placement RNG.</summary>
        public int Seed;

        /// <summary>Smallest allowed toroidal map width/height in world units.</summary>
        public float MinMapSize;

        /// <summary>Largest allowed toroidal map width/height in world units.</summary>
        public float MaxMapSize;

        /// <summary>Minimum teams spawned for this match (2–5).</summary>
        public int MinTeamsPerMatch;

        /// <summary>Maximum teams spawned for this match (2–5).</summary>
        public int MaxTeamsPerMatch;

        /// <summary>Collider/visual radius for home planet bodies.</summary>
        public float HomePlanetSize;

        /// <summary>Starting planet level for home worlds (upgrade ladder baseline).</summary>
        public int HomePlanetLevel;

        /// <summary>Distance from map center to home planet spawn ring.</summary>
        public float HomePlanetDistance;

        /// <summary>Minimum angular separation between two home planets on the spawn ring.</summary>
        public float MinHomePlanetPairSeparation;

        /// <summary>Exclusion radius around each home planet where neutrals cannot spawn.</summary>
        public float ClearanceRadiusAroundHomePlanet;

        /// <summary>Lower bound on neutral planet count for rolled map size.</summary>
        public int MinNeutralPlanets;

        /// <summary>Upper bound on neutral planet count for rolled map size.</summary>
        public int MaxNeutralPlanets;

        /// <summary>
        /// How many non-home planets each team starts owning (0 = all stay neutral).
        /// Capped/evened against available neutrals at spawn time.
        /// </summary>
        public int StartingOwnedNeutralPlanetsPerTeam;

        /// <summary>Smallest neutral planet body radius.</summary>
        public float MinPlanetSize;

        /// <summary>Largest neutral planet body radius.</summary>
        public float MaxPlanetSize;

        /// <summary>1 = roll random starting level per neutral; 0 = use fixed level.</summary>
        public byte RandomizeNeutralStartingLevel;

        /// <summary>Minimum neutral starting level when randomization is enabled.</summary>
        public int MinNeutralStartingLevel;

        /// <summary>Maximum neutral starting level when randomization is enabled.</summary>
        public int MaxNeutralStartingLevel;

        /// <summary>Padding from map edge when placing neutral planet ring.</summary>
        public float PlanetRingPlacementMargin;

        /// <summary>Target asteroid count when map size is at minimum (lerped toward max with map size).</summary>
        public int AsteroidsAtMinMapSize;

        /// <summary>Target asteroid count when map size is at maximum (lerped by map size).</summary>
        public int AsteroidsAtMaxMapSize;

        /// <summary>Minimum clusters per asteroid field group.</summary>
        public int MinAsteroidClusters;

        /// <summary>Maximum clusters per asteroid field group.</summary>
        public int MaxAsteroidClusters;

        /// <summary>Smallest gem value per mineable asteroid.</summary>
        public float MinAsteroidGemValue;

        /// <summary>Largest gem value per mineable asteroid.</summary>
        public float MaxAsteroidGemValue;

        /// <summary>Minimum distance between asteroid cluster centers.</summary>
        public float MinAsteroidSpacing;
    }
}
