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
    /// back to a first-order estimate (current offset + velocity × travel-time-to-now).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] The playable map is a torus on XZ — leaving one edge wraps to the
    /// opposite. Relative position must use <see cref="ToroidalMapEcs.ShortestOffsetXZ"/>,
    /// never raw Euclidean subtraction across a seam (that would aim the wrong way).
    /// Velocity is treated as planar world units/sec (Y ignored); gameplay flight stays on
    /// the XZ plane at <see cref="PlanetaryDefenseMath.FixedY"/>.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Critical contract — <b>bulletSpeed must match spawn</b>:
    /// the same per-level bank-scaled <c>stats.bulletSpeed</c> (from
    /// <c>PlanetaryDefenseConfig.GetCombatLevelStats</c>) is passed into this quadratic
    /// <b>and</b> written as <c>BulletElement.Velocity = fireDir * bulletSpeed</c>.
    /// Using authored Level-1 / a hardcoded default for aim while spawning at the
    /// bank-modified Level N (or the reverse) systematically under- or over-leads.
    /// Client barrel aim must use that same combat speed too.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Bug that made ships miss while transports looked fine (2026-08):
    /// acquisition range and bullet <c>MaxDistance</c> were both set to engage range.
    /// Lead aim correctly points at an intercept that is often <b>farther</b> than the
    /// current pad→target distance (crossing / fleeing ships). The sim then culled the
    /// bullet at engage range before it reached the intercept — looks like “bad prediction”
    /// or “wrong bullet speed,” especially at slow Lv1 muzzle speeds (asset ≈ 8 u/s).
    /// Inbound people transports close range, so intercept distance ≤ engage and they still
    /// got hit. Fix: combat must spawn with <c>MaxDistance ≥ intercept flight distance</c>
    /// (see <see cref="TryComputeFireSolution"/> / <see cref="ComputeBulletMaxDistance"/>).
    /// </para>
    /// </summary>
    public static class PlanetaryDefenseAimMath
    {
        /// <summary>
        /// Floor on predicted intercept time (seconds). Must cover slow low-level bullets
        /// against chassis-speed ships near max engage (production Lv1: range 20 / speed 8
        /// = 2.5s just to the acquire rim; flee/cross intercepts need more).
        /// Earlier hard cap of 2.5s truncated those solutions and under-aimed.
        /// </summary>
        public const float MaxLeadSeconds = 5f;

        /// <summary>
        /// Minimum bullet speed treated as valid (world units/sec). Matches combat clamps.
        /// </summary>
        public const float MinBulletSpeed = 1f;

        /// <summary>
        /// Extra Euclidean flight budget past the predicted intercept point (world units).
        /// Covers hull radius, discrete tick overshoot, and mild accel after fire.
        /// </summary>
        public const float InterceptFlightMargin = 2.5f;

        /// <summary>
        /// Multiplier applied to planar target velocity before the quadratic.
        /// <para>
        /// [TITAN-ORBIT] Kept at <c>1</c> on purpose: the intercept must be exact for
        /// straight-line constant-velocity strafe at the real muzzle speed (verification
        /// contract). Ship motors do accelerate, but a velocity bias would over-lead
        /// constant-speed passes and hide MaxDistance / speed-match bugs. Pass this
        /// constant (or 1) from combat and the visual driver so both sides stay identical.
        /// </para>
        /// </summary>
        public const float ShipVelocityLeadScale = 1f;

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
        /// Caps intercept time for this shot. Floored by <see cref="MaxLeadSeconds"/>, but
        /// grows when <c>engageRange / bulletSpeed</c> is large (slow low-level bullets).
        /// </summary>
        /// <param name="bulletSpeed">Per-level muzzle speed (same value as spawn).</param>
        /// <param name="engageRange">Per-level acquisition range from the pad.</param>
        /// <returns>Max lead time in seconds.</returns>
        public static float ComputeMaxLeadSeconds(float bulletSpeed, float engageRange)
        {
            float speed = math.max(MinBulletSpeed, bulletSpeed);
            float engage = math.max(0.5f, engageRange);
            // Time to cross the acquire sphere at this level's muzzle speed.
            float toEngage = engage / speed;
            // Crossing/fleeing intercepts sit past acquire distance; MaxDistance grows with
            // the aim point, so allow up to 2× that flight time (never below MaxLeadSeconds).
            return math.max(MaxLeadSeconds, toEngage * 2f);
        }

        /// <summary>
        /// Full lead solution: unit fire direction, intercept time, and Euclidean flight
        /// distance from muzzle to the aim point (what combat should use for
        /// <c>BulletElement.MaxDistance</c> after adding <see cref="InterceptFlightMargin"/>).
        /// </summary>
        /// <param name="muzzle">Turret muzzle / pad world position (Y ignored).</param>
        /// <param name="targetPos">Target world position now (Y ignored).</param>
        /// <param name="targetVel">Target planar velocity (world units/sec); zero = static aim.</param>
        /// <param name="bulletSpeed">
        /// Bullet speed in world units/sec — <b>must</b> match the speed multiplied into
        /// <c>BulletElement.Velocity</c> for this shot (per-level stats, not a constant).
        /// </param>
        /// <param name="mapW">Toroidal map width (X).</param>
        /// <param name="mapH">Toroidal map height (Z).</param>
        /// <param name="engageRange">
        /// Per-level acquire range — used only to size <see cref="ComputeMaxLeadSeconds"/>;
        /// acquisition itself still gates who we fire at.
        /// </param>
        /// <param name="velocityLeadScale">
        /// Scale for <paramref name="targetVel"/> before the quadratic. Use
        /// <see cref="ShipVelocityLeadScale"/> (1) for ships and transports so client/server match.
        /// </param>
        /// <param name="fireDir">Unit XZ direction to fire; undefined when return is false.</param>
        /// <param name="leadSeconds">Clamped intercept time used for the aim offset.</param>
        /// <param name="interceptDistance">
        /// Planar flight budget from muzzle toward the aim point (at least
        /// <c>bulletSpeed × leadSeconds</c> so first-order fallback still reaches).
        /// </param>
        /// <returns>False only when muzzle and target coincide (no meaningful aim).</returns>
        public static bool TryComputeFireSolution(
            float3 muzzle,
            float3 targetPos,
            float3 targetVel,
            float bulletSpeed,
            float mapW,
            float mapH,
            float engageRange,
            float velocityLeadScale,
            out float3 fireDir,
            out float leadSeconds,
            out float interceptDistance)
        {
            fireDir = default;
            leadSeconds = 0f;
            interceptDistance = 0f;

            // --- Toroidal relative position (muzzle → target, shortest wrap) ---
            // [TITAN-ORBIT] ShortestOffsetXZ returns the vector you walk on the torus —
            // never use targetPos - muzzle on a wrapping map.
            float3 relative = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
            relative.y = 0f;

            float distSq = math.lengthsq(relative);
            if (distSq < MinDirectionSq)
                return false;

            // --- Planar velocity (Y is not part of Titan Orbit flight) ---
            float scale = math.max(0f, velocityLeadScale);
            float3 vel = targetVel * scale;
            vel.y = 0f;

            // [TITAN-ORBIT] Identical clamp to combat spawn — never aim with a different floor.
            float speed = math.max(MinBulletSpeed, bulletSpeed);
            float maxLead = ComputeMaxLeadSeconds(speed, engageRange);

            // --- Intercept time (quadratic, or first-order fallback) ---
            float leadT = SolveInterceptTime(relative, vel, speed);
            leadT = math.clamp(leadT, 0f, maxLead);

            // --- Aim offset in unwrapped muzzle space ---
            // [STANDARD] Predicted relative position = current offset + velocity × time.
            // Working in offset space avoids wrapping bugs from targetPos + vel*t on a torus.
            // Velocity is NOT rotated for the torus — world XZ velocity is already planar;
            // only the position offset uses the shortest wrap.
            float3 aimOffset = relative + vel * leadT;
            aimOffset.y = 0f;

            // If lead collapsed (rare numerical edge), aim at current position.
            if (math.lengthsq(aimOffset) < MinDirectionSq)
                aimOffset = relative;

            fireDir = math.normalize(aimOffset);
            leadSeconds = leadT;
            // [TITAN-ORBIT] Flight budget the bullet needs. On a true quadratic root,
            // |aimOffset| == speed×leadT. On first-order fallback they can differ — take
            // the max so MaxDistance never despawns before the lead time elapses.
            // Engage-range acquisition can be shorter when the target is fleeing/crossing;
            // combat must NOT clamp MaxDistance to engageRange alone.
            interceptDistance = math.max(math.length(aimOffset), speed * leadT);
            return true;
        }

        /// <summary>
        /// Computes a unit XZ fire direction from muzzle toward a predicted intercept with a
        /// moving target. Uses velocity scale 1 and <see cref="MaxLeadSeconds"/> only (no
        /// engage-based lead cap). Prefer <see cref="TryComputeFireSolution"/> from combat
        /// when spawning bullets so MaxDistance matches the aim point.
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
            // engageRange unused for cap here — pass a dummy that yields MaxLeadSeconds floor.
            return TryComputeFireSolution(
                muzzle, targetPos, targetVel, bulletSpeed, mapW, mapH,
                engageRange: 0.5f,
                velocityLeadScale: 1f,
                out fireDir, out _, out _);
        }

        /// <summary>
        /// Client/server barrel aim with explicit engage range and velocity lead scale.
        /// Must use the same <paramref name="bulletSpeed"/>, <paramref name="engageRange"/>,
        /// and <paramref name="velocityLeadScale"/> as <see cref="TryComputeFireSolution"/>
        /// on the server for that shot.
        /// </summary>
        /// <param name="muzzle">Turret muzzle world position.</param>
        /// <param name="targetPos">Target world position now.</param>
        /// <param name="targetVel">Target planar velocity.</param>
        /// <param name="bulletSpeed">Bullet speed (world units/sec) — same as spawned shot.</param>
        /// <param name="mapW">Map width.</param>
        /// <param name="mapH">Map height.</param>
        /// <param name="engageRange">Per-level acquire range (sizes max lead time).</param>
        /// <param name="velocityLeadScale">Normally <see cref="ShipVelocityLeadScale"/>.</param>
        /// <param name="fireDir">Unit fire direction.</param>
        /// <returns>False when muzzle and target coincide.</returns>
        public static bool TryComputeFireDirection(
            float3 muzzle,
            float3 targetPos,
            float3 targetVel,
            float bulletSpeed,
            float mapW,
            float mapH,
            float engageRange,
            float velocityLeadScale,
            out float3 fireDir)
        {
            return TryComputeFireSolution(
                muzzle, targetPos, targetVel, bulletSpeed, mapW, mapH,
                engageRange, velocityLeadScale, out fireDir, out _, out _);
        }

        /// <summary>
        /// Recommended <c>BulletElement.MaxDistance</c> so the shot can reach the lead point.
        /// Never smaller than engage range (static / inbound targets stay unchanged).
        /// </summary>
        /// <param name="engageRange">Acquisition / level engage range.</param>
        /// <param name="interceptDistance">From <see cref="TryComputeFireSolution"/>.</param>
        /// <returns>Euclidean flight budget for the sim step-sum cull.</returns>
        public static float ComputeBulletMaxDistance(float engageRange, float interceptDistance)
        {
            float engage = math.max(0.5f, engageRange);
            float flight = math.max(0f, interceptDistance) + InterceptFlightMargin;
            return math.max(engage, flight);
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
            return TryComputeAimPoint(
                muzzle, targetPos, targetVel, bulletSpeed, mapW, mapH,
                engageRange: 0.5f, velocityLeadScale: 1f, out aimPoint);
        }

        /// <summary>
        /// Aim-point helper with engage range + velocity lead scale (match combat path).
        /// </summary>
        /// <param name="muzzle">Turret muzzle world position.</param>
        /// <param name="targetPos">Target world position now.</param>
        /// <param name="targetVel">Target planar velocity.</param>
        /// <param name="bulletSpeed">Bullet speed (world units/sec).</param>
        /// <param name="mapW">Map width.</param>
        /// <param name="mapH">Map height.</param>
        /// <param name="engageRange">Per-level acquire range.</param>
        /// <param name="velocityLeadScale">Velocity scale (use <see cref="ShipVelocityLeadScale"/>).</param>
        /// <param name="aimPoint">Predicted intercept point.</param>
        /// <returns>False when muzzle and target coincide.</returns>
        public static bool TryComputeAimPoint(
            float3 muzzle,
            float3 targetPos,
            float3 targetVel,
            float bulletSpeed,
            float mapW,
            float mapH,
            float engageRange,
            float velocityLeadScale,
            out float3 aimPoint)
        {
            aimPoint = targetPos;
            aimPoint.y = PlanetaryDefenseMath.FixedY;

            if (!TryComputeFireSolution(
                    muzzle, targetPos, targetVel, bulletSpeed, mapW, mapH,
                    engageRange, velocityLeadScale,
                    out _, out _, out float interceptDistance))
                return false;

            // Reconstruct aim point along the same offset used for fireDir.
            float3 relative = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
            relative.y = 0f;
            float3 vel = targetVel * math.max(0f, velocityLeadScale);
            vel.y = 0f;
            float speed = math.max(MinBulletSpeed, bulletSpeed);
            float maxLead = ComputeMaxLeadSeconds(speed, engageRange);
            float leadT = math.clamp(SolveInterceptTime(relative, vel, speed), 0f, maxLead);
            float3 aimOffset = relative + vel * leadT;
            if (math.lengthsq(aimOffset) < MinDirectionSq)
                aimOffset = relative;

            aimPoint = muzzle + aimOffset;
            aimPoint.y = PlanetaryDefenseMath.FixedY;
            _ = interceptDistance;
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
        /// <c>|relative| / bulletSpeed</c> (first-order travel time — direction then uses
        /// <c>relative + vel × t</c>, which is classic “aim ahead by travel time”).
        /// </para>
        /// </summary>
        /// <param name="relative">Toroidal muzzle→target offset (Y should already be 0).</param>
        /// <param name="targetVel">Planar target velocity.</param>
        /// <param name="bulletSpeed">Positive bullet speed.</param>
        /// <returns>Intercept time in seconds (always ≥ 0).</returns>
        public static float SolveInterceptTime(float3 relative, float3 targetVel, float bulletSpeed)
        {
            // --- Static / near-static target ---
            // [STANDARD] Travel time is distance / speed. With vel≈0 the aim offset stays
            // at <c>relative</c> (no lead). Callers still clamp by max lead.
            float speed = math.max(MinBulletSpeed, bulletSpeed);
            float dist = math.length(relative);
            float staticT = dist / speed;

            float velSq = math.lengthsq(targetVel);
            if (velSq < 1e-6f)
                return staticT;

            // --- Quadratic: (V·V - S²) t² + 2(R·V) t + R·R = 0 ---
            // [STANDARD] Classic first-order intercept for constant velocity target.
            // Sign of R·V: positive = target receding along the line of sight (harder);
            // negative = approaching (easier). Do not flip the sign of V.
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
                    return staticT; // No future intercept — first-order fallback.
                return tLinear;
            }

            float discriminant = (b * b) - (4f * a * c);
            if (discriminant < 0f)
                return staticT; // Imaginary roots — bullet cannot catch this trajectory.

            float sqrtD = math.sqrt(discriminant);
            float inv2a = 0.5f / a;
            float t0 = (-b - sqrtD) * inv2a;
            float t1 = (-b + sqrtD) * inv2a;

            // Prefer the earliest non-negative root (first time paths meet).
            // When target is faster than the bullet (a > 0), a future root still exists
            // only for some geometries (e.g. crossing / approaching) — same selection rule.
            float t = float.MaxValue;
            if (t0 >= 0f)
                t = t0;
            if (t1 >= 0f && t1 < t)
                t = t1;

            if (t == float.MaxValue)
                return staticT; // Both roots in the past — first-order fallback.

            return t;
        }
    }
}
