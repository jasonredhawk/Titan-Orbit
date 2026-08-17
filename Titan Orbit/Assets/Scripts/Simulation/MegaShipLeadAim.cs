using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Lead-targeting for MEGA auto-fire. Planetary defense turrets sit still, so
    /// <see cref="PlanetaryDefenseAimMath"/> only leads the target. MEGA rounds are
    /// Starblast-style: spawned velocity is <c>aimDir * bulletSpeed + shipVel</c>.
    /// If we aimed at the target's current position while the hull is moving, that
    /// inherited velocity pulls the shot off the line — usually an undershoot.
    /// <para>
    /// Fix: solve the same intercept quadratic in the <b>relative</b> frame
    /// <c>targetVel - shooterVel</c>. The fire direction that comes back is the
    /// muzzle-relative aim; adding <c>shipVel</c> at spawn then intercepts in world space.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Map size comes from <c>MapStateSingleton</c>. Relative position
    /// uses <see cref="ToroidalMapEcs.ShortestOffsetXZ"/> — never a raw subtract
    /// across a wrap seam. The MEGA hull stays unbounded (not wrapped).
    /// </para>
    /// Paired with <c>MegaShipAutoFireSystem</c> (writes AimPoint) and
    /// <c>BulletShotMath.Build</c> (adds shipVel at spawn).
    /// </summary>
    public static class MegaShipLeadAim
    {
        /// <summary>Squared length below which a planar offset cannot be normalized.</summary>
        const float MinDirectionSq = 0.0001f;

        /// <summary>
        /// Full moving-shooter lead: unit fire direction, intercept time, flight budget,
        /// and a world aim point the turret should look at (unwrapped near the muzzle).
        /// </summary>
        /// <param name="muzzle">This gun's world muzzle (Y ignored).</param>
        /// <param name="shooterVel">
        /// MEGA planar velocity (world units/sec) — the same
        /// <c>ShipKinematics.Velocity</c> added onto the bullet at spawn.
        /// </param>
        /// <param name="targetPos">Target world position now (Y ignored).</param>
        /// <param name="targetVel">Target planar velocity (world units/sec).</param>
        /// <param name="bulletSpeed">
        /// Muzzle-relative bullet speed — must match
        /// <c>BulletShotMath</c> after bank modifiers, not a hardcoded default.
        /// </param>
        /// <param name="mapW">Toroidal map width (X) from <c>MapStateSingleton</c>.</param>
        /// <param name="mapH">Toroidal map height (Z) from <c>MapStateSingleton</c>.</param>
        /// <param name="engageRange">
        /// This barrel's acquire range — sizes max lead time the same way
        /// planetary turrets do (slow rounds need more time).
        /// </param>
        /// <param name="fireDir">Unit XZ direction to point the barrel.</param>
        /// <param name="leadSeconds">Clamped intercept time used for the aim offset.</param>
        /// <param name="interceptDistance">
        /// Planar flight budget from muzzle to the aim point (use this to grow
        /// <c>BulletElement.MaxDistance</c> so fleeing intercepts are not culled early).
        /// </param>
        /// <param name="aimPoint">
        /// Predicted intercept in muzzle-unwrapped world space (gun look-at).
        /// </param>
        /// <returns>False only when muzzle and target coincide (no meaningful aim).</returns>
        public static bool TryComputeFireSolution(
            float3 muzzle,
            float3 shooterVel,
            float3 targetPos,
            float3 targetVel,
            float bulletSpeed,
            float mapW,
            float mapH,
            float engageRange,
            out float3 fireDir,
            out float leadSeconds,
            out float interceptDistance,
            out float3 aimPoint)
        {
            fireDir = default;
            leadSeconds = 0f;
            interceptDistance = 0f;
            aimPoint = targetPos;

            // --- Relative velocity (moving shooter + inherited bullet velocity) ---
            // [STANDARD] Bullet world vel = aim * speed + shooterVel. The intercept
            // equation |R + (Vt - Vs) t| = speed * t is the stationary-shooter
            // quadratic with targetVel replaced by relative vel.
            // [TITAN-ORBIT] Do not skip shooterVel — that is the undershoot the
            // player sees when a MEGA strafes or flies while auto-firing.
            float3 relativeVel = targetVel - shooterVel;
            relativeVel.y = 0f;
            shooterVel.y = 0f;

            if (!PlanetaryDefenseAimMath.TryComputeFireSolution(
                    muzzle,
                    targetPos,
                    relativeVel,
                    bulletSpeed,
                    mapW,
                    mapH,
                    engageRange,
                    PlanetaryDefenseAimMath.ShipVelocityLeadScale,
                    out fireDir,
                    out leadSeconds,
                    out interceptDistance))
                return false;

            // --- Reconstruct the look-at point in unwrapped muzzle space ---
            // [TITAN-ORBIT] Same offset PlanetaryDefenseAimMath used for fireDir.
            // Working from ShortestOffsetXZ avoids wrapping the predicted point
            // onto the wrong map tile.
            float3 relative = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
            relative.y = 0f;
            float3 aimOffset = relative + relativeVel * leadSeconds;
            aimOffset.y = 0f;
            if (math.lengthsq(aimOffset) < MinDirectionSq)
                aimOffset = relative;

            aimPoint = muzzle + aimOffset;
            aimPoint.y = muzzle.y;
            return true;
        }
    }
}
