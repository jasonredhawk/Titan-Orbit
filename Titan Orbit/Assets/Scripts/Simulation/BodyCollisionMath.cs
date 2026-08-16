using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// World-space collision and presentation radii for ships, planets, and asteroids. Aligns
    /// logical hit tests (bullets, orbit rings) with ECS visual scale — not bullet forgiveness
    /// radii in <see cref="BulletCollision"/>. Used by mining, orbit math, and legacy minimap.
    /// <para>
    /// [TITAN-ORBIT] Whole-hull tier growth: ship <c>LocalTransform.Scale</c> is
    /// <see cref="GetShipTierScale"/> ( +10% per ship level above 1 ). Visual proxies multiply that
    /// by <see cref="ShipPresentationScale"/>. Combat firePower does <b>not</b> use this — only size.
    /// </para>
    /// </summary>
    public static class BodyCollisionMath
    {
        // --- Presentation scale constants ---
        /// <summary>Matches <c>EcsWorldVisualizer</c> ship visual uniform scale factor (level-1 hull).</summary>
        public const float ShipPresentationScale = 0.155f;

        /// <summary>
        /// Extra uniform hull size per ship level above 1 (0.10 = +10% at level 2, +50% at level 6).
        /// Applied to the whole ship via <c>LocalTransform.Scale</c>, not per-component meshes.
        /// </summary>
        public const float ShipTierScalePerLevel = 0.10f;

        /// <summary>Approximate AstroEagle chassis half-width in unscaled model space.</summary>
        public const float ShipHullHalfExtentLocal = 0.85f;

        /// <summary>Floor radius so tiny scale values still collide.</summary>
        public const float MinShipHullRadiusWorld = 0.05f;

        /// <summary>SgtPlanet.radius on planet prefabs before world scale.</summary>
        public const float PlanetMeshBaseRadius = 0.5f;

        /// <summary>SgtPlanet base radius on asteroid prefabs.</summary>
        public const float AsteroidMeshBaseRadius = 0.5f;

        /// <summary>
        /// Max SgtPlanet vertex displacement on asteroid proxies
        /// (<c>WorldBodyVisualApplier</c>). The physics sphere stays at
        /// <see cref="AsteroidMeshBaseRadius"/> so ships bounce on the authored collider;
        /// the drawn rock can puff this far past that sphere in mesh-local units.
        /// </summary>
        public const float AsteroidVisualDisplacementLocal = 0.32f;

        /// <summary>
        /// Uniform ECS scale for a ship at <paramref name="shipLevel"/> (level 1 → 1.0, level 6 → 1.5).
        /// Written to <c>LocalTransform.Scale</c> by <c>ShipStatApplyLogic</c>.
        /// </summary>
        public static float GetShipTierScale(int shipLevel)
        {
            int levelsAfterFirst = math.max(0, shipLevel - 1);
            return 1f + ShipTierScalePerLevel * levelsAfterFirst;
        }

        /// <summary>
        /// World presentation multiplier for hybrid proxies / hull collider bake at a ship level.
        /// Level 1 = <see cref="ShipPresentationScale"/>; higher tiers grow the whole hull.
        /// </summary>
        public static float GetShipPresentationScaleForLevel(int shipLevel) =>
            ShipPresentationScale * GetShipTierScale(shipLevel);

        /// <summary>
        /// Effective ship hull sphere radius in world units from entity LocalTransform.Scale.
        /// Matches visual proxy scale in <see cref="EcsWorldVisualizer"/> for gameplay overlap tests.
        /// When tier scale is stored on <c>LocalTransform.Scale</c>, pass that value here.
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
