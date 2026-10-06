using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// When a gun/MEGA mount fires the reserved Rockets <see cref="BulletVfxBank"/>
    /// category, stamp homing flight (turn, acquire, lifetime) from
    /// <see cref="RocketCatalog"/>. Store ALT rockets also use that catalog's speed.
    /// Titan / MEGA launchers pass the catalog Weapon Missile <c>bulletSpeed</c>
    /// already stamped on the mount — no hull carry, no bank speed multiplier.
    /// Damage stays on the mount.
    /// </summary>
    public static class RocketHomingFire
    {
        /// <summary>True when this bank is the store-reserved Rockets category.</summary>
        public static bool IsRocketBank(int bankIndex) =>
            BulletBankProfileUtility.IsStoreReservedBankIndex(bankIndex);

        /// <summary>
        /// Flight speed for a Rockets-bank shot. A positive
        /// <paramref name="flightSpeedOverride"/> (MEGA Weapon Missile Stats /
        /// unique-row <c>bulletSpeed</c>) wins; otherwise <see cref="RocketCatalog"/>.
        /// </summary>
        public static float ResolveFlightSpeed(int shipLevel, float flightSpeedOverride = 0f)
        {
            if (flightSpeedOverride > 0.01f)
                return math.max(1f, flightSpeedOverride);

            float speed = RocketCatalog.Get(math.max(1, shipLevel)).speed;
            return math.max(1f, speed > 0.01f ? speed : 16f);
        }

        /// <summary>
        /// Rewrites <paramref name="plan"/> to rocket flight and returns homing kinematics.
        /// False when the bank is not Rockets — plan is unchanged.
        /// </summary>
        /// <param name="bankIndex">Fired <c>BulletVfxBank</c> category.</param>
        /// <param name="shipLevel">Store pack / MEGA hull level for the catalog row (clamped).</param>
        /// <param name="fireForward">Barrel aim on XZ.</param>
        /// <param name="plan">Shot plan from <see cref="BulletShotMath.Build"/>.</param>
        /// <param name="turnSpeedDeg">Max yaw rate written onto the bullet.</param>
        /// <param name="acquireRange">Toroidal lock bubble written onto the bullet.</param>
        /// <param name="flightSpeedOverride">
        /// MEGA missile mount <c>BulletSpeed</c>. 0 keeps store <see cref="RocketCatalog"/> speed.
        /// </param>
        public static bool TryApply(
            int bankIndex,
            int shipLevel,
            float3 fireForward,
            ref BulletShotPlan plan,
            out float turnSpeedDeg,
            out float acquireRange,
            float flightSpeedOverride = 0f)
        {
            turnSpeedDeg = 0f;
            acquireRange = 0f;
            if (!IsRocketBank(bankIndex))
                return false;

            RocketCatalog.LevelStats stats = RocketCatalog.Get(math.max(1, shipLevel));
            turnSpeedDeg = stats.turnSpeedDegreesPerSecond;
            acquireRange = stats.acquireRange > 0.01f
                ? stats.acquireRange
                : RocketCatalog.DefaultAcquireRange;

            fireForward.y = 0f;
            if (math.lengthsq(fireForward) < 0.0001f)
                fireForward = new float3(0f, 0f, 1f);
            else
                fireForward = math.normalize(fireForward);

            // --- Store rockets: RocketCatalog speed. Titan missiles: mount override. ---
            // No ship-velocity carry (same as ShipRocketFireSystem).
            float speed = ResolveFlightSpeed(shipLevel, flightSpeedOverride);
            plan.Velocity = fireForward * speed;
            plan.Lifetime = math.max(0.1f, stats.lifetime);
            plan.MaxDistance = stats.maxDistance > 0.01f
                ? stats.maxDistance
                : RocketCatalog.UnlimitedFlightDistance;
            return true;
        }
    }
}
