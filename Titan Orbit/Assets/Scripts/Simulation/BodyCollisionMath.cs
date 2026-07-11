using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// World-space collision and presentation radii for ships, planets, and asteroids. Aligns
    /// logical hit tests (bullets, orbit rings) with ECS visual scale — not bullet forgiveness
    /// radii in <see cref="BulletCollision"/>. Used by mining, orbit math, and legacy minimap.
    /// </summary>
    public static class BodyCollisionMath
    {
        // --- Presentation scale constants ---
        /// <summary>Matches <c>EcsWorldVisualizer</c> ship visual uniform scale factor.</summary>
        public const float ShipPresentationScale = 0.155f;

        /// <summary>Approximate AstroEagle chassis half-width in unscaled model space.</summary>
        public const float ShipHullHalfExtentLocal = 0.85f;

        /// <summary>Floor radius so tiny scale values still collide.</summary>
        public const float MinShipHullRadiusWorld = 0.05f;

        /// <summary>SgtPlanet.radius on planet prefabs before world scale.</summary>
        public const float PlanetMeshBaseRadius = 0.5f;

        /// <summary>SgtPlanet base radius on asteroid prefabs.</summary>
        public const float AsteroidMeshBaseRadius = 0.5f;

        /// <summary>
        /// Effective ship hull sphere radius in world units from entity LocalTransform.Scale.
        /// Matches visual proxy scale in <see cref="EcsWorldVisualizer"/> for gameplay overlap tests.
        /// </summary>
        public static float GetShipHullRadiusWorld(float transformScale)
        {
            float presentation = math.max(0.25f, transformScale) * ShipPresentationScale;
            return math.max(MinShipHullRadiusWorld, presentation * ShipHullHalfExtentLocal);
        }

        /// <summary>Planet body radius from baked planet scale component (SgtPlanet mesh × scale).</summary>
        public static float GetPlanetBodyRadiusWorld(float planetScale) =>
            math.max(0.25f, planetScale) * PlanetMeshBaseRadius;

        /// <summary>Asteroid body radius from baked asteroid scale.</summary>
        public static float GetAsteroidBodyRadiusWorld(float asteroidScale) =>
            math.max(0.1f, asteroidScale) * AsteroidMeshBaseRadius;
    }
}
