using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>World-space collision radii aligned with ECS presentation scales (not bullet forgiveness).</summary>
    public static class BodyCollisionMath
    {
        /// <summary>Matches <c>EcsWorldVisualizer.shipVisualScale</c>.</summary>
        public const float ShipPresentationScale = 0.155f;
        /// <summary>Approximate AstroEagle chassis half-width in unscaled model space.</summary>
        public const float ShipHullHalfExtentLocal = 0.85f;
        public const float MinShipHullRadiusWorld = 0.05f;

        /// <summary>SgtPlanet.radius on planet prefabs.</summary>
        public const float PlanetMeshBaseRadius = 0.5f;
        /// <summary>SgtPlanet base radius on <c>Asteroid.prefab</c>.</summary>
        public const float AsteroidMeshBaseRadius = 0.5f;

        public static float GetShipHullRadiusWorld(float transformScale)
        {
            float presentation = math.max(0.25f, transformScale) * ShipPresentationScale;
            return math.max(MinShipHullRadiusWorld, presentation * ShipHullHalfExtentLocal);
        }

        public static float GetPlanetBodyRadiusWorld(float planetScale) =>
            math.max(0.25f, planetScale) * PlanetMeshBaseRadius;

        public static float GetAsteroidBodyRadiusWorld(float asteroidScale) =>
            math.max(0.1f, asteroidScale) * AsteroidMeshBaseRadius;
    }
}
