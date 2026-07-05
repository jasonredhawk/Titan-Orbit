using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    public static class BulletCollision
    {
        /// <summary>Logical center repositioned to the map tile nearest <paramref name="unwrapOrigin"/>.</summary>
        public static float3 UnwrapCenterNear(float3 unwrapOrigin, float3 logicalCenter, float mapW, float mapH)
        {
            float3 center = unwrapOrigin + ToroidalMapEcs.ShortestOffsetXZ(unwrapOrigin, logicalCenter, mapW, mapH);
            center.y = logicalCenter.y;
            return center;
        }

        /// <summary>Swept segment vs sphere on a torus — unwraps obstacle center near the segment start.</summary>
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

        /// <summary>Swept segment vs sphere — returns the first contact point along [from, to].</summary>
        public static bool SegmentHitsSphere(float3 from, float3 to, float3 center, float radius, out float3 hitPoint)
        {
            hitPoint = to;
            from.y = center.y;
            to.y = center.y;

            float3 delta = to - from;
            float deltaLenSq = math.lengthsq(delta);
            if (deltaLenSq < 1e-8f)
                return math.distance(from, center) <= radius;

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

        public static float AsteroidHitRadius(float scale)
        {
            float meshRadius = scale * GemEconomyConstants.AsteroidMeshBaseRadius;
            return math.max(
                GemEconomyConstants.MinAsteroidHitRadius,
                meshRadius * GemEconomyConstants.AsteroidHitRadiusScale);
        }

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

        public static bool SegmentHitsMoonNear(
            float3 from,
            float3 to,
            float3 logicalPlanetCenter,
            float planetScale,
            int planetLevel,
            int planetId,
            double elapsedSeconds,
            bool isHomePlanet,
            float mapW,
            float mapH,
            out float3 hitPoint)
        {
            hitPoint = to;
            float radius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(planetScale, isHomePlanet);
            float3 moonCenter = PlanetOrbitMath.GetMoonWorldPositionNear(
                from,
                logicalPlanetCenter,
                planetScale,
                planetLevel,
                planetId,
                elapsedSeconds,
                mapW,
                mapH);
            return SegmentHitsSphere(from, to, moonCenter, radius, out hitPoint);
        }
    }
}
