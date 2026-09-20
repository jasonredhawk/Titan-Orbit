using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared MEGA cannon-laser acquire and DPS math. Server combat and client beams
    /// use the same 33° half-angle cone and firePower × fireRate damage rate.
    /// Sustained burn ramps that authored DPS from
    /// <see cref="RampDamageMin"/> to <see cref="RampDamageMax"/> over
    /// <see cref="RampDurationSeconds"/>. Map size comes from
    /// <see cref="MapStateSingleton"/> via the caller.
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

        /// <summary>
        /// Extra keep radius past acquire range. Muzzle motion on a wide Titan
        /// used to drop a lock every tick and re-search (beam / hum on-off).
        /// </summary>
        public const float KeepRangePad = 8f;

        /// <summary>Hold a live lock this far past acquire range (fraction).</summary>
        public const float KeepRangeFactor = 1.25f;

        /// <summary>
        /// Floor written to ghost <c>TargetDistance</c> while burning. A surface
        /// hit next to the muzzle used to write ~0 and hide the beam.
        /// </summary>
        public const float MinTrackingDistance = 0.25f;

        /// <summary>Hold Fire this long to reach <see cref="RampDamageMax"/>.</summary>
        public const float RampDurationSeconds = 5f;

        /// <summary>Damage multiplier on the first tick of a burn (50% authored DPS).</summary>
        public const float RampDamageMin = 0.5f;

        /// <summary>Damage multiplier after <see cref="RampDurationSeconds"/> (300% authored DPS).</summary>
        public const float RampDamageMax = 3f;

        /// <summary>Sticky hold range from an acquire range (hysteresis).</summary>
        public static float KeepRange(float acquireRange)
        {
            return math.max(0.5f, acquireRange) * KeepRangeFactor + KeepRangePad;
        }

        /// <summary>Minimum planar length before a direction can be normalized.</summary>
        const float MinDirectionSq = 0.0001f;

        /// <summary>Authored damage (and energy) per second while locked: firePower × fireRate.</summary>
        public static float ComputeDps(float firePower, float fireRate)
        {
            float power = math.max(0f, firePower);
            float rate = math.max(0.1f, fireRate);
            return power * rate;
        }

        /// <summary>
        /// Linear 50% → 300% over <see cref="RampDurationSeconds"/>. Caps at the max.
        /// Energy drain stays at <see cref="ComputeDps"/>; only the hit uses this.
        /// </summary>
        public static float ComputeRampMultiplier(float rampSeconds)
        {
            float t = math.saturate(math.max(0f, rampSeconds) / RampDurationSeconds);
            return math.lerp(RampDamageMin, RampDamageMax, t);
        }

        /// <summary>Authored DPS × the live burn ramp (50%–300%).</summary>
        public static float ComputeRampedDps(float firePower, float fireRate, float rampSeconds)
        {
            return ComputeDps(firePower, fireRate) * ComputeRampMultiplier(rampSeconds);
        }

        /// <summary>
        /// Advances one barrel's charge. Resets when Fire is released, lockout
        /// trips, or the lock entity changes. Same lock across a one-tick gap
        /// keeps the current value.
        /// </summary>
        public static float StepRampSeconds(float current, float dt, bool reset, bool charging)
        {
            if (reset)
                return 0f;
            if (!charging)
                return math.max(0f, current);
            return math.min(RampDurationSeconds, math.max(0f, current) + math.max(0f, dt));
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
