using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared place-time numbers for store mines. <c>ShipMineDeploySystem</c> stamps these
    /// onto <c>DeployedMineElement</c>; <c>RocketLoadoutHUD</c> prints the same damage so the
    /// readout matches the explode.
    /// <para>
    /// [TITAN-ORBIT] Catalog fire power is the center damage — mines do not use a bullet-bank
    /// fire-power multiplier. Infinite-mine debug uses the ship's live level — see
    /// <see cref="TitanOrbit.TitanOrbitDebugFlags.InfiniteMines"/>.
    /// </para>
    /// </summary>
    public static class MineShotMath
    {
        /// <summary>
        /// Resolves catalog numbers for a stamped (or debug ship) level.
        /// </summary>
        /// <param name="itemLevel">Purchase level, or ship level when infinite debug is on.</param>
        /// <param name="stats">Sanitized catalog row.</param>
        /// <param name="damage">Center damage written onto the deployed mine.</param>
        /// <param name="visualScale">Mesh size from the catalog row, or damage vs L1 when that field is 0.</param>
        public static void Resolve(
            int itemLevel,
            out MineCatalog.LevelStats stats,
            out float damage,
            out float visualScale)
        {
            int level = math.max(1, itemLevel);
            stats = MineCatalog.Get(level);
            damage = stats.firePower;

            // Catalog visualScale is the mesh size (0.25 = quarter of a 1× mine).
            // 0 = derive from damage vs level-1 so a blank row still has a ladder.
            visualScale = stats.visualScale > 0.01f
                ? math.max(0.05f, stats.visualScale)
                : ResolveVisualScaleFromDamage(damage);
        }

        /// <summary>Center damage only — HUD readout (matches the deployed mine).</summary>
        public static float ResolveDamage(int itemLevel)
        {
            Resolve(itemLevel, out _, out float damage, out _);
            return math.max(0.1f, damage);
        }

        /// <summary>
        /// Fallback when a catalog row leaves <c>visualScale</c> at 0.
        /// <c>1</c> at level-1 catalog damage; grows linearly with damage.
        /// </summary>
        public static float ResolveVisualScaleFromDamage(float damage)
        {
            float reference = MineCatalog.Get(1).firePower;
            return math.max(0.1f, damage / math.max(0.01f, reference));
        }
    }
}
