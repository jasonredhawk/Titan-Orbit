using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared MEGA cannon-laser acquire and DPS math. Server combat and client beams
    /// use the same 33° half-angle cone and firePower × fireRate damage rate.
    /// Map size comes from <see cref="MapStateSingleton"/> via the caller.
    /// </summary>
    [BurstCompile]
    public static class CannonLaserMath
    {
        /// <summary>Half-angle in front of the barrel forward (degrees).</summary>
        public const float ConeHalfAngleDeg = 33f;

        /// <summary>
        /// After the pool hits empty, cannons stay off until energy reaches this
        /// fraction of <c>MaxEnergy</c> (stops floor chatter).
        /// </summary>
        public const float RechargeRatio = 0.10f;

        /// <summary>Minimum planar length before a direction can be normalized.</summary>
        const float MinDirectionSq = 0.0001f;

        /// <summary>Damage (and energy) per second while the beam is locked: firePower × fireRate.</summary>
        public static float ComputeDps(float firePower, float fireRate)
        {
            float power = math.max(0f, firePower);
            float rate = math.max(0.1f, fireRate);
            return power * rate;
        }

        /// <summary>
        /// True when <paramref name="targetPos"/> is inside acquire range (toroidal).
        /// Overlap (almost on top of the target) counts as in range.
        /// </summary>
        [BurstCompile]
        public static bool IsInRange(
            in float3 muzzle,
            in float3 targetPos,
            float range,
            float mapW,
            float mapH,
            out float distance)
        {
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
            offset.y = 0f;
            distance = math.length(offset);
            return math.isfinite(distance) && distance <= range;
        }

        /// <summary>
        /// True when <paramref name="targetPos"/> is inside acquire range and the 33° cone
        /// in front of <paramref name="barrelFwd"/>. Distance uses
        /// <see cref="ToroidalMapEcs.ToroidalDistance"/> (shortest path on the torus).
        /// </summary>
        /// <param name="mapW">From <see cref="TitanOrbit.ECS.MapStateSingleton"/>.</param>
        /// <param name="mapH">From <see cref="TitanOrbit.ECS.MapStateSingleton"/>.</param>
        [BurstCompile]
        public static bool IsInRangeAndCone(
            in float3 muzzle,
            in float3 barrelFwd,
            in float3 targetPos,
            float range,
            float mapW,
            float mapH,
            out float distance)
        {
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
            offset.y = 0f;
            distance = math.length(offset);
            if (!math.isfinite(distance) || distance > range)
                return false;
            // Closing in / overlapping: still a hit. The old 0.05 reject dropped
            // the beam the moment a moving Titan reached the target.
            if (distance < 0.05f)
                return true;

            float3 toTarget = offset / distance;
            float3 fwd = barrelFwd;
            fwd.y = 0f;
            if (math.lengthsq(fwd) < MinDirectionSq)
                return false;
            fwd = math.normalize(fwd);

            float minCos = math.cos(math.radians(ConeHalfAngleDeg));
            return math.dot(fwd, toTarget) >= minCos;
        }

        /// <summary>
        /// Near-side sphere contact along the toroidal muzzle→center ray.
        /// Returned in the same unwrap as <paramref name="muzzle"/>.
        /// </summary>
        public static float3 PullToSphereSurface(
            in float3 muzzle,
            in float3 center,
            float radius,
            float mapW,
            float mapH)
        {
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, center, mapW, mapH);
            offset.y = 0f;
            float distance = math.length(offset);
            float r = math.max(0.05f, radius);
            if (distance < 0.05f)
                return muzzle;

            float3 dir = offset / distance;
            float along = math.max(0.05f, distance - r);
            float3 hit = muzzle + dir * along;
            hit.y = muzzle.y;
            return hit;
        }
    }
}
