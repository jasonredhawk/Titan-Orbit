using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Planet population cap and passive growth formulas shared by server ECS
    /// (<see cref="ECS.Systems.PlanetPopulationGrowthSystem"/>) and legacy planet code.
    /// Max population scales with planet size and level; growth rate doubles each level.
    /// Pure math — Burst-friendly via Unity.Mathematics where possible.
    /// </summary>
    public static class PlanetPopulationMath
    {
        /// <summary>
        /// [TITAN-ORBIT] Seconds to pause passive growth after hostile people unload on a planet.
        /// </summary>
        public const float PopulationGrowthPauseAfterAttackSeconds = 1f;

        /// <summary>
        /// Max population from legacy Planet formula: size × level^1.5 (rounded to int).
        /// </summary>
        /// <param name="planetSize">World/visual scale of the planet (minimum 0.25).</param>
        /// <param name="planetLevel">Planet level (minimum 1).</param>
        public static int GetMaxPopulation(float planetSize, int planetLevel)
        {
            // --- Legacy cap formula: size × level^1.5 ---
            planetSize = Mathf.Max(0.25f, planetSize);
            int level = Mathf.Max(1, planetLevel);
            return Mathf.RoundToInt(planetSize * Mathf.Pow(level, 1.5f));
        }

        /// <summary>
        /// Passive growth for every planet: 1 person / 5 sec at level 1; rate doubles each level.
        /// Bigger home worlds repopulate faster only because they have higher level and max cap.
        /// </summary>
        /// <param name="planetLevel">Planet level (clamped to at least 1).</param>
        /// <returns>People added per second at this level.</returns>
        public static float GetGrowthRatePerSecond(int planetLevel)
        {
            // --- Exponential growth: doubles each level from 0.2/s at L1 ---
            int level = math.max(1, planetLevel);
            int exponent = math.max(0, level - 1);
            // [TITAN-ORBIT] 2^(level-1) / 5 — doubles growth each level from 0.2/s at L1.
            return math.pow(2f, exponent) / 5f;
        }

        /// <summary>
        /// Rounds fractional sim population to integer for HUD labels and UI display.
        /// </summary>
        public static int FractionalToDisplayPopulation(float fractionalPopulation, int maxPopulation)
        {
            // --- Round and clamp for HUD labels ---
            return math.clamp((int)math.round(fractionalPopulation), 0, maxPopulation);
        }
    }
}
