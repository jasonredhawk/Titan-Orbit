using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Planet population cap and passive growth formulas shared by server ECS
    /// (<see cref="ECS.Systems.PlanetPopulationGrowthSystem"/>) and legacy planet code.
    /// Max population scales with planet size and level; passive growth is a fixed fraction of that
    /// cap so every planet takes the same time to refill from empty (see
    /// <see cref="FullRefillSeconds"/>). Pure math — Burst-friendly via Unity.Mathematics where possible.
    /// </summary>
    public static class PlanetPopulationMath
    {
        /// <summary>
        /// [TITAN-ORBIT] Seconds to pause passive growth after hostile people unload on a planet.
        /// </summary>
        public const float PopulationGrowthPauseAfterAttackSeconds = 1f;

        /// <summary>
        /// [TITAN-ORBIT] Seconds for a planet to grow from 0 to its current max population at the
        /// passive rate. Growth = maxPop / FullRefillSeconds people per second, so larger / higher-level
        /// planets add more people per second but still take this long to refill.
        /// Longer refill leaves a freshly captured (empty) planet vulnerable to recapture before it
        /// can stock defenders again.
        /// </summary>
        public const float FullRefillSeconds = 120f;

        /// <summary>
        /// [TITAN-ORBIT] Exponent on planet level in the max-population formula
        /// (<c>size × level^PopulationLevelExponent</c>). Higher than the old 1.5 so late-game
        /// planets hold more people relative to a few fully loaded high-level ships.
        /// </summary>
        public const float PopulationLevelExponent = 1.7f;

        /// <summary>
        /// Max population: size × level^<see cref="PopulationLevelExponent"/> (rounded to int).
        /// Example at level 6 (6^1.7 ≈ 21.0): size 15 → ~315, size 20 home → ~421.
        /// </summary>
        /// <param name="planetSize">World/visual scale of the planet (minimum 0.25).</param>
        /// <param name="planetLevel">Planet level (minimum 1).</param>
        public static int GetMaxPopulation(float planetSize, int planetLevel)
        {
            // --- Cap formula: size × level^1.7 ---
            // [TITAN-ORBIT] Raised from level^1.5 so top planets outscale a small late-game raid fleet.
            planetSize = Mathf.Max(0.25f, planetSize);
            int level = Mathf.Max(1, planetLevel);
            return Mathf.RoundToInt(planetSize * Mathf.Pow(level, PopulationLevelExponent));
        }

        /// <summary>
        /// Passive growth rate so empty → full always takes <see cref="FullRefillSeconds"/>.
        /// Pass the effective max (after territory / connection bonuses) so a boosted cap still
        /// refills in the same wall-clock time — rate scales with that larger max.
        /// </summary>
        /// <param name="maxPopulation">Effective population cap for this planet (minimum 1).</param>
        /// <returns>People added per second toward the cap.</returns>
        public static float GetGrowthRatePerSecond(int maxPopulation)
        {
            // --- Percent-of-cap growth: 1/FullRefillSeconds of max per second ---
            // [TITAN-ORBIT] Replaces the old 2^(level-1)/5 curve, which made high levels refill in seconds.
            int maxPop = math.max(1, maxPopulation);
            return maxPop / FullRefillSeconds;
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
