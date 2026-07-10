using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>Baked map-generation parameters (from <see cref="Data.MapGenerationSettings"/>).
    /// Singleton read by <see cref="MapGenerationSystem"/> on server boot to roll procedural layout.</summary>
    public struct MapGenerationConfig : IComponentData
    {
        public int Seed;
        public float MinMapSize;
        public float MaxMapSize;
        public int MinTeamsPerMatch;
        public int MaxTeamsPerMatch;
        public float HomePlanetSize;
        public int HomePlanetLevel;
        public float HomePlanetDistance;
        public float MinHomePlanetPairSeparation;
        public float ClearanceRadiusAroundHomePlanet;
        public int MinNeutralPlanets;
        public int MaxNeutralPlanets;
        public float MinPlanetSize;
        public float MaxPlanetSize;
        public byte RandomizeNeutralStartingLevel;
        public int MinNeutralStartingLevel;
        public int MaxNeutralStartingLevel;
        public float PlanetRingPlacementMargin;
        public int AsteroidsAtMinMapSize;
        public int AsteroidsAtMaxMapSize;
        public int MinAsteroidClusters;
        public int MaxAsteroidClusters;
        public float MinAsteroidGemValue;
        public float MaxAsteroidGemValue;
        public float MinAsteroidSpacing;
    }
}
