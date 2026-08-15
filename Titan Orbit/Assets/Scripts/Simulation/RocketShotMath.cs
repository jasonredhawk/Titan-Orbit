using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared fire-time numbers for store rockets. <c>ShipRocketFireSystem</c> spawns with these
    /// values; <c>RocketLoadoutHUD</c> prints the same damage so the readout matches the shot.
    /// <para>
    /// [TITAN-ORBIT] Catalog fire power is multiplied by the reserved Rockets bank profile
    /// (same <see cref="BulletBankCombatLogic.ApplyFireModifiers"/> path as live fire).
    /// Infinite-rocket debug uses the ship's live level — see
    /// <see cref="TitanOrbit.TitanOrbitDebugFlags.InfiniteRockets"/>.
    /// </para>
    /// </summary>
    public static class RocketShotMath
    {
        /// <summary>
        /// Resolves catalog + bank modifiers for a stamped (or debug ship) level.
        /// </summary>
        /// <param name="itemLevel">Purchase level, or ship level when infinite debug is on.</param>
        /// <param name="stats">Sanitized catalog row.</param>
        /// <param name="damage">Damage written onto the spawned bullet.</param>
        /// <param name="speed">Flight speed after bank speed mul.</param>
        /// <param name="maxDistance">Travel budget, or <see cref="RocketCatalog.UnlimitedFlightDistance"/> when lifetime-only.</param>
        /// <param name="lifetime">Catalog seconds of flight (not rebuilt from range).</param>
        /// <param name="bankIndex">Reserved Rockets category, or 0 if missing.</param>
        /// <param name="extras">Fire-power extra levels (<c>itemLevel - 1</c>).</param>
        public static void Resolve(
            int itemLevel,
            out RocketCatalog.LevelStats stats,
            out float damage,
            out float speed,
            out float maxDistance,
            out float lifetime,
            out int bankIndex,
            out int extras)
        {
            int level = math.max(1, itemLevel);
            stats = RocketCatalog.Get(level);
            damage = stats.firePower;
            // Catalog speed / lifetime are authoritative — bank range/speed muls must not
            // rebuild lifetime from maxDistance (rockets have no travel budget).
            speed = stats.speed;
            lifetime = stats.lifetime;
            maxDistance = stats.maxDistance > 0.01f
                ? stats.maxDistance
                : RocketCatalog.UnlimitedFlightDistance;
            extras = math.max(0, level - 1);

            int found = BulletBankProfileUtility.FindRocketBankIndex();
            bankIndex = found >= 0 ? found : 0;
            float dummySpeed = speed;
            float dummyRange = maxDistance;
            float dummyLife = lifetime;
            float fireRate = 1f;
            BulletBankCombatLogic.ApplyFireModifiers(
                bankIndex, ref damage, ref dummySpeed, ref dummyRange, ref dummyLife, ref fireRate,
                extras);
        }

        /// <summary>Fired damage only — HUD readout (matches the spawned bullet).</summary>
        public static float ResolveDamage(int itemLevel)
        {
            Resolve(itemLevel, out _, out float damage, out _, out _, out _, out _, out _);
            return math.max(0.1f, damage);
        }
    }
}
