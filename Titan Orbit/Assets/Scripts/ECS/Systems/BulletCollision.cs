using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Pure math helpers for swept bullet segment tests on a toroidal map. Shared by
    /// authoritative <see cref="BulletSimulationSystem"/> and cosmetic tracer update systems.
    /// [BurstCompile] target — no EntityManager access.
    /// </summary>
    public static class BulletCollision
    {
        /// <summary>
        /// [TITAN-ORBIT] Logical center repositioned to the map tile nearest unwrapOrigin for toroidal accuracy.
        /// </summary>
        public static float3 UnwrapCenterNear(float3 unwrapOrigin, float3 logicalCenter, float mapW, float mapH)
        {
            // [TITAN-ORBIT] Shortest toroidal offset places obstacle in the same "tile" as the segment.
            float3 center = unwrapOrigin + ToroidalMapEcs.ShortestOffsetXZ(unwrapOrigin, logicalCenter, mapW, mapH);
            center.y = logicalCenter.y;
            return center;
        }

        /// <summary>
        /// [TITAN-ORBIT] Swept segment vs sphere on a torus — unwraps obstacle center near segment start.
        /// </summary>
        public static bool SegmentHitsSphereToroidal(
            float3 from,
            float3 to,
            float3 logicalCenter,
            float radius,
            float mapW,
            float mapH,
            out float3 hitPoint)
        {
            float3 center = UnwrapCenterNear(from, logicalCenter, mapW, mapH);
            return SegmentHitsSphere(from, to, center, radius, out hitPoint);
        }

        /// <summary>
        /// [STANDARD] Swept segment vs sphere — returns the first contact point along [from, to].
        /// Uses quadratic ray-sphere intersection with t clamped to [0,1].
        /// </summary>
        public static bool SegmentHitsSphere(float3 from, float3 to, float3 center, float radius, out float3 hitPoint)
        {
            hitPoint = to;
            // --- Flatten to XZ plane (top-down shooter) ---
            from.y = center.y;
            to.y = center.y;

            float3 delta = to - from;
            float deltaLenSq = math.lengthsq(delta);
            // --- Degenerate segment: point-in-sphere test ---
            if (deltaLenSq < 1e-8f)
                return math.distance(from, center) <= radius;

            // --- Quadratic coefficients for ray-sphere ---
            float3 oc = from - center;
            float a = deltaLenSq;
            float b = 2f * math.dot(oc, delta);
            float c = math.lengthsq(oc) - radius * radius;
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
                return false;

            float sqrtDisc = math.sqrt(discriminant);
            float inv2a = 1f / (2f * a);
            float tEnter = (-b - sqrtDisc) * inv2a;
            float tExit = (-b + sqrtDisc) * inv2a;

            if (tEnter > 1f || tExit < 0f)
                return false;

            float t = math.clamp(tEnter, 0f, 1f);
            if (tEnter < 0f && tExit >= 0f)
                t = 0f;

            hitPoint = from + delta * t;
            hitPoint.y = center.y;
            return true;
        }

        /// <summary>World hit radius for asteroid mesh scale (mining VFX alignment).</summary>
        public static float AsteroidHitRadius(float scale)
        {
            float meshRadius = scale * GemEconomyConstants.AsteroidMeshBaseRadius;
            return math.max(
                GemEconomyConstants.MinAsteroidHitRadius,
                meshRadius * GemEconomyConstants.AsteroidHitRadiusScale);
        }

        /// <summary>Planet body sphere radius from visual scale.</summary>
        public static bool SegmentHitsPlanetToroidal(
            float3 from,
            float3 to,
            float3 logicalPlanetCenter,
            float planetScale,
            float mapW,
            float mapH,
            out float3 hitPoint)
        {
            float radius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetScale);
            return SegmentHitsSphereToroidal(from, to, logicalPlanetCenter, radius, mapW, mapH, out hitPoint);
        }

        /// <summary>
        /// Gem-moon position orbits the planet — unwrap moon center near segment start for toroidal accuracy.
        /// </summary>
        public static bool SegmentHitsMoonNear(
            float3 from,
            float3 to,
            float3 logicalPlanetCenter,
            float planetScale,
            int planetLevel,
            int planetId,
            double elapsedSeconds,
            bool isHomePlanet,
            float hitRadius,
            float mapW,
            float mapH,
            out float3 hitPoint)
        {
            hitPoint = to;
            float3 moonCenter = PlanetOrbitMath.GetMoonWorldPositionNear(
                from,
                logicalPlanetCenter,
                planetScale,
                planetLevel,
                planetId,
                elapsedSeconds,
                mapW,
                mapH);
            return SegmentHitsSphere(from, to, moonCenter, hitRadius, out hitPoint);
        }
    }
}
