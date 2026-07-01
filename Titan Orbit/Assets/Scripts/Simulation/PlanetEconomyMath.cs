using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>Planet gem capacity and level-up rules ported from legacy Planet.</summary>
    public static class PlanetEconomyMath
    {
        public const int MaxPlanetLevel = 6;
        public const float BaseMaxGemsLevel1 = 100f;

        public static float GetMaxGemsForLevel(int level)
        {
            level = math.max(1, level);
            return BaseMaxGemsLevel1 * math.pow(2f, level - 1);
        }

        /// <summary>Add gems up to the current level cap; level up and reset gems when full.</summary>
        public static void DepositGems(ref int planetLevel, ref float currentGems, float amount)
        {
            if (amount <= 0f)
                return;

            float maxGems = GetMaxGemsForLevel(planetLevel);
            currentGems = math.min(currentGems + amount, maxGems);
            if (currentGems >= maxGems - 0.001f)
            {
                currentGems = maxGems;
                TryLevelUp(ref planetLevel, ref currentGems);
            }
        }

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
