using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>Planar (XZ) oriented-box collision for compound ship hulls.</summary>
    public static class ShipCompoundCollisionMath
    {
        public struct Obb2D
        {
            public float2 Center;
            public float2 AxisX;
            public float2 AxisZ;
            public float2 HalfExtents;
        }

        public static Obb2D BuildObb2D(float3 worldCenter, quaternion worldRotation, float3 halfExtents)
        {
            float3 axisX3 = math.rotate(worldRotation, new float3(1f, 0f, 0f));
            float3 axisZ3 = math.rotate(worldRotation, new float3(0f, 0f, 1f));
            float2 axisX = NormalizeSafe2(new float2(axisX3.x, axisX3.z), new float2(1f, 0f));
            float2 axisZ = NormalizeSafe2(new float2(axisZ3.x, axisZ3.z), new float2(0f, 1f));
            return new Obb2D
            {
                Center = new float2(worldCenter.x, worldCenter.z),
                AxisX = axisX,
                AxisZ = axisZ,
                HalfExtents = new float2(math.max(0.001f, halfExtents.x), math.max(0.001f, halfExtents.z)),
            };
        }

        public static float DistancePointToObb(float2 point, in Obb2D obb)
        {
            float2 delta = point - obb.Center;
            float localX = math.dot(delta, obb.AxisX);
            float localZ = math.dot(delta, obb.AxisZ);
            float2 clamped = new float2(
                math.clamp(localX, -obb.HalfExtents.x, obb.HalfExtents.x),
                math.clamp(localZ, -obb.HalfExtents.y, obb.HalfExtents.y));
            float2 diff = new float2(localX, localZ) - clamped;
            return math.length(diff);
        }

        public static bool SegmentHitsObbExpanded(
            float2 from,
            float2 to,
            in Obb2D obb,
            float expandRadius,
            out float hitT,
            out float2 hitNormal)
        {
            hitT = 1f;
            hitNormal = float2.zero;

            float2 expandedHalf = obb.HalfExtents + expandRadius;
            float2 localFrom = WorldToObbLocal(from, obb);
            float2 localTo = WorldToObbLocal(to, obb);
            float2 seg = localTo - localFrom;

            float tEnter = 0f;
            float tExit = 1f;

            if (!ClipSegmentAxis(localFrom.x, seg.x, -expandedHalf.x, expandedHalf.x, ref tEnter, ref tExit) ||
                !ClipSegmentAxis(localFrom.y, seg.y, -expandedHalf.y, expandedHalf.y, ref tEnter, ref tExit))
                return false;

            hitT = math.clamp(tEnter, 0f, 1f);
            if (hitT > 1f || hitT <= 1e-4f)
                return false;

            float2 hitLocal = localFrom + seg * hitT;
            float2 normalLocal = ComputeAabbContactNormal(hitLocal, expandedHalf);
            hitNormal = NormalizeSafe2(
                normalLocal.x * obb.AxisX + normalLocal.y * obb.AxisZ,
                new float2(0f, 1f));
            return true;
        }

        public static bool TryDepenetrateObbFromCircle(
            in Obb2D obb,
            float2 circleCenter,
            float circleRadius,
            out float2 pushNormal,
            out float penetration)
        {
            pushNormal = float2.zero;
            penetration = 0f;

            float dist = DistancePointToObb(circleCenter, obb);
            penetration = circleRadius - dist;
            if (penetration <= 0f)
                return false;

            float2 closest = ClosestPointOnObb(circleCenter, obb);
            float2 away = closest - circleCenter;
            if (math.lengthsq(away) > 1e-8f)
                pushNormal = math.normalize(away);
            else
                pushNormal = NormalizeSafe2(obb.Center - circleCenter, new float2(0f, 1f));
            return true;
        }

        public static bool TryDepenetrateObbFromObb(in Obb2D a, in Obb2D b, out float2 pushOnA, out float penetration)
        {
            pushOnA = float2.zero;
            penetration = 0f;

            float2[] axes =
            {
                a.AxisX, a.AxisZ, b.AxisX, b.AxisZ,
                NormalizeSafe2(new float2(-a.AxisX.y, a.AxisX.x), new float2(1f, 0f)),
            };

            float bestOverlap = float.MaxValue;
            float2 bestAxis = float2.zero;

            for (int i = 0; i < axes.Length; i++)
            {
                float2 axis = NormalizeSafe2(axes[i], new float2(1f, 0f));
                ProjectObb(a, axis, out float minA, out float maxA);
                ProjectObb(b, axis, out float minB, out float maxB);
                float overlap = math.min(maxA, maxB) - math.max(minA, minB);
                if (overlap <= 0f)
                    return false;

                if (overlap < bestOverlap)
                {
                    bestOverlap = overlap;
                    float2 centerDelta = b.Center - a.Center;
                    bestAxis = math.dot(centerDelta, axis) < 0f ? -axis : axis;
                }
            }

            penetration = bestOverlap;
            pushOnA = bestAxis;
            return true;
        }

        static float2 ClosestPointOnObb(float2 point, in Obb2D obb)
        {
            float2 local = WorldToObbLocal(point, obb);
            float2 clamped = new float2(
                math.clamp(local.x, -obb.HalfExtents.x, obb.HalfExtents.x),
                math.clamp(local.y, -obb.HalfExtents.y, obb.HalfExtents.y));
            return obb.Center + obb.AxisX * clamped.x + obb.AxisZ * clamped.y;
        }

        static float2 WorldToObbLocal(float2 point, in Obb2D obb)
        {
            float2 delta = point - obb.Center;
            return new float2(math.dot(delta, obb.AxisX), math.dot(delta, obb.AxisZ));
        }

        static void ProjectObb(in Obb2D obb, float2 axis, out float min, out float max)
        {
            float2 axisN = NormalizeSafe2(axis, new float2(1f, 0f));
            float center = math.dot(obb.Center, axisN);
            float radius =
                obb.HalfExtents.x * math.abs(math.dot(obb.AxisX, axisN)) +
                obb.HalfExtents.y * math.abs(math.dot(obb.AxisZ, axisN));
            min = center - radius;
            max = center + radius;
        }

        static bool ClipSegmentAxis(float start, float delta, float min, float max, ref float tEnter, ref float tExit)
        {
            if (math.abs(delta) < 1e-8f)
                return start >= min && start <= max;

            float inv = 1f / delta;
            float t0 = (min - start) * inv;
            float t1 = (max - start) * inv;
            if (t0 > t1)
                (t0, t1) = (t1, t0);

            tEnter = math.max(tEnter, t0);
            tExit = math.min(tExit, t1);
            return tEnter <= tExit;
        }

        static float2 ComputeAabbContactNormal(float2 hitLocal, float2 halfExtents)
        {
            float2 absHit = math.abs(hitLocal);
            float2 penetration = halfExtents - absHit;
            if (penetration.x < penetration.y)
                return new float2(math.sign(hitLocal.x), 0f);
            return new float2(0f, math.sign(hitLocal.y));
        }

        static float2 NormalizeSafe2(float2 v, float2 fallback)
        {
            if (math.lengthsq(v) < 1e-8f)
                return fallback;
            return math.normalize(v);
        }
    }
}
