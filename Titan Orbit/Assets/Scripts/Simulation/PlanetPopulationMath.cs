using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Simulation
{
    public static class PlanetPopulationMath
    {
        /// <summary>Pause passive growth briefly after hostile people unload.</summary>
        public const float PopulationGrowthPauseAfterAttackSeconds = 1f;

        /// <summary>Max population from legacy Planet.GetMaxPopulationForPlanet (size × level^1.5).</summary>
        public static int GetMaxPopulation(float planetSize, int planetLevel)
        {
            planetSize = Mathf.Max(0.25f, planetSize);
            int level = Mathf.Max(1, planetLevel);
            return Mathf.RoundToInt(planetSize * Mathf.Pow(level, 1.5f));
        }

        /// <summary>
        /// Passive growth for every planet: 1 person / 5 sec at level 1, rate doubles each level.
        /// Bigger home worlds repopulate faster only because they have a higher level and max cap.
        /// </summary>
        public static float GetGrowthRatePerSecond(int planetLevel)
        {
            int level = math.max(1, planetLevel);
            int exponent = math.max(0, level - 1);
            return math.pow(2f, exponent) / 5f;
        }

        public static int FractionalToDisplayPopulation(float fractionalPopulation, int maxPopulation)
        {
            return math.clamp((int)math.round(fractionalPopulation), 0, maxPopulation);
        }
    }
}
