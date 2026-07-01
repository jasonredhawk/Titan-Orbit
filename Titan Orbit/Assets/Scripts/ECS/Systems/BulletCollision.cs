using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    public static class BulletCollision
    {
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
            return math.max(
                GemEconomyConstants.MinAsteroidHitRadius,
                scale * GemEconomyConstants.AsteroidHitRadiusScale);
        }
    }
}
