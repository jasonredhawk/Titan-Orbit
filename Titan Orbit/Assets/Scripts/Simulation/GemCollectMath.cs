using TitanOrbit.Data;
using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared cargo-scoop tests for server <c>GemPickupSystem</c> and client beam / predicted-hide.
    /// Wing tips stay a designer radius. Hull auto-collect uses the covering collider footprint
    /// (long / oval hulls included) expanded by the visible crystal so overlapping the mesh counts.
    /// </summary>
    public static class GemCollectMath
    {
        /// <summary>
        /// Wing-tip absorb radius: designer collect range, but never smaller than the crystal.
        /// </summary>
        /// <param name="settings">TractorBeamSettings (wing collect + size pad).</param>
        /// <param name="gemValue"><c>GemState.Value</c> — drives visual scale.</param>
        /// <param name="gemSize"><c>GemState.Size</c> — sim scale pad on the designer radius.</param>
        public static float ResolveWingCollectRadius(
            TractorBeamSettings settings,
            float gemValue,
            float gemSize)
        {
            float designed = settings.ResolveWingCollectRadius(gemSize);
            float visual = GemPresentationScale.ComputeVisualRadius(gemValue);
            return math.max(designed, visual);
        }

        /// <summary>
        /// How far past the hull surface a gem center may sit and still count as touching.
        /// Visible crystal radius, or the designer gem-size pad when that is larger.
        /// Does not add the legacy fixed hull radius — the collider footprint is the zone.
        /// </summary>
        public static float ResolveHullSurfacePad(
            TractorBeamSettings settings,
            float gemValue,
            float gemSize)
        {
            float visual = GemPresentationScale.ComputeVisualRadius(gemValue);
            float sizePad = settings != null
                ? settings.ResolveGemSizePad(gemSize)
                : math.max(0f, gemSize) * 0.5f;
            return math.max(visual, sizePad);
        }

        /// <summary>
        /// Yaw-aligned XZ scoop. Half extents and bevel are world units and match the
        /// covering box (presentation extents × ship scale). <see cref="IsCircle"/> is the
        /// uniform-sphere fallback used before that box is baked.
        /// </summary>
        public struct HullCollectFootprint
        {
            public float2 HalfExtents;
            public float Bevel;
            public float2 LocalCenter;
            public bool IsCircle;
            public float CircleRadius;

            /// <summary>Radius that encloses the footprint, for spatial gathers.</summary>
            public float BroadphaseRadius() =>
                IsCircle ? CircleRadius : math.length(HalfExtents);

            /// <summary>Uniform scale (tractor VFX slack). 1 leaves the footprint unchanged.</summary>
            public HullCollectFootprint Scaled(float scale)
            {
                if (scale == 1f)
                    return this;
                var copy = this;
                copy.HalfExtents *= scale;
                copy.Bevel *= scale;
                copy.LocalCenter *= scale;
                copy.CircleRadius *= scale;
                return copy;
            }
        }

        /// <summary>Uniform hull sphere until the covering collider extents exist.</summary>
        public static HullCollectFootprint CircleHullFootprint(float shipScale)
        {
            return new HullCollectFootprint
            {
                IsCircle = true,
                CircleRadius = BodyCollisionMath.GetShipHullRadiusWorld(shipScale),
            };
        }

        /// <summary>
        /// Build the covering-box footprint. Extents and center are presentation space
        /// (world = value × <paramref name="transformScale"/>), same as the physics body.
        /// <paramref name="bevelFraction"/> must match the collider bake.
        /// </summary>
        public static bool TryMakeHullFootprint(
            float extentX,
            float extentY,
            float extentZ,
            float centerX,
            float centerZ,
            float transformScale,
            float bevelFraction,
            out HullCollectFootprint footprint)
        {
            float scale = math.max(0.01f, transformScale);
            float hx = extentX * scale;
            float hy = extentY * scale;
            float hz = extentZ * scale;
            if (hx < 0.05f || hz < 0.05f)
            {
                footprint = default;
                return false;
            }

            float bevel = math.max(0f, math.min(hx, math.min(hy, hz)) * math.max(0f, bevelFraction));
            bevel = math.min(bevel, math.min(hx, hz));
            footprint = new HullCollectFootprint
            {
                HalfExtents = new float2(hx, hz),
                Bevel = bevel,
                LocalCenter = new float2(centerX, centerZ) * scale,
            };
            return true;
        }

        /// <summary>
        /// True when the gem touches the hull footprint, including a one-tick sweep from
        /// <paramref name="shipFrom"/> to <paramref name="shipTo"/> so a fast hull does not
        /// tunnel through a crystal. Map width/height come from <c>MapStateSingleton</c>.
        /// </summary>
        public static bool HullReachesGem(
            float3 gemPos,
            float3 shipTo,
            float3 shipFrom,
            quaternion shipRotation,
            in HullCollectFootprint footprint,
            float surfacePad,
            float mapW,
            float mapH)
        {
            float pad = math.max(0f, surfacePad);
            if (footprint.IsCircle)
            {
                float radius = footprint.CircleRadius + pad;
                return SegmentReachesPoint(gemPos, shipFrom, shipTo, radius, mapW, mapH);
            }

            float2 fromLocal = GemToHullLocal(
                gemPos, shipFrom, shipRotation, footprint.LocalCenter, mapW, mapH);
            float2 toLocal = GemToHullLocal(
                gemPos, shipTo, shipRotation, footprint.LocalCenter, mapW, mapH);
            float2 inner = math.max(footprint.HalfExtents - new float2(footprint.Bevel), float2.zero);
            return DistanceSegmentToAabb(fromLocal, toLocal, inner) <= footprint.Bevel + pad;
        }

        /// <summary>Gem XZ in the ship's local frame (toroidal). Local +X is right, +Z is forward.</summary>
        static float2 GemToHullLocal(
            float3 gemPos,
            float3 shipPos,
            quaternion shipRotation,
            float2 localCenter,
            float mapW,
            float mapH)
        {
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(shipPos, gemPos, mapW, mapH);
            float3 local = math.mul(math.inverse(shipRotation), offset);
            return local.xz - localCenter;
        }

        /// <summary>
        /// Distance from a segment to an axis-aligned box. Zero when the segment touches it.
        /// A rounded hull is this inner box expanded by the bevel, so callers compare to bevel + pad.
        /// </summary>
        static float DistanceSegmentToAabb(float2 a, float2 b, float2 half)
        {
            if (DistancePointToAabb(a, half) <= 0f || DistancePointToAabb(b, half) <= 0f)
                return 0f;
            if (SegmentHitsAabb(a, b, half))
                return 0f;

            float d = math.min(DistancePointToAabb(a, half), DistancePointToAabb(b, half));
            d = math.min(d, DistanceSegmentToSegment(a, b, new float2(-half.x, -half.y), new float2(half.x, -half.y)));
            d = math.min(d, DistanceSegmentToSegment(a, b, new float2(half.x, -half.y), new float2(half.x, half.y)));
            d = math.min(d, DistanceSegmentToSegment(a, b, new float2(half.x, half.y), new float2(-half.x, half.y)));
            d = math.min(d, DistanceSegmentToSegment(a, b, new float2(-half.x, half.y), new float2(-half.x, -half.y)));
            return d;
        }

        /// <summary>Signed distance to an AABB. Negative means the point is inside.</summary>
        static float DistancePointToAabb(float2 p, float2 half)
        {
            float2 q = math.abs(p) - half;
            float outside = math.length(math.max(q, float2.zero));
            float inside = math.min(math.max(q.x, q.y), 0f);
            return outside + inside;
        }

        static bool SegmentHitsAabb(float2 a, float2 b, float2 half)
        {
            float2 delta = b - a;
            float t0 = 0f;
            float t1 = 1f;
            if (!Slab(a.x, delta.x, -half.x, half.x, ref t0, ref t1))
                return false;
            if (!Slab(a.y, delta.y, -half.y, half.y, ref t0, ref t1))
                return false;
            return t0 <= t1;
        }

        static bool Slab(float origin, float dir, float min, float max, ref float t0, ref float t1)
        {
            if (math.abs(dir) < 1e-8f)
                return origin >= min && origin <= max;

            float inv = 1f / dir;
            float ta = (min - origin) * inv;
            float tb = (max - origin) * inv;
            if (ta > tb)
            {
                float swap = ta;
                ta = tb;
                tb = swap;
            }

            t0 = math.max(t0, ta);
            t1 = math.min(t1, tb);
            return t0 <= t1;
        }

        static float DistanceSegmentToSegment(float2 p1, float2 q1, float2 p2, float2 q2)
        {
            float2 d1 = q1 - p1;
            float2 d2 = q2 - p2;
            float2 r = p1 - p2;
            float a = math.dot(d1, d1);
            float e = math.dot(d2, d2);
            float f = math.dot(d2, r);
            float s;
            float t;
            const float eps = 1e-8f;
            if (a <= eps && e <= eps)
                return math.length(p1 - p2);
            if (a <= eps)
            {
                s = 0f;
                t = math.saturate(f / e);
            }
            else
            {
                float c = math.dot(d1, r);
                if (e <= eps)
                {
                    t = 0f;
                    s = math.saturate(-c / a);
                }
                else
                {
                    float b = math.dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > eps ? math.saturate((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = math.saturate(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = math.saturate((b - c) / a);
                    }
                }
            }

            float2 c1 = p1 + d1 * s;
            float2 c2 = p2 + d2 * t;
            return math.length(c1 - c2);
        }

        /// <summary>
        /// True when <paramref name="point"/> is within <paramref name="radius"/> of the
        /// toroidal segment from <paramref name="segmentFrom"/> to <paramref name="segmentTo"/>.
        /// Covers a ship that tunnels through a crystal in one sim tick.
        /// </summary>
        public static bool SegmentReachesPoint(
            float3 point,
            float3 segmentFrom,
            float3 segmentTo,
            float radius,
            float mapW,
            float mapH)
        {
            if (radius <= 0f)
                return false;

            float3 toPoint = ToroidalMapEcs.ShortestOffsetXZ(segmentFrom, point, mapW, mapH);
            float3 along = ToroidalMapEcs.ShortestOffsetXZ(segmentFrom, segmentTo, mapW, mapH);
            float alongLenSq = math.lengthsq(along);
            if (alongLenSq < 1e-8f)
                return math.lengthsq(toPoint) <= radius * radius;

            float t = math.saturate(math.dot(toPoint, along) / alongLenSq);
            float3 closest = toPoint - along * t;
            closest.y = 0f;
            return math.lengthsq(closest) <= radius * radius;
        }
    }
}
