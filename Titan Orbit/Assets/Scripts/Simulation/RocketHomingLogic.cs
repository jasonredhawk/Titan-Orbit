using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// [TITAN-ORBIT] Turn-limited homing for store rockets. Shared by server
    /// <c>BulletSimulationSystem</c> (authoritative hits) and client <c>BulletVfxDriver</c>
    /// (cosmetic tracers). Ships can out-turn a rocket because yaw is clamped per tick —
    /// same idea as <c>ShipPhysicsDriveLogic.TryRotateTowardAim</c>.
    /// <para>
    /// Distances and aim use the torus shortest path on XZ. Flight stays unbounded
    /// (velocity is never wrapped). Acquire / retarget is the caller's job; this type
    /// only steers the current velocity toward a point.
    /// </para>
    /// </summary>
    public static class RocketHomingLogic
    {
        /// <summary>
        /// Rotates <paramref name="velocity"/> toward <paramref name="targetPos"/> by at most
        /// <paramref name="turnSpeedDeg"/> × <paramref name="dt"/> degrees. Speed is preserved
        /// (catalog flight speed, not a new acceleration).
        /// </summary>
        /// <param name="position">Current rocket position (logical / unbounded XZ).</param>
        /// <param name="velocity">Current planar velocity (updated in place).</param>
        /// <param name="targetPos">Lock point (enemy ship or turret).</param>
        /// <param name="turnSpeedDeg">Max yaw rate in degrees per second.</param>
        /// <param name="dt">Tick delta seconds.</param>
        /// <param name="mapW">Toroidal map width from <c>MapStateSingleton</c>.</param>
        /// <param name="mapH">Toroidal map height from <c>MapStateSingleton</c>.</param>
        /// <returns>True when a turn was applied; false when inputs are unusable (fly straight).</returns>
        public static bool TrySteerToward(
            float3 position,
            ref float3 velocity,
            float3 targetPos,
            float turnSpeedDeg,
            float dt,
            float mapW,
            float mapH)
        {
            // --- Guards ---
            if (dt <= 0f || turnSpeedDeg <= 0.01f)
                return false;
            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return false;

            float speed = math.length(velocity);
            if (speed < 0.01f)
                return false;

            // --- Desired heading on the torus ---
            // [TITAN-ORBIT] Shortest offset so a lock across the seam does not aim the long way.
            float3 toTarget = ToroidalMapEcs.ShortestOffsetXZ(position, targetPos, mapW, mapH);
            toTarget.y = 0f;
            if (math.lengthsq(toTarget) < 0.0001f)
                return false;

            float3 desiredDir = math.normalize(toTarget);
            float3 currentDir = velocity / speed;
            currentDir.y = 0f;
            if (math.lengthsq(currentDir) < 0.0001f)
                currentDir = desiredDir;
            else
                currentDir = math.normalize(currentDir);

            // --- Clamp the yaw step ---
            // [STANDARD] math.slerp is for quaternions, so we rotate current→desired about Y.
            // Skip tiny errors — snapping to a jittering desiredDir weaves left/right.
            float angle = math.acos(math.clamp(math.dot(currentDir, desiredDir), -1f, 1f));
            if (angle <= math.radians(AlignDeadzoneDegrees))
                return false;

            float maxRadians = math.radians(turnSpeedDeg) * dt;
            float3 newDir;
            if (angle <= maxRadians)
            {
                newDir = desiredDir;
            }
            else
            {
                float3 axis = math.cross(currentDir, desiredDir);
                if (math.lengthsq(axis) < 1e-8f)
                    return false;
                axis = math.normalize(axis);
                quaternion step = quaternion.AxisAngle(axis, maxRadians);
                newDir = math.normalize(math.mul(step, currentDir));
            }

            velocity = newDir * speed;
            velocity.y = 0f;
            return true;
        }

        /// <summary>Ignore heading error inside this cone so ghost jitter cannot flip the turn axis.</summary>
        public const float AlignDeadzoneDegrees = 3f;

        /// <summary>
        /// True when <paramref name="dist"/> is inside the search bubble.
        /// A missing / zero radius uses <see cref="TitanOrbit.Data.RocketCatalog.DefaultAcquireRange"/>
        /// (~50). Never treats 0 as whole-map — fly straight until something enters the bubble.
        /// </summary>
        public static bool IsInAcquireRange(float dist, float acquireRange)
        {
            float radius = acquireRange > 0.01f
                ? acquireRange
                : TitanOrbit.Data.RocketCatalog.DefaultAcquireRange;
            return dist <= radius;
        }
    }
}
