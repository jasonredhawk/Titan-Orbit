using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Converts editor <see cref="MapGenerationSettings"/> ScriptableObject into ECS
    /// <see cref="MapGenerationConfig"/> for server map generation. Used by scene bake,
    /// <see cref="MapGenerationSettingsCache"/> runtime loader, and <see cref="MapGenerationSystem"/>.
    /// </summary>
    public static class MapGenerationConfigUtility
    {
        // --- Type members ---
        /// <summary>
        /// Maps every designer-tunable field from the asset into the ECS struct consumed by
        /// <see cref="MapGenerationLogic"/>. No gameplay logic here — pure data copy.
        /// </summary>
        public static MapGenerationConfig FromSettings(MapGenerationSettings s) => new MapGenerationConfig
        {
            Seed = s.seed,
            MinMapSize = s.minMapSize,
            MaxMapSize = s.maxMapSize,
            MinTeamsPerMatch = s.minTeamsPerMatch,
            MaxTeamsPerMatch = s.maxTeamsPerMatch,
            HomePlanetSize = s.homePlanetSize,
            HomePlanetLevel = s.homePlanetLevel,
            HomePlanetDistance = s.homePlanetDistance,
            MinHomePlanetPairSeparation = s.minHomePlanetPairSeparation,
            ClearanceRadiusAroundHomePlanet = s.clearanceRadiusAroundHomePlanet,
            MinNeutralPlanets = s.minNeutralPlanets,
            MaxNeutralPlanets = s.maxNeutralPlanets,
            MinPlanetSize = s.minPlanetSize,
            MaxPlanetSize = s.maxPlanetSize,
            RandomizeNeutralStartingLevel = (byte)(s.randomizeNeutralStartingLevel ? 1 : 0),
            MinNeutralStartingLevel = s.minNeutralStartingLevel,
            MaxNeutralStartingLevel = s.maxNeutralStartingLevel,
            PlanetRingPlacementMargin = s.planetRingPlacementMargin,
            AsteroidsAtMinMapSize = s.asteroidsAtMinMapSize,
            AsteroidsAtMaxMapSize = s.asteroidsAtMaxMapSize,
            MinAsteroidClusters = s.minAsteroidClusters,
            MaxAsteroidClusters = s.maxAsteroidClusters,
            MinAsteroidGemValue = s.minAsteroidGemValue,
            MaxAsteroidGemValue = s.maxAsteroidGemValue,
            MinAsteroidSpacing = s.minAsteroidSpacing,
        };

        /// <summary>
        /// Fallback config when no ScriptableObject is assigned in the scene or Resources cache.
        /// Creates a throwaway <see cref="MapGenerationSettings"/> with Unity default field values.
        /// </summary>
        public static MapGenerationConfig Default() =>
            FromSettings(ScriptableObject.CreateInstance<MapGenerationSettings>());
    }
}
