using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// When a gun/MEGA mount fires the reserved Rockets <see cref="BulletVfxBank"/>
    /// category, stamp the same homing flight as store ALT rockets
    /// (<see cref="RocketCatalog"/> speed, turn, acquire, lifetime).
    /// Catalog speed is authoritative — no hull/gun speed, no ship-velocity carry,
    /// no bank speed multiplier (same as <see cref="RocketShotMath"/> / <c>ShipRocketFireSystem</c>).
    /// Damage stays on the mount.
    /// </summary>
    public static class RocketHomingFire
    {
        /// <summary>True when this bank is the store-reserved Rockets category.</summary>
        public static bool IsRocketBank(int bankIndex) =>
            BulletBankProfileUtility.IsStoreReservedBankIndex(bankIndex);

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
        public static bool TryApply(
            int bankIndex,
            int shipLevel,
            float3 fireForward,
            ref BulletShotPlan plan,
            out float turnSpeedDeg,
            out float acquireRange)
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

            // --- Same velocity as ShipRocketFireSystem: catalog speed, no ship carry ---
            float speed = stats.speed > 0.01f ? stats.speed : 16f;
            plan.Velocity = fireForward * math.max(1f, speed);
            plan.Lifetime = math.max(0.1f, stats.lifetime);
            plan.MaxDistance = stats.maxDistance > 0.01f
                ? stats.maxDistance
                : RocketCatalog.UnlimitedFlightDistance;
            return true;
        }
    }
}
