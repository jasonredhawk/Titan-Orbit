using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Pure lead-targeting (aim prediction) math for planetary defense turrets.
    /// Shared by server combat (<c>PlanetaryDefenseCombatSystem</c>) and client cosmetic
    /// barrel aim (<c>PlanetaryDefenseVisualDriver</c>) so barrels point where bullets go.
    /// <para>
    /// Lead targeting means: do not aim at where the target is <b>now</b>; aim at where it
    /// will be when the bullet arrives. A bullet takes travel time
    /// <c>distance / bulletSpeed</c>; in that same time a moving ship or people transport
    /// slides along its velocity. Solving for the intercept time (when bullet and target
    /// occupy the same point) is a standard quadratic; if no valid solution exists we fall
    /// back to current-position aim (the old behavior).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] The playable map is a torus on XZ — leaving one edge wraps to the
    /// opposite. Relative position must use <see cref="ToroidalMapEcs.ShortestOffsetXZ"/>,
    /// never raw Euclidean subtraction across a seam (that would aim the wrong way).
    /// Velocity is treated as planar world units/sec (Y ignored); gameplay flight stays on
    /// the XZ plane at <see cref="PlanetaryDefenseMath.FixedY"/>.
    /// </para>
    /// </summary>
    public static class PlanetaryDefenseAimMath
    {
        /// <summary>
        /// Hard cap on predicted intercept time (seconds). Stops extreme target speeds from
        /// aiming wildly past the target when the quadratic roots are large.
        /// </summary>
        public const float MaxLeadSeconds = 2.5f;

        /// <summary>
        /// Minimum bullet speed treated as valid (world units/sec). Matches combat clamps.
        /// </summary>
        public const float MinBulletSpeed = 1f;

        /// <summary>
        /// Squared length below which a direction is treated as zero (cannot normalize).
        /// </summary>
        const float MinDirectionSq = 0.0001f;

        /// <summary>
        /// Absolute value of the quadratic <c>a</c> coefficient below which we treat the
        /// equation as linear (target speed ≈ bullet speed).
        /// </summary>
        const float LinearAEpsilon = 1e-4f;

        /// <summary>
        /// Computes a unit XZ fire direction from muzzle toward a predicted intercept with a
        /// moving target. Falls back to current-position aim when intercept is invalid.
        /// </summary>
        /// <param name="muzzle">Turret muzzle / pad world position (Y ignored).</param>
        /// <param name="targetPos">Target world position now (Y ignored).</param>
        /// <param name="targetVel">Target planar velocity (world units/sec); zero = static aim.</param>
        /// <param name="bulletSpeed">Bullet speed in world units/sec (must match sim spawn).</param>
        /// <param name="mapW">Toroidal map width (X).</param>
        /// <param name="mapH">Toroidal map height (Z).</param>
        /// <param name="fireDir">Unit XZ direction to fire; undefined when return is false.</param>
        /// <returns>False only when muzzle and target coincide (no meaningful aim).</returns>
        public static bool TryComputeFireDirection(
            float3 muzzle,
            float3 targetPos,
            float3 targetVel,
            float bulletSpeed,
            float mapW,
            float mapH,
            out float3 fireDir)
        {
            // --- Toroidal relative position (muzzle → target, shortest wrap) ---
            // [TITAN-ORBIT] ShortestOffsetXZ returns the vector you walk on the torus —
            // never use targetPos - muzzle on a wrapping map.
            float3 relative = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
            relative.y = 0f;

            float distSq = math.lengthsq(relative);
            if (distSq < MinDirectionSq)
            {
                fireDir = default;
                return false;
            }

            // --- Planar velocity (Y is not part of Titan Orbit flight) ---
            float3 vel = targetVel;
            vel.y = 0f;

            float speed = math.max(MinBulletSpeed, bulletSpeed);

            // --- Intercept time (quadratic, or first-order fallback) ---
            float leadT = SolveInterceptTime(relative, vel, speed);
            leadT = math.clamp(leadT, 0f, MaxLeadSeconds);

            // --- Aim offset in unwrapped muzzle space ---
            // [STANDARD] Predicted relative position = current offset + velocity × time.
            // Working in offset space avoids wrapping bugs from targetPos + vel*t on a torus.
            float3 aimOffset = relative + vel * leadT;
            aimOffset.y = 0f;

            // If lead collapsed (rare numerical edge), aim at current position.
            if (math.lengthsq(aimOffset) < MinDirectionSq)
                aimOffset = relative;

            fireDir = math.normalize(aimOffset);
            return true;
        }

        /// <summary>
        /// Same intercept as <see cref="TryComputeFireDirection"/>, but returns the predicted
        /// world aim point (muzzle + lead offset) for presentation / debug. Y is forced to
        /// <see cref="PlanetaryDefenseMath.FixedY"/>.
        /// </summary>
        /// <param name="muzzle">Turret muzzle world position.</param>
        /// <param name="targetPos">Target world position now.</param>
        /// <param name="targetVel">Target planar velocity.</param>
        /// <param name="bulletSpeed">Bullet speed (world units/sec).</param>
        /// <param name="mapW">Map width.</param>
        /// <param name="mapH">Map height.</param>
        /// <param name="aimPoint">Predicted intercept point in world space near the muzzle tile.</param>
        /// <returns>False when muzzle and target coincide.</returns>
        public static bool TryComputeAimPoint(
            float3 muzzle,
            float3 targetPos,
            float3 targetVel,
            float bulletSpeed,
            float mapW,
            float mapH,
            out float3 aimPoint)
        {
            aimPoint = targetPos;
            aimPoint.y = PlanetaryDefenseMath.FixedY;

            if (!TryComputeFireDirection(
                    muzzle, targetPos, targetVel, bulletSpeed, mapW, mapH, out float3 fireDir))
                return false;

            // Reconstruct aim point along the fire ray at the solved lead distance.
            float3 relative = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
            relative.y = 0f;
            float3 vel = targetVel;
            vel.y = 0f;
            float speed = math.max(MinBulletSpeed, bulletSpeed);
            float leadT = math.clamp(SolveInterceptTime(relative, vel, speed), 0f, MaxLeadSeconds);
            float3 aimOffset = relative + vel * leadT;
            if (math.lengthsq(aimOffset) < MinDirectionSq)
                aimOffset = relative;

            aimPoint = muzzle + aimOffset;
            aimPoint.y = PlanetaryDefenseMath.FixedY;
            _ = fireDir;
            return true;
        }

        /// <summary>
        /// Solves for the earliest non-negative time <c>t</c> when a bullet fired from the
        /// origin of <paramref name="relative"/> at speed <paramref name="bulletSpeed"/>
        /// meets a target that starts at <paramref name="relative"/> and moves with
        /// <paramref name="targetVel"/>.
        /// <para>
        /// Equation (planar): <c>|relative + targetVel · t| = bulletSpeed · t</c>.
        /// Squaring both sides yields a quadratic in <c>t</c>. When the target is nearly
        /// stationary, or when no positive root exists, we return the simple estimate
        /// <c>|relative| / bulletSpeed</c> (aim-at-current with travel-time delay of zero
        /// velocity — i.e. current-position aim once velocity is zero).
        /// </para>
        /// </summary>
        /// <param name="relative">Toroidal muzzle→target offset (Y should already be 0).</param>
        /// <param name="targetVel">Planar target velocity.</param>
        /// <param name="bulletSpeed">Positive bullet speed.</param>
        /// <returns>Intercept time in seconds (always ≥ 0).</returns>
        public static float SolveInterceptTime(float3 relative, float3 targetVel, float bulletSpeed)
        {
            // --- Static / near-static target ---
            // [STANDARD] No lead needed; travel time is just distance / speed (unused for
            // direction when vel≈0, but callers still clamp).
            float speed = math.max(MinBulletSpeed, bulletSpeed);
            float dist = math.length(relative);
            float staticT = dist / speed;

            float velSq = math.lengthsq(targetVel);
            if (velSq < 1e-6f)
                return staticT;

            // --- Quadratic: (V·V - S²) t² + 2(R·V) t + R·R = 0 ---
            // [STANDARD] Classic first-order intercept for constant velocity target.
            float a = velSq - (speed * speed);
            float b = 2f * math.dot(relative, targetVel);
            float c = math.lengthsq(relative);

            // Target speed ≈ bullet speed → treat as linear: 2(R·V)t + R·R = 0
            if (math.abs(a) < LinearAEpsilon)
            {
                if (math.abs(b) < 1e-6f)
                    return staticT;
                float tLinear = -c / b;
                if (tLinear < 0f)
                    return staticT; // No future intercept — fall back to current-position aim.
                return tLinear;
            }

            float discriminant = (b * b) - (4f * a * c);
            if (discriminant < 0f)
                return staticT; // Imaginary roots — bullet cannot catch this trajectory.

            float sqrtD = math.sqrt(discriminant);
            float inv2a = 0.5f / a;
            float t0 = (-b - sqrtD) * inv2a;
            float t1 = (-b + sqrtD) * inv2a;

            // Prefer the earliest non-negative root.
            float t = float.MaxValue;
            if (t0 >= 0f)
                t = t0;
            if (t1 >= 0f && t1 < t)
                t = t1;

            if (t == float.MaxValue)
                return staticT; // Both roots in the past — fall back.

            return t;
        }
    }
}
