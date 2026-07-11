using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Planet gem capacity and level-up rules shared by server ECS systems and legacy planet logic.
    /// Pure math — no EntityManager. Each level doubles max gem cap from level 1 base; depositing
    /// fills the bar and triggers level-up when full (gems reset to 0 on level up). Paired with
    /// <see cref="PlanetPopulationMath"/> for people economy. Burst-compatible via Unity.Mathematics.
    /// </summary>
    public static class PlanetEconomyMath
    {
        /// <summary>[TITAN-ORBIT] Highest planet level reachable via gem deposits.</summary>
        public const int MaxPlanetLevel = 6;

        /// <summary>[TITAN-ORBIT] Gem cap at level 1 before doubling per level.</summary>
        public const float BaseMaxGemsLevel1 = 100f;

        /// <summary>
        /// Max gems storable at a given planet level. Level 1 = 100, level 2 = 200, etc. (×2 per level).
        /// </summary>
        /// <param name="level">Planet level (clamped to at least 1).</param>
        public static float GetMaxGemsForLevel(int level)
        {
            level = math.max(1, level);
            // [STANDARD] Exponential cap: base × 2^(level-1).
            return BaseMaxGemsLevel1 * math.pow(2f, level - 1);
        }

        /// <summary>
        /// Adds gems up to the current level cap; when full, attempts one level-up and resets gems.
        /// Called by server deposit systems when ships contribute gems to a planet.
        /// </summary>
        /// <param name="planetLevel">Current level; incremented on successful level-up.</param>
        /// <param name="currentGems">Running gem total; clamped to cap, zeroed on level-up.</param>
        /// <param name="amount">Gems to add this deposit (ignored if ≤ 0).</param>
        public static void DepositGems(ref int planetLevel, ref float currentGems, float amount)
        {
            if (amount <= 0f)
                return;

            // --- Fill toward cap ---
            float maxGems = GetMaxGemsForLevel(planetLevel);
            currentGems = math.min(currentGems + amount, maxGems);

            // --- Level-up when bar is full ---
            // [TITAN-ORBIT] Small epsilon avoids float edge cases at exactly full.
            if (currentGems >= maxGems - 0.001f)
            {
                currentGems = maxGems;
                TryLevelUp(ref planetLevel, ref currentGems);
            }
        }

        /// <summary>
        /// Attempts one level-up when gems are at the current level cap. Returns false at max level
        /// or when gems are not full.
        /// </summary>
        /// <param name="planetLevel">Incremented on success.</param>
        /// <param name="currentGems">Reset to 0 on success.</param>
        /// <returns>True when level increased.</returns>
        public static bool TryLevelUp(ref int planetLevel, ref float currentGems)
        {
            if (planetLevel >= MaxPlanetLevel)
                return false;

            float maxGems = GetMaxGemsForLevel(planetLevel);
            if (currentGems < maxGems - 0.001f)
                return false;

            planetLevel++;
            currentGems = 0f;
            return true;
        }
    }
}
